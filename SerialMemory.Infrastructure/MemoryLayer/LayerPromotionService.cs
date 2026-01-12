using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace SerialMemory.Infrastructure.MemoryLayer;

/// <summary>
/// Service responsible for promoting memories through the cognitive layer hierarchy.
/// Layers: L0_RAW → L1_CONTEXT → L2_SUMMARY → L3_KNOWLEDGE → L4_HEURISTIC
/// </summary>
public sealed class LayerPromotionService(
    IConfiguration configuration,
    ILogger<LayerPromotionService> logger)
{
    private readonly string _connectionString = configuration.GetConnectionString("Postgres")
                                                ?? BuildConnectionString();
    private readonly LayerPromotionConfig _config = new()
    {
        L0ToL1_MinAccessCount = configuration.GetValue("MemoryLayerWorker:L0ToL1:MinAccessCount", 2),
        L0ToL1_MinAgeDays = configuration.GetValue("MemoryLayerWorker:L0ToL1:MinAgeDays", 1),
        L1ToL2_MinClusterSize = configuration.GetValue("MemoryLayerWorker:L1ToL2:MinClusterSize", 3),
        L1ToL2_SimilarityThreshold = configuration.GetValue("MemoryLayerWorker:L1ToL2:SimilarityThreshold", 0.8f),
        L2ToL3_MinAccessCount = configuration.GetValue("MemoryLayerWorker:L2ToL3:MinAccessCount", 5),
        L2ToL3_MinConfidence = configuration.GetValue("MemoryLayerWorker:L2ToL3:MinConfidence", 0.7f),
        L3ToL4_MinSupportingFacts = configuration.GetValue("MemoryLayerWorker:L3ToL4:MinSupportingFacts", 5),
        L3ToL4_MinPatternConfidence = configuration.GetValue("MemoryLayerWorker:L3ToL4:MinPatternConfidence", 0.8f),
        BatchSize = configuration.GetValue("MemoryLayerWorker:BatchSize", 50)
    };

    // Load configuration with defaults

    private static string BuildConnectionString()
        => SerialMemory.Infrastructure.Configuration.ConnectionStringFactory.BuildConnectionString();

    /// <summary>
    /// Finds memories eligible for promotion to the next layer.
    /// </summary>
    public async Task<IReadOnlyList<PromotionCandidate>> FindPromotionCandidatesAsync(
        MemoryLayer fromLayer,
        int limit = 50,
        CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var toLayer = GetNextLayer(fromLayer);
        if (toLayer == null)
        {
            logger.LogDebug("No promotion path from {FromLayer}", fromLayer);
            return [];
        }

        var sql = fromLayer switch
        {
            MemoryLayer.L0_RAW => GetL0ToL1CandidatesSql(),
            MemoryLayer.L1_CONTEXT => GetL1ToL2CandidatesSql(),
            MemoryLayer.L2_SUMMARY => GetL2ToL3CandidatesSql(),
            MemoryLayer.L3_KNOWLEDGE => GetL3ToL4CandidatesSql(),
            _ => throw new ArgumentOutOfRangeException(nameof(fromLayer))
        };

        var parameters = GetPromotionParameters(fromLayer, limit);
        var rows = await conn.QueryAsync<PromotionCandidateRow>(sql, parameters);

        return rows.Select(r => new PromotionCandidate(
            r.memory_id,
            r.tenant_id,
            fromLayer,
            toLayer.Value,
            r.score,
            r.reason
        )).ToList();
    }

    /// <summary>
    /// Checks if a specific memory should be promoted to the target layer.
    /// </summary>
    public async Task<PromotionEvaluation> ShouldPromoteAsync(
        Guid memoryId,
        MemoryLayer targetLayer,
        CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var memory = await conn.QueryFirstOrDefaultAsync<MemoryRow>(
            "SELECT id, tenant_id, layer, access_count, created_at, metadata FROM memories WHERE id = @MemoryId",
            new { MemoryId = memoryId });

        if (memory == null)
        {
            return new PromotionEvaluation(false, "Memory not found", 0);
        }

        var currentLayer = ParseLayer(memory.layer);
        var expectedPreviousLayer = GetPreviousLayer(targetLayer);

        if (expectedPreviousLayer == null || currentLayer != expectedPreviousLayer)
        {
            return new PromotionEvaluation(false,
                $"Memory is at {currentLayer}, cannot promote to {targetLayer} (expected {expectedPreviousLayer})", 0);
        }

        return targetLayer switch
        {
            MemoryLayer.L1_CONTEXT => await EvaluateL0ToL1Async(conn, memory, ct),
            MemoryLayer.L2_SUMMARY => await EvaluateL1ToL2Async(conn, memory, ct),
            MemoryLayer.L3_KNOWLEDGE => await EvaluateL2ToL3Async(conn, memory, ct),
            MemoryLayer.L4_HEURISTIC => await EvaluateL3ToL4Async(conn, memory, ct),
            _ => new PromotionEvaluation(false, "Invalid target layer", 0)
        };
    }

    /// <summary>
    /// Queues a memory for layer promotion.
    /// </summary>
    public async Task<Guid> QueuePromotionAsync(
        Guid memoryId,
        Guid tenantId,
        MemoryLayer fromLayer,
        MemoryLayer toLayer,
        int priority = 5,
        CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var id = Guid.CreateVersion7();

        await conn.ExecuteAsync("""
            INSERT INTO layer_transition_queue (id, tenant_id, memory_id, from_layer, to_layer, priority, status, scheduled_at)
            VALUES (@Id, @TenantId, @MemoryId, @FromLayer, @ToLayer, @Priority, 'pending', NOW())
            ON CONFLICT (memory_id, to_layer) WHERE status IN ('pending', 'processing')
            DO NOTHING
            """,
            new
            {
                Id = id,
                TenantId = tenantId,
                MemoryId = memoryId,
                FromLayer = fromLayer.ToString(),
                ToLayer = toLayer.ToString(),
                Priority = priority
            });

        logger.LogDebug("Queued promotion {Id}: {MemoryId} from {From} to {To}",
            id, memoryId, fromLayer, toLayer);

        return id;
    }

    /// <summary>
    /// Claims the next pending promotion from the queue for processing.
    /// </summary>
    public async Task<LayerTransition?> ClaimNextTransitionAsync(
        string processorId,
        CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var row = await conn.QueryFirstOrDefaultAsync<TransitionRow>("""
            UPDATE layer_transition_queue
            SET status = 'processing',
                processor_id = @ProcessorId,
                started_at = NOW(),
                updated_at = NOW()
            WHERE id = (
                SELECT id FROM layer_transition_queue
                WHERE status = 'pending'
                  AND scheduled_at <= NOW()
                  AND retry_count < max_retries
                ORDER BY priority ASC, scheduled_at ASC
                LIMIT 1
                FOR UPDATE SKIP LOCKED
            )
            RETURNING id, tenant_id, memory_id, from_layer, to_layer, priority, retry_count
            """,
            new { ProcessorId = processorId });

        if (row == null) return null;

        return new LayerTransition(
            row.id,
            row.tenant_id,
            row.memory_id,
            ParseLayer(row.from_layer),
            ParseLayer(row.to_layer),
            row.priority,
            row.retry_count
        );
    }

    /// <summary>
    /// Completes a layer transition with success or failure.
    /// </summary>
    public async Task CompleteTransitionAsync(
        Guid transitionId,
        bool success,
        string? resultData = null,
        Guid[]? resultMemoryIds = null,
        string? errorMessage = null,
        CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        if (success)
        {
            await conn.ExecuteAsync("""
                UPDATE layer_transition_queue
                SET status = 'completed',
                    completed_at = NOW(),
                    processing_duration_ms = EXTRACT(EPOCH FROM (NOW() - started_at)) * 1000,
                    result_data = @ResultData::jsonb,
                    result_memory_ids = @ResultMemoryIds,
                    updated_at = NOW()
                WHERE id = @TransitionId
                """,
                new { TransitionId = transitionId, ResultData = resultData, ResultMemoryIds = resultMemoryIds });
        }
        else
        {
            // Mark as failed, but allow retry if under limit
            await conn.ExecuteAsync("""
                UPDATE layer_transition_queue
                SET status = CASE WHEN retry_count + 1 >= max_retries THEN 'failed' ELSE 'pending' END,
                    completed_at = CASE WHEN retry_count + 1 >= max_retries THEN NOW() ELSE NULL END,
                    processing_duration_ms = EXTRACT(EPOCH FROM (NOW() - started_at)) * 1000,
                    error_message = @ErrorMessage,
                    retry_count = retry_count + 1,
                    scheduled_at = CASE WHEN retry_count + 1 < max_retries THEN NOW() + (retry_count + 1) * INTERVAL '5 minutes' ELSE scheduled_at END,
                    processor_id = CASE WHEN retry_count + 1 < max_retries THEN NULL ELSE processor_id END,
                    started_at = CASE WHEN retry_count + 1 < max_retries THEN NULL ELSE started_at END,
                    updated_at = NOW()
                WHERE id = @TransitionId
                """,
                new { TransitionId = transitionId, ErrorMessage = errorMessage });
        }

        logger.LogDebug("Completed transition {TransitionId}: success={Success}", transitionId, success);
    }

    /// <summary>
    /// Updates a memory's layer after successful promotion.
    /// </summary>
    public async Task UpdateMemoryLayerAsync(
        Guid memoryId,
        MemoryLayer newLayer,
        Guid[]? parentMemoryIds = null,
        CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await conn.ExecuteAsync("""
            UPDATE memories
            SET layer = @NewLayer,
                parent_memory_ids = COALESCE(@ParentMemoryIds, parent_memory_ids),
                updated_at = NOW()
            WHERE id = @MemoryId
            """,
            new { MemoryId = memoryId, NewLayer = newLayer.ToString(), ParentMemoryIds = parentMemoryIds });
    }

    /// <summary>
    /// Gets statistics about layer distribution.
    /// </summary>
    public async Task<LayerStatistics> GetStatisticsAsync(
        Guid? tenantId = null,
        CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var tenantFilter = tenantId.HasValue ? "WHERE tenant_id = @TenantId" : "";

        var layerCounts = await conn.QueryAsync<(string layer, int count)>($"""
            SELECT COALESCE(layer, 'L0_RAW') as layer, COUNT(*) as count
            FROM memories
            {tenantFilter}
            GROUP BY layer
            """,
            new { TenantId = tenantId });

        var queueStats = await conn.QueryFirstAsync<(int pending, int processing, int failed)>("""
            SELECT
                COUNT(*) FILTER (WHERE status = 'pending') as pending,
                COUNT(*) FILTER (WHERE status = 'processing') as processing,
                COUNT(*) FILTER (WHERE status = 'failed') as failed
            FROM layer_transition_queue
            WHERE created_at > NOW() - INTERVAL '7 days'
            """);

        var layerDict = layerCounts.ToDictionary(x => x.layer, x => x.count);

        return new LayerStatistics(
            L0Count: layerDict.GetValueOrDefault("L0_RAW", 0),
            L1Count: layerDict.GetValueOrDefault("L1_CONTEXT", 0),
            L2Count: layerDict.GetValueOrDefault("L2_SUMMARY", 0),
            L3Count: layerDict.GetValueOrDefault("L3_KNOWLEDGE", 0),
            L4Count: layerDict.GetValueOrDefault("L4_HEURISTIC", 0),
            PendingTransitions: queueStats.pending,
            ProcessingTransitions: queueStats.processing,
            FailedTransitions: queueStats.failed
        );
    }

    #region Private Helper Methods

    private string GetL0ToL1CandidatesSql() => """
        SELECT
            id as memory_id,
            tenant_id,
            access_count::float / GREATEST(1, EXTRACT(EPOCH FROM (NOW() - created_at)) / 86400) as score,
            'Has entities and accessed ' || access_count || ' times' as reason
        FROM memories
        WHERE layer = 'L0_RAW' OR layer IS NULL
          AND access_count >= @MinAccessCount
          AND created_at <= NOW() - @MinAgeDays * INTERVAL '1 day'
          AND EXISTS (SELECT 1 FROM memory_entities WHERE memory_id = memories.id)
          AND NOT EXISTS (
              SELECT 1 FROM layer_transition_queue
              WHERE memory_id = memories.id AND status IN ('pending', 'processing')
          )
        ORDER BY score DESC
        LIMIT @Limit
        """;

    private string GetL1ToL2CandidatesSql() => """
        WITH memory_clusters AS (
            SELECT
                m.id as memory_id,
                m.tenant_id,
                COUNT(DISTINCT me.entity_id) as entity_count,
                (SELECT COUNT(*) FROM memories m2
                 JOIN memory_entities me2 ON m2.id = me2.memory_id
                 WHERE m2.layer = 'L1_CONTEXT'
                   AND m2.tenant_id = m.tenant_id
                   AND me2.entity_id IN (SELECT entity_id FROM memory_entities WHERE memory_id = m.id)
                   AND m2.id != m.id) as related_count
            FROM memories m
            JOIN memory_entities me ON m.id = me.memory_id
            WHERE m.layer = 'L1_CONTEXT'
            GROUP BY m.id, m.tenant_id
        )
        SELECT
            memory_id,
            tenant_id,
            related_count::float as score,
            'Has ' || related_count || ' related L1 memories with shared entities' as reason
        FROM memory_clusters
        WHERE related_count >= @MinClusterSize
          AND NOT EXISTS (
              SELECT 1 FROM layer_transition_queue
              WHERE memory_id = memory_clusters.memory_id AND status IN ('pending', 'processing')
          )
        ORDER BY score DESC
        LIMIT @Limit
        """;

    private string GetL2ToL3CandidatesSql() => """
        SELECT
            id as memory_id,
            tenant_id,
            access_count::float * COALESCE((metadata->>'confidence')::float, 0.5) as score,
            'Summary accessed ' || access_count || ' times with high confidence' as reason
        FROM memories
        WHERE layer = 'L2_SUMMARY'
          AND access_count >= @MinAccessCount
          AND COALESCE((metadata->>'confidence')::float, 0.5) >= @MinConfidence
          AND NOT EXISTS (
              SELECT 1 FROM layer_transition_queue
              WHERE memory_id = memories.id AND status IN ('pending', 'processing')
          )
        ORDER BY score DESC
        LIMIT @Limit
        """;

    private string GetL3ToL4CandidatesSql() => """
        WITH fact_patterns AS (
            SELECT
                m.id as memory_id,
                m.tenant_id,
                COUNT(DISTINCT h.pattern_type) as pattern_types,
                COUNT(*) as supporting_count
            FROM memories m
            LEFT JOIN learned_heuristics h ON m.id = ANY(h.supporting_memory_ids)
            WHERE m.layer = 'L3_KNOWLEDGE'
            GROUP BY m.id, m.tenant_id
        )
        SELECT
            fp.memory_id,
            fp.tenant_id,
            fp.pattern_types::float + (fp.supporting_count::float / 10) as score,
            'Knowledge with ' || fp.pattern_types || ' pattern types detected' as reason
        FROM fact_patterns fp
        JOIN memories m ON fp.memory_id = m.id
        WHERE fp.supporting_count >= @MinSupportingFacts
          OR (fp.pattern_types >= 2 AND fp.supporting_count >= 3)
          AND NOT EXISTS (
              SELECT 1 FROM layer_transition_queue
              WHERE memory_id = fp.memory_id AND status IN ('pending', 'processing')
          )
        ORDER BY score DESC
        LIMIT @Limit
        """;

    private object GetPromotionParameters(MemoryLayer fromLayer, int limit) => fromLayer switch
    {
        MemoryLayer.L0_RAW => new { MinAccessCount = _config.L0ToL1_MinAccessCount, MinAgeDays = _config.L0ToL1_MinAgeDays, Limit = limit },
        MemoryLayer.L1_CONTEXT => new { MinClusterSize = _config.L1ToL2_MinClusterSize, SimilarityThreshold = _config.L1ToL2_SimilarityThreshold, Limit = limit },
        MemoryLayer.L2_SUMMARY => new { MinAccessCount = _config.L2ToL3_MinAccessCount, MinConfidence = _config.L2ToL3_MinConfidence, Limit = limit },
        MemoryLayer.L3_KNOWLEDGE => new { MinSupportingFacts = _config.L3ToL4_MinSupportingFacts, MinPatternConfidence = _config.L3ToL4_MinPatternConfidence, Limit = limit },
        _ => new { Limit = limit }
    };

    private async Task<PromotionEvaluation> EvaluateL0ToL1Async(NpgsqlConnection conn, MemoryRow memory, CancellationToken ct)
    {
        var age = DateTime.UtcNow - memory.created_at;
        var hasEntities = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM memory_entities WHERE memory_id = @MemoryId",
            new { MemoryId = memory.id }) > 0;

        if (memory.access_count < _config.L0ToL1_MinAccessCount)
            return new PromotionEvaluation(false, $"Access count {memory.access_count} < {_config.L0ToL1_MinAccessCount}", memory.access_count / (float)_config.L0ToL1_MinAccessCount);

        if (age.TotalDays < _config.L0ToL1_MinAgeDays)
            return new PromotionEvaluation(false, $"Age {age.TotalDays:F1} days < {_config.L0ToL1_MinAgeDays}", (float)(age.TotalDays / _config.L0ToL1_MinAgeDays));

        if (!hasEntities)
            return new PromotionEvaluation(false, "No entities extracted", 0.5f);

        return new PromotionEvaluation(true, "Meets all L0→L1 criteria", 1.0f);
    }

    private async Task<PromotionEvaluation> EvaluateL1ToL2Async(NpgsqlConnection conn, MemoryRow memory, CancellationToken ct)
    {
        var relatedCount = await conn.ExecuteScalarAsync<int>("""
            SELECT COUNT(DISTINCT m2.id)
            FROM memories m2
            JOIN memory_entities me2 ON m2.id = me2.memory_id
            WHERE m2.layer = 'L1_CONTEXT'
              AND m2.tenant_id = @TenantId
              AND me2.entity_id IN (SELECT entity_id FROM memory_entities WHERE memory_id = @MemoryId)
              AND m2.id != @MemoryId
            """, new { TenantId = memory.tenant_id, MemoryId = memory.id });

        if (relatedCount < _config.L1ToL2_MinClusterSize)
            return new PromotionEvaluation(false, $"Related memories {relatedCount} < {_config.L1ToL2_MinClusterSize}", relatedCount / (float)_config.L1ToL2_MinClusterSize);

        return new PromotionEvaluation(true, $"Has {relatedCount} related memories for summarization", 1.0f);
    }

    private Task<PromotionEvaluation> EvaluateL2ToL3Async(NpgsqlConnection conn, MemoryRow memory, CancellationToken ct)
    {
        if (memory.access_count < _config.L2ToL3_MinAccessCount)
            return Task.FromResult(new PromotionEvaluation(false, $"Access count {memory.access_count} < {_config.L2ToL3_MinAccessCount}", memory.access_count / (float)_config.L2ToL3_MinAccessCount));

        return Task.FromResult(new PromotionEvaluation(true, "Summary validated by access patterns", 1.0f));
    }

    private async Task<PromotionEvaluation> EvaluateL3ToL4Async(NpgsqlConnection conn, MemoryRow memory, CancellationToken ct)
    {
        var supportingCount = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM memories WHERE layer = 'L3_KNOWLEDGE' AND tenant_id = @TenantId",
            new { TenantId = memory.tenant_id });

        if (supportingCount < _config.L3ToL4_MinSupportingFacts)
            return new PromotionEvaluation(false, $"Supporting facts {supportingCount} < {_config.L3ToL4_MinSupportingFacts}", supportingCount / (float)_config.L3ToL4_MinSupportingFacts);

        return new PromotionEvaluation(true, $"Has {supportingCount} facts supporting heuristic generation", 1.0f);
    }

    private static MemoryLayer? GetNextLayer(MemoryLayer current) => current switch
    {
        MemoryLayer.L0_RAW => MemoryLayer.L1_CONTEXT,
        MemoryLayer.L1_CONTEXT => MemoryLayer.L2_SUMMARY,
        MemoryLayer.L2_SUMMARY => MemoryLayer.L3_KNOWLEDGE,
        MemoryLayer.L3_KNOWLEDGE => MemoryLayer.L4_HEURISTIC,
        MemoryLayer.L4_HEURISTIC => null,
        _ => null
    };

    private static MemoryLayer? GetPreviousLayer(MemoryLayer current) => current switch
    {
        MemoryLayer.L1_CONTEXT => MemoryLayer.L0_RAW,
        MemoryLayer.L2_SUMMARY => MemoryLayer.L1_CONTEXT,
        MemoryLayer.L3_KNOWLEDGE => MemoryLayer.L2_SUMMARY,
        MemoryLayer.L4_HEURISTIC => MemoryLayer.L3_KNOWLEDGE,
        MemoryLayer.L0_RAW => null,
        _ => null
    };

    private static MemoryLayer ParseLayer(string? layer) => layer?.ToUpperInvariant() switch
    {
        "L0_RAW" => MemoryLayer.L0_RAW,
        "L1_CONTEXT" => MemoryLayer.L1_CONTEXT,
        "L2_SUMMARY" => MemoryLayer.L2_SUMMARY,
        "L3_KNOWLEDGE" => MemoryLayer.L3_KNOWLEDGE,
        "L4_HEURISTIC" => MemoryLayer.L4_HEURISTIC,
        null or "" => MemoryLayer.L0_RAW,
        _ => MemoryLayer.L0_RAW
    };

    #endregion

    #region DTOs

    private sealed class PromotionCandidateRow
    {
        public Guid memory_id { get; set; }
        public Guid tenant_id { get; set; }
        public float score { get; set; }
        public string reason { get; set; } = "";
    }

    private sealed class MemoryRow
    {
        public Guid id { get; set; }
        public Guid tenant_id { get; set; }
        public string? layer { get; set; }
        public int access_count { get; set; }
        public DateTime created_at { get; set; }
        public string? metadata { get; set; }
    }

    private sealed class TransitionRow
    {
        public Guid id { get; set; }
        public Guid tenant_id { get; set; }
        public Guid memory_id { get; set; }
        public string from_layer { get; set; } = "";
        public string to_layer { get; set; } = "";
        public int priority { get; set; }
        public int retry_count { get; set; }
    }

    #endregion
}

