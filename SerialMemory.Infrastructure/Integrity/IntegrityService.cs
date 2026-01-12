using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using SerialMemory.Core.Interfaces;

namespace SerialMemory.Infrastructure.Integrity;

/// <summary>
/// PostgreSQL-backed integrity service for computing and verifying cryptographic proofs.
/// Implements tamper-evident hashing with chain verification.
/// </summary>
public sealed class IntegrityService(
    string connectionString,
    ITenantContext tenantContext,
    ILogger<IntegrityService> logger)
    : IIntegrityService
{
    private readonly ITenantContext _tenantContext = tenantContext;
    private readonly Guid _tenantId = Guid.Parse(tenantContext.TenantId);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public async Task<IntegrityProof> ComputeProofAsync(MemoryIntegrityRecord memory, CancellationToken ct = default)
    {
        // Get tenant's hash algorithm preference
        var algorithm = await GetHashAlgorithmAsync(ct);

        // Create canonical form
        var canonicalForm = CryptoUtilities.CreateCanonicalForm(
            memory.TenantId,
            memory.Id,
            memory.CreatedAt,
            memory.Content,
            memory.Metadata,
            memory.Source);

        // Serialize and compute content hash
        var canonicalString = CryptoUtilities.SerializeCanonicalForm(canonicalForm);
        var contentHash = CryptoUtilities.ComputeHash(canonicalString, algorithm);

        // Get previous chain hash
        var previousChainHash = memory.PreviousChainHash ?? await GetPreviousChainHashAsync(memory.Id, memory.CreatedAt, ct);

        // Compute chain hash
        var chainHash = CryptoUtilities.ComputeChainHash(previousChainHash, contentHash, algorithm);

        return new IntegrityProof
        {
            MemoryId = memory.Id,
            TenantId = memory.TenantId,
            Timestamp = memory.CreatedAt,
            HashAlgorithm = algorithm,
            ContentHash = contentHash,
            ChainHash = chainHash,
            PreviousChainHash = previousChainHash,
            CanonicalForm = canonicalForm,
            ComputedAt = DateTimeOffset.UtcNow,
            ProofVersion = 1
        };
    }

    public async Task<IntegrityVerificationResult> VerifyProofAsync(Guid memoryId, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await conn.ExecuteAsync($"SET app.tenant_id = '{_tenantId}'");

        // Get memory data
        var memory = await conn.QueryFirstOrDefaultAsync<MemoryRow>(@"
            SELECT id, tenant_id, content, metadata, source, created_at,
                   content_hash, chain_hash, integrity_status, proof_bundle
            FROM memories
            WHERE id = @MemoryId",
            new { MemoryId = memoryId });

        if (memory == null)
        {
            return new IntegrityVerificationResult
            {
                MemoryId = memoryId,
                IsValid = false,
                Status = "not_found",
                FailureReason = "Memory not found"
            };
        }

        // Recompute proof
        var record = new MemoryIntegrityRecord
        {
            Id = memory.id,
            TenantId = memory.tenant_id,
            CreatedAt = memory.created_at,
            Content = memory.content,
            Metadata = memory.metadata,
            Source = memory.source
        };

        var computedProof = await ComputeProofAsync(record, ct);

        // Compare hashes
        var contentHashValid = string.Equals(memory.content_hash, computedProof.ContentHash, StringComparison.OrdinalIgnoreCase);
        var chainHashValid = string.Equals(memory.chain_hash, computedProof.ChainHash, StringComparison.OrdinalIgnoreCase);

        var isValid = contentHashValid && chainHashValid;
        var status = isValid ? "valid" : (memory.content_hash == null ? "pending" : "invalid");

        if (!isValid && memory.content_hash != null)
        {
            logger.LogWarning("Integrity verification failed for memory {MemoryId}: content={ContentMatch}, chain={ChainMatch}",
                memoryId, contentHashValid, chainHashValid);
        }

        return new IntegrityVerificationResult
        {
            MemoryId = memoryId,
            IsValid = isValid,
            Status = status,
            ExpectedContentHash = computedProof.ContentHash,
            ActualContentHash = memory.content_hash,
            ExpectedChainHash = computedProof.ChainHash,
            ActualChainHash = memory.chain_hash,
            FailureReason = !isValid ? BuildFailureReason(contentHashValid, chainHashValid) : null
        };
    }

    public async Task<ChainVerificationResult> VerifyChainAsync(Guid tenantId, CancellationToken ct = default)
    {
        var runId = Guid.CreateVersion7();
        var startedAt = DateTimeOffset.UtcNow;

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await conn.ExecuteAsync($"SET app.tenant_id = '{tenantId}'");

        // Record verification run
        await conn.ExecuteAsync(@"
            INSERT INTO integrity_verification_runs (id, tenant_id, started_at, status, triggered_by)
            VALUES (@Id, @TenantId, @StartedAt, 'running', 'api')",
            new { Id = runId, TenantId = tenantId, StartedAt = startedAt });

        var invalidIds = new List<Guid>();
        var orphanIds = new List<Guid>();
        int verifiedCount = 0;
        int pendingCount = 0;
        string? errorMessage = null;

        try
        {
            // Get all memories in chronological order
            var memories = await conn.QueryAsync<MemoryRow>(@"
                SELECT id, tenant_id, content, metadata, source, created_at,
                       content_hash, chain_hash, integrity_status
                FROM memories
                ORDER BY created_at ASC");

            var memoryList = memories.ToList();
            string? previousChainHash = "GENESIS";

            foreach (var memory in memoryList)
            {
                if (memory.content_hash == null)
                {
                    pendingCount++;
                    continue;
                }

                var record = new MemoryIntegrityRecord
                {
                    Id = memory.id,
                    TenantId = memory.tenant_id,
                    CreatedAt = memory.created_at,
                    Content = memory.content,
                    Metadata = memory.metadata,
                    Source = memory.source,
                    PreviousChainHash = previousChainHash
                };

                var proof = await ComputeProofAsync(record, ct);

                var contentValid = string.Equals(memory.content_hash, proof.ContentHash, StringComparison.OrdinalIgnoreCase);
                var chainValid = string.Equals(memory.chain_hash, proof.ChainHash, StringComparison.OrdinalIgnoreCase);

                if (!contentValid || !chainValid)
                {
                    invalidIds.Add(memory.id);

                    // Check if it's an orphan (broken chain link)
                    if (!chainValid && contentValid)
                    {
                        orphanIds.Add(memory.id);
                    }

                    // Mark as invalid in database
                    await conn.ExecuteAsync(@"
                        UPDATE memories SET integrity_status = 'invalid' WHERE id = @Id",
                        new { Id = memory.id });
                }
                else
                {
                    verifiedCount++;
                    previousChainHash = memory.chain_hash;

                    // Mark as valid
                    await conn.ExecuteAsync(@"
                        UPDATE memories SET integrity_status = 'valid' WHERE id = @Id",
                        new { Id = memory.id });
                }
            }

            // Update run record
            await conn.ExecuteAsync(@"
                UPDATE integrity_verification_runs
                SET completed_at = @CompletedAt, status = 'completed',
                    total_memories = @Total, verified_count = @Verified,
                    invalid_count = @Invalid, orphan_count = @Orphan
                WHERE id = @Id",
                new
                {
                    Id = runId,
                    CompletedAt = DateTimeOffset.UtcNow,
                    Total = memoryList.Count,
                    Verified = verifiedCount,
                    Invalid = invalidIds.Count,
                    Orphan = orphanIds.Count
                });
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            logger.LogError(ex, "Chain verification failed for tenant {TenantId}", tenantId);

            await conn.ExecuteAsync(@"
                UPDATE integrity_verification_runs
                SET completed_at = @CompletedAt, status = 'failed', error_message = @Error
                WHERE id = @Id",
                new { Id = runId, CompletedAt = DateTimeOffset.UtcNow, Error = errorMessage });
        }

        return new ChainVerificationResult
        {
            TenantId = tenantId,
            VerificationRunId = runId,
            IsValid = invalidIds.Count == 0 && errorMessage == null,
            TotalMemories = verifiedCount + invalidIds.Count + pendingCount,
            VerifiedCount = verifiedCount,
            InvalidCount = invalidIds.Count,
            OrphanCount = orphanIds.Count,
            PendingCount = pendingCount,
            InvalidMemoryIds = invalidIds,
            OrphanMemoryIds = orphanIds,
            StartedAt = startedAt,
            CompletedAt = DateTimeOffset.UtcNow,
            ErrorMessage = errorMessage
        };
    }

    public async Task<IntegrityProof?> GetProofAsync(Guid memoryId, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await conn.ExecuteAsync($"SET app.tenant_id = '{_tenantId}'");

        var proofJson = await conn.ExecuteScalarAsync<string?>(@"
            SELECT proof_bundle FROM memories WHERE id = @MemoryId",
            new { MemoryId = memoryId });

        if (string.IsNullOrEmpty(proofJson))
            return null;

        return JsonSerializer.Deserialize<IntegrityProof>(proofJson, JsonOptions);
    }

    public async Task<IntegrityProof> UpdateIntegrityAsync(Guid memoryId, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await conn.ExecuteAsync($"SET app.tenant_id = '{_tenantId}'");

        // Get memory data
        var memory = await conn.QueryFirstOrDefaultAsync<MemoryRow>(@"
            SELECT id, tenant_id, content, metadata, source, created_at
            FROM memories WHERE id = @MemoryId",
            new { MemoryId = memoryId });

        if (memory == null)
            throw new InvalidOperationException($"Memory {memoryId} not found");

        var record = new MemoryIntegrityRecord
        {
            Id = memory.id,
            TenantId = memory.tenant_id,
            CreatedAt = memory.created_at,
            Content = memory.content,
            Metadata = memory.metadata,
            Source = memory.source
        };

        var proof = await ComputeProofAsync(record, ct);
        var proofJson = JsonSerializer.Serialize(proof, JsonOptions);

        // Update memory with integrity data
        await conn.ExecuteAsync(@"
            UPDATE memories
            SET content_hash = @ContentHash,
                chain_hash = @ChainHash,
                integrity_status = 'valid',
                proof_bundle = @ProofBundle::jsonb
            WHERE id = @MemoryId",
            new
            {
                MemoryId = memoryId,
                proof.ContentHash,
                proof.ChainHash,
                ProofBundle = proofJson
            });

        logger.LogInformation("Updated integrity for memory {MemoryId}", memoryId);

        return proof;
    }

    public async Task MarkInvalidAsync(Guid memoryId, string reason, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await conn.ExecuteAsync($"SET app.tenant_id = '{_tenantId}'");

        await conn.ExecuteAsync(@"
            UPDATE memories
            SET integrity_status = 'invalid',
                proof_bundle = COALESCE(proof_bundle, '{}'::jsonb) || jsonb_build_object('invalidReason', @Reason, 'invalidAt', @Now)
            WHERE id = @MemoryId",
            new { MemoryId = memoryId, Reason = reason, Now = DateTimeOffset.UtcNow });

        logger.LogWarning("Marked memory {MemoryId} as invalid: {Reason}", memoryId, reason);
    }

    private async Task<string> GetHashAlgorithmAsync(CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await conn.ExecuteAsync($"SET app.tenant_id = '{_tenantId}'");

        var algorithm = await conn.ExecuteScalarAsync<string?>(@"
            SELECT hash_algorithm FROM tenant_privacy_settings WHERE tenant_id = @TenantId",
            new { TenantId = _tenantId });

        return algorithm ?? "SHA256";
    }

    private async Task<string> GetPreviousChainHashAsync(Guid memoryId, DateTimeOffset createdAt, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await conn.ExecuteAsync($"SET app.tenant_id = '{_tenantId}'");

        var previousHash = await conn.ExecuteScalarAsync<string?>(@"
            SELECT chain_hash FROM memories
            WHERE tenant_id = @TenantId
              AND created_at < @CreatedAt
              AND chain_hash IS NOT NULL
            ORDER BY created_at DESC
            LIMIT 1",
            new { TenantId = _tenantId, CreatedAt = createdAt });

        return previousHash ?? "GENESIS";
    }

    private static string BuildFailureReason(bool contentValid, bool chainValid)
    {
        if (!contentValid && !chainValid)
            return "Content hash and chain hash mismatch - possible tampering";
        if (!contentValid)
            return "Content hash mismatch - content may have been modified";
        if (!chainValid)
            return "Chain hash mismatch - chain integrity broken";
        return "Unknown verification failure";
    }

    private sealed record MemoryRow
    {
        public Guid id { get; init; }
        public Guid tenant_id { get; init; }
        public string content { get; init; } = "";
        public string? metadata { get; init; }
        public string? source { get; init; }
        public DateTimeOffset created_at { get; init; }
        public string? content_hash { get; init; }
        public string? chain_hash { get; init; }
        public string? integrity_status { get; init; }
        public string? proof_bundle { get; init; }
    }
}
