using Dapper;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using SerialMemory.Core.Interfaces;

namespace SerialMemory.Infrastructure.Integrity;

/// <summary>
/// Background worker for automatic integrity hashing and verification.
/// Processes new memories and runs periodic chain verification.
/// </summary>
public sealed class IntegrityWorker(
    IServiceProvider serviceProvider,
    string connectionString,
    ILogger<IntegrityWorker> logger)
    : BackgroundService
{
    private readonly IServiceProvider _serviceProvider = serviceProvider;

    private readonly TimeSpan _hashingInterval = TimeSpan.FromSeconds(30);
    private readonly TimeSpan _verificationInterval = TimeSpan.FromHours(1);
    private readonly int _batchSize = 100;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("IntegrityWorker started");

        var lastVerificationTime = DateTimeOffset.MinValue;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Process unhashed memories
                await ProcessUnhashedMemoriesAsync(stoppingToken);

                // Run periodic chain verification (every hour)
                if (DateTimeOffset.UtcNow - lastVerificationTime > _verificationInterval)
                {
                    await RunPeriodicVerificationAsync(stoppingToken);
                    lastVerificationTime = DateTimeOffset.UtcNow;
                }

                await Task.Delay(_hashingInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "IntegrityWorker error");
                await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
            }
        }

        logger.LogInformation("IntegrityWorker stopped");
    }

    private async Task ProcessUnhashedMemoriesAsync(CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);

        // Set internal admin role and a valid tenant_id to bypass RLS
        await conn.ExecuteAsync("SET app.role = 'internal_admin'; SET app.tenant_id = '00000000-0000-0000-0000-000000000000'");

        // Get distinct tenants with unhashed memories
        var tenants = await conn.QueryAsync<Guid>(@"
            SELECT DISTINCT tenant_id
            FROM memories
            WHERE content_hash IS NULL
            LIMIT 10");

        var tenantList = tenants.ToList();
        if (tenantList.Count == 0)
            return;

        logger.LogDebug("Processing unhashed memories for {Count} tenants", tenantList.Count);

        foreach (var tenantId in tenantList)
        {
            await ProcessTenantMemoriesAsync(tenantId, ct);
        }
    }

    private async Task ProcessTenantMemoriesAsync(Guid tenantId, CancellationToken ct)
    {
        try
        {
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync(ct);
            await conn.ExecuteAsync("SET app.role = 'internal_admin'; SET app.tenant_id = @TenantId::text", new { TenantId = tenantId });
            await conn.ExecuteAsync("SELECT set_tenant_context(@TenantId)", new { TenantId = tenantId });

            // Get unhashed memories in chronological order
            var memories = await conn.QueryAsync<MemoryHashRow>(@"
                SELECT id, tenant_id, content, metadata, source, created_at
                FROM memories
                WHERE tenant_id = @TenantId AND content_hash IS NULL
                ORDER BY created_at ASC
                LIMIT @BatchSize",
                new { TenantId = tenantId, BatchSize = _batchSize });

            var memoryList = memories.ToList();
            if (memoryList.Count == 0)
                return;

            logger.LogDebug("Hashing {Count} memories for tenant {TenantId}", memoryList.Count, tenantId);

            // Get tenant's hash algorithm
            var algorithm = await conn.ExecuteScalarAsync<string?>(@"
                SELECT hash_algorithm FROM tenant_privacy_settings WHERE tenant_id = @TenantId",
                new { TenantId = tenantId }) ?? "SHA256";

            string? previousChainHash = null;

            foreach (var memory in memoryList)
            {
                // Get previous chain hash if not already fetched
                if (previousChainHash == null)
                {
                    previousChainHash = await conn.ExecuteScalarAsync<string?>(@"
                        SELECT chain_hash FROM memories
                        WHERE tenant_id = @TenantId AND created_at < @CreatedAt AND chain_hash IS NOT NULL
                        ORDER BY created_at DESC LIMIT 1",
                        new { TenantId = tenantId, CreatedAt = memory.created_at }) ?? "GENESIS";
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

                // Compute content hash
                var canonicalForm = CryptoUtilities.CreateCanonicalForm(
                    record.TenantId,
                    record.Id,
                    record.CreatedAt,
                    record.Content,
                    record.Metadata,
                    record.Source);

                var canonicalString = CryptoUtilities.SerializeCanonicalForm(canonicalForm);
                var contentHash = CryptoUtilities.ComputeHash(canonicalString, algorithm);
                var chainHash = CryptoUtilities.ComputeChainHash(previousChainHash, contentHash, algorithm);

                // Store proof bundle
                var proofBundle = System.Text.Json.JsonSerializer.Serialize(new IntegrityProof
                {
                    MemoryId = record.Id,
                    TenantId = record.TenantId,
                    Timestamp = record.CreatedAt,
                    HashAlgorithm = algorithm,
                    ContentHash = contentHash,
                    ChainHash = chainHash,
                    PreviousChainHash = previousChainHash,
                    ComputedAt = DateTimeOffset.UtcNow
                });

                // Update memory
                await conn.ExecuteAsync(@"
                    UPDATE memories
                    SET content_hash = @ContentHash,
                        chain_hash = @ChainHash,
                        integrity_status = 'valid',
                        proof_bundle = @ProofBundle::jsonb
                    WHERE id = @MemoryId",
                    new
                    {
                        MemoryId = record.Id,
                        ContentHash = contentHash,
                        ChainHash = chainHash,
                        ProofBundle = proofBundle
                    });

                previousChainHash = chainHash;
            }

            logger.LogInformation("Hashed {Count} memories for tenant {TenantId}", memoryList.Count, tenantId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to hash memories for tenant {TenantId}", tenantId);
        }
    }

    private async Task RunPeriodicVerificationAsync(CancellationToken ct)
    {
        logger.LogInformation("Starting periodic integrity verification");

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);

        // Set internal admin role and a valid tenant_id to bypass RLS
        await conn.ExecuteAsync("SET app.role = 'internal_admin'; SET app.tenant_id = '00000000-0000-0000-0000-000000000000'");

        // Get tenants with hash verification enabled
        var tenants = await conn.QueryAsync<Guid>(@"
            SELECT tenant_id FROM tenant_privacy_settings
            WHERE enable_hash_verification = true");

        var tenantList = tenants.ToList();
        logger.LogDebug("Running verification for {Count} tenants", tenantList.Count);

        foreach (var tenantId in tenantList)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                await VerifyTenantChainAsync(tenantId, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Verification failed for tenant {TenantId}", tenantId);
            }
        }

        logger.LogInformation("Completed periodic integrity verification");
    }

    private async Task VerifyTenantChainAsync(Guid tenantId, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await conn.ExecuteAsync("SET app.role = 'internal_admin'; SET app.tenant_id = @TenantId::text", new { TenantId = tenantId });
        await conn.ExecuteAsync("SELECT set_tenant_context(@TenantId)", new { TenantId = tenantId });

        // Check settings
        var settings = await conn.QueryFirstOrDefaultAsync<SettingsRow>(@"
            SELECT hash_algorithm, auto_verify_interval_hours
            FROM tenant_privacy_settings
            WHERE tenant_id = @TenantId",
            new { TenantId = tenantId });

        if (settings == null)
            return;

        // Get memories with hashes in order
        var memories = await conn.QueryAsync<MemoryVerifyRow>(@"
            SELECT id, content, metadata, source, created_at, content_hash, chain_hash
            FROM memories
            WHERE tenant_id = @TenantId AND content_hash IS NOT NULL
            ORDER BY created_at ASC",
            new { TenantId = tenantId });

        var memoryList = memories.ToList();
        if (memoryList.Count == 0)
            return;

        var invalidCount = 0;
        string? previousChainHash = "GENESIS";

        foreach (var memory in memoryList)
        {
            // Recompute hashes
            var canonicalForm = CryptoUtilities.CreateCanonicalForm(
                tenantId,
                memory.id,
                memory.created_at,
                memory.content,
                memory.metadata,
                memory.source);

            var canonicalString = CryptoUtilities.SerializeCanonicalForm(canonicalForm);
            var expectedContentHash = CryptoUtilities.ComputeHash(canonicalString, settings.hash_algorithm);
            var expectedChainHash = CryptoUtilities.ComputeChainHash(previousChainHash, expectedContentHash, settings.hash_algorithm);

            var contentValid = string.Equals(memory.content_hash, expectedContentHash, StringComparison.OrdinalIgnoreCase);
            var chainValid = string.Equals(memory.chain_hash, expectedChainHash, StringComparison.OrdinalIgnoreCase);

            if (!contentValid || !chainValid)
            {
                invalidCount++;
                await conn.ExecuteAsync(@"
                    UPDATE memories SET integrity_status = 'invalid' WHERE id = @Id",
                    new { Id = memory.id });

                logger.LogWarning("Integrity mismatch for memory {MemoryId}: content={ContentValid}, chain={ChainValid}",
                    memory.id, contentValid, chainValid);
            }

            previousChainHash = memory.chain_hash;
        }

        if (invalidCount > 0)
        {
            logger.LogWarning("Tenant {TenantId} has {Count} memories with integrity issues", tenantId, invalidCount);
        }
        else
        {
            logger.LogDebug("Tenant {TenantId} chain verified: {Count} memories OK", tenantId, memoryList.Count);
        }
    }

    private sealed record MemoryHashRow
    {
        public Guid id { get; init; }
        public Guid tenant_id { get; init; }
        public string content { get; init; } = "";
        public string? metadata { get; init; }
        public string? source { get; init; }
        public DateTimeOffset created_at { get; init; }
    }

    private sealed record MemoryVerifyRow
    {
        public Guid id { get; init; }
        public string content { get; init; } = "";
        public string? metadata { get; init; }
        public string? source { get; init; }
        public DateTimeOffset created_at { get; init; }
        public string content_hash { get; init; } = "";
        public string chain_hash { get; init; } = "";
    }

    private sealed record SettingsRow
    {
        public string hash_algorithm { get; init; } = "SHA256";
        public int auto_verify_interval_hours { get; init; }
    }
}