#region Public Types

/// <summary>
/// Memory cognitive layer hierarchy.
/// </summary>
public enum MemoryLayer
{
    /// <summary>Raw, unprocessed memory as originally ingested.</summary>
    L0_RAW,
    /// <summary>Memory with contextual metadata and entity links.</summary>
    L1_CONTEXT,
    /// <summary>Summarized memory consolidating related L1 memories.</summary>
    L2_SUMMARY,
    /// <summary>Extracted structured knowledge/facts.</summary>
    L3_KNOWLEDGE,
    /// <summary>Learned heuristics and patterns from L3 knowledge.</summary>
    L4_HEURISTIC
}

/// <summary>
/// Configuration for layer promotion criteria.
/// </summary>
public sealed class LayerPromotionConfig
{
    // L0 → L1 criteria
    public int L0ToL1_MinAccessCount { get; set; } = 2;
    public int L0ToL1_MinAgeDays { get; set; } = 1;

    // L1 → L2 criteria
    public int L1ToL2_MinClusterSize { get; set; } = 3;
    public float L1ToL2_SimilarityThreshold { get; set; } = 0.8f;

    // L2 → L3 criteria
    public int L2ToL3_MinAccessCount { get; set; } = 5;
    public float L2ToL3_MinConfidence { get; set; } = 0.7f;

