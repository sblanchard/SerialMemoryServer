using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using SerialMemory.Core.Interfaces;
using SerialMemory.Infrastructure.Classification;
using MemoryLayerEnum = SerialMemory.Core.Interfaces.MemoryLayer;

namespace SerialMemory.Infrastructure.Backfill;

/// <summary>
/// Service for detecting and processing unprocessed historical memories.
/// Implements idempotent layer generation with comprehensive error handling.
/// </summary>
public sealed class MemoryBackfillService(
    NpgsqlDataSource dataSource,
    IClassificationService classificationService,
    IEventWriter eventWriter,
    IL2EmbeddingService l2EmbeddingService,
    ClassificationConfig dwellConfig,
    ILogger<MemoryBackfillService> logger)
    : IMemoryBackfillService
{
    private static readonly string[] AllLayers = ["L0_RAW", "L1_CONTEXT", "L2_SUMMARY", "L3_KNOWLEDGE", "L4_HEURISTIC"];

    /// <inheritdoc />
    public async Task<BackfillStatistics> GetBackfillStatisticsAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenInternalConnectionAsync(ct);

        var stats = await conn.QueryFirstAsync<dynamic>(
            """
            WITH layer_counts AS (
                SELECT
                    m.id AS memory_id,
                    m.tenant_id,
                    COUNT(DISTINCT ml.layer) AS layer_count,
                    BOOL_OR(ml.layer = 'L4_HEURISTIC') AS has_l4,
                    m.classification_status
                FROM memories m
                LEFT JOIN memory_layers ml ON ml.memory_id = m.id AND ml.is_current = TRUE
                GROUP BY m.id, m.tenant_id, m.classification_status
            )
            SELECT
                COUNT(*) AS total_memories,
                COUNT(*) FILTER (WHERE layer_count = 5 AND has_l4 = TRUE) AS fully_processed,
                COUNT(*) FILTER (WHERE layer_count > 0 AND layer_count < 5) AS partially_processed,
                COUNT(*) FILTER (WHERE layer_count = 0 OR layer_count IS NULL) AS unprocessed,
                COUNT(*) FILTER (WHERE classification_status = 'FAILED') AS failed,
                (SELECT COUNT(*) FROM memory_processing_queue WHERE status IN ('PENDING', 'PROCESSING')) AS queued
            FROM layer_counts
            """);

        // Get counts by layer
        var byLayer = (await conn.QueryAsync<dynamic>(
            """
            SELECT layer::text AS layer_name, COUNT(*) AS count
            FROM memory_layers
            WHERE is_current = TRUE
            GROUP BY layer
            """)).ToDictionary(x => (string)x.layer_name, x => (int)x.count);

        // Get counts by tenant
        var byTenant = (await conn.QueryAsync<dynamic>(
            """
            SELECT tenant_id, COUNT(*) FILTER (WHERE layer_count < 5) AS unprocessed_count
            FROM (
                SELECT m.tenant_id, COUNT(DISTINCT ml.layer) AS layer_count
                FROM memories m
                LEFT JOIN memory_layers ml ON ml.memory_id = m.id AND ml.is_current = TRUE
                GROUP BY m.id, m.tenant_id
            ) sub
            GROUP BY tenant_id
            HAVING COUNT(*) FILTER (WHERE layer_count < 5) > 0
            """)).ToDictionary(x => (Guid)x.tenant_id, x => (int)x.unprocessed_count);

        return new BackfillStatistics
        {
            TotalMemories = (int)(stats.total_memories ?? 0),
            FullyProcessedMemories = (int)(stats.fully_processed ?? 0),
            PartiallyProcessedMemories = (int)(stats.partially_processed ?? 0),
            UnprocessedMemories = (int)(stats.unprocessed ?? 0),
            FailedMemories = (int)(stats.failed ?? 0),
            QueuedMemories = (int)(stats.queued ?? 0),
            ByLayer = byLayer,
            ByTenant = byTenant
        };
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<UnprocessedMemory>> GetUnprocessedMemoriesAsync(
        int limit = 100,
        Guid? tenantId = null,
        CancellationToken ct = default)
    {
        await using var conn = await OpenInternalConnectionAsync(ct);

        var sql = """
            WITH memory_layer_status AS (
                SELECT
                    m.id AS memory_id,
                    m.tenant_id,
                    m.created_at,
                    m.classification_status,
                    COALESCE(q.retry_count, 0) AS retry_count,
                    q.last_error,
                    BOOL_OR(ml.layer = 'L0_RAW' AND ml.is_current) AS has_l0,
                    BOOL_OR(ml.layer = 'L1_CONTEXT' AND ml.is_current) AS has_l1,
                    BOOL_OR(ml.layer = 'L2_SUMMARY' AND ml.is_current) AS has_l2,
                    BOOL_OR(ml.layer = 'L3_KNOWLEDGE' AND ml.is_current) AS has_l3,
                    BOOL_OR(ml.layer = 'L4_HEURISTIC' AND ml.is_current) AS has_l4,
                    COALESCE(m.current_layer::text, 'L0_RAW') AS current_layer,
                    MAX(ml.created_at) FILTER (WHERE ml.is_current) AS highest_layer_at
                FROM memories m
                LEFT JOIN memory_layers ml ON ml.memory_id = m.id
                LEFT JOIN memory_processing_queue q ON q.memory_id = m.id
                WHERE (@TenantId IS NULL OR m.tenant_id = @TenantId)
                GROUP BY m.id, m.tenant_id, m.created_at, m.classification_status, m.current_layer, q.retry_count, q.last_error
            )
            SELECT *
            FROM memory_layer_status
            WHERE NOT (has_l0 AND has_l1 AND has_l2 AND has_l3 AND has_l4)
              AND classification_status IS DISTINCT FROM 'FAILED'
            ORDER BY created_at ASC
            LIMIT @Limit
            """;

        var results = await conn.QueryAsync<dynamic>(sql, new { TenantId = tenantId, Limit = limit });
        var now = DateTimeOffset.UtcNow;

        return results.Select(r =>
        {
            var missingLayers = new List<string>();
            if (!(r.has_l0 ?? false)) missingLayers.Add("L0_RAW");
            if (!(r.has_l1 ?? false)) missingLayers.Add("L1_CONTEXT");
            if (!(r.has_l2 ?? false)) missingLayers.Add("L2_SUMMARY");
            if (!(r.has_l3 ?? false)) missingLayers.Add("L3_KNOWLEDGE");
            if (!(r.has_l4 ?? false)) missingLayers.Add("L4_HEURISTIC");

            DateTimeOffset? highestLayerAt = r.highest_layer_at is DateTimeOffset dt ? dt : null;

            return new UnprocessedMemory
            {
                MemoryId = r.memory_id,
                TenantId = r.tenant_id,
                CurrentLayer = r.current_layer ?? "L0_RAW",
                MissingLayers = missingLayers.ToArray(),
                HasL0 = r.has_l0 ?? false,
                HasL1 = r.has_l1 ?? false,
                HasL2 = r.has_l2 ?? false,
                HasL3 = r.has_l3 ?? false,
                HasL4 = r.has_l4 ?? false,
                RetryCount = r.retry_count,
                LastError = r.last_error,
                CreatedAt = r.created_at,
                HighestLayerCreatedAt = highestLayerAt
            };
        })
        .Where(m => IsReadyForNextLayer(m, now))
        .ToList();
    }

    /// <summary>
    /// Determines if a memory is ready for its next layer based on dwell time.
    /// Memories with no layers yet are always ready. Memories created before the
    /// dwell cutoff date are exempt from dwell requirements.
    /// </summary>
    private bool IsReadyForNextLayer(UnprocessedMemory memory, DateTimeOffset now)
    {
        // No layers yet — always ready for L0
        if (memory.HighestLayerCreatedAt == null)
            return true;

        // Pre-cutoff memories skip dwell times
        if (memory.CreatedAt < dwellConfig.DwellCutoffDate)
            return true;

        // Find the highest existing layer to check its dwell time
        var highestLayer = GetHighestExistingLayer(memory);
        if (highestLayer == null)
            return true;

        var dwellRequired = dwellConfig.GetDwellTime(highestLayer);
        if (dwellRequired <= TimeSpan.Zero)
            return true;

        var elapsed = now - memory.HighestLayerCreatedAt.Value;
        return elapsed >= dwellRequired;
    }

    private static string? GetHighestExistingLayer(UnprocessedMemory memory)
    {
        if (memory.HasL4) return "L4_HEURISTIC";
        if (memory.HasL3) return "L3_KNOWLEDGE";
        if (memory.HasL2) return "L2_SUMMARY";
        if (memory.HasL1) return "L1_CONTEXT";
        if (memory.HasL0) return "L0_RAW";
        return null;
    }

    /// <inheritdoc />
    public async Task<int> EnqueueUnprocessedMemoriesAsync(
        int batchSize = 50,
        Guid? tenantId = null,
        int priority = 10,
        CancellationToken ct = default)
    {
        await using var conn = await OpenInternalConnectionAsync(ct);

        var enqueued = await conn.ExecuteAsync(
            """
            WITH unprocessed AS (
                SELECT m.id AS memory_id, m.tenant_id
                FROM memories m
                LEFT JOIN memory_layers ml ON ml.memory_id = m.id AND ml.layer = 'L4_HEURISTIC' AND ml.is_current = TRUE
                LEFT JOIN memory_processing_queue q ON q.memory_id = m.id
                WHERE ml.id IS NULL
                  AND (q.id IS NULL OR q.status = 'FAILED')
                  AND (@TenantId IS NULL OR m.tenant_id = @TenantId)
                ORDER BY m.created_at ASC
                LIMIT @BatchSize
            )
            INSERT INTO memory_processing_queue (memory_id, tenant_id, priority, status, current_stage)
            SELECT memory_id, tenant_id, @Priority, 'PENDING', 'L0_RAW'
            FROM unprocessed
            ON CONFLICT (memory_id) DO UPDATE
            SET status = 'PENDING',
                retry_count = 0,
                priority = GREATEST(memory_processing_queue.priority, EXCLUDED.priority),
                last_error = NULL
            WHERE memory_processing_queue.status IN ('FAILED', 'COMPLETE')
            """,
            new { TenantId = tenantId, BatchSize = batchSize, Priority = priority });

        logger.LogInformation("Enqueued {Count} unprocessed memories for classification (priority: {Priority})",
            enqueued, priority);

        return enqueued;
    }

    /// <inheritdoc />
    public async Task<LayerProcessingResult> ProcessMemoryWithIdempotencyAsync(
        Guid tenantId,
        Guid memoryId,
        int maxRetries = 3,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var layersGenerated = new List<string>();
        var layersSkipped = new List<string>();
        var layersFailed = new List<string>();
        var errors = new Dictionary<string, string>();

        logger.LogInformation("Processing memory {MemoryId} with idempotency (tenant: {TenantId})",
            memoryId, tenantId);

        await using var conn = await OpenInternalConnectionAsync(ct);
        await conn.ExecuteAsync(
            "SELECT set_config('app.tenant_id', @TenantId, false)",
            new { TenantId = tenantId.ToString() });

        // Get memory content
        var memory = await conn.QueryFirstOrDefaultAsync<MemoryRecord>(
            "SELECT id, content, metadata, created_at FROM memories WHERE id = @Id AND tenant_id = @TenantId",
            new { Id = memoryId, TenantId = tenantId });

        if (memory == null)
        {
            logger.LogWarning("Memory {MemoryId} not found for tenant {TenantId}", memoryId, tenantId);
            return new LayerProcessingResult
            {
                MemoryId = memoryId,
                Success = false,
                Errors = new Dictionary<string, string> { ["general"] = "Memory not found" }
            };
        }

        // Get existing layers
        var existingLayers = await GetExistingLayersAsync(conn, memoryId);

        // Process each layer in order with idempotency
        foreach (var layer in AllLayers)
        {
            if (ct.IsCancellationRequested)
                break;

            // Skip if layer already exists
            if (existingLayers.ContainsKey(layer))
            {
                layersSkipped.Add(layer);
                logger.LogDebug("Layer {Layer} already exists for memory {MemoryId}, skipping",
                    layer, memoryId);
                continue;
            }

            // Check dwell time and get previous layer content
            var prevLayer = GetPreviousLayer(layer);
            string? previousContent = null;
            if (prevLayer != null && existingLayers.TryGetValue(prevLayer, out var prevInfo))
            {
                // Check dwell time: is the previous layer old enough to advance?
                var dwellRequired = dwellConfig.GetDwellTime(prevLayer);
                var isDwellExempt = memory.created_at < dwellConfig.DwellCutoffDate;

                if (dwellRequired > TimeSpan.Zero && !isDwellExempt)
                {
                    var elapsed = DateTimeOffset.UtcNow - prevInfo.CreatedAt;
                    if (elapsed < dwellRequired)
                    {
                        var remaining = dwellRequired - elapsed;
                        logger.LogDebug(
                            "Memory {MemoryId} dwelling at {Layer} ({Elapsed:F1}h / {Required:F1}h, {Remaining:F1}h remaining)",
                            memoryId, prevLayer, elapsed.TotalHours, dwellRequired.TotalHours, remaining.TotalHours);
                        break; // Memory not ready for next layer yet
                    }
                }

                previousContent = await GetLayerContentAsync(conn, memoryId, prevLayer);
            }

            // Process layer with retries
            var success = false;
            for (var attempt = 1; attempt <= maxRetries && !success; attempt++)
            {
                try
                {
                    logger.LogDebug("Processing {Layer} for memory {MemoryId} (attempt {Attempt}/{MaxRetries})",
                        layer, memoryId, attempt, maxRetries);

                    await ProcessSingleLayerAsync(conn, tenantId, memoryId, memory.content, layer, previousContent, ct);

                    layersGenerated.Add(layer);
                    success = true;

                    // Update existingLayers for next iteration
                    existingLayers[layer] = new LayerInfo { Layer = layer };

                    // After L2, the embedding is indexed — run targeted contradiction check
                    if (layer == "L2_SUMMARY")
                    {
                        await RunTargetedConflictCheckAsync(conn, tenantId, memoryId, ct);
                    }

                    logger.LogInformation("Generated {Layer} for memory {MemoryId}", layer, memoryId);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to process {Layer} for memory {MemoryId} (attempt {Attempt}/{MaxRetries})",
                        layer, memoryId, attempt, maxRetries);

                    if (attempt == maxRetries)
                    {
                        layersFailed.Add(layer);
                        errors[layer] = ex.Message;
                    }
                    else
                    {
                        // Exponential backoff before retry
                        await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
                    }
                }
            }

            // If this layer failed, stop processing subsequent layers
            if (!success)
            {
                logger.LogWarning("Stopping pipeline for memory {MemoryId} due to failure at {Layer}",
                    memoryId, layer);
                break;
            }
        }

        sw.Stop();

        var isFullyProcessed = existingLayers.Count + layersGenerated.Count == 5 && layersFailed.Count == 0;

        // Update memory status
        await conn.ExecuteAsync(
            """
            UPDATE memories
            SET classification_status = @Status::memory_processing_status,
                classification_completed_at = CASE WHEN @IsComplete THEN NOW() ELSE NULL END
            WHERE id = @MemoryId
            """,
            new
            {
                MemoryId = memoryId,
                Status = isFullyProcessed ? "COMPLETE" : (layersFailed.Count > 0 ? "PARTIAL" : "PROCESSING"),
                IsComplete = isFullyProcessed
            });

        logger.LogInformation(
            "Completed processing memory {MemoryId}: generated={Generated}, skipped={Skipped}, failed={Failed}, duration={DurationMs}ms",
            memoryId, layersGenerated.Count, layersSkipped.Count, layersFailed.Count, sw.ElapsedMilliseconds);

        return new LayerProcessingResult
        {
            MemoryId = memoryId,
            Success = layersFailed.Count == 0,
            LayersGenerated = layersGenerated.ToArray(),
            LayersSkipped = layersSkipped.ToArray(),
            LayersFailed = layersFailed.ToArray(),
            Errors = errors,
            TotalDurationMs = (int)sw.ElapsedMilliseconds,
            IsFullyProcessed = isFullyProcessed
        };
    }

    /// <inheritdoc />
    public async Task<BackfillResult> RunFullBackfillAsync(
        int batchSize = 25,
        int maxConcurrency = 1,
        Guid? tenantId = null,
        CancellationToken ct = default)
    {
        var startTime = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        var processed = 0;
        var succeeded = 0;
        var partial = 0;
        var failed = 0;
        var skipped = 0;
        var totalLayers = 0;
        var allErrors = new List<string>();

        logger.LogInformation("Starting full backfill (batchSize={BatchSize}, maxConcurrency={MaxConcurrency}, tenant={TenantId})",
            batchSize, maxConcurrency, tenantId?.ToString() ?? "all");

        while (!ct.IsCancellationRequested)
        {
            // Get next batch of unprocessed memories
            var batch = await GetUnprocessedMemoriesAsync(batchSize, tenantId, ct);

            if (batch.Count == 0)
            {
                logger.LogInformation("No more unprocessed memories found, backfill complete");
                break;
            }

            logger.LogInformation("Processing batch of {Count} memories (total processed so far: {TotalProcessed})",
                batch.Count, processed);

            // Process each memory (serial for now, can be parallelized later)
            foreach (var memory in batch)
            {
                if (ct.IsCancellationRequested)
                    break;

                try
                {
                    var result = await ProcessMemoryWithIdempotencyAsync(
                        memory.TenantId,
                        memory.MemoryId,
                        maxRetries: 3,
                        ct);

                    processed++;
                    totalLayers += result.LayersGenerated.Length;

                    if (result.IsFullyProcessed)
                        succeeded++;
                    else if (result.LayersGenerated.Length > 0)
                        partial++;
                    else if (result.LayersFailed.Length > 0)
                        failed++;
                    else
                        skipped++;

                    foreach (var error in result.Errors)
                    {
                        allErrors.Add($"Memory {memory.MemoryId} {error.Key}: {error.Value}");
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Unexpected error processing memory {MemoryId}", memory.MemoryId);
                    failed++;
                    allErrors.Add($"Memory {memory.MemoryId}: {ex.Message}");
                }

                // Log progress every 10 memories
                if (processed % 10 == 0)
                {
                    logger.LogInformation(
                        "Backfill progress: processed={Processed}, succeeded={Succeeded}, partial={Partial}, failed={Failed}",
                        processed, succeeded, partial, failed);
                }
            }

            // Small delay between batches to avoid overloading
            await Task.Delay(100, ct);
        }

        sw.Stop();

        var result2 = new BackfillResult
        {
            TotalMemoriesProcessed = processed,
            SuccessfullyCompleted = succeeded,
            PartiallyCompleted = partial,
            Failed = failed,
            Skipped = skipped,
            TotalLayersGenerated = totalLayers,
            Duration = sw.Elapsed,
            Errors = allErrors,
            StartedAt = startTime,
            CompletedAt = DateTimeOffset.UtcNow
        };

        logger.LogInformation(
            "Backfill completed: processed={Processed}, succeeded={Succeeded}, partial={Partial}, failed={Failed}, layers={Layers}, duration={Duration}",
            result2.TotalMemoriesProcessed,
            result2.SuccessfullyCompleted,
            result2.PartiallyCompleted,
            result2.Failed,
            result2.TotalLayersGenerated,
            result2.Duration);

        return result2;
    }

    /// <inheritdoc />
    public async Task<bool> IsMemoryFullyProcessedAsync(Guid tenantId, Guid memoryId, CancellationToken ct = default)
    {
        await using var conn = await OpenInternalConnectionAsync(ct);

        var layerCount = await conn.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(DISTINCT layer)
            FROM memory_layers
            WHERE tenant_id = @TenantId AND memory_id = @MemoryId AND is_current = TRUE
            """,
            new { TenantId = tenantId, MemoryId = memoryId });

        return layerCount == 5;
    }

    /// <inheritdoc />
    public async Task<MemoryLayerStatus> GetMemoryLayerStatusAsync(Guid tenantId, Guid memoryId, CancellationToken ct = default)
    {
        await using var conn = await OpenInternalConnectionAsync(ct);

        var layers = await conn.QueryAsync<LayerInfo>(
            """
            SELECT id AS layer_id, layer::text AS layer, content_hash, model_name, confidence_score AS confidence,
                   processing_duration_ms AS duration_ms, created_at, is_current, version
            FROM memory_layers
            WHERE tenant_id = @TenantId AND memory_id = @MemoryId AND is_current = TRUE
            ORDER BY layer
            """,
            new { TenantId = tenantId, MemoryId = memoryId });

        var layerDict = layers.ToDictionary(l => l.Layer, l => (LayerInfo?)l);

        // Ensure all layers are represented
        foreach (var layer in AllLayers)
        {
            layerDict.TryAdd(layer, null);
        }

        var missingLayers = AllLayers.Where(l => layerDict[l] == null).ToArray();
        var highestLayer = AllLayers.Reverse().FirstOrDefault(l => layerDict[l] != null) ?? "NONE";

        return new MemoryLayerStatus
        {
            MemoryId = memoryId,
            TenantId = tenantId,
            Layers = layerDict,
            HighestLayer = highestLayer,
            IsComplete = missingLayers.Length == 0,
            MissingLayers = missingLayers
        };
    }

    /// <inheritdoc />
    public async Task<IntegrityCheckResult> VerifyMemoryIntegrityAsync(Guid tenantId, Guid memoryId, CancellationToken ct = default)
    {
        await using var conn = await OpenInternalConnectionAsync(ct);

        var layers = await conn.QueryAsync<dynamic>(
            """
            SELECT layer::text AS layer, content_json, content_hash
            FROM memory_layers
            WHERE tenant_id = @TenantId AND memory_id = @MemoryId AND is_current = TRUE
            """,
            new { TenantId = tenantId, MemoryId = memoryId });

        var issues = new List<string>();
        var layerHashValid = new Dictionary<string, bool>();

        foreach (var layer in layers)
        {
            var computedHash = ComputeHash(layer.content_json?.ToString() ?? "");
            var storedHash = layer.content_hash?.ToString() ?? "";
            var isValid = string.Equals(computedHash, storedHash, StringComparison.OrdinalIgnoreCase);

            layerHashValid[layer.layer] = isValid;

            if (!isValid)
            {
                issues.Add($"Hash mismatch for {layer.layer}: stored={storedHash}, computed={computedHash}");
            }
        }

        // Check for missing layers
        foreach (var layer in AllLayers)
        {
            if (!layerHashValid.ContainsKey(layer))
            {
                issues.Add($"Missing layer: {layer}");
            }
        }

        return new IntegrityCheckResult
        {
            MemoryId = memoryId,
            IsValid = issues.Count == 0,
            Issues = issues,
            LayerHashValid = layerHashValid
        };
    }

    /// <inheritdoc />
    public async Task<LayerProcessingResult> RepairMemoryAsync(
        Guid tenantId,
        Guid memoryId,
        bool forceRegenerate = false,
        CancellationToken ct = default)
    {
        if (forceRegenerate)
        {
            // Delete all current layers to force regeneration
            await using var conn = await OpenInternalConnectionAsync(ct);
            await conn.ExecuteAsync(
                """
                UPDATE memory_layers
                SET is_current = FALSE, superseded_by = NULL
                WHERE tenant_id = @TenantId AND memory_id = @MemoryId
                """,
                new { TenantId = tenantId, MemoryId = memoryId });

            await conn.ExecuteAsync(
                """
                DELETE FROM memory_processing_queue WHERE memory_id = @MemoryId
                """,
                new { MemoryId = memoryId });

            logger.LogInformation("Cleared all layers for memory {MemoryId} for forced regeneration", memoryId);
        }

        return await ProcessMemoryWithIdempotencyAsync(tenantId, memoryId, maxRetries: 3, ct);
    }

    #region Private Helpers

    private async Task ProcessSingleLayerAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        Guid memoryId,
        string content,
        string layerName,
        string? previousLayerContent,
        CancellationToken ct)
    {
        var layer = Enum.Parse<MemoryLayerEnum>(layerName);
        var sw = Stopwatch.StartNew();

        // Log layer start
        await conn.ExecuteAsync(
            """
            INSERT INTO memory_classification_events (memory_id, tenant_id, event_type, layer)
            VALUES (@MemoryId, @TenantId, 'LAYER_STARTED', @Layer::memory_layer_type)
            """,
            new { MemoryId = memoryId, TenantId = tenantId, Layer = layerName });

        // Classify using the classification service
        var result = await classificationService.ClassifyAsync(content, layer, previousLayerContent, ct);

        sw.Stop();

        // Store the layer result using the SQL function
        var layerId = await conn.ExecuteScalarAsync<Guid>(
            """
            SELECT complete_layer_classification(
                @MemoryId, @Layer::memory_layer_type, @ContentJson::jsonb,
                @ModelName, @DurationMs, @Confidence
            )
            """,
            new
            {
                MemoryId = memoryId,
                Layer = layerName,
                ContentJson = result.ContentJson,
                ModelName = result.ModelName,
                DurationMs = (int)sw.ElapsedMilliseconds,
                Confidence = result.Confidence
            });

        // Emit LayerGenerated event
        var eventId = await eventWriter.EmitLayerGeneratedAsync(
            tenantId,
            memoryId,
            layerName,
            result.ContentJson ?? "{}",
            result.ModelName ?? "unknown",
            (int)sw.ElapsedMilliseconds,
            (decimal)result.Confidence!,
            "backfill_worker",
            ct);

        // Create timeline snapshot
        await eventWriter.CreateTimelineSnapshotAsync(
            tenantId,
            memoryId,
            eventId,
            "LayerGenerated",
            content,
            layerName,
            (decimal)result.Confidence!,
            new { layer_id = layerId, model = result.ModelName },
            ct);

        // For L2, index the embedding
        if (layer == MemoryLayerEnum.L2_SUMMARY)
        {
            try
            {
                await l2EmbeddingService.IndexL2Async(tenantId, memoryId, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to index L2 embedding for memory {MemoryId}", memoryId);
            }
        }

        // For L3/L4, extract knowledge nodes
        if (layer is MemoryLayerEnum.L3_KNOWLEDGE or MemoryLayerEnum.L4_HEURISTIC && result.KnowledgeNodes?.Count > 0)
        {
            await ExtractKnowledgeNodesAsync(conn, memoryId, tenantId, layerId, layer, result, ct);
        }
    }

    private async Task ExtractKnowledgeNodesAsync(
        NpgsqlConnection conn,
        Guid memoryId,
        Guid tenantId,
        Guid layerId,
        MemoryLayerEnum layer,
        ClassificationResult result,
        CancellationToken ct)
    {
        if (result.KnowledgeNodes == null || result.KnowledgeNodes.Count == 0)
            return;

        foreach (var node in result.KnowledgeNodes)
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO knowledge_graph_nodes
                    (tenant_id, memory_id, layer_id, node_type, subject, predicate, object,
                     confidence, source_layer, evidence_text, metadata)
                VALUES
                    (@TenantId, @MemoryId, @LayerId, @NodeType, @Subject, @Predicate, @Object,
                     @Confidence, @SourceLayer::memory_layer_type, @Evidence, @Metadata::jsonb)
                ON CONFLICT DO NOTHING
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
                    Metadata = node.Metadata ?? "{}"
                });
        }

        logger.LogDebug("Extracted {Count} knowledge nodes for memory {MemoryId} layer {Layer}",
            result.KnowledgeNodes.Count, memoryId, layer);
    }

    /// <summary>
    /// Lightweight contradiction check for a single memory using pgvector KNN.
    /// Finds the top N semantically similar active memories and logs any new conflicts.
    /// </summary>
    private async Task RunTargetedConflictCheckAsync(
        NpgsqlConnection conn, Guid tenantId, Guid memoryId, CancellationToken ct)
    {
        const int maxNeighbors = 5;
        const float similarityThreshold = 0.85f;

        try
        {
            var conflicts = await conn.QueryAsync<(Guid neighbor_id, float similarity)>(
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

            foreach (var (neighborId, similarity) in conflicts)
            {
                var exists = await conn.ExecuteScalarAsync<bool>(
                    """
                    SELECT EXISTS(
                        SELECT 1 FROM conflict_log
                        WHERE tenant_id = @TenantId
                          AND ((memory_a_id = @MemoryAId AND memory_b_id = @MemoryBId)
                               OR (memory_a_id = @MemoryBId AND memory_b_id = @MemoryAId))
                    )
                    """,
                    new { TenantId = tenantId, MemoryAId = memoryId, MemoryBId = neighborId });

                if (exists)
                    continue;

                var conflictType = similarity >= 0.95f ? "duplicate" : "contradiction";
                var severity = similarity >= 0.95f ? 0.9f : Math.Min(similarity * 1.2f, 1.0f);

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
                        MemoryBId = neighborId,
                        Similarity = similarity,
                        ConflictType = conflictType,
                        Severity = severity,
                        Description = $"High semantic similarity ({similarity:P0}) detected during backfill classification"
                    });

                await eventWriter.EmitConflictDetectedAsync(
                    tenantId, memoryId, neighborId,
                    (decimal)similarity, conflictType,
                    $"Detected during backfill L2 classification (similarity: {similarity:P1})",
                    "backfill_worker", ct);

                logger.LogInformation(
                    "Conflict detected during backfill: memory {MemoryId} vs {NeighborId} (similarity: {Similarity:P1}, type: {Type})",
                    memoryId, neighborId, similarity, conflictType);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Targeted conflict check failed for memory {MemoryId} during backfill", memoryId);
        }
    }

    private async Task<Dictionary<string, LayerInfo>> GetExistingLayersAsync(NpgsqlConnection conn, Guid memoryId)
    {
        var layers = await conn.QueryAsync<LayerInfo>(
            """
            SELECT id AS layer_id, layer::text AS layer, content_hash, model_name,
                   confidence_score AS confidence, processing_duration_ms AS duration_ms,
                   created_at, is_current, version
            FROM memory_layers
            WHERE memory_id = @MemoryId AND is_current = TRUE
            """,
            new { MemoryId = memoryId });

        return layers.ToDictionary(l => l.Layer, l => l);
    }

    private async Task<string?> GetLayerContentAsync(NpgsqlConnection conn, Guid memoryId, string layer)
    {
        return await conn.ExecuteScalarAsync<string>(
            """
            SELECT content_json::text FROM memory_layers
            WHERE memory_id = @MemoryId AND layer = @Layer::memory_layer_type AND is_current = TRUE
            """,
            new { MemoryId = memoryId, Layer = layer });
    }

    private static string? GetPreviousLayer(string layer)
    {
        return layer switch
        {
            "L1_CONTEXT" => "L0_RAW",
            "L2_SUMMARY" => "L1_CONTEXT",
            "L3_KNOWLEDGE" => "L2_SUMMARY",
            "L4_HEURISTIC" => "L3_KNOWLEDGE",
            _ => null
        };
    }

    private static string ComputeHash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private async Task<NpgsqlConnection> OpenInternalConnectionAsync(CancellationToken ct)
    {
        var conn = await dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync("SELECT set_config('app.role', 'internal_admin', false)");
        return conn;
    }

    private sealed class MemoryRecord
    {
        public Guid id { get; set; }
        public string content { get; set; } = "";
        public string? metadata { get; set; }
        public DateTimeOffset created_at { get; set; }
    }

    #endregion
}
