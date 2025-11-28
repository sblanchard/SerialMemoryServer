using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using SerialMemory.Core.Interfaces;

namespace SerialMemory.Infrastructure.SelfHealing;

/// <summary>
/// Generates and manages healing recommendations based on anomaly analysis.
/// Uses HybridRetrievalEngine for context enrichment.
/// </summary>
public sealed class HealingRecommendationService : IHealingRecommendationService
{
    private readonly IAnomalyDetectionService _anomalyDetector;
    private readonly IMemorySelfHealing _selfHealing;
    private readonly IKnowledgeGraphStore _knowledgeGraph;
    private readonly string _connectionString;
    private readonly ILogger<HealingRecommendationService> _logger;
    private readonly bool _isSelfHosted;
    private readonly string _workerId;

    public HealingRecommendationService(
        IAnomalyDetectionService anomalyDetector,
        IMemorySelfHealing selfHealing,
        IKnowledgeGraphStore knowledgeGraph,
        IConfiguration configuration,
        ILogger<HealingRecommendationService> logger)
    {
        _anomalyDetector = anomalyDetector;
        _selfHealing = selfHealing;
        _knowledgeGraph = knowledgeGraph;
        _connectionString = configuration.GetConnectionString("Postgres")
            ?? BuildConnectionString();
        _logger = logger;
        _isSelfHosted = string.Equals(
            configuration["DeploymentMode"] ?? Environment.GetEnvironmentVariable("DEPLOYMENT_MODE"),
            "selfhosted",
            StringComparison.OrdinalIgnoreCase);
        _workerId = $"healer-{Environment.MachineName}-{Environment.ProcessId}";
    }

    private static string BuildConnectionString()
    {
        var host = Environment.GetEnvironmentVariable("POSTGRES_HOST") ?? "localhost";
        var port = Environment.GetEnvironmentVariable("POSTGRES_PORT") ?? "5434";
        var user = Environment.GetEnvironmentVariable("POSTGRES_USER") ?? "postgres";
        var password = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD") ?? "postgres";
        var database = Environment.GetEnvironmentVariable("POSTGRES_DB") ?? "contextdb";
        return $"Host={host};Port={port};Username={user};Password={password};Database={database}";
    }

    public async Task<IReadOnlyList<HealingRecommendation>> AnalyzeAndRecommendAsync(
        Guid? tenantId = null,
        int maxRecommendations = 50,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting anomaly analysis for recommendations");

        // Step 1: Detect anomalies
        var anomalies = await _anomalyDetector.DetectAnomaliesAsync(tenantId, maxRecommendations * 2, cancellationToken);

        // Step 2: Generate recommendations for each anomaly
        var recommendations = new List<HealingRecommendation>();

        foreach (var anomaly in anomalies)
        {
            var recommendation = await GenerateRecommendationAsync(anomaly, cancellationToken);
            if (recommendation != null)
            {
                recommendations.Add(recommendation);

                // Persist recommendation
                await PersistRecommendationAsync(recommendation, cancellationToken);
            }

            if (recommendations.Count >= maxRecommendations)
                break;
        }

        _logger.LogInformation(
            "Generated {Count} healing recommendations from {AnomalyCount} anomalies",
            recommendations.Count, anomalies.Count);

        return recommendations;
    }

    private async Task<HealingRecommendation?> GenerateRecommendationAsync(
        AnomalyFinding anomaly,
        CancellationToken cancellationToken)
    {
        // Map anomaly type to recommended action
        var (action, reason) = MapAnomalyToAction(anomaly);

        if (action == null)
            return null;

        // Get enriched context if we have a memory or entity ID
        Dictionary<string, object>? context = null;

        if (anomaly.MemoryId.HasValue)
        {
            context = await GetMemoryContextAsync(anomaly.MemoryId.Value, cancellationToken);
        }
        else if (anomaly.EntityId.HasValue)
        {
            context = await GetEntityContextAsync(anomaly.EntityId.Value, cancellationToken);
        }

        // Merge anomaly evidence with context
        if (anomaly.Evidence != null)
        {
            context ??= new Dictionary<string, object>();
            foreach (var kv in anomaly.Evidence)
            {
                context[$"evidence_{kv.Key}"] = kv.Value;
            }
        }

        // Calculate confidence based on anomaly severity and available context
        var confidence = CalculateConfidence(anomaly, context);

        return new HealingRecommendation
        {
            Id = Guid.CreateVersion7(),
            AnomalyType = anomaly.Type,
            AnomalyId = anomaly.Id,
            Confidence = confidence,
            Severity = anomaly.SeverityScore,
            RecommendedAction = action,
            TargetMemoryId = anomaly.MemoryId,
            TargetEntityId = anomaly.EntityId,
            TenantId = anomaly.TenantId,
            Reason = reason,
            Status = RecommendationStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
            Context = context
        };
    }

