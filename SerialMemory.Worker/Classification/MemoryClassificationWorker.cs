using System.Diagnostics;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using SerialMemory.Core.Interfaces;

namespace SerialMemory.Worker.Classification;

/// <summary>
/// Background worker that processes memories through L0-L4 classification layers.
/// Uses internal admin role for RLS bypass during processing.
/// </summary>
public sealed class MemoryClassificationWorker : BackgroundService
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly IClassificationService _classificationService;
    private readonly IEventWriter _eventWriter;
    private readonly ILogger<MemoryClassificationWorker> _logger;
    private readonly string _workerId;

    private const int BatchSize = 10;
    private const int PollingIntervalMs = 1000;
    private const int ErrorBackoffMs = 5000;
    private static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(5);

    public MemoryClassificationWorker(
        NpgsqlDataSource dataSource,
        IClassificationService classificationService,
        IEventWriter eventWriter,
        ILogger<MemoryClassificationWorker> logger)
    {
        _dataSource = dataSource;
        _classificationService = classificationService;
        _eventWriter = eventWriter;
        _logger = logger;
        _workerId = $"worker-{Environment.MachineName}-{Guid.CreateVersion7():N}";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Memory Classification Worker {WorkerId} starting", _workerId);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await ProcessBatchAsync(stoppingToken);

                if (processed == 0)
                {
                    // No work found, wait before polling again
                    await Task.Delay(PollingIntervalMs, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in classification worker loop");
                await Task.Delay(ErrorBackoffMs, stoppingToken);
            }
        }

        _logger.LogInformation("Memory Classification Worker {WorkerId} stopping", _workerId);
    }

    private async Task<int> ProcessBatchAsync(CancellationToken ct)
    {
        await using var conn = await OpenInternalConnectionAsync(ct);

        // Acquire a batch of memories to process
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

        _logger.LogDebug("Acquired {Count} memories for classification", batch.Count);

        foreach (var item in batch)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                await ProcessMemoryAsync(conn, item, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process memory {MemoryId}", item.memory_id);
                // Error already logged to DB in ProcessMemoryAsync
            }
        }

        return batch.Count;
    }

    private async Task ProcessMemoryAsync(NpgsqlConnection conn, QueueItem item, CancellationToken ct)
    {
        _logger.LogDebug("Processing memory {MemoryId} for tenant {TenantId}, current stage: {Stage}",
            item.memory_id, item.tenant_id, item.current_stage);

        // Set tenant context for this operation
        await conn.ExecuteAsync(
            "SELECT set_config('app.tenant_id', @TenantId, false)",
            new { TenantId = item.tenant_id.ToString() });

        // Load the memory content
        var memory = await conn.QueryFirstOrDefaultAsync<MemoryRecord>(
            "SELECT id, content, metadata, created_at FROM memories WHERE id = @Id",
            new { Id = item.memory_id });

        if (memory == null)
        {
            _logger.LogWarning("Memory {MemoryId} not found, removing from queue", item.memory_id);
            await conn.ExecuteAsync(
                "DELETE FROM memory_processing_queue WHERE memory_id = @MemoryId",
                new { MemoryId = item.memory_id });
            return;
        }

        // Process each layer in sequence
        var layers = new[]
        {
            MemoryLayer.L0_RAW,
            MemoryLayer.L1_CONTEXT,
            MemoryLayer.L2_SUMMARY,
            MemoryLayer.L3_KNOWLEDGE,
            MemoryLayer.L4_HEURISTIC
        };

        foreach (var layer in layers)
        {
            if (ct.IsCancellationRequested) break;

            // Skip layers already processed
            if (ShouldSkipLayer(item.current_stage, layer))
                continue;

            try
            {
                await ProcessLayerAsync(conn, item, memory, layer, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to classify {Layer} for memory {MemoryId}",
                    layer, item.memory_id);

                await conn.ExecuteAsync(
                    "SELECT fail_layer_classification(@MemoryId, @Layer::memory_layer_type, @Error)",
                    new
                    {
                        MemoryId = item.memory_id,
                        Layer = layer.ToString(),
                        Error = ex.Message
                    });

                // Stop processing this memory on error
                return;
            }
        }

        _logger.LogInformation("Completed classification for memory {MemoryId}", item.memory_id);
    }

    private async Task ProcessLayerAsync(
        NpgsqlConnection conn,
        QueueItem item,
        MemoryRecord memory,
        MemoryLayer layer,
        CancellationToken ct)
    {
        _logger.LogDebug("Processing {Layer} for memory {MemoryId}", layer, memory.id);

        var sw = Stopwatch.StartNew();

        // Log layer start event
        await conn.ExecuteAsync(
            """
            INSERT INTO memory_classification_events (memory_id, tenant_id, event_type, layer)
            VALUES (@MemoryId, @TenantId, 'LAYER_STARTED', @Layer::memory_layer_type)
            """,
            new { MemoryId = memory.id, TenantId = item.tenant_id, Layer = layer.ToString() });

        // Get previous layer content for context
        var previousLayerContent = await GetPreviousLayerContentAsync(conn, memory.id, layer);

        // Classify using the classification service
        var result = await _classificationService.ClassifyAsync(
            memory.content,
            layer,
            previousLayerContent,
            ct);

        sw.Stop();

        // Store the layer result
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

        // *** EMIT LayerGenerated EVENT ***
        var eventId = await _eventWriter.EmitLayerGeneratedAsync(
            item.tenant_id,
            memory.id,
            layer.ToString(),
            result.ContentJson ?? "{}",
            result.ModelName ?? "unknown",
            (int)sw.ElapsedMilliseconds,
            result.Confidence,
            _workerId,
            ct);

        // *** CREATE TIMELINE SNAPSHOT ***
        await _eventWriter.CreateTimelineSnapshotAsync(
            item.tenant_id,
            memory.id,
            eventId,
            "LayerGenerated",
            memory.content,
            layer.ToString(),
            result.Confidence,
            new { layer_id = layerId, model = result.ModelName },
            ct);

        // For L3/L4, extract knowledge graph nodes
        if (layer is MemoryLayer.L3_KNOWLEDGE or MemoryLayer.L4_HEURISTIC)
        {
            await ExtractKnowledgeNodesAsync(conn, memory.id, item.tenant_id, layerId, layer, result, ct);
        }

        _logger.LogDebug("Completed {Layer} for memory {MemoryId} in {ElapsedMs}ms (event: {EventId})",
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

            // *** EMIT KnowledgeNodeAdded EVENT ***
            await _eventWriter.EmitMemoryEventAsync(
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

        _logger.LogDebug("Extracted {Count} knowledge nodes for memory {MemoryId}",
            result.KnowledgeNodes.Count, memoryId);
    }

    private static bool ShouldSkipLayer(string currentStage, MemoryLayer targetLayer)
    {
        var stageOrder = new Dictionary<string, int>
        {
            ["L0_RAW"] = 0,
            ["L1_CONTEXT"] = 1,
            ["L2_SUMMARY"] = 2,
            ["L3_KNOWLEDGE"] = 3,
            ["L4_HEURISTIC"] = 4
        };

        var currentOrder = stageOrder.GetValueOrDefault(currentStage, -1);
        var targetOrder = stageOrder.GetValueOrDefault(targetLayer.ToString(), 0);

        // Skip if we've already processed this layer
        return targetOrder <= currentOrder && currentOrder >= 0;
    }

    /// <summary>
    /// Opens a connection with internal admin role for RLS bypass.
    /// </summary>
    private async Task<NpgsqlConnection> OpenInternalConnectionAsync(CancellationToken ct)
    {
        var conn = await _dataSource.OpenConnectionAsync(ct);
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

    #endregion
}
