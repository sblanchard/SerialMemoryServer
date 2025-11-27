using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using SerialMemory.Core.Interfaces;

namespace SerialMemory.Infrastructure;

/// <summary>
/// Privacy-safe audit service. NEVER stores user content.
/// Only stores: hashes, timestamps, counts, tenant IDs.
/// </summary>
public sealed class PostgresPrivacyAuditService : IPrivacyAuditService
{
    private readonly string _connectionString;
    private readonly ILogger<PostgresPrivacyAuditService> _logger;

    public PostgresPrivacyAuditService(string connectionString, ILogger<PostgresPrivacyAuditService> logger)
    {
        _connectionString = connectionString;
        _logger = logger;
    }

    public async Task<PrivacyAuditEntry> LogMemoryOperationAsync(
        string tenantId,
        PrivacyAuditOperation operation,
        Guid memoryId,
        string contentHash,
        int? contentLength = null,
        CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var row = await conn.QueryFirstAsync<AuditRow>("""
            SELECT * FROM append_privacy_audit(
                @TenantId,
                @Operation::privacy_audit_operation,
                @ContentHash,
                NULL,
                @TargetMemoryId,
                1,
                @ContentLength,
                NULL,
                NULL
            )
            """,
            new
            {
                TenantId = tenantId,
                Operation = operation.ToString(),
                ContentHash = contentHash,
                TargetMemoryId = memoryId,
                ContentLength = contentLength
            });

        _logger.LogDebug("Privacy audit: {Operation} on memory {MemoryId} for tenant {TenantId}",
            operation, memoryId, tenantId);

        return MapToEntry(row);
    }

    public async Task<PrivacyAuditEntry> LogAccessAsync(
        string tenantId,
        PrivacyAuditOperation operation,
        int itemCount,
        string? queryHash = null,
        CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var row = await conn.QueryFirstAsync<AuditRow>("""
            SELECT * FROM append_privacy_audit(
                @TenantId,
                @Operation::privacy_audit_operation,
                NULL,
                @QueryHash,
                NULL,
                @ItemCount,
                NULL,
                NULL,
                NULL
            )
            """,
            new
            {
                TenantId = tenantId,
                Operation = operation.ToString(),
                QueryHash = queryHash,
                ItemCount = itemCount
            });

        _logger.LogDebug("Privacy audit: {Operation} accessed {Count} items for tenant {TenantId}",
            operation, itemCount, tenantId);

        return MapToEntry(row);
    }

    public async Task<PrivacyAuditEntry> LogBatchOperationAsync(
        string tenantId,
        PrivacyAuditOperation operation,
        int itemCount,
        int successCount,
        int failCount,
        CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var row = await conn.QueryFirstAsync<AuditRow>("""
            SELECT * FROM append_privacy_audit(
                @TenantId,
                @Operation::privacy_audit_operation,
                NULL,
                NULL,
                NULL,
                @ItemCount,
                NULL,
                @SuccessCount,
                @FailCount
            )
            """,
            new
            {
                TenantId = tenantId,
                Operation = operation.ToString(),
                ItemCount = itemCount,
                SuccessCount = successCount,
                FailCount = failCount
            });

        _logger.LogDebug("Privacy audit: {Operation} batch {Total} items ({Success} success, {Fail} fail) for tenant {TenantId}",
            operation, itemCount, successCount, failCount, tenantId);

        return MapToEntry(row);
    }

    public async Task<List<PrivacyAuditEntry>> GetAuditTrailAsync(
        string tenantId,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        int limit = 100,
        CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var rows = await conn.QueryAsync<AuditRow>("""
            SELECT *
            FROM privacy_audit_entries
            WHERE tenant_id = @TenantId
                AND (@From IS NULL OR timestamp >= @From)
                AND (@To IS NULL OR timestamp <= @To)
            ORDER BY sequence_number DESC
            LIMIT @Limit
            """,
            new
            {
                TenantId = tenantId,
                From = from,
                To = to,
                Limit = limit
            });

        return rows.Select(MapToEntry).ToList();
    }