    private (string? Action, string Reason) MapAnomalyToAction(AnomalyFinding anomaly)
    {
        return anomaly.Type switch
        {
            AnomalyTypes.OrphanedGraphNode => (
                HealingActions.RecrawlEntities,
                $"Entity has no linked memories. Recrawling may reconnect it or identify it for cleanup."),

            AnomalyTypes.ConflictCluster => (
                HealingActions.FlagForReview,
                $"Memory is involved in multiple conflicts. Manual review recommended to resolve contradictions."),

            AnomalyTypes.HighHallucinationRegion => (
                HealingActions.DecayConfidence,
                $"High hallucination rate detected. Reducing confidence of affected memories."),

            AnomalyTypes.DriftSpike => (
                HealingActions.FlagForReview,
                $"Sudden confidence change detected. Review to determine if legitimate or anomalous."),

            AnomalyTypes.RepeatedContradiction => (
                HealingActions.ResolveContradiction,
                $"Repeated contradictions between memory pair. Automated resolution may be possible."),

            AnomalyTypes.IntegrityFailure => (
                HealingActions.ReembedMemory,
                $"Integrity check failed. Re-embedding may restore consistency."),

            AnomalyTypes.ConfidenceDecayCluster => anomaly.SeverityScore > 0.7
                ? (HealingActions.InvalidateMemory,
                   $"Memory has critically low confidence ({anomaly.Evidence?.GetValueOrDefault("confidenceScore")}). Consider invalidation.")
                : (HealingActions.ReinforceMemory,
                   $"Memory has low confidence but may be recoverable with reinforcement."),

            AnomalyTypes.SecurityAnomaly => (
                HealingActions.FlagForReview,
                $"Security anomaly detected. Manual security review required."),

            AnomalyTypes.UnvalidatedMemory => (
                HealingActions.ReembedMemory,
                $"Memory has never been validated. Re-embedding and recrawling recommended."),

            _ => (null, "Unknown anomaly type")
        };
    }

