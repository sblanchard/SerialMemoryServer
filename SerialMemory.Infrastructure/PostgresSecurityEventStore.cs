using System.Text.Json;
using Dapper;
using Npgsql;
using SerialMemory.Core.Interfaces;
using SerialMemory.Core.Models;

namespace SerialMemory.Infrastructure;

/// <summary>
/// PostgreSQL implementation of security event storage
/// </summary>
public class PostgresSecurityEventStore(string connectionString) : ISecurityEventStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private NpgsqlConnection CreateConnection() => new(connectionString);

    public async Task<Guid> LogEventAsync(IntegrityEvent integrityEvent, CancellationToken ct = default)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        var sql = """
            INSERT INTO security_events (
                event_id, event_type, severity, memory_id, entity_id,
                message, details, expected_hash, actual_hash,
                contradicting_memory_ids, contradiction_score,
                loop_path, loop_depth,
                occurred_at, detected_by, session_id, scan_batch_id
            ) VALUES (
                @EventId, @EventType::security_event_type, @Severity::security_severity, @MemoryId, @EntityId,
                @Message, @Details::jsonb, @ExpectedHash, @ActualHash,
                @ContradictingMemoryIds, @ContradictionScore,
                @LoopPath, @LoopDepth,
                @OccurredAt, @DetectedBy, @SessionId, @ScanBatchId
            )
            RETURNING event_id
            """;

        var eventId = await conn.ExecuteScalarAsync<Guid>(sql, new
        {
            integrityEvent.EventId,
            EventType = integrityEvent.EventType.ToString(),
            Severity = integrityEvent.Severity.ToString(),
            integrityEvent.MemoryId,
            integrityEvent.EntityId,
            integrityEvent.Message,
            Details = integrityEvent.Details != null ? JsonSerializer.Serialize(integrityEvent.Details, JsonOptions) : "{}",
            integrityEvent.ExpectedHash,
            integrityEvent.ActualHash,
            ContradictingMemoryIds = integrityEvent.ContradictingMemoryIds?.ToArray(),
            integrityEvent.ContradictionScore,
            LoopPath = integrityEvent.LoopPath?.ToArray(),
            integrityEvent.LoopDepth,
            integrityEvent.OccurredAt,
            integrityEvent.DetectedBy,
            integrityEvent.SessionId,
            integrityEvent.ScanBatchId
        });

        return eventId;
    }

    public async Task LogEventsAsync(IEnumerable<IntegrityEvent> events, CancellationToken ct = default)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);
        await using var transaction = await conn.BeginTransactionAsync(ct);

        try
        {
            foreach (var evt in events)
            {
                var sql = """
                    INSERT INTO security_events (
                        event_id, event_type, severity, memory_id, entity_id,
                        message, details, expected_hash, actual_hash,
                        contradicting_memory_ids, contradiction_score,
                        loop_path, loop_depth,
                        occurred_at, detected_by, session_id, scan_batch_id
                    ) VALUES (
                        @EventId, @EventType::security_event_type, @Severity::security_severity, @MemoryId, @EntityId,
                        @Message, @Details::jsonb, @ExpectedHash, @ActualHash,
                        @ContradictingMemoryIds, @ContradictionScore,
                        @LoopPath, @LoopDepth,
                        @OccurredAt, @DetectedBy, @SessionId, @ScanBatchId
                    )
                    """;

                await conn.ExecuteAsync(sql, new
                {
                    evt.EventId,
                    EventType = evt.EventType.ToString(),
                    Severity = evt.Severity.ToString(),
                    evt.MemoryId,
                    evt.EntityId,
                    evt.Message,
                    Details = evt.Details != null ? JsonSerializer.Serialize(evt.Details, JsonOptions) : "{}",
                    evt.ExpectedHash,
                    evt.ActualHash,
                    ContradictingMemoryIds = evt.ContradictingMemoryIds?.ToArray(),
                    evt.ContradictionScore,
                    LoopPath = evt.LoopPath?.ToArray(),
                    evt.LoopDepth,
                    evt.OccurredAt,
                    evt.DetectedBy,
                    evt.SessionId,
                    evt.ScanBatchId
                }, transaction);
            }

            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<List<IntegrityEvent>> GetRecentEventsAsync(int limit = 100, CancellationToken ct = default)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        var sql = """
            SELECT event_id, event_type, severity, memory_id, entity_id,
                   message, details, expected_hash, actual_hash,
                   contradicting_memory_ids, contradiction_score,
                   loop_path, loop_depth,
                   occurred_at, detected_by, session_id, scan_batch_id
            FROM security_events
            ORDER BY occurred_at DESC
            LIMIT @Limit
            """;

        var rows = await conn.QueryAsync<SecurityEventRow>(sql, new { Limit = limit });
        return rows.Select(MapFromRow).ToList();
    }

    public async Task<List<IntegrityEvent>> GetEventsByTypeAsync(IntegrityEventType eventType, int limit = 100, CancellationToken ct = default)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        var sql = """
            SELECT event_id, event_type, severity, memory_id, entity_id,
                   message, details, expected_hash, actual_hash,
                   contradicting_memory_ids, contradiction_score,
                   loop_path, loop_depth,
                   occurred_at, detected_by, session_id, scan_batch_id
            FROM security_events
            WHERE event_type = @EventType::security_event_type
            ORDER BY occurred_at DESC
            LIMIT @Limit
            """;

        var rows = await conn.QueryAsync<SecurityEventRow>(sql, new { EventType = eventType.ToString(), Limit = limit });
        return rows.Select(MapFromRow).ToList();
    }

    public async Task<List<IntegrityEvent>> GetEventsForMemoryAsync(Guid memoryId, CancellationToken ct = default)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        var sql = """
            SELECT event_id, event_type, severity, memory_id, entity_id,
                   message, details, expected_hash, actual_hash,
                   contradicting_memory_ids, contradiction_score,
                   loop_path, loop_depth,
                   occurred_at, detected_by, session_id, scan_batch_id
            FROM security_events
            WHERE memory_id = @MemoryId
            ORDER BY occurred_at DESC
            """;

        var rows = await conn.QueryAsync<SecurityEventRow>(sql, new { MemoryId = memoryId });
        return rows.Select(MapFromRow).ToList();
    }

    public async Task<List<IntegrityEvent>> GetEventsInTimeRangeAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        var sql = """
            SELECT event_id, event_type, severity, memory_id, entity_id,
                   message, details, expected_hash, actual_hash,
                   contradicting_memory_ids, contradiction_score,
                   loop_path, loop_depth,
                   occurred_at, detected_by, session_id, scan_batch_id
            FROM security_events
            WHERE occurred_at BETWEEN @From AND @To
            ORDER BY occurred_at DESC
            """;

        var rows = await conn.QueryAsync<SecurityEventRow>(sql, new { From = from, To = to });
        return rows.Select(MapFromRow).ToList();
    }

    public async Task<Guid> StartScanAsync(SecurityScan scan, CancellationToken ct = default)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        var sql = """
            INSERT INTO security_scans (
                scan_id, scan_type, started_at, status, triggered_by, metadata
            ) VALUES (
                @ScanId, @ScanType, @StartedAt, @Status, @TriggeredBy, @Metadata::jsonb
            )
            RETURNING scan_id
            """;

        return await conn.ExecuteScalarAsync<Guid>(sql, new
        {
            scan.ScanId,
            scan.ScanType,
            scan.StartedAt,
            scan.Status,
            scan.TriggeredBy,
            Metadata = scan.Metadata != null ? JsonSerializer.Serialize(scan.Metadata, JsonOptions) : "{}"
        });
    }

    public async Task CompleteScanAsync(Guid scanId, int eventsGenerated, int issuesFound, int criticalIssues, CancellationToken ct = default)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        var sql = """
            UPDATE security_scans
            SET completed_at = NOW(),
                status = 'completed',
                events_generated = @EventsGenerated,
                issues_found = @IssuesFound,
                critical_issues = @CriticalIssues
            WHERE scan_id = @ScanId
            """;

        await conn.ExecuteAsync(sql, new { ScanId = scanId, EventsGenerated = eventsGenerated, IssuesFound = issuesFound, CriticalIssues = criticalIssues });
    }

    public async Task FailScanAsync(Guid scanId, string errorMessage, CancellationToken ct = default)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        var sql = """
            UPDATE security_scans
            SET completed_at = NOW(),
                status = 'failed',
                error_message = @ErrorMessage
            WHERE scan_id = @ScanId
            """;

        await conn.ExecuteAsync(sql, new { ScanId = scanId, ErrorMessage = errorMessage });
    }

    public async Task<SecurityScan?> GetScanAsync(Guid scanId, CancellationToken ct = default)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        var sql = """
            SELECT scan_id, scan_type, started_at, completed_at,
                   memories_scanned, entities_scanned, relationships_scanned,
                   events_generated, issues_found, critical_issues,
                   status, error_message, triggered_by, metadata
            FROM security_scans
            WHERE scan_id = @ScanId
            """;

        var row = await conn.QuerySingleOrDefaultAsync<SecurityScanRow>(sql, new { ScanId = scanId });
        return row == null ? null : MapScanFromRow(row);
    }

    public async Task<List<SecurityScan>> GetRecentScansAsync(int limit = 20, CancellationToken ct = default)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        var sql = """
            SELECT scan_id, scan_type, started_at, completed_at,
                   memories_scanned, entities_scanned, relationships_scanned,
                   events_generated, issues_found, critical_issues,
                   status, error_message, triggered_by, metadata
            FROM security_scans
            ORDER BY started_at DESC
            LIMIT @Limit
            """;

        var rows = await conn.QueryAsync<SecurityScanRow>(sql, new { Limit = limit });
        return rows.Select(MapScanFromRow).ToList();
    }

    public async Task<SecurityMetrics> GetMetricsAsync(CancellationToken ct = default)
    {
        return await GetMetricsForTimeRangeAsync(DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow, ct);
    }

    public async Task<SecurityMetrics> GetMetricsForTimeRangeAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        var metrics = new SecurityMetrics { GeneratedAt = DateTimeOffset.UtcNow };

        // Get event counts by type
        var eventCountsSql = """
            SELECT event_type, severity, COUNT(*) as count
            FROM security_events
            WHERE occurred_at BETWEEN @From AND @To
            GROUP BY event_type, severity
            """;

        var eventCounts = await conn.QueryAsync<(string event_type, string severity, long count)>(eventCountsSql, new { From = from, To = to });

        foreach (var (eventType, severity, count) in eventCounts)
        {
            metrics.TotalEvents += count;

            switch (severity)
            {
                case "Info": metrics.InfoEvents += (int)count; break;
                case "Warning": metrics.WarningEvents += (int)count; break;
                case "Error": metrics.ErrorEvents += (int)count; break;
                case "Critical": metrics.CriticalEvents += (int)count; break;
            }

            switch (eventType)
            {
                case "HashValidationPass": metrics.HashValidationPasses += (int)count; break;
                case "HashValidationFail":
                case "HashMismatch": metrics.HashValidationFailures += (int)count; break;
                case "ContradictionDetected": metrics.ContradictionsDetected += (int)count; break;
                case "CausalLoopDetected": metrics.LoopsDetected += (int)count; break;
                case "IntegrityViolation": metrics.IntegrityViolations += (int)count; break;
                case "AnomalyDetected":
                case "HallucinationSuspected": metrics.AnomaliesDetected += (int)count; break;
            }
        }

        // Time-based metrics
        var timeMetricsSql = """
            SELECT
                (SELECT COUNT(*) FROM security_events WHERE occurred_at > NOW() - INTERVAL '24 hours') as events_24h,
                (SELECT COUNT(*) FROM security_events WHERE occurred_at > NOW() - INTERVAL '1 hour') as events_1h,
                (SELECT MAX(occurred_at) FROM security_events) as last_event,
                (SELECT MAX(started_at) FROM security_scans WHERE status = 'completed') as last_scan,
                (SELECT COUNT(*) FROM security_scans WHERE started_at BETWEEN @From AND @To) as total_scans
            """;

        var timeMetrics = await conn.QuerySingleAsync<(int events_24h, int events_1h, DateTimeOffset? last_event, DateTimeOffset? last_scan, int total_scans)>(
            timeMetricsSql, new { From = from, To = to });

        metrics.EventsLast24Hours = timeMetrics.events_24h;
        metrics.EventsLastHour = timeMetrics.events_1h;
        metrics.LastEventAt = timeMetrics.last_event;
        metrics.LastScanAt = timeMetrics.last_scan;
        metrics.TotalScans = timeMetrics.total_scans;

        // Memory statistics
        var memorySql = """
            SELECT
                COUNT(DISTINCT memory_id) FILTER (WHERE memory_id IS NOT NULL) as memories_scanned,
                COUNT(DISTINCT memory_id) FILTER (WHERE memory_id IS NOT NULL AND severity IN ('Error', 'Critical')) as memories_with_issues
            FROM security_events
            WHERE occurred_at BETWEEN @From AND @To
            """;

        var memoryStats = await conn.QuerySingleAsync<(long memories_scanned, long memories_with_issues)>(memorySql, new { From = from, To = to });
        metrics.MemoriesScanned = memoryStats.memories_scanned;
        metrics.MemoriesWithIssues = memoryStats.memories_with_issues;

        // Calculate pass rates
        var totalHashChecks = metrics.HashValidationPasses + metrics.HashValidationFailures;
        metrics.HashValidationPassRate = totalHashChecks > 0 ? (double)metrics.HashValidationPasses / totalHashChecks : 1.0;

        var totalIssues = metrics.HashValidationFailures + metrics.ContradictionsDetected + metrics.LoopsDetected + metrics.IntegrityViolations;
        metrics.OverallPassRate = metrics.TotalEvents > 0 ? 1.0 - ((double)totalIssues / metrics.TotalEvents) : 1.0;

        return metrics;
    }

    public async Task<List<IntegrityEvent>> GetActiveIssuesAsync(int limit = 50, CancellationToken ct = default)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        var sql = """
            SELECT event_id, event_type, severity, memory_id, entity_id,
                   message, details, expected_hash, actual_hash,
                   contradicting_memory_ids, contradiction_score,
                   loop_path, loop_depth,
                   occurred_at, detected_by, session_id, scan_batch_id
            FROM security_events
            WHERE severity IN ('Error', 'Critical')
                AND occurred_at > NOW() - INTERVAL '7 days'
            ORDER BY
                CASE severity WHEN 'Critical' THEN 1 WHEN 'Error' THEN 2 ELSE 3 END,
                occurred_at DESC
            LIMIT @Limit
            """;

        var rows = await conn.QueryAsync<SecurityEventRow>(sql, new { Limit = limit });
        return rows.Select(MapFromRow).ToList();
    }

    public async Task<int> GetUnresolvedIssueCountAsync(CancellationToken ct = default)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        var sql = """
            SELECT COUNT(*)
            FROM security_events
            WHERE severity IN ('Error', 'Critical')
                AND occurred_at > NOW() - INTERVAL '7 days'
            """;

        return await conn.ExecuteScalarAsync<int>(sql);
    }

    // Helper row types for Dapper
    private class SecurityEventRow
    {
        public Guid event_id { get; set; }
        public string event_type { get; set; } = "";
        public string severity { get; set; } = "";
        public Guid? memory_id { get; set; }
        public Guid? entity_id { get; set; }
        public string message { get; set; } = "";
        public string? details { get; set; }
        public string? expected_hash { get; set; }
        public string? actual_hash { get; set; }
        public Guid[]? contradicting_memory_ids { get; set; }
        public float? contradiction_score { get; set; }
        public Guid[]? loop_path { get; set; }
        public int? loop_depth { get; set; }
        public DateTimeOffset occurred_at { get; set; }
        public string detected_by { get; set; } = "";
        public Guid? session_id { get; set; }
        public Guid? scan_batch_id { get; set; }
    }

    private class SecurityScanRow
    {
        public Guid scan_id { get; set; }
        public string scan_type { get; set; } = "";
        public DateTimeOffset started_at { get; set; }
        public DateTimeOffset? completed_at { get; set; }
        public int memories_scanned { get; set; }
        public int entities_scanned { get; set; }
        public int relationships_scanned { get; set; }
        public int events_generated { get; set; }
        public int issues_found { get; set; }
        public int critical_issues { get; set; }
        public string status { get; set; } = "";
        public string? error_message { get; set; }
        public string triggered_by { get; set; } = "";
        public string? metadata { get; set; }
    }

    private static IntegrityEvent MapFromRow(SecurityEventRow row) => new()
    {
        EventId = row.event_id,
        EventType = Enum.Parse<IntegrityEventType>(row.event_type),
        Severity = Enum.Parse<IntegritySeverity>(row.severity),
        MemoryId = row.memory_id,
        EntityId = row.entity_id,
        Message = row.message,
        Details = string.IsNullOrEmpty(row.details) ? null : JsonSerializer.Deserialize<Dictionary<string, object>>(row.details, JsonOptions),
        ExpectedHash = row.expected_hash,
        ActualHash = row.actual_hash,
        ContradictingMemoryIds = row.contradicting_memory_ids?.ToList(),
        ContradictionScore = row.contradiction_score,
        LoopPath = row.loop_path?.ToList(),
        LoopDepth = row.loop_depth,
        OccurredAt = row.occurred_at,
        DetectedBy = row.detected_by,
        SessionId = row.session_id,
        ScanBatchId = row.scan_batch_id
    };

    private static SecurityScan MapScanFromRow(SecurityScanRow row) => new()
    {
        ScanId = row.scan_id,
        ScanType = row.scan_type,
        StartedAt = row.started_at,
        CompletedAt = row.completed_at,
        MemoriesScanned = row.memories_scanned,
        EntitiesScanned = row.entities_scanned,
        RelationshipsScanned = row.relationships_scanned,
        EventsGenerated = row.events_generated,
        IssuesFound = row.issues_found,
        CriticalIssues = row.critical_issues,
        Status = row.status,
        ErrorMessage = row.error_message,
        TriggeredBy = row.triggered_by,
        Metadata = string.IsNullOrEmpty(row.metadata) ? null : JsonSerializer.Deserialize<Dictionary<string, object>>(row.metadata, JsonOptions)
    };
}