    public async Task<PrivacyAuditStats> GetStatsAsync(
        string tenantId,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var fromDate = from ?? DateTimeOffset.UtcNow.AddDays(-30);
        var toDate = to ?? DateTimeOffset.UtcNow;

        // Get operation counts
        var opCounts = await conn.QueryAsync<(string operation_type, long operation_count, long total_items_accessed)>("""
            SELECT
                operation::text AS operation_type,
                COUNT(*)::BIGINT AS operation_count,
                COALESCE(SUM(item_count), 0)::BIGINT AS total_items_accessed
            FROM privacy_audit_entries
            WHERE tenant_id = @TenantId
                AND timestamp >= @From
                AND timestamp <= @To
            GROUP BY operation
            """,
            new { TenantId = tenantId, From = fromDate, To = toDate });

        var operationCounts = opCounts.ToDictionary(x => x.operation_type, x => (int)x.operation_count);
        var totalItems = opCounts.Sum(x => x.total_items_accessed);

        // Get hourly counts
        var hourlyCounts = await conn.QueryAsync<(string hour, long count)>("""
            SELECT
                DATE_TRUNC('hour', timestamp)::text AS hour,
                COUNT(*)::BIGINT AS count
            FROM privacy_audit_entries
            WHERE tenant_id = @TenantId
                AND timestamp >= @From
                AND timestamp <= @To
            GROUP BY DATE_TRUNC('hour', timestamp)
            ORDER BY hour
            """,
            new { TenantId = tenantId, From = fromDate, To = toDate });

        // Get daily counts
        var dailyCounts = await conn.QueryAsync<(string day, long count)>("""
            SELECT
                DATE_TRUNC('day', timestamp)::text AS day,
                COUNT(*)::BIGINT AS count
            FROM privacy_audit_entries
            WHERE tenant_id = @TenantId
                AND timestamp >= @From
                AND timestamp <= @To
            GROUP BY DATE_TRUNC('day', timestamp)
            ORDER BY day
            """,
            new { TenantId = tenantId, From = fromDate, To = toDate });

        return new PrivacyAuditStats
        {
            TenantId = tenantId,
            From = fromDate,
            To = toDate,
            TotalOperations = operationCounts.Values.Sum(),
            OperationCounts = operationCounts,
            TotalMemoriesAccessed = (int)totalItems,
            TotalSearches = operationCounts.GetValueOrDefault("MemorySearch", 0),
            TotalExports = operationCounts.GetValueOrDefault("MemoryExport", 0) +
                          operationCounts.GetValueOrDefault("BatchExport", 0),
            HourlyCounts = hourlyCounts.ToDictionary(x => x.hour, x => (int)x.count),
            DailyCounts = dailyCounts.ToDictionary(x => x.day, x => (int)x.count)
        };
    }

    public async Task<PrivacyAuditProof> VerifyIntegrityAsync(
        string tenantId,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var fromDate = from ?? DateTimeOffset.MinValue;
        var toDate = to ?? DateTimeOffset.UtcNow;

        var result = await conn.QueryFirstAsync<VerifyRow>("""
            SELECT * FROM verify_privacy_audit_chain(@TenantId, @From, @To)
            """,
            new { TenantId = tenantId, From = from, To = to });

        var issues = new List<PrivacyAuditIssue>();
        if (!result.is_valid && result.first_issue_sequence.HasValue)
        {
            issues.Add(new PrivacyAuditIssue
            {
                SequenceNumber = result.first_issue_sequence.Value,
                IssueType = result.first_issue_type ?? "unknown",
                ExpectedHash = result.expected_hash ?? "",
                ActualHash = result.actual_hash ?? ""
            });
        }

        // Get first and last hashes
        var hashes = await conn.QueryFirstOrDefaultAsync<(string? first_hash, string? last_hash)>("""
            SELECT
                (SELECT entry_hash FROM privacy_audit_entries
                 WHERE tenant_id = @TenantId
                   AND (@From IS NULL OR timestamp >= @From)
                   AND (@To IS NULL OR timestamp <= @To)
                 ORDER BY sequence_number LIMIT 1) AS first_hash,
                (SELECT entry_hash FROM privacy_audit_entries
                 WHERE tenant_id = @TenantId
                   AND (@From IS NULL OR timestamp >= @From)
                   AND (@To IS NULL OR timestamp <= @To)
                 ORDER BY sequence_number DESC LIMIT 1) AS last_hash
            """,
            new { TenantId = tenantId, From = from, To = to });

        // Compute Merkle root
        var merkleRoot = await conn.ExecuteScalarAsync<string>("""
            SELECT compute_privacy_audit_merkle_root(@TenantId, @From, @To)
            """,
            new { TenantId = tenantId, From = fromDate, To = toDate });

        // Generate proof signature
        var proofSignature = GenerateProofSignature(
            tenantId, fromDate, toDate,
            result.total_entries, result.verified_entries,
            hashes.first_hash ?? "", hashes.last_hash ?? "",
            merkleRoot ?? "");

        _logger.LogInformation("Privacy audit verification for tenant {TenantId}: {Valid}, {Total} entries",
            tenantId, result.is_valid, result.total_entries);

        return new PrivacyAuditProof
        {
            TenantId = tenantId,
            From = fromDate,
            To = toDate,
            IsValid = result.is_valid,
            TotalEntries = result.total_entries,
            VerifiedEntries = result.verified_entries,
            FirstEntryHash = hashes.first_hash ?? "",
            LastEntryHash = hashes.last_hash ?? "",
            MerkleRoot = merkleRoot ?? "",
            Issues = issues,
            ProofSignature = proofSignature
        };
    }

