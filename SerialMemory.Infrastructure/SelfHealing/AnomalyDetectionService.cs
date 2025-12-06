using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using SerialMemory.Core.Interfaces;

namespace SerialMemory.Infrastructure.SelfHealing;

/// <summary>
/// ML-style anomaly detection using statistical thresholds and weighted scoring.
/// No external ML dependency - uses z-scores, moving averages, and pattern matching.
/// </summary>
public sealed class AnomalyDetectionService(
    IConfiguration configuration,
    ILogger<AnomalyDetectionService> logger)
    : IAnomalyDetectionService
{
    private readonly string _connectionString = configuration.GetConnectionString("Postgres")
                                                ?? BuildConnectionString();

    // Statistical thresholds
    private const double ContradictionZScoreThreshold = 2.0;
    private const double HallucinationRateThreshold = 0.15;
    private const double ConfidenceDecayThreshold = 0.3;
    private const int OrphanedNodeMinAge = 7; // days
    private const int ConflictClusterMinSize = 3;

    private static string BuildConnectionString()
        => Configuration.ConnectionStringFactory.BuildConnectionString();

    public async Task<IReadOnlyList<AnomalyFinding>> DetectAnomaliesAsync(
        Guid? tenantId = null,
        int maxResults = 100,
        CancellationToken cancellationToken = default)
    {
        var findings = new List<AnomalyFinding>();

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        // Run all detection strategies in parallel
        var tasks = new List<Task<IReadOnlyList<AnomalyFinding>>>
        {
            DetectOrphanedNodesAsync(conn, tenantId, cancellationToken),
            DetectConflictClustersAsync(conn, tenantId, cancellationToken),
            DetectHighHallucinationRegionsAsync(conn, tenantId, cancellationToken),
            DetectDriftSpikesAsync(conn, tenantId, cancellationToken),
            DetectRepeatedContradictionsAsync(conn, tenantId, cancellationToken),
            DetectIntegrityFailuresAsync(conn, tenantId, cancellationToken),
            DetectConfidenceDecayClustersAsync(conn, tenantId, cancellationToken),
            DetectSecurityAnomaliesAsync(conn, tenantId, cancellationToken)
        };

        var results = await Task.WhenAll(tasks);

        foreach (var result in results)
        {
            findings.AddRange(result);
        }

        // Sort by severity and limit
        var sortedFindings = findings
            .OrderByDescending(f => f.SeverityScore)
            .Take(maxResults)
            .ToList();

        logger.LogInformation(
            "Detected {Count} anomalies across {Categories} categories",
            sortedFindings.Count,
            sortedFindings.Select(f => f.Category).Distinct().Count());

        return sortedFindings;
    }

    private async Task<IReadOnlyList<AnomalyFinding>> DetectOrphanedNodesAsync(
        NpgsqlConnection conn,
        Guid? tenantId,
        CancellationToken cancellationToken)
    {
        // Find entities with no memory links or relationships
        const string sql = """
            SELECT e.id, e.name, e.entity_type,
                   (SELECT COUNT(*) FROM memory_entities me WHERE me.entity_id = e.id) as memory_count,
                   (SELECT COUNT(*) FROM entity_relationships er
                    WHERE er.source_entity_id = e.id OR er.target_entity_id = e.id) as relationship_count,
                   e.created_at
            FROM entities e
            WHERE (SELECT COUNT(*) FROM memory_entities me WHERE me.entity_id = e.id) = 0
              AND e.created_at < NOW() - @MinAge::interval
            ORDER BY e.created_at
            LIMIT 50
            """;

        var rows = await conn.QueryAsync<(Guid id, string name, string entity_type, int memory_count, int relationship_count, DateTime created_at)>(
            sql, new { MinAge = $"{OrphanedNodeMinAge} days" });

        return rows.Select(r => new AnomalyFinding(
            Guid.CreateVersion7(),
            AnomalyTypes.OrphanedGraphNode,
            "GraphIntegrity",
            CalculateOrphanedNodeSeverity(r.relationship_count, r.created_at),
            null,
            r.id,
            tenantId,
            DateTimeOffset.UtcNow,
            $"Entity '{r.name}' ({r.entity_type}) has no linked memories",
            new Dictionary<string, object>
            {
                ["entityName"] = r.name,
                ["entityType"] = r.entity_type,
                ["relationshipCount"] = r.relationship_count,
                ["ageInDays"] = (DateTime.UtcNow - r.created_at).TotalDays
            }
        )).ToList();
    }

    private static double CalculateOrphanedNodeSeverity(int relationshipCount, DateTime createdAt)
    {
        var ageDays = (DateTime.UtcNow - createdAt).TotalDays;
        var baseSeverity = 0.3;

        // Higher severity if completely isolated
        if (relationshipCount == 0)
            baseSeverity += 0.3;

        switch (ageDays)
        {
            // Lower severity for recent nodes
            case < 14:
                baseSeverity *= 0.5;
                break;
            case > 30:
                baseSeverity += 0.2;
                break;
        }

        return Math.Min(1.0, baseSeverity);
    }

    private async Task<IReadOnlyList<AnomalyFinding>> DetectConflictClustersAsync(
        NpgsqlConnection conn,
        Guid? tenantId,
        CancellationToken cancellationToken)
    {
        // Find memories involved in multiple conflicts
        const string sql = """
            WITH conflict_counts AS (
                SELECT memory_id_a as memory_id, COUNT(*) as conflict_count
                FROM memory_contradictions
                WHERE resolved_at IS NULL
                GROUP BY memory_id_a
                UNION ALL
                SELECT memory_id_b as memory_id, COUNT(*) as conflict_count
                FROM memory_contradictions
                WHERE resolved_at IS NULL
                GROUP BY memory_id_b
            ),
            aggregated AS (
                SELECT memory_id, SUM(conflict_count) as total_conflicts
                FROM conflict_counts
                GROUP BY memory_id
                HAVING SUM(conflict_count) >= @MinClusterSize
            )
            SELECT a.memory_id, a.total_conflicts, m.content
            FROM aggregated a
            JOIN memories m ON a.memory_id = m.id
            ORDER BY a.total_conflicts DESC
            LIMIT 30
            """;

        var rows = await conn.QueryAsync<(Guid memory_id, int total_conflicts, string content)>(
            sql, new { MinClusterSize = ConflictClusterMinSize });

        return rows.Select(r => new AnomalyFinding(
            Guid.CreateVersion7(),
            AnomalyTypes.ConflictCluster,
            "MemoryIntegrity",
            CalculateConflictClusterSeverity(r.total_conflicts),
            r.memory_id,
            null,
            tenantId,
            DateTimeOffset.UtcNow,
            $"Memory involved in {r.total_conflicts} unresolved conflicts",
            new Dictionary<string, object>
            {
                ["conflictCount"] = r.total_conflicts,
                ["contentSnippet"] = r.content.Length > 100 ? r.content[..100] + "..." : r.content
            }
        )).ToList();
    }

    private static double CalculateConflictClusterSeverity(int conflictCount)
    {
        return Math.Min(1.0, 0.4 + (conflictCount - ConflictClusterMinSize) * 0.15);
    }

    private async Task<IReadOnlyList<AnomalyFinding>> DetectHighHallucinationRegionsAsync(
        NpgsqlConnection conn,
        Guid? tenantId,
        CancellationToken cancellationToken)
    {
        // Find time periods with high hallucination rates
        const string sql = """
            WITH hourly_stats AS (
                SELECT
                    date_trunc('hour', occurred_at) as hour,
                    COUNT(*) as hallucination_count,
                    AVG(severity) as avg_severity
                FROM mind_hallucination_events
                WHERE occurred_at > NOW() - INTERVAL '7 days'
                  AND resolved_at IS NULL
                GROUP BY date_trunc('hour', occurred_at)
            ),
            overall_stats AS (
                SELECT AVG(hallucination_count) as mean, STDDEV(hallucination_count) as stddev
                FROM hourly_stats
            )
            SELECT h.hour, h.hallucination_count, h.avg_severity,
                   (h.hallucination_count - o.mean) / NULLIF(o.stddev, 0) as z_score
            FROM hourly_stats h, overall_stats o
            WHERE (h.hallucination_count - o.mean) / NULLIF(o.stddev, 0) > @ZScoreThreshold
            ORDER BY h.hour DESC
            LIMIT 20
            """;

        try
        {
            var rows = await conn.QueryAsync<(DateTime hour, int hallucination_count, float avg_severity, float? z_score)>(
                sql, new { ZScoreThreshold = ContradictionZScoreThreshold });

            return rows.Select(r => new AnomalyFinding(
                Guid.CreateVersion7(),
                AnomalyTypes.HighHallucinationRegion,
                "ContentQuality",
                CalculateHallucinationSeverity(r.hallucination_count, r.z_score ?? 0),
                null,
                null,
                tenantId,
                DateTimeOffset.UtcNow,
                $"High hallucination rate detected at {r.hour:yyyy-MM-dd HH:00}: {r.hallucination_count} events",
                new Dictionary<string, object>
                {
                    ["hour"] = r.hour,
                    ["count"] = r.hallucination_count,
                    ["avgSeverity"] = r.avg_severity,
                    ["zScore"] = r.z_score ?? 0
                }
            )).ToList();
        }
        catch
        {
            // Table might not exist or be empty
            return [];
        }
    }

    private static double CalculateHallucinationSeverity(int count, float zScore)
    {
        return Math.Min(1.0, 0.5 + (zScore - ContradictionZScoreThreshold) * 0.1 + count * 0.02);
    }

    private async Task<IReadOnlyList<AnomalyFinding>> DetectDriftSpikesAsync(
        NpgsqlConnection conn,
        Guid? tenantId,
        CancellationToken cancellationToken)
    {
        // Find sudden confidence drops
        const string sql = """
            WITH daily_confidence AS (
                SELECT
                    date_trunc('day', created_at) as day,
                    AVG(confidence_score) as avg_confidence
                FROM memory_projections
                WHERE created_at > NOW() - INTERVAL '30 days'
                GROUP BY date_trunc('day', created_at)
            ),
            confidence_changes AS (
                SELECT
                    day,
                    avg_confidence,
                    LAG(avg_confidence) OVER (ORDER BY day) as prev_confidence,
                    avg_confidence - LAG(avg_confidence) OVER (ORDER BY day) as confidence_change
                FROM daily_confidence
            )
            SELECT day, avg_confidence, prev_confidence, confidence_change
            FROM confidence_changes
            WHERE ABS(confidence_change) > @Threshold
            ORDER BY day DESC
            LIMIT 20
            """;

        try
        {
            var rows = await conn.QueryAsync<(DateTime day, float avg_confidence, float? prev_confidence, float? confidence_change)>(
                sql, new { Threshold = ConfidenceDecayThreshold });

            return rows.Where(r => r.confidence_change.HasValue).Select(r => new AnomalyFinding(
                Guid.CreateVersion7(),
                AnomalyTypes.DriftSpike,
                "ConfidenceHealth",
                CalculateDriftSeverity(r.confidence_change!.Value),
                null,
                null,
                tenantId,
                DateTimeOffset.UtcNow,
                $"Confidence {(r.confidence_change > 0 ? "spike" : "drop")} detected on {r.day:yyyy-MM-dd}: {r.confidence_change:+0.00;-0.00}",
                new Dictionary<string, object>
                {
                    ["day"] = r.day,
                    ["avgConfidence"] = r.avg_confidence,
                    ["previousConfidence"] = r.prev_confidence ?? 0,
                    ["change"] = r.confidence_change ?? 0
                }
            )).ToList();
        }
        catch
        {
            return [];
        }
    }

    private static double CalculateDriftSeverity(float change)
    {
        return Math.Min(1.0, Math.Abs(change) / 0.5);
    }

    private async Task<IReadOnlyList<AnomalyFinding>> DetectRepeatedContradictionsAsync(
        NpgsqlConnection conn,
        Guid? tenantId,
        CancellationToken cancellationToken)
    {
        // Find entity pairs with repeated contradictions
        const string sql = """
            WITH entity_contradictions AS (
                SELECT
                    LEAST(mc.memory_id_a, mc.memory_id_b) as mem_a,
                    GREATEST(mc.memory_id_a, mc.memory_id_b) as mem_b,
                    COUNT(*) as contradiction_count,
                    MAX(mc.detected_at) as last_detected
                FROM memory_contradictions mc
                WHERE mc.resolved_at IS NULL
                GROUP BY LEAST(mc.memory_id_a, mc.memory_id_b), GREATEST(mc.memory_id_a, mc.memory_id_b)
                HAVING COUNT(*) >= 2
            )
            SELECT mem_a, mem_b, contradiction_count, last_detected
            FROM entity_contradictions
            ORDER BY contradiction_count DESC, last_detected DESC
            LIMIT 30
            """;

        try
        {
            var rows = await conn.QueryAsync<(Guid mem_a, Guid mem_b, int contradiction_count, DateTime last_detected)>(sql);

            return rows.Select(r => new AnomalyFinding(
                Guid.CreateVersion7(),
                AnomalyTypes.RepeatedContradiction,
                "MemoryIntegrity",
                CalculateRepeatedContradictionSeverity(r.contradiction_count),
                r.mem_a,
                null,
                tenantId,
                DateTimeOffset.UtcNow,
                $"Repeated contradictions ({r.contradiction_count}x) between memory pair",
                new Dictionary<string, object>
                {
                    ["memoryIdA"] = r.mem_a,
                    ["memoryIdB"] = r.mem_b,
                    ["count"] = r.contradiction_count,
                    ["lastDetected"] = r.last_detected
                }
            )).ToList();
        }
        catch
        {
            return [];
        }
    }

    private static double CalculateRepeatedContradictionSeverity(int count)
    {
        return Math.Min(1.0, 0.5 + count * 0.15);
    }

    private async Task<IReadOnlyList<AnomalyFinding>> DetectIntegrityFailuresAsync(
        NpgsqlConnection conn,
        Guid? tenantId,
        CancellationToken cancellationToken)
    {
        // Check for integrity issues from security events
        const string sql = """
            SELECT id, event_type, severity, message, metadata, created_at
            FROM security_events
            WHERE category = 'memory_integrity'
              AND created_at > NOW() - INTERVAL '7 days'
              AND severity IN ('warning', 'error', 'critical')
            ORDER BY created_at DESC
            LIMIT 30
            """;

        try
        {
            var rows = await conn.QueryAsync<(Guid id, string event_type, string severity, string message, string metadata, DateTime created_at)>(sql);

            return rows.Select(r => new AnomalyFinding(
                Guid.CreateVersion7(),
                AnomalyTypes.IntegrityFailure,
                "Security",
                SeverityToScore(r.severity),
                null,
                null,
                tenantId,
                new DateTimeOffset(r.created_at, TimeSpan.Zero),
                r.message,
                TryParseMetadata(r.metadata)
            )).ToList();
        }
        catch
        {
            return [];
        }
    }

    private async Task<IReadOnlyList<AnomalyFinding>> DetectConfidenceDecayClustersAsync(
        NpgsqlConnection conn,
        Guid? tenantId,
        CancellationToken cancellationToken)
    {
        // Find groups of memories with very low confidence
        const string sql = """
            SELECT memory_id, confidence_score, last_accessed_at, access_count
            FROM memory_projections
            WHERE is_active = TRUE
              AND confidence_score < @Threshold
            ORDER BY confidence_score ASC
            LIMIT 30
            """;

        try
        {
            var rows = await conn.QueryAsync<(Guid memory_id, float confidence_score, DateTime? last_accessed_at, int access_count)>(
                sql, new { Threshold = ConfidenceDecayThreshold });

            return rows.Select(r => new AnomalyFinding(
                Guid.CreateVersion7(),
                AnomalyTypes.ConfidenceDecayCluster,
                "ConfidenceHealth",
                CalculateDecayClusterSeverity(r.confidence_score, r.access_count),
                r.memory_id,
                null,
                tenantId,
                DateTimeOffset.UtcNow,
                $"Memory has very low confidence ({r.confidence_score:F2}) with {r.access_count} accesses",
                new Dictionary<string, object>
                {
                    ["confidenceScore"] = r.confidence_score,
                    ["accessCount"] = r.access_count,
                    ["lastAccessed"] = r.last_accessed_at?.ToString("o") ?? "never"
                }
            )).ToList();
        }
        catch
        {
            return [];
        }
    }

    private static double CalculateDecayClusterSeverity(float confidence, int accessCount)
    {
        var baseSeverity = 1.0 - confidence;

        // Lower severity if rarely accessed (might be legitimately old)
        if (accessCount < 3)
            baseSeverity *= 0.6;

        return Math.Min(1.0, baseSeverity);
    }

    private async Task<IReadOnlyList<AnomalyFinding>> DetectSecurityAnomaliesAsync(
        NpgsqlConnection conn,
        Guid? tenantId,
        CancellationToken cancellationToken)
    {
        // Check for security anomalies
        const string sql = """
            SELECT id, anomaly_type, severity_score, entity_name, entity_type, detection_method, metadata, detected_at
            FROM security_anomalies
            WHERE detected_at > NOW() - INTERVAL '7 days'
              AND acknowledged_at IS NULL
            ORDER BY severity_score DESC, detected_at DESC
            LIMIT 30
            """;

        try
        {
            var rows = await conn.QueryAsync<(Guid id, string anomaly_type, float severity_score, string? entity_name, string? entity_type, string detection_method, string? metadata, DateTime detected_at)>(sql);

            return rows.Select(r => new AnomalyFinding(
                Guid.CreateVersion7(),
                AnomalyTypes.SecurityAnomaly,
                "Security",
                r.severity_score,
                null,
                null,
                tenantId,
                new DateTimeOffset(r.detected_at, TimeSpan.Zero),
                $"Security anomaly: {r.anomaly_type} on {r.entity_name ?? "unknown"} ({r.entity_type ?? "unknown"})",
                TryParseMetadata(r.metadata)
            )).ToList();
        }
        catch
        {
            return Array.Empty<AnomalyFinding>();
        }
    }

    private static double SeverityToScore(string severity) => severity.ToLowerInvariant() switch
    {
        "critical" => 1.0,
        "error" => 0.8,
        "warning" => 0.5,
        "info" => 0.2,
        _ => 0.3
    };

    private static Dictionary<string, object>? TryParseMetadata(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object>>(json);
        }
        catch
        {
            return null;
        }
    }

    public async Task<AnomalyStats> GetAnomalyStatsAsync(
        Guid? tenantId = null,
        int daysBack = 30,
        CancellationToken cancellationToken = default)
    {
        var findings = await DetectAnomaliesAsync(tenantId, 500, cancellationToken);

        var critical = findings.Count(f => f.SeverityScore >= 0.9);
        var high = findings.Count(f => f.SeverityScore >= 0.7 && f.SeverityScore < 0.9);
        var medium = findings.Count(f => f.SeverityScore >= 0.4 && f.SeverityScore < 0.7);
        var low = findings.Count(f => f.SeverityScore < 0.4);

        return new AnomalyStats(
            findings.Count,
            critical,
            high,
            medium,
            low,
            findings.GroupBy(f => f.Type).ToDictionary(g => g.Key, g => g.Count()),
            findings.GroupBy(f => f.Category).ToDictionary(g => g.Key, g => g.Count()),
            findings.Any() ? findings.Average(f => f.SeverityScore) : 0,
            findings.Any() ? findings.Max(f => f.DetectedAt) : null
        );
    }

    public async Task<IReadOnlyList<AnomalyTrendPoint>> GetAnomalyTrendAsync(
        Guid? tenantId = null,
        int daysBack = 30,
        CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        // Get historical anomaly data from self_healing_logs
        const string sql = """
            WITH daily_anomalies AS (
                SELECT
                    date_trunc('day', created_at) as day,
                    operation_type,
                    COUNT(*) as count,
                    AVG(duration_ms)::float as avg_duration
                FROM self_healing_logs
                WHERE created_at > NOW() - @DaysBack::interval
                  AND operation_type LIKE '%detected%'
                GROUP BY date_trunc('day', created_at), operation_type
            )
            SELECT day, SUM(count) as total_count,
                   STRING_AGG(operation_type || ':' || count::text, ',') as by_type
            FROM daily_anomalies
            GROUP BY day
            ORDER BY day
            """;

        try
        {
            var rows = await conn.QueryAsync<(DateTime day, int total_count, string by_type)>(
                sql, new { DaysBack = $"{daysBack} days" });

            return rows.Select(r => new AnomalyTrendPoint(
                new DateTimeOffset(r.day, TimeSpan.Zero),
                r.total_count,
                0.5, // Default avg severity
                ParseByType(r.by_type)
            )).ToList();
        }
        catch
        {
            return Array.Empty<AnomalyTrendPoint>();
        }
    }

    private static Dictionary<string, int> ParseByType(string? byType)
    {
        if (string.IsNullOrEmpty(byType)) return new Dictionary<string, int>();

        var result = new Dictionary<string, int>();
        foreach (var pair in byType.Split(','))
        {
            var parts = pair.Split(':');
            if (parts.Length == 2 && int.TryParse(parts[1], out var count))
            {
                result[parts[0]] = count;
            }
        }
        return result;
    }
}