    private async Task<Dictionary<string, object>?> GetMemoryContextAsync(
        Guid memoryId,
        CancellationToken cancellationToken)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        try
        {
            var memory = await conn.QueryFirstOrDefaultAsync<(string content, string? source, DateTime created_at, string? metadata)>(
                "SELECT content, source, created_at, metadata::text FROM memories WHERE id = @Id",
                new { Id = memoryId });

            if (memory.content == null)
                return null;

            var entityCount = await conn.QueryFirstOrDefaultAsync<int>(
                "SELECT COUNT(*) FROM memory_entities WHERE memory_id = @Id",
                new { Id = memoryId });

            var contradictionCount = await conn.QueryFirstOrDefaultAsync<int>(
                "SELECT COUNT(*) FROM memory_contradictions WHERE memory_id_a = @Id OR memory_id_b = @Id",
                new { Id = memoryId });

            return new Dictionary<string, object>
            {
                ["contentSnippet"] = memory.content.Length > 200 ? memory.content[..200] + "..." : memory.content,
                ["source"] = memory.source ?? "unknown",
                ["createdAt"] = memory.created_at,
                ["entityCount"] = entityCount,
                ["contradictionCount"] = contradictionCount
            };
        }
        catch
        {
            return null;
        }
    }

    private async Task<Dictionary<string, object>?> GetEntityContextAsync(
        Guid entityId,
        CancellationToken cancellationToken)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        try
        {
            var entity = await conn.QueryFirstOrDefaultAsync<(string name, string entity_type, DateTime created_at)>(
                "SELECT name, entity_type, created_at FROM entities WHERE id = @Id",
                new { Id = entityId });

            if (entity.name == null)
                return null;

            var memoryCount = await conn.QueryFirstOrDefaultAsync<int>(
                "SELECT COUNT(*) FROM memory_entities WHERE entity_id = @Id",
                new { Id = entityId });

            var relationshipCount = await conn.QueryFirstOrDefaultAsync<int>(
                "SELECT COUNT(*) FROM entity_relationships WHERE source_entity_id = @Id OR target_entity_id = @Id",
                new { Id = entityId });

            return new Dictionary<string, object>
            {
                ["entityName"] = entity.name,
                ["entityType"] = entity.entity_type,
                ["createdAt"] = entity.created_at,
                ["memoryCount"] = memoryCount,
                ["relationshipCount"] = relationshipCount
            };
        }
        catch
        {
            return null;
        }
    }

    private static double CalculateConfidence(AnomalyFinding anomaly, Dictionary<string, object>? context)
    {
        var baseConfidence = 0.5;

        // Higher severity = higher confidence in recommendation
        baseConfidence += anomaly.SeverityScore * 0.3;

        // More context = more confidence
        if (context != null && context.Count > 3)
            baseConfidence += 0.1;

        // Evidence increases confidence
        if (anomaly.Evidence != null && anomaly.Evidence.Count > 0)
            baseConfidence += 0.1;

        return Math.Min(1.0, baseConfidence);
    }

    private async Task PersistRecommendationAsync(
        HealingRecommendation recommendation,
        CancellationToken cancellationToken)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        const string sql = """
            INSERT INTO self_healing_recommendations (
                id, anomaly_type, anomaly_id, confidence, severity,
                recommended_action, target_memory_id, target_entity_id,
                tenant_id, reason, status, context, created_at, worker_id
            ) VALUES (
                @Id, @AnomalyType, @AnomalyId, @Confidence, @Severity,
                @RecommendedAction, @TargetMemoryId, @TargetEntityId,
                @TenantId, @Reason, @Status, @Context::jsonb, @CreatedAt, @WorkerId
            )
            ON CONFLICT (id) DO NOTHING
            """;

        await conn.ExecuteAsync(sql, new
        {
            recommendation.Id,
            recommendation.AnomalyType,
            recommendation.AnomalyId,
            recommendation.Confidence,
            recommendation.Severity,
            recommendation.RecommendedAction,
            recommendation.TargetMemoryId,
            recommendation.TargetEntityId,
            recommendation.TenantId,
            recommendation.Reason,
            Status = recommendation.Status.ToString(),
            Context = recommendation.Context != null ? JsonSerializer.Serialize(recommendation.Context) : "{}",
            recommendation.CreatedAt,
            WorkerId = _workerId
        });
    }

    public async Task<IReadOnlyList<HealingRecommendation>> GetPendingRecommendationsAsync(
        Guid? tenantId = null,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        var sql = """
            SELECT id, anomaly_type, anomaly_id, confidence, severity,
                   recommended_action, target_memory_id, target_entity_id,
                   tenant_id, reason, status, context, created_at,
                   applied_at, applied_by, dismissed_reason
            FROM self_healing_recommendations
            WHERE status = 'Pending'
            """ + (tenantId.HasValue ? " AND (tenant_id = @TenantId OR tenant_id IS NULL)" : "") + """
            ORDER BY severity DESC, created_at DESC
            LIMIT @Limit
            """;

        var rows = await conn.QueryAsync<RecommendationRow>(
            sql, new { TenantId = tenantId, Limit = limit });

        return rows.Select(MapRowToRecommendation).ToList();
    }

    public async Task<HealingActionResult> ApplyRecommendationAsync(
        Guid recommendationId,
        Guid? tenantId = null,
        string? appliedBy = null,
        CancellationToken cancellationToken = default)
    {
        var startTime = DateTimeOffset.UtcNow;

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        // Get the recommendation
        var recommendation = await conn.QueryFirstOrDefaultAsync<RecommendationRow>(
            """
            SELECT id, anomaly_type, anomaly_id, confidence, severity,
                   recommended_action, target_memory_id, target_entity_id,
                   tenant_id, reason, status, context, created_at,
                   applied_at, applied_by, dismissed_reason
            FROM self_healing_recommendations
            WHERE id = @Id AND status = 'Pending'
            """,
            new { Id = recommendationId });

        if (recommendation == null)
        {
            return new HealingActionResult(
                false, "Recommendation not found or already processed",
                recommendationId, "", null, null, 0);
        }

        var rec = MapRowToRecommendation(recommendation);

        // Check if action is allowed
        var risk = HealingActionRiskMap.GetRisk(rec.RecommendedAction);
        var isLabTenant = tenantId.HasValue && await IsLabTenantAsync(conn, tenantId.Value);

        if (risk >= HealingActionRisk.High && !_isSelfHosted && !isLabTenant)
        {
            return new HealingActionResult(
                false, $"Action '{rec.RecommendedAction}' requires manual approval in production mode",
                recommendationId, rec.RecommendedAction, rec.TargetMemoryId ?? rec.TargetEntityId, null, 0);
        }

        // Execute the action
        string? details = null;
        var success = false;

        try
        {
            switch (rec.RecommendedAction)
            {
                case HealingActions.DecayConfidence:
                    if (rec.TargetMemoryId.HasValue)
                    {
                        var decayed = await _selfHealing.ApplyDecayAsync(TimeSpan.FromDays(0), 0.8f, cancellationToken);
                        details = $"Applied confidence decay to memory, affected {decayed} records";
                        success = true;
                    }
                    break;

                case HealingActions.ReembedMemory:
                    if (rec.TargetMemoryId.HasValue)
                    {
                        await CreateReembedJobAsync(conn, rec.TargetMemoryId.Value);
                        details = "Created re-embed job for memory";
                        success = true;
                    }
                    break;

                case HealingActions.RecrawlEntities:
                    if (rec.TargetMemoryId.HasValue || rec.TargetEntityId.HasValue)
                    {
                        await CreateRecrawlJobAsync(conn, rec.TargetMemoryId, rec.TargetEntityId);
                        details = "Created recrawl job";
                        success = true;
                    }
                    break;

                case HealingActions.FlagForReview:
                    await FlagForReviewAsync(conn, rec);
                    details = "Flagged for manual review";
                    success = true;
                    break;

                case HealingActions.ReinforceMemory:
                    if (rec.TargetMemoryId.HasValue)
                    {
                        var reinforced = await _selfHealing.ReinforceMemoriesAsync(
                            new[] { rec.TargetMemoryId.Value }, 1.2f, "healing_recommendation", cancellationToken);
                        details = $"Reinforced memory, affected {reinforced} records";
                        success = true;
                    }
                    break;

                default:
                    return new HealingActionResult(
                        false, $"Action '{rec.RecommendedAction}' is not yet implemented for automated execution",
                        recommendationId, rec.RecommendedAction, rec.TargetMemoryId ?? rec.TargetEntityId, null, 0);
            }

            // Update recommendation status
            if (success)
            {
                await conn.ExecuteAsync(
                    """
                    UPDATE self_healing_recommendations
                    SET status = 'Applied', applied_at = @AppliedAt, applied_by = @AppliedBy
                    WHERE id = @Id
                    """,
                    new { Id = recommendationId, AppliedAt = DateTimeOffset.UtcNow, AppliedBy = appliedBy ?? _workerId });

                // Log to security_events
                await LogHealingActionAsync(conn, rec, details, appliedBy);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply healing recommendation {Id}", recommendationId);

            await conn.ExecuteAsync(
                "UPDATE self_healing_recommendations SET status = 'Failed' WHERE id = @Id",
                new { Id = recommendationId });

            return new HealingActionResult(
                false, ex.Message, recommendationId, rec.RecommendedAction,
                rec.TargetMemoryId ?? rec.TargetEntityId, null,
                (long)(DateTimeOffset.UtcNow - startTime).TotalMilliseconds);
        }

        var duration = (long)(DateTimeOffset.UtcNow - startTime).TotalMilliseconds;
        return new HealingActionResult(success, null, recommendationId, rec.RecommendedAction,
            rec.TargetMemoryId ?? rec.TargetEntityId, details, duration);
    }

    private async Task<bool> IsLabTenantAsync(NpgsqlConnection conn, Guid tenantId)
    {
        try
        {
            var isLab = await conn.QueryFirstOrDefaultAsync<bool>(
                "SELECT is_lab_tenant FROM tenants WHERE id = @Id",
                new { Id = tenantId });
            return isLab;
        }
        catch
        {
            return false;
        }
    }

    private static async Task CreateReembedJobAsync(NpgsqlConnection conn, Guid memoryId)
    {
        await conn.ExecuteAsync(
            """
            INSERT INTO memory_reembed_jobs (id, memory_id, status, created_at)
            VALUES (@Id, @MemoryId, 'pending', NOW())
            ON CONFLICT DO NOTHING
            """,
            new { Id = Guid.CreateVersion7(), MemoryId = memoryId });
    }

    private static async Task CreateRecrawlJobAsync(NpgsqlConnection conn, Guid? memoryId, Guid? entityId)
    {
        await conn.ExecuteAsync(
            """
            INSERT INTO memory_recrawl_jobs (id, memory_id, entity_id, status, created_at)
            VALUES (@Id, @MemoryId, @EntityId, 'pending', NOW())
            ON CONFLICT DO NOTHING
            """,
            new { Id = Guid.CreateVersion7(), MemoryId = memoryId, EntityId = entityId });
    }

    private static async Task FlagForReviewAsync(NpgsqlConnection conn, HealingRecommendation rec)
    {
        await conn.ExecuteAsync(
            """
            INSERT INTO manual_review_items (id, item_type, target_id, reason, severity, created_at)
            VALUES (@Id, @ItemType, @TargetId, @Reason, @Severity, NOW())
            ON CONFLICT DO NOTHING
            """,
            new
            {
                Id = Guid.CreateVersion7(),
                ItemType = rec.AnomalyType,
                TargetId = rec.TargetMemoryId ?? rec.TargetEntityId,
                rec.Reason,
                rec.Severity
            });
    }

    private static async Task LogHealingActionAsync(NpgsqlConnection conn, HealingRecommendation rec, string? details, string? appliedBy)
    {
        await conn.ExecuteAsync(
            """
            INSERT INTO security_events (id, event_type, category, severity, message, metadata, created_at)
            VALUES (@Id, @EventType, 'healing_action', 'info', @Message, @Metadata::jsonb, NOW())
            """,
            new
            {
                Id = Guid.CreateVersion7(),
                EventType = $"healing_{rec.RecommendedAction.ToLowerInvariant()}",
                Message = $"Applied healing action: {rec.RecommendedAction} - {details}",
                Metadata = JsonSerializer.Serialize(new
                {
                    recommendationId = rec.Id,
                    anomalyType = rec.AnomalyType,
                    targetMemoryId = rec.TargetMemoryId,
                    targetEntityId = rec.TargetEntityId,
                    appliedBy
                })
            });
    }

    public async Task<bool> DismissRecommendationAsync(
        Guid recommendationId,
        string reason,
        Guid? tenantId = null,
        CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        var updated = await conn.ExecuteAsync(
            """
            UPDATE self_healing_recommendations
            SET status = 'Dismissed', dismissed_reason = @Reason
            WHERE id = @Id AND status = 'Pending'
            """,
            new { Id = recommendationId, Reason = reason });

        return updated > 0;
    }

    public async Task<RecommendationStats> GetRecommendationStatsAsync(
        Guid? tenantId = null,
        int daysBack = 30,
        CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        var tenantFilter = tenantId.HasValue ? "AND (tenant_id = @TenantId OR tenant_id IS NULL)" : "";

        var stats = await conn.QueryFirstOrDefaultAsync<(int total, int pending, int applied, int dismissed, int failed, double avg_confidence, DateTime? last_generated, DateTime? last_applied)>(
            $"""
            SELECT
                COUNT(*) as total,
                COUNT(*) FILTER (WHERE status = 'Pending') as pending,
                COUNT(*) FILTER (WHERE status = 'Applied') as applied,
                COUNT(*) FILTER (WHERE status = 'Dismissed') as dismissed,
                COUNT(*) FILTER (WHERE status = 'Failed') as failed,
                COALESCE(AVG(confidence), 0) as avg_confidence,
                MAX(created_at) as last_generated,
                MAX(applied_at) as last_applied
            FROM self_healing_recommendations
            WHERE created_at > NOW() - @DaysBack::interval {tenantFilter}
            """,
            new { DaysBack = $"{daysBack} days", TenantId = tenantId });

        var byAction = await conn.QueryAsync<(string recommended_action, int count)>(
            $"""
            SELECT recommended_action, COUNT(*) as count
            FROM self_healing_recommendations
            WHERE created_at > NOW() - @DaysBack::interval {tenantFilter}
            GROUP BY recommended_action
            """,
            new { DaysBack = $"{daysBack} days", TenantId = tenantId });

        var byAnomalyType = await conn.QueryAsync<(string anomaly_type, int count)>(
            $"""
            SELECT anomaly_type, COUNT(*) as count
            FROM self_healing_recommendations
            WHERE created_at > NOW() - @DaysBack::interval {tenantFilter}
            GROUP BY anomaly_type
            """,
            new { DaysBack = $"{daysBack} days", TenantId = tenantId });

        return new RecommendationStats(
            stats.total,
            stats.pending,
            stats.applied,
            stats.dismissed,
            stats.failed,
            byAction.ToDictionary(x => x.recommended_action, x => x.count),
            byAnomalyType.ToDictionary(x => x.anomaly_type, x => x.count),
            stats.avg_confidence,
            stats.last_generated.HasValue ? new DateTimeOffset(stats.last_generated.Value, TimeSpan.Zero) : null,
            stats.last_applied.HasValue ? new DateTimeOffset(stats.last_applied.Value, TimeSpan.Zero) : null
        );
    }

    private static HealingRecommendation MapRowToRecommendation(RecommendationRow row)
    {
        return new HealingRecommendation
        {
            Id = row.id,
            AnomalyType = row.anomaly_type,
            AnomalyId = row.anomaly_id,
            Confidence = row.confidence,
            Severity = row.severity,
            RecommendedAction = row.recommended_action,
            TargetMemoryId = row.target_memory_id,
            TargetEntityId = row.target_entity_id,
            TenantId = row.tenant_id,
            Reason = row.reason,
            Status = Enum.TryParse<RecommendationStatus>(row.status, out var s) ? s : RecommendationStatus.Pending,
            CreatedAt = new DateTimeOffset(row.created_at, TimeSpan.Zero),
            AppliedAt = row.applied_at.HasValue ? new DateTimeOffset(row.applied_at.Value, TimeSpan.Zero) : null,
            AppliedBy = row.applied_by,
            DismissedReason = row.dismissed_reason,
            Context = string.IsNullOrEmpty(row.context) ? null : JsonSerializer.Deserialize<Dictionary<string, object>>(row.context)
        };
    }

    private sealed class RecommendationRow
    {
        public Guid id { get; set; }
        public string anomaly_type { get; set; } = "";
        public Guid? anomaly_id { get; set; }
        public double confidence { get; set; }
        public double severity { get; set; }
        public string recommended_action { get; set; } = "";
        public Guid? target_memory_id { get; set; }
        public Guid? target_entity_id { get; set; }
        public Guid? tenant_id { get; set; }
        public string reason { get; set; } = "";
        public string status { get; set; } = "";
        public string? context { get; set; }
        public DateTime created_at { get; set; }
        public DateTime? applied_at { get; set; }
        public string? applied_by { get; set; }
        public string? dismissed_reason { get; set; }
    }
}
