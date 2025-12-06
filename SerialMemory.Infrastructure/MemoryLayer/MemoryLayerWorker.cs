using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using SerialMemory.Core.Interfaces;

namespace SerialMemory.Infrastructure.MemoryLayer;

/// <summary>
/// Background worker that processes memory layer transitions.
/// Orchestrates the promotion of memories through cognitive layers:
/// L0_RAW → L1_CONTEXT → L2_SUMMARY → L3_KNOWLEDGE → L4_HEURISTIC
/// </summary>
public sealed class MemoryLayerWorker : BackgroundService
{
    private readonly ILogger<MemoryLayerWorker> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly string _connectionString;
    private readonly MemoryLayerWorkerConfig _config;
    private readonly TimeSpan _processingInterval;
    private readonly TimeSpan _queueCheckInterval;

    public MemoryLayerWorker(
        IConfiguration configuration,
        ILogger<MemoryLayerWorker> logger,
        IServiceProvider serviceProvider)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _connectionString = configuration.GetConnectionString("Postgres")
            ?? BuildConnectionString();

        // ILiveEventEmitter is obtained via _serviceProvider when needed (scoped service)

        _config = new MemoryLayerWorkerConfig
        {
            Enabled = configuration.GetValue("MemoryLayerWorker:Enabled", true),
            BatchSize = configuration.GetValue("MemoryLayerWorker:BatchSize", 50),
            MaxConcurrentTransitions = configuration.GetValue("MemoryLayerWorker:MaxConcurrentTransitions", 5),
            ProcessingIntervalSeconds = configuration.GetValue("MemoryLayerWorker:ProcessingIntervalSeconds", 60),
            QueueCheckIntervalSeconds = configuration.GetValue("MemoryLayerWorker:QueueCheckIntervalSeconds", 10),
            StaleTransitionMinutes = configuration.GetValue("MemoryLayerWorker:StaleTransitionMinutes", 30),
            EnableL0ToL1 = configuration.GetValue("MemoryLayerWorker:L0ToL1:Enabled", true),
            EnableL1ToL2 = configuration.GetValue("MemoryLayerWorker:L1ToL2:Enabled", true),
            EnableL2ToL3 = configuration.GetValue("MemoryLayerWorker:L2ToL3:Enabled", true),
            EnableL3ToL4 = configuration.GetValue("MemoryLayerWorker:L3ToL4:Enabled", false) // Heuristic learning disabled by default
        };