    public async Task<PrivacyAuditProof> GenerateProofAsync(
        string tenantId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct = default)
    {
        var proof = await VerifyIntegrityAsync(tenantId, from, to, ct);

        // Store proof for external verification
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await conn.ExecuteAsync("""
            INSERT INTO privacy_audit_proofs (
                id, tenant_id, from_timestamp, to_timestamp,
                is_valid, total_entries, verified_entries,
                first_entry_hash, last_entry_hash, merkle_root,
                proof_signature
            ) VALUES (
                @Id, @TenantId, @From, @To,
                @IsValid, @TotalEntries, @VerifiedEntries,
                @FirstEntryHash, @LastEntryHash, @MerkleRoot,
                @ProofSignature
            )
            """,
            new
            {
                Id = proof.ProofId,
                TenantId = tenantId,
                From = from,
                To = to,
                proof.IsValid,
                proof.TotalEntries,
                proof.VerifiedEntries,
                proof.FirstEntryHash,
                proof.LastEntryHash,
                proof.MerkleRoot,
                proof.ProofSignature
            });

        _logger.LogInformation("Generated privacy audit proof {ProofId} for tenant {TenantId}: {From} to {To}",
            proof.ProofId, tenantId, from, to);

        return proof;
    }

    private static string GenerateProofSignature(
        string tenantId,
        DateTimeOffset from,
        DateTimeOffset to,
        int totalEntries,
        int verifiedEntries,
        string firstHash,
        string lastHash,
        string merkleRoot)
    {
        var content = string.Join("|",
            tenantId,
            from.ToString("O"),
            to.ToString("O"),
            totalEntries,
            verifiedEntries,
            firstHash,
            lastHash,
            merkleRoot);

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexStringLower(bytes);
    }

    /// <summary>Hash content for audit logging. NEVER store raw content.</summary>
    public static string HashContent(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexStringLower(bytes);
    }

    /// <summary>Hash query for audit logging. NEVER store raw query.</summary>
    public static string HashQuery(string query)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(query));
        return Convert.ToHexStringLower(bytes);
    }

    private static PrivacyAuditEntry MapToEntry(AuditRow row)
    {
        return new PrivacyAuditEntry
        {
            Id = row.id,
            SequenceNumber = row.sequence_number,
            TenantId = row.tenant_id,
            Operation = Enum.Parse<PrivacyAuditOperation>(row.operation),
            Timestamp = row.timestamp,
            ContentHash = row.content_hash,
            QueryHash = row.query_hash,
            TargetMemoryId = row.target_memory_id,
            ItemCount = row.item_count,
            ContentLength = row.content_length,
            SuccessCount = row.success_count,
            FailCount = row.fail_count,
            PreviousHash = row.previous_hash,
            EntryHash = row.entry_hash
        };
    }

    private sealed class AuditRow
    {
        public Guid id { get; init; }
        public long sequence_number { get; init; }
        public string tenant_id { get; init; } = "";
        public string operation { get; init; } = "";
        public DateTimeOffset timestamp { get; init; }
        public string? content_hash { get; init; }
        public string? query_hash { get; init; }
        public Guid? target_memory_id { get; init; }
        public int? item_count { get; init; }
        public int? content_length { get; init; }
        public int? success_count { get; init; }
        public int? fail_count { get; init; }
        public string previous_hash { get; init; } = "";
        public string entry_hash { get; init; } = "";
    }

    private sealed class VerifyRow
    {
        public bool is_valid { get; init; }
        public int total_entries { get; init; }
        public int verified_entries { get; init; }
        public long? first_issue_sequence { get; init; }
        public string? first_issue_type { get; init; }
        public string? expected_hash { get; init; }
        public string? actual_hash { get; init; }
    }
}