    // L3 → L4 criteria
    public int L3ToL4_MinSupportingFacts { get; set; } = 5;
    public float L3ToL4_MinPatternConfidence { get; set; } = 0.8f;

    public int BatchSize { get; set; } = 50;
}

/// <summary>
/// A memory candidate for layer promotion.
/// </summary>
public sealed record PromotionCandidate(
    Guid MemoryId,
    Guid TenantId,
    MemoryLayer FromLayer,
    MemoryLayer ToLayer,
    float Score,
    string Reason);

/// <summary>
/// Result of evaluating whether a memory should be promoted.
/// </summary>
public sealed record PromotionEvaluation(
    bool ShouldPromote,
    string Reason,
    float ReadinessScore);

/// <summary>
/// A layer transition in the processing queue.
/// </summary>
public sealed record LayerTransition(
    Guid Id,
    Guid TenantId,
    Guid MemoryId,
    MemoryLayer FromLayer,
    MemoryLayer ToLayer,
    int Priority,
    int RetryCount);

/// <summary>
/// Statistics about memory layer distribution.
/// </summary>
public sealed record LayerStatistics(
    int L0Count,
    int L1Count,
    int L2Count,
    int L3Count,
    int L4Count,
    int PendingTransitions,
    int ProcessingTransitions,
    int FailedTransitions);

#endregion