        _processingInterval = TimeSpan.FromSeconds(_config.ProcessingIntervalSeconds);
        _queueCheckInterval = TimeSpan.FromSeconds(_config.QueueCheckIntervalSeconds);
    }

    private static string BuildConnectionString()
        => SerialMemory.Infrastructure.Configuration.ConnectionStringFactory.BuildConnectionString();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.Enabled)
        {
            _logger.LogInformation("MemoryLayerWorker is disabled");
            return;
        }

        _logger.LogInformation("MemoryLayerWorker starting with config: {@Config}", _config);

        // Wait for application startup
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        var lastScanTime = DateTime.MinValue;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Process pending queue items
                await ProcessTransitionQueueAsync(stoppingToken);

                // Periodically scan for new promotion candidates
                if (DateTime.UtcNow - lastScanTime > _processingInterval)
                {
                    await ScanForPromotionCandidatesAsync(stoppingToken);
                    lastScanTime = DateTime.UtcNow;
                }

                // Cleanup stale transitions
                await CleanupStaleTransitionsAsync(stoppingToken);

                await Task.Delay(_queueCheckInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in MemoryLayerWorker processing loop");
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }

        _logger.LogInformation("MemoryLayerWorker stopped");
    }

    /// <summary>
    /// Scans all tenants for memories eligible for layer promotion.
    /// </summary>
    private async Task ScanForPromotionCandidatesAsync(CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        // Set internal admin role to bypass RLS for cross-tenant access
        await conn.ExecuteAsync("SET app.role = 'internal_admin'");

        // Get active tenants
        var tenants = await conn.QueryAsync<Guid>("""
            SELECT DISTINCT id FROM tenants
            WHERE status = 'active'
            ORDER BY id
            LIMIT 100
            """);

        foreach (var tenantId in tenants)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                await ScanTenantForCandidatesAsync(conn, tenantId, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error scanning tenant {TenantId} for promotion candidates", tenantId);
            }
        }
    }

    private async Task ScanTenantForCandidatesAsync(NpgsqlConnection conn, Guid tenantId, CancellationToken ct)
    {
        // L0 → L1: Context enrichment
        if (_config.EnableL0ToL1)
        {
            var l0Candidates = await FindL0ToL1CandidatesAsync(conn, tenantId, ct);
            foreach (var candidate in l0Candidates)
            {
                await QueueTransitionAsync(conn, candidate, ct);
            }
        }

        // L1 → L2: Summarization (handled by MemorySummarizationService)
        if (_config.EnableL1ToL2)
        {
            // L1→L2 is cluster-based, handled separately
            await QueueL1ToL2SummarizationAsync(conn, tenantId, ct);
        }

        // L2 → L3: Knowledge extraction
        if (_config.EnableL2ToL3)
        {
            var l2Candidates = await FindL2ToL3CandidatesAsync(conn, tenantId, ct);
            foreach (var candidate in l2Candidates)
            {
                await QueueTransitionAsync(conn, candidate, ct);
            }
        }

        // L3 → L4: Heuristic learning
        if (_config.EnableL3ToL4)
        {
            var l3Candidates = await FindL3ToL4CandidatesAsync(conn, tenantId, ct);
            foreach (var candidate in l3Candidates)
            {
                await QueueTransitionAsync(conn, candidate, ct);
            }
        }
    }

    /// <summary>
    /// Processes pending transitions from the queue.
    /// </summary>
    private async Task ProcessTransitionQueueAsync(CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        // Set internal admin role to bypass RLS for cross-tenant access
        await conn.ExecuteAsync("SET app.role = 'internal_admin'");

        // Claim pending transitions
        var transitions = await ClaimPendingTransitionsAsync(conn, _config.MaxConcurrentTransitions, ct);

        foreach (var transition in transitions)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                await ProcessTransitionAsync(conn, transition, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing transition {TransitionId}", transition.Id);
                await MarkTransitionFailedAsync(conn, transition.Id, ex.Message, ct);
            }
        }
    }

    private async Task ProcessTransitionAsync(NpgsqlConnection conn, TransitionQueueItem transition, CancellationToken ct)
    {
        _logger.LogDebug(
            "Processing transition {TransitionId}: {FromLayer} → {ToLayer} for memory {MemoryId}",
            transition.Id, transition.FromLayer, transition.ToLayer, transition.MemoryId);

        var success = transition.ToLayer switch
        {
            "L1_CONTEXT" => await ProcessL0ToL1TransitionAsync(conn, transition, ct),
            "L2_SUMMARY" => await ProcessL1ToL2TransitionAsync(conn, transition, ct),
            "L3_KNOWLEDGE" => await ProcessL2ToL3TransitionAsync(conn, transition, ct),
            "L4_HEURISTIC" => await ProcessL3ToL4TransitionAsync(conn, transition, ct),
            _ => throw new InvalidOperationException($"Unknown target layer: {transition.ToLayer}")
        };

        if (success)
        {
            await MarkTransitionCompletedAsync(conn, transition.Id, ct);
            _logger.LogInformation(
                "Completed transition {TransitionId}: {FromLayer} → {ToLayer}",
                transition.Id, transition.FromLayer, transition.ToLayer);

            // Broadcast via SignalR
            await BroadcastTransitionAsync(transition, "completed", null);
        }
        else
        {
            await MarkTransitionFailedAsync(conn, transition.Id, "Transition processing returned false", ct);

            // Broadcast failure via SignalR
            await BroadcastTransitionAsync(transition, "failed", "Transition processing returned false");
        }
    }

    /// <summary>
    /// L0 → L1: Context enrichment - uses LLM to extract entities, context, and meaning.
    /// </summary>
    private async Task<bool> ProcessL0ToL1TransitionAsync(
        NpgsqlConnection conn,
        TransitionQueueItem transition,
        CancellationToken ct)
    {
        // Get the raw memory content
        var memory = await conn.QueryFirstOrDefaultAsync<MemoryContent>("""
            SELECT content, metadata
            FROM memories
            WHERE id = @MemoryId::uuid
              AND tenant_id = @TenantId::uuid
              AND layer = 'L0_RAW'
            """, new { transition.MemoryId, transition.TenantId });

        if (memory == null || string.IsNullOrWhiteSpace(memory.Content))
            return false;

        // Get cognitive service from DI
        var cognitiveService = _serviceProvider.GetService<CognitiveLayerService>();
        if (cognitiveService == null)
        {
            _logger.LogWarning("CognitiveLayerService not available, skipping L0→L1 enrichment");
            return false;
        }

        // Process through LLM
        var result = await cognitiveService.EnrichToL1Async(memory.Content, ct);

        // Serialize the enrichment result
        var enrichmentJson = System.Text.Json.JsonSerializer.Serialize(result);

        // Update memory with enriched content
        var updated = await conn.ExecuteAsync("""
            UPDATE memories
            SET layer = 'L1_CONTEXT',
                metadata = COALESCE(metadata, '{}'::jsonb) ||
                    jsonb_build_object(
                        'promoted_at', NOW()::text,
                        'promoted_from', 'L0_RAW',
                        'l1_enrichment', @Enrichment::jsonb,
                        'topic', @Topic,
                        'entity_count', @EntityCount
                    ),
                updated_at = NOW()
            WHERE id = @MemoryId::uuid
              AND tenant_id = @TenantId::uuid
              AND layer = 'L0_RAW'
            """, new
        {
            transition.MemoryId,
            transition.TenantId,
            Enrichment = enrichmentJson,
            Topic = result.Topic ?? "unknown",
            EntityCount = result.Entities?.Count ?? 0
        });

        _logger.LogInformation(
            "L0→L1 enrichment completed for {MemoryId}: {EntityCount} entities, topic: {Topic}",
            transition.MemoryId, result.Entities?.Count ?? 0, result.Topic);

        return updated > 0;
    }

    /// <summary>
    /// L1 → L2: Summarization - uses LLM to create concise summary from contextualized memory.
    /// </summary>
    private async Task<bool> ProcessL1ToL2TransitionAsync(
        NpgsqlConnection conn,
        TransitionQueueItem transition,
        CancellationToken ct)
    {
        // Get the contextualized memory
        var memory = await conn.QueryFirstOrDefaultAsync<MemoryContent>("""
            SELECT content, metadata
            FROM memories
            WHERE id = @MemoryId::uuid
              AND tenant_id = @TenantId::uuid
              AND layer = 'L1_CONTEXT'
            """, new { transition.MemoryId, transition.TenantId });

        if (memory == null || string.IsNullOrWhiteSpace(memory.Content))
            return false;

        // Get cognitive service from DI
        var cognitiveService = _serviceProvider.GetService<CognitiveLayerService>();
        if (cognitiveService == null)
        {
            _logger.LogWarning("CognitiveLayerService not available, skipping L1→L2 summarization");
            return false;
        }

        // Get related memories for context (same tenant, similar time frame)
        var relatedMemories = await conn.QueryAsync<string>("""
            SELECT content FROM memories
            WHERE tenant_id = @TenantId::uuid
              AND layer = 'L1_CONTEXT'
              AND id != @MemoryId::uuid
              AND created_at > NOW() - INTERVAL '7 days'
            ORDER BY created_at DESC
            LIMIT 3
            """, new { transition.TenantId, transition.MemoryId });

        // Process through LLM
        var result = await cognitiveService.SummarizeToL2Async(
            memory.Content,
            relatedMemories.ToList(),
            ct);

        // Serialize the summary result
        var summaryJson = System.Text.Json.JsonSerializer.Serialize(result);

        // Update memory with summary
        var updated = await conn.ExecuteAsync("""
            UPDATE memories
            SET layer = 'L2_SUMMARY',
                metadata = COALESCE(metadata, '{}'::jsonb) ||
                    jsonb_build_object(
                        'promoted_at', NOW()::text,
                        'promoted_from', 'L1_CONTEXT',
                        'l2_summary', @Summary::jsonb,
                        'category', @Category,
                        'importance', @Importance
                    ),
                updated_at = NOW()
            WHERE id = @MemoryId::uuid
              AND tenant_id = @TenantId::uuid
              AND layer = 'L1_CONTEXT'
            """, new
        {
            transition.MemoryId,
            transition.TenantId,
            Summary = summaryJson,
            Category = result.Category ?? "OTHER",
            Importance = result.Importance
        });

        _logger.LogInformation(
            "L1→L2 summarization completed for {MemoryId}: category {Category}, importance {Importance}",
            transition.MemoryId, result.Category, result.Importance);

        return updated > 0;
    }

    /// <summary>
    /// L2 → L3: Knowledge extraction - uses LLM to extract generalizable knowledge.
    /// </summary>
    private async Task<bool> ProcessL2ToL3TransitionAsync(
        NpgsqlConnection conn,
        TransitionQueueItem transition,
        CancellationToken ct)
    {
        // Get the summarized memory
        var memory = await conn.QueryFirstOrDefaultAsync<MemoryContent>("""
            SELECT content, metadata
            FROM memories
            WHERE id = @MemoryId::uuid
              AND tenant_id = @TenantId::uuid
              AND layer = 'L2_SUMMARY'
            """, new { transition.MemoryId, transition.TenantId });

        if (memory == null || string.IsNullOrWhiteSpace(memory.Content))
            return false;

        // Get cognitive service from DI
        var cognitiveService = _serviceProvider.GetService<CognitiveLayerService>();
        if (cognitiveService == null)
        {
            _logger.LogWarning("CognitiveLayerService not available, skipping L2→L3 knowledge extraction");
            return false;
        }

        // Get related summaries for cross-referencing
        var relatedSummaries = await conn.QueryAsync<string>("""
            SELECT content FROM memories
            WHERE tenant_id = @TenantId::uuid
              AND layer = 'L2_SUMMARY'
              AND id != @MemoryId::uuid
              AND created_at > NOW() - INTERVAL '30 days'
            ORDER BY created_at DESC
            LIMIT 5
            """, new { transition.TenantId, transition.MemoryId });

        // Process through LLM
        var result = await cognitiveService.ExtractKnowledgeToL3Async(
            memory.Content,
            relatedSummaries.ToList(),
            ct);

        // Serialize the knowledge result
        var knowledgeJson = System.Text.Json.JsonSerializer.Serialize(result);

        // Update memory with extracted knowledge
        var updated = await conn.ExecuteAsync("""
            UPDATE memories
            SET layer = 'L3_KNOWLEDGE',
                metadata = COALESCE(metadata, '{}'::jsonb) ||
                    jsonb_build_object(
                        'promoted_at', NOW()::text,
                        'promoted_from', 'L2_SUMMARY',
                        'l3_knowledge', @Knowledge::jsonb,
                        'fact_count', @FactCount,
                        'rule_count', @RuleCount,
                        'domains', @Domains::jsonb
                    ),
                updated_at = NOW()
            WHERE id = @MemoryId::uuid
              AND tenant_id = @TenantId::uuid
              AND layer = 'L2_SUMMARY'
            """, new
        {
            transition.MemoryId,
            transition.TenantId,
            Knowledge = knowledgeJson,
            FactCount = result.Facts?.Count ?? 0,
            RuleCount = result.Rules?.Count ?? 0,
            Domains = System.Text.Json.JsonSerializer.Serialize(result.Domains ?? new List<string>())
        });

        _logger.LogInformation(
            "L2→L3 knowledge extraction completed for {MemoryId}: {FactCount} facts, {RuleCount} rules",
            transition.MemoryId, result.Facts?.Count ?? 0, result.Rules?.Count ?? 0);

        return updated > 0;
    }

    /// <summary>
    /// L3 → L4: Heuristic learning - identifies patterns that can inform future behavior.
    /// </summary>
    private async Task<bool> ProcessL3ToL4TransitionAsync(
        NpgsqlConnection conn,
        TransitionQueueItem transition,
        CancellationToken ct)
    {
        // Get the knowledge memory
        var memory = await conn.QueryFirstOrDefaultAsync<MemoryContent>("""
            SELECT content, metadata
            FROM memories
            WHERE id = @MemoryId::uuid
              AND tenant_id = @TenantId::uuid
              AND layer = 'L3_KNOWLEDGE'
            """, new { transition.MemoryId, transition.TenantId });

        if (memory == null) return false;

        // Create a learned heuristic entry
        var heuristicId = Guid.CreateVersion7();

        await conn.ExecuteAsync("""
            INSERT INTO learned_heuristics (
                id, tenant_id, source_memory_id, pattern_type, pattern_description,
                confidence, supporting_evidence_count, metadata, created_at, updated_at
            )
            VALUES (
                @Id, @TenantId::uuid, @MemoryId::uuid, 'knowledge_pattern', @Content,
                0.7, 1, '{}'::jsonb, NOW(), NOW()
            )
            ON CONFLICT DO NOTHING
            """, new
        {
            Id = heuristicId,
            transition.TenantId,
            transition.MemoryId,
            memory.Content
        });

        // Update memory layer
        await conn.ExecuteAsync("""
            UPDATE memories
            SET layer = 'L4_HEURISTIC',
                metadata = COALESCE(metadata, '{}'::jsonb) ||
                    jsonb_build_object(
                        'promoted_at', NOW()::text,
                        'promoted_from', 'L3_KNOWLEDGE',
                        'heuristic_id', @HeuristicId::text
                    ),
                updated_at = NOW()
            WHERE id = @MemoryId::uuid
              AND tenant_id = @TenantId::uuid
            """, new { transition.MemoryId, transition.TenantId, HeuristicId = heuristicId });

        return true;
    }

    #region Candidate Finding

    private async Task<List<TransitionCandidate>> FindL0ToL1CandidatesAsync(
        NpgsqlConnection conn, Guid tenantId, CancellationToken ct)
    {
        var candidates = await conn.QueryAsync<TransitionCandidate>("""
            SELECT
                m.id AS MemoryId,
                m.tenant_id AS TenantId,
                'L0_RAW' AS FromLayer,
                'L1_CONTEXT' AS ToLayer,
                COALESCE(m.access_count, 0) + EXTRACT(EPOCH FROM NOW() - m.created_at) / 3600 AS Score
            FROM memories m
            WHERE m.tenant_id = @TenantId::uuid
              AND m.layer = 'L0_RAW'
              AND m.created_at < NOW() - INTERVAL '5 minutes'
              AND COALESCE(m.access_count, 0) >= 0
              AND NOT EXISTS (
                  SELECT 1 FROM layer_transition_queue q
                  WHERE q.memory_id = m.id
                    AND q.to_layer = 'L1_CONTEXT'
                    AND q.status IN ('pending', 'processing')
              )
            ORDER BY Score DESC
            LIMIT @Limit
            """, new { TenantId = tenantId, Limit = _config.BatchSize });

        return candidates.ToList();
    }

    private async Task QueueL1ToL2SummarizationAsync(
        NpgsqlConnection conn, Guid tenantId, CancellationToken ct)
    {
        // Queue cluster-based summarization batches
        // This triggers the MemorySummarizationService to process clusters

        // Check if there are enough L1 memories for summarization
        var l1Count = await conn.QueryFirstAsync<int>("""
            SELECT COUNT(*)
            FROM memories
            WHERE tenant_id = @TenantId::uuid
              AND layer = 'L1_CONTEXT'
              AND NOT EXISTS (
                  SELECT 1 FROM layer_transition_queue q
                  WHERE q.memory_id = memories.id
                    AND q.to_layer = 'L2_SUMMARY'
                    AND q.status IN ('pending', 'processing', 'completed')
              )
            """, new { TenantId = tenantId });

        if (l1Count >= 3) // Minimum cluster size
        {
            // Queue summarization work
            await conn.ExecuteAsync("""
                INSERT INTO layer_transition_queue (
                    id, tenant_id, memory_id, from_layer, to_layer, priority, status, scheduled_at, metadata
                )
                SELECT
                    gen_random_uuid(),
                    @TenantId::uuid,
                    m.id,
                    'L1_CONTEXT',
                    'L2_SUMMARY',
                    5,
                    'pending',
                    NOW(),
                    jsonb_build_object('batch_type', 'summarization')
                FROM memories m
                WHERE m.tenant_id = @TenantId::uuid
                  AND m.layer = 'L1_CONTEXT'
                  AND NOT EXISTS (
                      SELECT 1 FROM layer_transition_queue q
                      WHERE q.memory_id = m.id
                        AND q.to_layer = 'L2_SUMMARY'
                        AND q.status IN ('pending', 'processing', 'completed')
                  )
                LIMIT @Limit
                ON CONFLICT DO NOTHING
                """, new { TenantId = tenantId, Limit = _config.BatchSize });
        }
    }

    private async Task<List<TransitionCandidate>> FindL2ToL3CandidatesAsync(
        NpgsqlConnection conn, Guid tenantId, CancellationToken ct)
    {
        var candidates = await conn.QueryAsync<TransitionCandidate>("""
            SELECT
                m.id AS MemoryId,
                m.tenant_id AS TenantId,
                'L2_SUMMARY' AS FromLayer,
                'L3_KNOWLEDGE' AS ToLayer,
                COALESCE(m.access_count, 0) * COALESCE((m.metadata->>'confidence')::float, 0.5) + 1 AS Score
            FROM memories m
            WHERE m.tenant_id = @TenantId::uuid
              AND m.layer = 'L2_SUMMARY'
              AND COALESCE(m.access_count, 0) >= 0
              AND COALESCE((m.metadata->>'confidence')::float, 0.5) >= 0.3
              AND NOT EXISTS (
                  SELECT 1 FROM layer_transition_queue q
                  WHERE q.memory_id = m.id
                    AND q.to_layer = 'L3_KNOWLEDGE'
                    AND q.status IN ('pending', 'processing')
              )
            ORDER BY Score DESC
            LIMIT @Limit
            """, new { TenantId = tenantId, Limit = _config.BatchSize });

        return candidates.ToList();
    }

    private async Task<List<TransitionCandidate>> FindL3ToL4CandidatesAsync(
        NpgsqlConnection conn, Guid tenantId, CancellationToken ct)
    {
        var candidates = await conn.QueryAsync<TransitionCandidate>("""
            WITH supporting_facts AS (
                SELECT
                    m.id,
                    COUNT(DISTINCT me.entity_id) AS entity_count
                FROM memories m
                LEFT JOIN memory_entities me ON m.id = me.memory_id
                WHERE m.tenant_id = @TenantId::uuid
                  AND m.layer = 'L3_KNOWLEDGE'
                GROUP BY m.id
            )
            SELECT
                m.id AS MemoryId,
                m.tenant_id AS TenantId,
                'L3_KNOWLEDGE' AS FromLayer,
                'L4_HEURISTIC' AS ToLayer,
                sf.entity_count * COALESCE((m.metadata->>'confidence')::float, 0.5) AS Score
            FROM memories m
            JOIN supporting_facts sf ON m.id = sf.id
            WHERE m.tenant_id = @TenantId::uuid
              AND m.layer = 'L3_KNOWLEDGE'
              AND sf.entity_count >= 5
              AND COALESCE((m.metadata->>'confidence')::float, 0) >= 0.8
              AND NOT EXISTS (
                  SELECT 1 FROM layer_transition_queue q
                  WHERE q.memory_id = m.id
                    AND q.to_layer = 'L4_HEURISTIC'
                    AND q.status IN ('pending', 'processing')
              )
            ORDER BY Score DESC
            LIMIT @Limit
            """, new { TenantId = tenantId, Limit = _config.BatchSize });

        return candidates.ToList();
    }

    #endregion

    #region Queue Operations

    private async Task QueueTransitionAsync(
        NpgsqlConnection conn, TransitionCandidate candidate, CancellationToken ct)
    {
        await conn.ExecuteAsync("""
            INSERT INTO layer_transition_queue (
                id, tenant_id, memory_id, from_layer, to_layer, priority, status, scheduled_at
            )
            VALUES (
                @Id, @TenantId::uuid, @MemoryId::uuid, @FromLayer, @ToLayer, @Priority, 'pending', NOW()
            )
            ON CONFLICT (memory_id, to_layer) WHERE status IN ('pending', 'processing')
            DO NOTHING
            """, new
        {
            Id = Guid.CreateVersion7(),
            candidate.TenantId,
            candidate.MemoryId,
            candidate.FromLayer,
            candidate.ToLayer,
            Priority = (int)Math.Max(1, Math.Min(10, 10 - candidate.Score))
        });
    }

    private async Task<List<TransitionQueueItem>> ClaimPendingTransitionsAsync(
        NpgsqlConnection conn, int limit, CancellationToken ct)
    {
        var transitions = await conn.QueryAsync<TransitionQueueItem>("""
            WITH claimed AS (
                UPDATE layer_transition_queue
                SET status = 'processing',
                    started_at = NOW(),
                    updated_at = NOW()
                WHERE id IN (
                    SELECT id FROM layer_transition_queue
                    WHERE status = 'pending'
                      AND (scheduled_at IS NULL OR scheduled_at <= NOW())
                    ORDER BY priority, scheduled_at
                    LIMIT @Limit
                    FOR UPDATE SKIP LOCKED
                )
                RETURNING id, tenant_id AS TenantId, memory_id AS MemoryId,
                          from_layer AS FromLayer, to_layer AS ToLayer, priority
            )
            SELECT * FROM claimed
            """, new { Limit = limit });

        return transitions.ToList();
    }

    private async Task MarkTransitionCompletedAsync(NpgsqlConnection conn, Guid transitionId, CancellationToken ct)
    {
        await conn.ExecuteAsync("""
            UPDATE layer_transition_queue
            SET status = 'completed',
                completed_at = NOW(),
                updated_at = NOW()
            WHERE id = @Id::uuid
            """, new { Id = transitionId });
    }

    private async Task MarkTransitionFailedAsync(NpgsqlConnection conn, Guid transitionId, string error, CancellationToken ct)
    {
        await conn.ExecuteAsync("""
            UPDATE layer_transition_queue
            SET status = 'failed',
                error_message = @Error,
                retry_count = COALESCE(retry_count, 0) + 1,
                completed_at = NOW(),
                updated_at = NOW()
            WHERE id = @Id::uuid
            """, new { Id = transitionId, Error = error });
    }

    /// <summary>
    /// Broadcasts a layer transition event via SignalR.
    /// </summary>
    private async Task BroadcastTransitionAsync(TransitionQueueItem transition, string status, string? errorMessage)
    {
        try
        {
            var emitter = _serviceProvider.GetService<ILiveEventEmitter>();
            if (emitter == null) return;

            await emitter.EmitLayerTransitionAsync(new LayerTransitionBroadcast
            {
                TransitionId = transition.Id,
                TenantId = transition.TenantId.ToString(),
                MemoryId = transition.MemoryId,
                FromLayer = transition.FromLayer,
                ToLayer = transition.ToLayer,
                Status = status,
                ErrorMessage = errorMessage,
                Timestamp = DateTimeOffset.UtcNow
            });

            // Also broadcast updated stats after each batch of transitions
            await BroadcastLayerStatsAsync(transition.TenantId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to broadcast layer transition event");
        }
    }

    /// <summary>
    /// Broadcasts updated layer statistics for a tenant.
    /// </summary>
    private async Task BroadcastLayerStatsAsync(Guid tenantId)
    {
        try
        {
            var emitter = _serviceProvider.GetService<ILiveEventEmitter>();
            if (emitter == null) return;

            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            var stats = await conn.QueryAsync<(string Layer, int Count)>("""
                SELECT COALESCE(layer, 'L0_RAW') as layer, COUNT(*)::int as count
                FROM memories
                WHERE tenant_id = @TenantId::uuid
                GROUP BY layer
                """, new { TenantId = tenantId });

            var layerCounts = stats.ToDictionary(s => s.Layer, s => s.Count);

            var queueStats = await conn.QueryFirstOrDefaultAsync<(int Pending, int Processing, int CompletedHour)>("""
                SELECT
                    COUNT(*) FILTER (WHERE status = 'pending')::int as pending,
                    COUNT(*) FILTER (WHERE status = 'processing')::int as processing,
                    COUNT(*) FILTER (WHERE status = 'completed' AND completed_at > NOW() - INTERVAL '1 hour')::int as completed_hour
                FROM layer_transition_queue
                WHERE tenant_id = @TenantId::uuid
                """, new { TenantId = tenantId });

            await emitter.EmitLayerStatsUpdateAsync(new LayerStatsUpdateBroadcast
            {
                TenantId = tenantId.ToString(),
                L0Count = layerCounts.GetValueOrDefault("L0_RAW", 0),
                L1Count = layerCounts.GetValueOrDefault("L1_CONTEXT", 0),
                L2Count = layerCounts.GetValueOrDefault("L2_SUMMARY", 0),
                L3Count = layerCounts.GetValueOrDefault("L3_KNOWLEDGE", 0),
                L4Count = layerCounts.GetValueOrDefault("L4_HEURISTIC", 0),
                QueuePending = queueStats.Pending,
                QueueProcessing = queueStats.Processing,
                CompletedLastHour = queueStats.CompletedHour,
                Timestamp = DateTimeOffset.UtcNow
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to broadcast layer stats update");
        }
    }

    private async Task CleanupStaleTransitionsAsync(CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        // Set internal admin role to bypass RLS
        await conn.ExecuteAsync("SET app.role = 'internal_admin'");

        // Reset stale processing transitions
        var reset = await conn.ExecuteAsync("""
            UPDATE layer_transition_queue
            SET status = 'pending',
                started_at = NULL,
                retry_count = COALESCE(retry_count, 0) + 1,
                updated_at = NOW()
            WHERE status = 'processing'
              AND started_at < NOW() - INTERVAL '@Minutes minutes'
              AND COALESCE(retry_count, 0) < 3
            """.Replace("@Minutes", _config.StaleTransitionMinutes.ToString()));

        if (reset > 0)
        {
            _logger.LogWarning("Reset {Count} stale transitions", reset);
        }

        // Mark repeatedly failing transitions as permanently failed
        await conn.ExecuteAsync("""
            UPDATE layer_transition_queue
            SET status = 'failed',
                error_message = 'Max retries exceeded',
                completed_at = NOW(),
                updated_at = NOW()
            WHERE status = 'processing'
              AND started_at < NOW() - INTERVAL '@Minutes minutes'
              AND COALESCE(retry_count, 0) >= 3
            """.Replace("@Minutes", _config.StaleTransitionMinutes.ToString()));
    }

    #endregion

    #region DTOs

    private sealed class TransitionCandidate
    {
        public Guid MemoryId { get; set; }
        public Guid TenantId { get; set; }
        public string FromLayer { get; set; } = "";
        public string ToLayer { get; set; } = "";
        public double Score { get; set; }
    }

    private sealed class TransitionQueueItem
    {
        public Guid Id { get; set; }
        public Guid TenantId { get; set; }
        public Guid MemoryId { get; set; }
        public string FromLayer { get; set; } = "";
        public string ToLayer { get; set; } = "";
        public int Priority { get; set; }
    }

    private sealed class MemoryContent
    {
        public string Content { get; set; } = "";
        public string? Metadata { get; set; }
    }

    #endregion
}

/// <summary>
/// Configuration for the memory layer worker.
/// </summary>
public sealed class MemoryLayerWorkerConfig
{
    public bool Enabled { get; set; } = true;
    public int BatchSize { get; set; } = 50;
    public int MaxConcurrentTransitions { get; set; } = 5;
    public int ProcessingIntervalSeconds { get; set; } = 60;
    public int QueueCheckIntervalSeconds { get; set; } = 10;
    public int StaleTransitionMinutes { get; set; } = 30;
    public bool EnableL0ToL1 { get; set; } = true;
    public bool EnableL1ToL2 { get; set; } = true;
    public bool EnableL2ToL3 { get; set; } = true;
    public bool EnableL3ToL4 { get; set; } = true;
}
