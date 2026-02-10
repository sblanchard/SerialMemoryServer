using System.Diagnostics;
using Dapper;
using Npgsql;
using SerialMemory.Core.Interfaces;
using SerialMemory.Infrastructure.Classification;

namespace SerialMemory.Worker.Classification;

/// <summary>
/// Background worker that processes memories through L0-L4 classification layers.
/// Implements cognitive maturation: memories dwell at each layer for a configured
/// duration before advancing, giving contradiction detection time to resolve conflicts.
/// Only ONE layer is processed per cycle per memory.
/// </summary>
public sealed class MemoryClassificationWorker(
    NpgsqlDataSource dataSource,
    IClassificationService classificationService,
    IEventWriter eventWriter,
    IL2EmbeddingService l2EmbeddingService,
    ClassificationConfig dwellConfig,
    ILogger<MemoryClassificationWorker> logger)
    : BackgroundService
{
    private readonly string _workerId = $"worker-{Environment.MachineName}-{Guid.CreateVersion7():N}";

    private const int BatchSize = 10;
    private const int PollingIntervalMs = 1000;
    private const int ErrorBackoffMs = 5000;
    private static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(5);

    private static readonly string[] LayerOrder = ["L0_RAW", "L1_CONTEXT", "L2_SUMMARY", "L3_KNOWLEDGE", "L4_HEURISTIC"];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Memory Classification Worker {WorkerId} starting (dwell: L1={L1}d, L2={L2}d, L3={L3}d, cutoff={Cutoff})",
            _workerId,
            dwellConfig.DwellTimeL1.TotalDays,
            dwellConfig.DwellTimeL2.TotalDays,
            dwellConfig.DwellTimeL3.TotalDays,
            dwellConfig.DwellCutoffDate);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await ProcessBatchAsync(stoppingToken);

                if (processed == 0)
                {
                    await Task.Delay(PollingIntervalMs, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in classification worker loop");
                await Task.Delay(ErrorBackoffMs, stoppingToken);
            }
        }

        logger.LogInformation("Memory Classification Worker {WorkerId} stopping", _workerId);
    }

    private async Task<int> ProcessBatchAsync(CancellationToken ct)
    {
        await using var conn = await OpenInternalConnectionAsync(ct);

        var batch = (await conn.QueryAsync<QueueItem>(
            "SELECT * FROM acquire_classification_batch(@WorkerId, @BatchSize, @LockDuration::interval)",
            new
            {
                WorkerId = _workerId,
                BatchSize,
                LockDuration = LockDuration.ToString()
            })).ToList();

        if (batch.Count == 0)
            return 0;

        logger.LogDebug("Acquired {Count} memories for classification", batch.Count);

        foreach (var item in batch)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                await ProcessMemoryAsync(conn, item, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to process memory {MemoryId}", item.memory_id);
            }
        }

        return batch.Count;
    }

    private async Task ProcessMemoryAsync(NpgsqlConnection conn, QueueItem item, CancellationToken ct)
    {
        logger.LogDebug("Processing memory {MemoryId} for tenant {TenantId}, current stage: {Stage}",
            item.memory_id, item.tenant_id, item.current_stage);

        await conn.ExecuteAsync(
            "SELECT set_config('app.tenant_id', @TenantId, false)",
            new { TenantId = item.tenant_id.ToString() });

        var memory = await conn.QueryFirstOrDefaultAsync<MemoryRecord>(
            "SELECT id, content, metadata, created_at FROM memories WHERE id = @Id",
            new { Id = item.memory_id });

        if (memory == null)
        {
            logger.LogWarning("Memory {MemoryId} not found, removing from queue", item.memory_id);
            await conn.ExecuteAsync(
                "DELETE FROM memory_processing_queue WHERE memory_id = @MemoryId",
                new { MemoryId = item.memory_id });
            return;
        }

        // Find the next layer to process
        var existingLayers = await GetExistingLayerTimesAsync(conn, item.memory_id);
        var nextLayer = FindNextLayer(existingLayers);

        if (nextLayer == null)
        {
            logger.LogInformation("Memory {MemoryId} fully classified (all layers exist)", item.memory_id);
            return;
        }

        // Check dwell time: is the previous layer old enough?
        var previousLayer = ClassificationConfig.GetPreviousLayer(nextLayer);
        if (previousLayer != null && existingLayers.TryGetValue(previousLayer, out var prevCreatedAt))
        {
            var dwellRequired = dwellConfig.GetDwellTime(previousLayer);
            var isDwellExempt = memory.created_at < dwellConfig.DwellCutoffDate;

            if (dwellRequired > TimeSpan.Zero && !isDwellExempt)
            {
                var elapsed = DateTimeOffset.UtcNow - prevCreatedAt;
                if (elapsed < dwellRequired)
                {
                    var remaining = dwellRequired - elapsed;
                    logger.LogDebug(
                        "Memory {MemoryId} dwelling at {Layer} ({Elapsed:F1}h / {Required:F1}h, {Remaining:F1}h remaining)",
                        item.memory_id, previousLayer, elapsed.TotalHours, dwellRequired.TotalHours, remaining.TotalHours);

                    // Release the lock - this memory isn't ready yet
                    await conn.ExecuteAsync(
                        """
                        UPDATE memory_processing_queue
                        SET status = 'PENDING', locked_by = NULL, locked_until = NULL
                        WHERE memory_id = @MemoryId
                        """,
                        new { MemoryId = item.memory_id });
                    return;
                }
            }
        }

        // Process exactly ONE layer
        var layer = Enum.Parse<MemoryLayer>(nextLayer);
        try
        {
            await ProcessLayerAsync(conn, item, memory, layer, ct);
            logger.LogInformation("Completed {Layer} for memory {MemoryId} (next layer will dwell)",
                layer, item.memory_id);

            // After L2, the embedding is indexed — run targeted contradiction check
            if (layer == MemoryLayer.L2_SUMMARY)
            {
                await RunTargetedConflictCheckAsync(conn, item.tenant_id, item.memory_id, ct);
            }

            // Release lock so memory re-enters queue for next layer
            // (it will be picked up again after dwell time passes)
            if (layer != MemoryLayer.L4_HEURISTIC)
            {
                await conn.ExecuteAsync(
                    """
                    UPDATE memory_processing_queue
                    SET status = 'PENDING', locked_by = NULL, locked_until = NULL
                    WHERE memory_id = @MemoryId
                    """,
                    new { MemoryId = item.memory_id });
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to classify {Layer} for memory {MemoryId}", layer, item.memory_id);

            await conn.ExecuteAsync(
                "SELECT fail_layer_classification(@MemoryId, @Layer::memory_layer_type, @Error)",
                new
                {
                    MemoryId = item.memory_id,
                    Layer = layer.ToString(),
                    Error = ex.Message
                });
        }
    }

    /// <summary>
    /// Runs a lightweight contradiction check for a single memory using pgvector KNN.
    /// Finds the top N semantically similar active memories and logs any new conflicts.
    /// </summary>
    private async Task RunTargetedConflictCheckAsync(
        NpgsqlConnection conn, Guid tenantId, Guid memoryId, CancellationToken ct)
    {
        const int maxNeighbors = 5;
        const float similarityThreshold = 0.85f;

        try
        {
            var conflicts = await conn.QueryAsync<ConflictCandidate>(
                """
                SELECT
                    m.id AS neighbor_id,
                    1 - (m.embedding <=> target.embedding) AS similarity
                FROM memories m
                CROSS JOIN (SELECT embedding FROM memories WHERE id = @MemoryId) target
                WHERE m.id != @MemoryId
                  AND m.tenant_id = @TenantId
                  AND m.is_active = true
                  AND m.embedding IS NOT NULL
                  AND target.embedding IS NOT NULL
                  AND 1 - (m.embedding <=> target.embedding) >= @Threshold
                ORDER BY m.embedding <=> target.embedding
                LIMIT @MaxNeighbors
                """,
                new
                {
                    MemoryId = memoryId,
                    TenantId = tenantId,
                    Threshold = similarityThreshold,
                    MaxNeighbors = maxNeighbors
                });

            foreach (var c in conflicts)
            {
                // Check if this conflict already exists (either direction)
                var exists = await conn.ExecuteScalarAsync<bool>(
                    """
                    SELECT EXISTS(
                        SELECT 1 FROM conflict_log
                        WHERE tenant_id = @TenantId
                          AND ((memory_a_id = @MemoryAId AND memory_b_id = @MemoryBId)
                               OR (memory_a_id = @MemoryBId AND memory_b_id = @MemoryAId))
                    )
                    """,
                    new { TenantId = tenantId, MemoryAId = memoryId, MemoryBId = c.neighbor_id });

                if (exists)
                    continue;

                var conflictType = c.similarity >= 0.95f ? "duplicate" : "contradiction";
                var severity = c.similarity >= 0.95f ? 0.9f : Math.Min(c.similarity * 1.2f, 1.0f);

                await conn.ExecuteAsync(
                    """
                    INSERT INTO conflict_log (
                        id, tenant_id, memory_a_id, memory_b_id, similarity_score,
                        conflict_type, severity, status, description, detected_at
                    ) VALUES (
                        @Id, @TenantId, @MemoryAId, @MemoryBId, @Similarity,
                        @ConflictType, @Severity, 'unresolved', @Description, NOW()
                    )
                    ON CONFLICT DO NOTHING
                    """,
                    new
                    {
                        Id = Guid.CreateVersion7(),
                        TenantId = tenantId,
                        MemoryAId = memoryId,
                        MemoryBId = c.neighbor_id,
                        Similarity = c.similarity,
                        ConflictType = conflictType,
                        Severity = severity,
                        Description = $"High semantic similarity ({c.similarity:P0}) detected during classification"
                    });

                await eventWriter.EmitConflictDetectedAsync(
                    tenantId, memoryId, c.neighbor_id,
                    (decimal)c.similarity, conflictType,
                    $"Detected during L2 classification (similarity: {c.similarity:P1})",
                    _workerId, ct);

                logger.LogInformation(
                    "Conflict detected: memory {MemoryId} vs {NeighborId} (similarity: {Similarity:P1}, type: {Type})",
                    memoryId, c.neighbor_id, c.similarity, conflictType);
            }
        }
        catch (Exception ex)
        {
            // Non-fatal: log and continue — don't block classification pipeline
            logger.LogWarning(ex, "Targeted conflict check failed for memory {MemoryId}", memoryId);
        }
    }

    /// <summary>
    /// Gets existing layers and their creation timestamps for dwell time checks.
    /// </summary>
    private static async Task<Dictionary<string, DateTimeOffset>> GetExistingLayerTimesAsync(
        NpgsqlConnection conn, Guid memoryId)
    {
        var layers = await conn.QueryAsync<(string layer, DateTimeOffset created_at)>(
            """
            SELECT layer::text, created_at
            FROM memory_layers
            WHERE memory_id = @MemoryId AND is_current = TRUE
            """,
            new { MemoryId = memoryId });

        return layers.ToDictionary(l => l.layer, l => l.created_at);
    }

    /// <summary>
    /// Finds the next layer that needs to be created (first missing layer in order).
    /// </summary>
    private static string? FindNextLayer(Dictionary<string, DateTimeOffset> existingLayers)
    {
        foreach (var layer in LayerOrder)
        {
            if (!existingLayers.ContainsKey(layer))
                return layer;
        }
        return null; // All layers exist
    }

    private async Task ProcessLayerAsync(
        NpgsqlConnection conn,
        QueueItem item,
        MemoryRecord memory,
        MemoryLayer layer,
        CancellationToken ct)
    {
        logger.LogDebug("Processing {Layer} for memory {MemoryId}", layer, memory.id);

        var sw = Stopwatch.StartNew();

        await conn.ExecuteAsync(
            """
            INSERT INTO memory_classification_events (memory_id, tenant_id, event_type, layer)
            VALUES (@MemoryId, @TenantId, 'LAYER_STARTED', @Layer::memory_layer_type)
            """,
            new { MemoryId = memory.id, TenantId = item.tenant_id, Layer = layer.ToString() });

        var previousLayerContent = await GetPreviousLayerContentAsync(conn, memory.id, layer);

        var result = await classificationService.ClassifyAsync(
            memory.content,
            layer,
            previousLayerContent,
            ct);

        sw.Stop();

        var layerId = await conn.ExecuteScalarAsync<Guid>(
            """
            SELECT complete_layer_classification(
                @MemoryId, @Layer::memory_layer_type, @ContentJson::jsonb,
                @ModelName, @DurationMs, @Confidence
            )
            """,
            new
            {
                MemoryId = memory.id,
                Layer = layer.ToString(),
                ContentJson = result.ContentJson,
                ModelName = result.ModelName,
                DurationMs = (int)sw.ElapsedMilliseconds,
                Confidence = result.Confidence
            });

        var eventId = await eventWriter.EmitLayerGeneratedAsync(
            item.tenant_id,
            memory.id,
            layer.ToString(),
            result.ContentJson ?? "{}",
            result.ModelName ?? "unknown",
            (int)sw.ElapsedMilliseconds,
            (decimal)result.Confidence!,
            _workerId,
            ct);

        await eventWriter.CreateTimelineSnapshotAsync(
            item.tenant_id,
            memory.id,
            eventId,
            "LayerGenerated",
            memory.content,
            layer.ToString(),
            (decimal)result.Confidence!,
            new { layer_id = layerId, model = result.ModelName },
            ct);

        if (layer == MemoryLayer.L2_SUMMARY)
        {
            try
            {
                await l2EmbeddingService.IndexL2Async(item.tenant_id, memory.id, ct);
                logger.LogDebug("Indexed L2 embedding for memory {MemoryId}", memory.id);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to index L2 embedding for memory {MemoryId}", memory.id);
            }
        }

        if (layer is MemoryLayer.L3_KNOWLEDGE or MemoryLayer.L4_HEURISTIC)
        {
            await ExtractKnowledgeNodesAsync(conn, memory.id, item.tenant_id, layerId, layer, result, ct);
        }

        logger.LogDebug("Completed {Layer} for memory {MemoryId} in {ElapsedMs}ms (event: {EventId})",
            layer, memory.id, sw.ElapsedMilliseconds, eventId);
    }

    private async Task<string?> GetPreviousLayerContentAsync(
        NpgsqlConnection conn,
        Guid memoryId,
        MemoryLayer currentLayer)
    {
        if (currentLayer == MemoryLayer.L0_RAW)
            return null;

        var previousLayer = currentLayer switch
        {
            MemoryLayer.L1_CONTEXT => "L0_RAW",
            MemoryLayer.L2_SUMMARY => "L1_CONTEXT",
            MemoryLayer.L3_KNOWLEDGE => "L2_SUMMARY",
            MemoryLayer.L4_HEURISTIC => "L3_KNOWLEDGE",
            _ => null
        };

        if (previousLayer == null)
            return null;

        return await conn.ExecuteScalarAsync<string>(
            """
            SELECT content_json::text FROM memory_layers
            WHERE memory_id = @MemoryId AND layer = @Layer::memory_layer_type AND is_current = TRUE
            """,
            new { MemoryId = memoryId, Layer = previousLayer });
    }

    private async Task ExtractKnowledgeNodesAsync(
        NpgsqlConnection conn,
        Guid memoryId,
        Guid tenantId,
        Guid layerId,
        MemoryLayer layer,
        ClassificationResult result,
        CancellationToken ct)
    {
        if (result.KnowledgeNodes == null || result.KnowledgeNodes.Count == 0)
            return;

        foreach (var node in result.KnowledgeNodes)
        {
            var nodeId = await conn.ExecuteScalarAsync<Guid>(
                """
                INSERT INTO knowledge_graph_nodes
                    (tenant_id, memory_id, layer_id, node_type, subject, predicate, object,
                     confidence, source_layer, evidence_text, metadata)
                VALUES
                    (@TenantId, @MemoryId, @LayerId, @NodeType, @Subject, @Predicate, @Object,
                     @Confidence, @SourceLayer::memory_layer_type, @Evidence, @Metadata::jsonb)
                RETURNING id
                """,
                new
                {
                    TenantId = tenantId,
                    MemoryId = memoryId,
                    LayerId = layerId,
                    NodeType = node.NodeType,
                    Subject = node.Subject,
                    Predicate = node.Predicate,
                    Object = node.Object,
                    Confidence = node.Confidence,
                    SourceLayer = layer.ToString(),
                    Evidence = node.Evidence,
                    Metadata = node.Metadata
                });

            await eventWriter.EmitMemoryEventAsync(
                tenantId,
                memoryId,
                "KnowledgeNodeAdded",
                new
                {
                    node_id = nodeId,
                    node_type = node.NodeType,
                    subject = node.Subject,
                    predicate = node.Predicate,
                    @object = node.Object,
                    source_layer = layer.ToString()
                },
                _workerId,
                null,
                ct);
        }

        logger.LogDebug("Extracted {Count} knowledge nodes for memory {MemoryId}",
            result.KnowledgeNodes.Count, memoryId);
    }

    private async Task<NpgsqlConnection> OpenInternalConnectionAsync(CancellationToken ct)
    {
        var conn = await dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync("SELECT set_config('app.role', 'internal_admin', false)");
        return conn;
    }

    #region DTOs

    private sealed class QueueItem
    {
        public Guid queue_id { get; set; }
        public Guid memory_id { get; set; }
        public Guid tenant_id { get; set; }
        public string current_stage { get; set; } = "L0_RAW";
        public int retry_count { get; set; }
    }

    private sealed class MemoryRecord
    {
        public Guid id { get; set; }
        public string content { get; set; } = "";
        public string? metadata { get; set; }
        public DateTimeOffset created_at { get; set; }
    }

    private sealed class ConflictCandidate
    {
        public Guid neighbor_id { get; set; }
        public float similarity { get; set; }
    }

    #endregion
}
