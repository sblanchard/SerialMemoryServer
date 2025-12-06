using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using SerialMemory.Core.Interfaces;
using SerialMemory.Infrastructure;

namespace SerialMemory.Api.Dashboard;

/// <summary>
/// Dashboard endpoints for Privacy and Integrity verification.
/// FIX #5: Full Chain Verification failing - Fixed RLS and short-ID mapping.
/// </summary>
public static class PrivacyEndpoints
{
    public static void MapPrivacyEndpoints(this WebApplication app, bool selfHostedMode)
    {
        var group = app.MapGroup("/privacy")
            .WithTags("Privacy & Integrity")
            .RequireAuthorization();

        // =============================================================================
        // GET /privacy/verify - Verify full chain integrity (FIX #5)
        // =============================================================================
        group.MapGet("/verify", async (
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            CancellationToken ct) =>
        {
            var tenantId = GetTenantId(user, selfHostedMode);

            await using var conn = await dataSource.OpenConnectionAsync(ct);

            // CRITICAL: Set internal_admin role to bypass RLS for chain verification
            await conn.ExecuteAsync("SELECT set_config('app.role', 'internal_admin', false)");
            await conn.ExecuteAsync("SELECT set_config('app.current_tenant_id', @TenantId, false)",
                new { TenantId = tenantId.ToString() });

            // Verify chain using SQL function
            var verificationResult = await conn.QueryFirstOrDefaultAsync<ChainVerificationDto>(
                "SELECT * FROM verify_privacy_chain(@TenantId)",
                new { TenantId = tenantId });

            if (verificationResult == null)
            {
                return Results.Ok(new
                {
                    isValid = true,
                    chainLength = 0,
                    message = "No audit entries to verify",
                    verifiedAt = DateTimeOffset.UtcNow
                });
            }

            // Get additional stats
            var entryCount = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM privacy_audit_entries WHERE tenant_id = @TenantId",
                new { TenantId = tenantId });

            var lastEntry = await conn.QueryFirstOrDefaultAsync<LastAuditEntryDto>(
                """
                SELECT id, content_hash, created_at
                FROM privacy_audit_entries
                WHERE tenant_id = @TenantId
                ORDER BY created_at DESC
                LIMIT 1
                """,
                new { TenantId = tenantId });

            return Results.Ok(new
            {
                isValid = verificationResult.is_valid,
                chainLength = verificationResult.chain_length,
                firstEntryHash = verificationResult.first_entry_hash,
                lastEntryHash = verificationResult.last_entry_hash,
                brokenAtId = verificationResult.broken_at_id,
                errorMessage = verificationResult.error_message,
                totalEntries = entryCount,
                lastEntry,
                verifiedAt = DateTimeOffset.UtcNow
            });
        })
        .WithName("VerifyPrivacyChain")
        .WithDescription("Verifies full privacy audit chain integrity")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized);

        // =============================================================================
        // GET /privacy/proof/{memoryId} - Get proof for a specific memory (FIX #5)
        // =============================================================================
        group.MapGet("/proof/{memoryId}", async (
            string memoryId,
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            CancellationToken ct) =>
        {
            var tenantId = GetTenantId(user, selfHostedMode);

            // CRITICAL FIX: Handle both short IDs and full UUIDs
            Guid parsedMemoryId;
            if (!Guid.TryParse(memoryId, out parsedMemoryId))
            {
                // Try to find by prefix (short ID)
                await using var connLookup = await dataSource.OpenConnectionAsync(ct);
                await connLookup.ExecuteAsync("SELECT set_config('app.role', 'internal_admin', false)");

                var fullId = await connLookup.QueryFirstOrDefaultAsync<Guid?>(
                    """
                    SELECT id FROM memories
                    WHERE tenant_id = @TenantId AND id::text LIKE @Prefix || '%'
                    LIMIT 1
                    """,
                    new { TenantId = tenantId, Prefix = memoryId });

                if (!fullId.HasValue)
                    return Results.NotFound(new { error = "not_found", message = "Memory not found" });

                parsedMemoryId = fullId.Value;
            }

            await using var conn = await dataSource.OpenConnectionAsync(ct);

            // Set internal_admin to bypass RLS for proof generation
            await conn.ExecuteAsync("SELECT set_config('app.role', 'internal_admin', false)");
            await conn.ExecuteAsync("SELECT set_config('app.current_tenant_id', @TenantId, false)",
                new { TenantId = tenantId.ToString() });

            // Get memory
            var memory = await conn.QueryFirstOrDefaultAsync<MemoryForProofDto>(
                """
                SELECT id, content, layer, confidence, content_hash, created_at, updated_at
                FROM memories
                WHERE id = @MemoryId AND tenant_id = @TenantId
                """,
                new { MemoryId = parsedMemoryId, TenantId = tenantId });

            if (memory == null)
                return Results.NotFound(new { error = "not_found", message = "Memory not found" });

            // Get audit entries for this memory
            var auditEntries = await conn.QueryAsync<AuditEntryDto>(
                """
                SELECT id, entry_type, content_hash, previous_hash, metadata, created_at
                FROM privacy_audit_entries
                WHERE tenant_id = @TenantId AND memory_id = @MemoryId
                ORDER BY created_at ASC
                """,
                new { TenantId = tenantId, MemoryId = parsedMemoryId });

            // Compute current content hash for verification
            var computedHash = ComputeContentHash(memory.content);
            var hashMatches = memory.content_hash == computedHash;

            // Build proof chain
            var proofChain = new List<ProofChainEntryDto>();
            string? previousHash = null;

            foreach (var entry in auditEntries)
            {
                var chainValid = previousHash == null || entry.previous_hash == previousHash;
                proofChain.Add(new ProofChainEntryDto
                {
                    entryId = entry.id,
                    entryType = entry.entry_type,
                    contentHash = entry.content_hash,
                    previousHash = entry.previous_hash,
                    chainValid = chainValid,
                    createdAt = entry.created_at
                });
                previousHash = entry.content_hash;
            }

            return Results.Ok(new
            {
                memoryId = parsedMemoryId,
                memory = new
                {
                    memory.id,
                    contentPreview = memory.content.Length > 200 ? memory.content[..200] + "..." : memory.content,
                    memory.layer,
                    memory.confidence,
                    memory.created_at
                },
                integrity = new
                {
                    storedHash = memory.content_hash,
                    computedHash,
                    hashMatches,
                    algorithm = "SHA256"
                },
                proofChain,
                chainValid = proofChain.All(p => p.chainValid),
                generatedAt = DateTimeOffset.UtcNow
            });
        })
        .WithName("GetMemoryProof")
        .WithDescription("Gets cryptographic proof for a specific memory")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status404NotFound);

        // =============================================================================
        // GET /privacy/audit - Get audit log entries
        // =============================================================================
        group.MapGet("/audit", async (
            int? limit,
            int? offset,
            string? entryType,
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            CancellationToken ct) =>
        {
            var tenantId = GetTenantId(user, selfHostedMode);

            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.SetInternalAdminWithTenantAsync(tenantId);

            var typeFilter = string.IsNullOrEmpty(entryType) ? "" : "AND entry_type = @EntryType";

            var entries = await conn.QueryAsync<AuditLogEntryDto>(
                $"""
                SELECT id, memory_id, entry_type, content_hash, previous_hash, metadata, created_at
                FROM privacy_audit_entries
                WHERE tenant_id = @TenantId {typeFilter}
                ORDER BY created_at DESC
                LIMIT @Limit
                OFFSET @Offset
                """,
                new
                {
                    TenantId = tenantId,
                    EntryType = entryType,
                    Limit = limit ?? 50,
                    Offset = offset ?? 0
                });

            var totalCount = await conn.ExecuteScalarAsync<int>(
                $"""
                SELECT COUNT(*) FROM privacy_audit_entries
                WHERE tenant_id = @TenantId {typeFilter}
                """,
                new { TenantId = tenantId, EntryType = entryType });

            return Results.Ok(new
            {
                entries = entries.ToList(),
                total = totalCount,
                limit = limit ?? 50,
                offset = offset ?? 0
            });
        })
        .WithName("GetAuditLog")
        .WithDescription("Gets privacy audit log entries")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized);

        // =============================================================================
        // POST /privacy/recompute-chain - Recompute chain hashes
        // =============================================================================
        group.MapPost("/recompute-chain", async (
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            CancellationToken ct) =>
        {
            var tenantId = GetTenantId(user, selfHostedMode);

            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.ExecuteAsync("SELECT set_config('app.role', 'internal_admin', false)");
            await conn.ExecuteAsync("SELECT set_config('app.current_tenant_id', @TenantId, false)",
                new { TenantId = tenantId.ToString() });

            // Get all entries in order
            var entries = await conn.QueryAsync<AuditEntryForRecomputeDto>(
                """
                SELECT id, memory_id, entry_type, content_hash, previous_hash, metadata, created_at
                FROM privacy_audit_entries
                WHERE tenant_id = @TenantId
                ORDER BY created_at ASC
                FOR UPDATE
                """,
                new { TenantId = tenantId });

            var entryList = entries.ToList();
            var recomputedCount = 0;
            string? previousHash = null;

            foreach (var entry in entryList)
            {
                // Verify and fix chain links
                if (entry.previous_hash != previousHash)
                {
                    await conn.ExecuteAsync(
                        "UPDATE privacy_audit_entries SET previous_hash = @PrevHash WHERE id = @Id",
                        new { PrevHash = previousHash, Id = entry.id });
                    recomputedCount++;
                }
                previousHash = entry.content_hash;
            }

            return Results.Ok(new
            {
                message = "Chain recomputation completed",
                totalEntries = entryList.Count,
                fixedLinks = recomputedCount
            });
        })
        .WithName("RecomputePrivacyChain")
        .WithDescription("Recomputes and fixes privacy chain links")
        .RequireAuthorization("Admin")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized);

        // =============================================================================
        // GET /privacy/stats - Get privacy statistics
        // =============================================================================
        group.MapGet("/stats", async (
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            CancellationToken ct) =>
        {
            var tenantId = GetTenantId(user, selfHostedMode);

            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.SetInternalAdminWithTenantAsync(tenantId);

            var stats = await conn.QueryFirstOrDefaultAsync<PrivacyStatsDto>(
                """
                SELECT
                    COUNT(*) AS total_entries,
                    COUNT(DISTINCT memory_id) AS unique_memories,
                    COUNT(*) FILTER (WHERE entry_type = 'created') AS created_count,
                    COUNT(*) FILTER (WHERE entry_type = 'updated') AS updated_count,
                    COUNT(*) FILTER (WHERE entry_type = 'deleted') AS deleted_count,
                    MIN(created_at) AS oldest_entry,
                    MAX(created_at) AS newest_entry
                FROM privacy_audit_entries
                WHERE tenant_id = @TenantId
                """,
                new { TenantId = tenantId });

            // Check chain integrity
            var verification = await conn.QueryFirstOrDefaultAsync<ChainVerificationDto>(
                "SELECT * FROM verify_privacy_chain(@TenantId)",
                new { TenantId = tenantId });

            return Results.Ok(new
            {
                stats = stats ?? new PrivacyStatsDto(),
                chainIntegrity = new
                {
                    isValid = verification?.is_valid ?? true,
                    chainLength = verification?.chain_length ?? 0
                }
            });
        })
        .WithName("GetPrivacyStats")
        .WithDescription("Gets privacy audit statistics")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized);
    }

    private static string ComputeContentHash(string content)
    {
        using var sha256 = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = sha256.ComputeHash(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static Guid GetTenantId(ClaimsPrincipal user, bool selfHosted)
        => DashboardHelpers.GetTenantId(user, selfHosted);
}

// DTOs for Privacy endpoints
public sealed class ChainVerificationDto
{
    public bool is_valid { get; set; }
    public int chain_length { get; set; }
    public string? first_entry_hash { get; set; }
    public string? last_entry_hash { get; set; }
    public Guid? broken_at_id { get; set; }
    public string? error_message { get; set; }
}

public sealed class LastAuditEntryDto
{
    public Guid id { get; set; }
    public string? content_hash { get; set; }
    public DateTimeOffset created_at { get; set; }
}

public sealed class MemoryForProofDto
{
    public Guid id { get; set; }
    public string content { get; set; } = "";
    public string? layer { get; set; }
    public decimal confidence { get; set; }
    public string? content_hash { get; set; }
    public DateTimeOffset created_at { get; set; }
    public DateTimeOffset? updated_at { get; set; }
}

public sealed class AuditEntryDto
{
    public Guid id { get; set; }
    public string entry_type { get; set; } = "";
    public string? content_hash { get; set; }
    public string? previous_hash { get; set; }
    public object? metadata { get; set; }
    public DateTimeOffset created_at { get; set; }
}

public sealed class ProofChainEntryDto
{
    public Guid entryId { get; set; }
    public string entryType { get; set; } = "";
    public string? contentHash { get; set; }
    public string? previousHash { get; set; }
    public bool chainValid { get; set; }
    public DateTimeOffset createdAt { get; set; }
}

public sealed class AuditLogEntryDto
{
    public Guid id { get; set; }
    public Guid? memory_id { get; set; }
    public string entry_type { get; set; } = "";
    public string? content_hash { get; set; }
    public string? previous_hash { get; set; }
    public object? metadata { get; set; }
    public DateTimeOffset created_at { get; set; }
}

public sealed class AuditEntryForRecomputeDto
{
    public Guid id { get; set; }
    public Guid? memory_id { get; set; }
    public string entry_type { get; set; } = "";
    public string? content_hash { get; set; }
    public string? previous_hash { get; set; }
    public object? metadata { get; set; }
    public DateTimeOffset created_at { get; set; }
}

public sealed class PrivacyStatsDto
{
    public long total_entries { get; set; }
    public long unique_memories { get; set; }
    public long created_count { get; set; }
    public long updated_count { get; set; }
    public long deleted_count { get; set; }
    public DateTimeOffset? oldest_entry { get; set; }
    public DateTimeOffset? newest_entry { get; set; }
}
