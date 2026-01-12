using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using SerialMemory.Core.Interfaces;

namespace SerialMemory.Infrastructure.Services;

/// <summary>
/// PostgreSQL-backed conflict scanner service.
/// FIX #2: Conflicts & Contradictions - Run Detection Scan must actually run.
/// Uses pgvector for semantic similarity detection.
/// </summary>
public sealed class ConflictScannerService(
    NpgsqlDataSource dataSource,
    ILogger<ConflictScannerService> logger)
    : IConflictScanner
{
    private readonly ILogger<ConflictScannerService> _logger = logger;

    public async Task<ConflictScanResult> RunScanAsync(
        Guid tenantId,
        float threshold = 0.85f,
        CancellationToken ct = default)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var scanId = Guid.CreateVersion7();

        _logger.LogInformation(
            "Starting conflict scan {ScanId} for tenant {TenantId} with threshold {Threshold}",
            scanId, tenantId, threshold);

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await conn.SetInternalAdminWithTenantAsync(tenantId);

        // Get memory count
        var memoryCount = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM memories WHERE tenant_id = @TenantId AND is_active = true",
            new { TenantId = tenantId });

        if (memoryCount == 0)
        {
            return new ConflictScanResult
            {
                ScanId = scanId,
                StartedAt = startedAt,
                CompletedAt = DateTimeOffset.UtcNow,
                MemoriesScanned = 0,
                ConflictsFound = 0,
                NewConflicts = 0,
                DuplicateConflicts = 0,
                Status = "completed"
            };
        }

        // Find potential conflicts using vector similarity
        // This query finds pairs of memories that are semantically very similar
        var potentialConflicts = await conn.QueryAsync<PotentialConflictRow>(
            """
            SELECT
                m1.id AS memory_a_id,
                m2.id AS memory_b_id,
                m1.content AS memory_a_content,
                m2.content AS memory_b_content,
                1 - (m1.embedding <=> m2.embedding) AS similarity
            FROM memories m1
            JOIN memories m2 ON m1.id < m2.id  -- Avoid duplicates and self-comparison
            WHERE m1.tenant_id = @TenantId
              AND m2.tenant_id = @TenantId
              AND m1.is_active = true
              AND m2.is_active = true
              AND m1.embedding IS NOT NULL
              AND m2.embedding IS NOT NULL
              AND 1 - (m1.embedding <=> m2.embedding) >= @Threshold
            ORDER BY similarity DESC
            LIMIT 100
            """,
            new { TenantId = tenantId, Threshold = threshold });

        var conflicts = new List<ConflictRecord>();
        var newConflicts = 0;
        var duplicateConflicts = 0;

        foreach (var row in potentialConflicts)
        {
            // Check if this conflict already exists
            var existingConflict = await conn.ExecuteScalarAsync<Guid?>(
                """
                SELECT id FROM conflict_log
                WHERE tenant_id = @TenantId
                  AND ((memory_a_id = @MemoryAId AND memory_b_id = @MemoryBId)
                       OR (memory_a_id = @MemoryBId AND memory_b_id = @MemoryAId))
                LIMIT 1
                """,
                new { TenantId = tenantId, MemoryAId = row.memory_a_id, MemoryBId = row.memory_b_id });

            if (existingConflict.HasValue)
            {
                duplicateConflicts++;
                continue;
            }

            // Determine conflict type and severity based on similarity
            var conflictType = row.similarity >= 0.95f ? "duplicate" : "contradiction";
            var severity = row.similarity >= 0.95f ? 0.9f : Math.Min(row.similarity * 1.2f, 1.0f);

            // Insert new conflict
            var conflictId = Guid.CreateVersion7();
            await conn.ExecuteAsync(
                """
                INSERT INTO conflict_log (
                    id, tenant_id, memory_a_id, memory_b_id, similarity_score,
                    conflict_type, severity, status, description, detected_at
                ) VALUES (
                    @Id, @TenantId, @MemoryAId, @MemoryBId, @Similarity,
                    @ConflictType, @Severity, 'unresolved', @Description, NOW()
                )
                """,
                new
                {
                    Id = conflictId,
                    TenantId = tenantId,
                    MemoryAId = row.memory_a_id,
                    MemoryBId = row.memory_b_id,
                    Similarity = row.similarity,
                    ConflictType = conflictType,
                    Severity = severity,
                    Description = $"High semantic similarity ({row.similarity:P0}) detected between memories"
                });

            conflicts.Add(new ConflictRecord
            {
                Id = conflictId,
                MemoryAId = row.memory_a_id,
                MemoryBId = row.memory_b_id,
                MemoryAPreview = Truncate(row.memory_a_content, 200),
                MemoryBPreview = Truncate(row.memory_b_content, 200),
                SimilarityScore = row.similarity,
                ConflictType = conflictType,
                Severity = severity,
                Status = "unresolved",
                DetectedAt = DateTimeOffset.UtcNow
            });

            newConflicts++;
        }

        // Log the scan
        await conn.ExecuteAsync(
            """
            INSERT INTO event_log (
                id, tenant_id, event_type, severity, message, payload, created_at
            ) VALUES (
                @Id, @TenantId, 'conflict_scan', 'info', @Message, @Payload, NOW()
            )
            """,
            new
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                Message = $"Conflict scan completed: {memoryCount} memories scanned, {conflicts.Count} conflicts found",
                Payload = System.Text.Json.JsonSerializer.Serialize(new
                {
                    scanId,
                    memoriesScanned = memoryCount,
                    conflictsFound = conflicts.Count,
                    newConflicts,
                    duplicateConflicts,
                    threshold
                })
            });

        _logger.LogInformation(
            "Conflict scan {ScanId} completed: {MemoriesScanned} memories, {ConflictsFound} conflicts ({NewConflicts} new)",
            scanId, memoryCount, conflicts.Count, newConflicts);

        return new ConflictScanResult
        {
            ScanId = scanId,
            StartedAt = startedAt,
            CompletedAt = DateTimeOffset.UtcNow,
            MemoriesScanned = memoryCount,
            ConflictsFound = conflicts.Count,
            NewConflicts = newConflicts,
            DuplicateConflicts = duplicateConflicts,
            Conflicts = conflicts,
            Status = "completed"
        };
    }

    public async Task<IReadOnlyList<ConflictRecord>> GetConflictsAsync(
        Guid tenantId,
        bool includeResolved = false,
        int limit = 100,
        CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await conn.SetInternalAdminWithTenantAsync(tenantId);

        var conflicts = await conn.QueryAsync<ConflictRow>(
            """
            SELECT
                c.id, c.memory_a_id, c.memory_b_id, c.similarity_score,
                c.conflict_type, c.description, c.severity, c.status,
                c.resolution, c.detected_at, c.resolved_at,
                SUBSTRING(ma.content, 1, 200) AS memory_a_preview,
                SUBSTRING(mb.content, 1, 200) AS memory_b_preview
            FROM conflict_log c
            LEFT JOIN memories ma ON c.memory_a_id = ma.id
            LEFT JOIN memories mb ON c.memory_b_id = mb.id
            WHERE c.tenant_id = @TenantId
              AND (@IncludeResolved = true OR c.status = 'unresolved')
            ORDER BY c.severity DESC, c.detected_at DESC
            LIMIT @Limit
            """,
            new { TenantId = tenantId, IncludeResolved = includeResolved, Limit = limit });

        return conflicts.Select(c => new ConflictRecord
        {
            Id = c.id,
            MemoryAId = c.memory_a_id,
            MemoryBId = c.memory_b_id,
            MemoryAPreview = c.memory_a_preview,
            MemoryBPreview = c.memory_b_preview,
            SimilarityScore = c.similarity_score,
            ConflictType = c.conflict_type,
            Description = c.description,
            Severity = c.severity,
            Status = c.status,
            Resolution = c.resolution,
            DetectedAt = c.detected_at,
            ResolvedAt = c.resolved_at
        }).ToList();
    }

    public async Task<bool> ResolveConflictAsync(
        Guid tenantId,
        Guid conflictId,
        ScanConflictResolution action,
        string? resolution = null,
        CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await conn.SetInternalAdminWithTenantAsync(tenantId);

        // Get the conflict
        var conflict = await conn.QueryFirstOrDefaultAsync<ConflictRow>(
            "SELECT * FROM conflict_log WHERE id = @ConflictId AND tenant_id = @TenantId",
            new { ConflictId = conflictId, TenantId = tenantId });

        if (conflict == null)
            return false;

        var status = action switch
        {
            ScanConflictResolution.Dismiss => "dismissed",
            ScanConflictResolution.KeepBoth => "accepted",
            _ => "resolved"
        };

        // Update conflict status
        await conn.ExecuteAsync(
            """
            UPDATE conflict_log
            SET status = @Status, resolution = @Resolution, resolved_at = NOW()
            WHERE id = @ConflictId AND tenant_id = @TenantId
            """,
            new { Status = status, Resolution = resolution ?? action.ToString(), ConflictId = conflictId, TenantId = tenantId });

        // Apply resolution action
        switch (action)
        {
            case ScanConflictResolution.KeepA:
                await conn.ExecuteAsync(
                    "UPDATE memories SET is_active = false WHERE id = @MemoryId AND tenant_id = @TenantId",
                    new { MemoryId = conflict.memory_b_id, TenantId = tenantId });
                break;

            case ScanConflictResolution.KeepB:
                await conn.ExecuteAsync(
                    "UPDATE memories SET is_active = false WHERE id = @MemoryId AND tenant_id = @TenantId",
                    new { MemoryId = conflict.memory_a_id, TenantId = tenantId });
                break;

            case ScanConflictResolution.InvalidateBoth:
                await conn.ExecuteAsync(
                    "UPDATE memories SET is_active = false WHERE id IN (@MemoryAId, @MemoryBId) AND tenant_id = @TenantId",
                    new { MemoryAId = conflict.memory_a_id, MemoryBId = conflict.memory_b_id, TenantId = tenantId });
                break;
        }

        return true;
    }

    public async Task<ConflictStats> GetStatsAsync(
        Guid tenantId,
        CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await conn.SetInternalAdminWithTenantAsync(tenantId);

        var stats = await conn.QueryFirstOrDefaultAsync<ConflictStatsRow>(
            """
            SELECT
                COUNT(*) AS total_conflicts,
                COUNT(*) FILTER (WHERE status = 'unresolved') AS unresolved_count,
                COUNT(*) FILTER (WHERE status = 'resolved' OR status = 'accepted') AS resolved_count,
                COUNT(*) FILTER (WHERE status = 'dismissed') AS dismissed_count,
                AVG(similarity_score) AS avg_similarity,
                AVG(severity) AS avg_severity,
                MAX(detected_at) AS last_scan_at,
                COUNT(*) FILTER (WHERE detected_at > NOW() - INTERVAL '7 days') AS conflicts_last_7_days,
                COUNT(*) FILTER (WHERE detected_at > NOW() - INTERVAL '30 days') AS conflicts_last_30_days
            FROM conflict_log
            WHERE tenant_id = @TenantId
            """,
            new { TenantId = tenantId });

        return new ConflictStats
        {
            TotalConflicts = stats?.total_conflicts ?? 0,
            UnresolvedCount = stats?.unresolved_count ?? 0,
            ResolvedCount = stats?.resolved_count ?? 0,
            DismissedCount = stats?.dismissed_count ?? 0,
            AvgSimilarity = stats?.avg_similarity ?? 0,
            AvgSeverity = stats?.avg_severity ?? 0,
            LastScanAt = stats?.last_scan_at,
            ConflictsLast7Days = stats?.conflicts_last_7_days ?? 0,
            ConflictsLast30Days = stats?.conflicts_last_30_days ?? 0
        };
    }

    private static string? Truncate(string? text, int maxLength)
    {
        if (text == null) return null;
        return text.Length <= maxLength ? text : text[..maxLength] + "...";
    }

    private sealed class PotentialConflictRow
    {
        public Guid memory_a_id { get; init; }
        public Guid memory_b_id { get; init; }
        public string memory_a_content { get; init; } = "";
        public string memory_b_content { get; init; } = "";
        public float similarity { get; init; }
    }

    private sealed class ConflictRow
    {
        public Guid id { get; init; }
        public Guid memory_a_id { get; init; }
        public Guid memory_b_id { get; init; }
        public float similarity_score { get; init; }
        public string conflict_type { get; init; } = "";
        public string? description { get; init; }
        public float severity { get; init; }
        public string status { get; init; } = "";
        public string? resolution { get; init; }
        public DateTimeOffset detected_at { get; init; }
        public DateTimeOffset? resolved_at { get; init; }
        public string? memory_a_preview { get; init; }
        public string? memory_b_preview { get; init; }
    }

    private sealed class ConflictStatsRow
    {
        public int total_conflicts { get; init; }
        public int unresolved_count { get; init; }
        public int resolved_count { get; init; }
        public int dismissed_count { get; init; }
        public float avg_similarity { get; init; }
        public float avg_severity { get; init; }
        public DateTimeOffset? last_scan_at { get; init; }
        public int conflicts_last_7_days { get; init; }
        public int conflicts_last_30_days { get; init; }
    }
}
