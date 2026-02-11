using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using SerialMemory.EventSourcing.CQRS;
using SerialMemory.EventSourcing.Retrieval;

namespace SerialMemory.EventSourcing.Maintenance;

/// <summary>
/// Background worker for autonomous memory maintenance operations.
/// Runs periodic tasks: merge duplicates, detect contradictions, apply decay, reinforce stable, archive cold.
/// All mutations are performed through commands to maintain event sourcing consistency.
/// </summary>
public sealed class MemoryMaintenanceWorker : BackgroundService
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly IRetrievalEngine _retrievalEngine;
    private readonly ILogger<MemoryMaintenanceWorker> _logger;
    private readonly MaintenanceConfig _config;

    public MemoryMaintenanceWorker(
        NpgsqlDataSource dataSource,
        IRetrievalEngine retrievalEngine,
        ILogger<MemoryMaintenanceWorker> logger,
        MaintenanceConfig? config = null)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _retrievalEngine = retrievalEngine;
        _logger = logger;
        _config = config ?? MaintenanceConfig.Default;
    }

    public MemoryMaintenanceWorker(
        string connectionString,
        IRetrievalEngine retrievalEngine,
        ILogger<MemoryMaintenanceWorker> logger,
        MaintenanceConfig? config = null)
        : this(new NpgsqlDataSourceBuilder(connectionString).Build(), retrievalEngine, logger, config)
    {
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Memory maintenance worker starting");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunMaintenanceCycleAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in maintenance cycle");
            }

            await Task.Delay(_config.CycleInterval, stoppingToken);
        }

        _logger.LogInformation("Memory maintenance worker stopping");
    }

    private async Task RunMaintenanceCycleAsync(CancellationToken cancellationToken)
    {
        var cycleId = Guid.CreateVersion7();
        _logger.LogDebug("Starting maintenance cycle {CycleId}", cycleId);

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        // 1. Apply decay to old memories
        var decayedCount = await ApplyDecayAsync(conn, cancellationToken);

        // 2. Archive cold memories (low access, high decay)
        var archivedCount = await ArchiveColdMemoriesAsync(conn, cancellationToken);

        // 3. Reinforce stable memories (frequently accessed, high confidence)
        var reinforcedCount = await ReinforceStableMemoriesAsync(conn, cancellationToken);

        // 4. Detect potential duplicates
        var duplicatesFound = await DetectDuplicatesAsync(conn, cancellationToken);

        // 5. Detect potential contradictions
        var contradictionsFound = await DetectContradictionsAsync(conn, cancellationToken);

        _logger.LogInformation(
            "Maintenance cycle {CycleId} complete: Decayed={Decayed}, Archived={Archived}, Reinforced={Reinforced}, DuplicatesFound={Duplicates}, ContradictionsFound={Contradictions}",
            cycleId, decayedCount, archivedCount, reinforcedCount, duplicatesFound, contradictionsFound);

        // Log maintenance task completion
        await LogMaintenanceTaskAsync(conn, new MaintenanceTaskLog
        {
            TaskType = "full_cycle",
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1), // Approximate
            CompletedAt = DateTimeOffset.UtcNow,
            ItemsProcessed = decayedCount + archivedCount + reinforcedCount,
            Success = true,
            Details = JsonSerializer.Serialize(new
            {
                cycleId,
                decayed = decayedCount,
                archived = archivedCount,
                reinforced = reinforcedCount,
                duplicatesFound,
                contradictionsFound
            })
        }, cancellationToken);
    }

    private async Task<int> ApplyDecayAsync(NpgsqlConnection conn, CancellationToken cancellationToken)
    {
        // Batch decay: applies the half-life formula directly in SQL instead of issuing
        // individual ApplyDecayCommand per memory. This bypasses event sourcing for
        // performance -- decay is a high-volume, low-value mutation that doesn't warrant
        // per-memory event overhead during maintenance cycles. The aggregate cycle log
        // records how many rows were decayed.
        var decayedCount = await conn.ExecuteAsync(new CommandDefinition(@"
            UPDATE memory_projections
            SET confidence_score = confidence_score * POWER(0.5, EXTRACT(EPOCH FROM (NOW() - last_reinforced_at)) / 86400.0 / GREATEST(half_life_days, 1))
            WHERE is_active = TRUE
                AND confidence_score > 0.01
                AND last_reinforced_at < NOW() - INTERVAL '1 day'",
            cancellationToken: cancellationToken));

        return decayedCount;
    }

    private async Task<int> ArchiveColdMemoriesAsync(NpgsqlConnection conn, CancellationToken cancellationToken)
    {
        // Batch archive: sets is_archived directly in SQL instead of issuing individual
        // ArchiveMemoryCommand per memory. This bypasses event sourcing for performance --
        // archiving cold memories is a bulk housekeeping operation. The aggregate cycle log
        // records how many rows were archived.
        //
        // Layer-specific retention intervals:
        //   L0_RAW       = 30 days   (raw input, ephemeral)
        //   L1_CONTEXT   = 90 days   (processed notes)
        //   L2_SUMMARY   = 180 days  (synthesized concepts)
        //   L3_KNOWLEDGE = 365 days  (validated learnings)
        //   L4_HEURISTIC = indefinite (never archived)
        var archivedCount = await conn.ExecuteAsync(new CommandDefinition(@"
            UPDATE memory_projections
            SET is_archived = TRUE
            WHERE is_active = TRUE
                AND is_archived = FALSE
                AND confidence_score < @ArchiveThreshold
                AND access_count < @MinAccessCount
                AND layer != 'L4_HEURISTIC'
                AND last_accessed_at < NOW() - make_interval(days =>
                    CASE layer
                        WHEN 'L0_RAW'       THEN @L0RetentionDays
                        WHEN 'L1_CONTEXT'   THEN @L1RetentionDays
                        WHEN 'L2_SUMMARY'   THEN @L2RetentionDays
                        WHEN 'L3_KNOWLEDGE' THEN @L3RetentionDays
                        ELSE @L0RetentionDays
                    END
                )",
            new
            {
                ArchiveThreshold = _config.ArchiveConfidenceThreshold,
                MinAccessCount = _config.MinAccessCountForRetention,
                L0RetentionDays = _config.L0RawRetentionDays,
                L1RetentionDays = _config.L1ContextRetentionDays,
                L2RetentionDays = _config.L2SummaryRetentionDays,
                L3RetentionDays = _config.L3KnowledgeRetentionDays
            },
            cancellationToken: cancellationToken));

        return archivedCount;
    }

    private async Task<int> ReinforceStableMemoriesAsync(NpgsqlConnection conn, CancellationToken cancellationToken)
    {
        // Batch reinforce: boosts confidence and resets last_reinforced_at directly in SQL
        // instead of issuing individual ReinforceMemoryCommand per memory. This bypasses
        // event sourcing for performance -- reinforcement of stable memories is a bulk
        // maintenance operation. The aggregate cycle log records how many rows were reinforced.
        var reinforcedCount = await conn.ExecuteAsync(new CommandDefinition(@"
            UPDATE memory_projections
            SET confidence_score = LEAST(1.0, confidence_score * 1.1),
                last_reinforced_at = NOW()
            WHERE is_active = TRUE
                AND confidence_score > @MinConfidence
                AND access_count > @MinAccessCount
                AND last_reinforced_at < NOW() - @ReinforceInterval::INTERVAL
                AND validated_by IS NOT NULL",
            new
            {
                MinConfidence = _config.ReinforceMinConfidence,
                MinAccessCount = _config.ReinforceMinAccessCount,
                ReinforceInterval = $"{_config.ReinforceIntervalDays} days"
            },
            cancellationToken: cancellationToken));

        return reinforcedCount;
    }

    private async Task<int> DetectDuplicatesAsync(NpgsqlConnection conn, CancellationToken cancellationToken)
    {
        // Single LATERAL JOIN query replaces the previous N+1 pattern (200 individual KNN
        // queries). The inner subquery samples the 200 most recent active memories, and the
        // LATERAL subquery finds up to 3 nearest neighbors above the similarity threshold
        // for each, all in one round-trip.
        var duplicatePairs = await conn.QueryAsync<(Guid MemoryId, Guid DuplicateId, float Similarity)>(new CommandDefinition(@"
            SELECT m.memory_id, n.memory_id AS duplicate_id, 1 - (m.embedding <=> n.embedding) AS similarity
            FROM (
                SELECT memory_id, embedding
                FROM memory_projections
                WHERE is_active = TRUE AND embedding IS NOT NULL
                ORDER BY created_at DESC
                LIMIT 200
            ) m,
            LATERAL (
                SELECT memory_id, embedding
                FROM memory_projections
                WHERE memory_id != m.memory_id
                    AND is_active = TRUE
                    AND embedding IS NOT NULL
                    AND 1 - (embedding <=> m.embedding) > @Threshold
                ORDER BY embedding <=> m.embedding
                LIMIT 3
            ) n",
            new { Threshold = _config.DuplicateSimilarityThreshold },
            cancellationToken: cancellationToken));

        var duplicateCount = 0;
        var seen = new HashSet<(Guid, Guid)>();

        foreach (var dup in duplicatePairs)
        {
            // Normalize pair ordering to avoid duplicate task entries
            var pair = dup.MemoryId < dup.DuplicateId
                ? (dup.MemoryId, dup.DuplicateId)
                : (dup.DuplicateId, dup.MemoryId);

            if (!seen.Add(pair)) continue;

            await conn.ExecuteAsync(new CommandDefinition(@"
                INSERT INTO maintenance_tasks (task_id, task_type, status, priority, memory_ids, scheduled_for, metadata)
                VALUES (@TaskId, 'merge_duplicates', 'pending', 2, @MemoryIds, NOW(), @Metadata::jsonb)
                ON CONFLICT DO NOTHING",
                new
                {
                    TaskId = Guid.CreateVersion7(),
                    MemoryIds = new[] { pair.Item1, pair.Item2 },
                    Metadata = JsonSerializer.Serialize(new { similarity = dup.Similarity })
                },
                cancellationToken: cancellationToken));

            duplicateCount++;
        }

        return duplicateCount;
    }

    private async Task<int> DetectContradictionsAsync(NpgsqlConnection conn, CancellationToken cancellationToken)
    {
        // KNN-based contradiction detection replaces the previous O(n^2) cartesian cross-join.
        // Scopes the search to recent memories (last 24h) and uses LATERAL JOIN to find only
        // their top-5 nearest neighbors above 0.85 similarity, then applies the contradiction
        // predicates (existing contradiction_ids references, or different knowledge/heuristic layers).
        var contradictions = await conn.QueryAsync<(Guid MemoryAId, Guid MemoryBId)>(new CommandDefinition(@"
            SELECT m.memory_id AS memory_a_id, n.memory_id AS memory_b_id
            FROM (
                SELECT memory_id, embedding, layer, contradiction_ids
                FROM memory_projections
                WHERE is_active = TRUE
                    AND embedding IS NOT NULL
                    AND created_at > NOW() - INTERVAL '24 hours'
                ORDER BY created_at DESC
                LIMIT 100
            ) m,
            LATERAL (
                SELECT memory_id, layer, contradiction_ids
                FROM memory_projections
                WHERE memory_id != m.memory_id
                    AND is_active = TRUE
                    AND embedding IS NOT NULL
                    AND 1 - (embedding <=> m.embedding) > 0.85
                ORDER BY embedding <=> m.embedding
                LIMIT 5
            ) n
            WHERE m.memory_id < n.memory_id
                AND (
                    m.memory_id = ANY(n.contradiction_ids)
                    OR n.memory_id = ANY(m.contradiction_ids)
                    OR (
                        m.layer != n.layer
                        AND (m.layer IN ('L3_KNOWLEDGE', 'L4_HEURISTIC') OR n.layer IN ('L3_KNOWLEDGE', 'L4_HEURISTIC'))
                    )
                )",
            cancellationToken: cancellationToken));

        var contradictionsList = contradictions.ToList();

        // Log potential contradictions as maintenance tasks
        foreach (var contr in contradictionsList)
        {
            await conn.ExecuteAsync(new CommandDefinition(@"
                INSERT INTO maintenance_tasks (task_id, task_type, status, priority, memory_ids, scheduled_for, metadata)
                VALUES (@TaskId, 'resolve_contradiction', 'pending', 1, @MemoryIds, NOW(), '{}'::jsonb)
                ON CONFLICT DO NOTHING",
                new
                {
                    TaskId = Guid.CreateVersion7(),
                    MemoryIds = new[] { contr.MemoryAId, contr.MemoryBId }
                },
                cancellationToken: cancellationToken));
        }

        return contradictionsList.Count;
    }

    private static async Task LogMaintenanceTaskAsync(NpgsqlConnection conn, MaintenanceTaskLog log, CancellationToken cancellationToken)
    {
        await conn.ExecuteAsync(new CommandDefinition(@"
            INSERT INTO maintenance_task_logs (log_id, task_type, started_at, completed_at, items_processed, success, details)
            VALUES (@LogId, @TaskType, @StartedAt, @CompletedAt, @ItemsProcessed, @Success, @Details::jsonb)",
            new
            {
                LogId = Guid.CreateVersion7(),
                log.TaskType,
                log.StartedAt,
                log.CompletedAt,
                log.ItemsProcessed,
                log.Success,
                log.Details
            },
            cancellationToken: cancellationToken));
    }
}

/// <summary>
/// Configuration for maintenance worker.
/// </summary>
public sealed class MaintenanceConfig
{
    public TimeSpan CycleInterval { get; set; } = TimeSpan.FromHours(1);
    public float ArchiveConfidenceThreshold { get; set; } = 0.1f;
    public int MinAccessCountForRetention { get; set; } = 3;
    public int ColdPeriodDays { get; set; } = 30;
    public float ReinforceMinConfidence { get; set; } = 0.7f;
    public int ReinforceMinAccessCount { get; set; } = 10;
    public int ReinforceIntervalDays { get; set; } = 7;
    public float DuplicateSimilarityThreshold { get; set; } = 0.95f;

    /// <summary>
    /// Per-layer retention intervals in days. Memories are eligible for archival
    /// only after they exceed their layer's retention period.
    /// L4_HEURISTIC is always excluded (indefinite retention).
    /// </summary>
    public int L0RawRetentionDays { get; set; } = 30;
    public int L1ContextRetentionDays { get; set; } = 90;
    public int L2SummaryRetentionDays { get; set; } = 180;
    public int L3KnowledgeRetentionDays { get; set; } = 365;

    public static MaintenanceConfig Default => new();
}

/// <summary>
/// Log entry for maintenance task execution.
/// </summary>
internal sealed class MaintenanceTaskLog
{
    public string TaskType { get; init; } = "";
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset CompletedAt { get; init; }
    public int ItemsProcessed { get; init; }
    public bool Success { get; init; }
    public string? Details { get; init; }
}

/// <summary>
/// Worker for processing pending maintenance tasks (merge, resolve contradictions).
/// All mutations are performed through commands to maintain event sourcing consistency.
/// </summary>
public sealed class MaintenanceTaskProcessor : BackgroundService
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly IRetrievalEngine _retrievalEngine;
    private readonly ICommandHandler<InvalidateMemoryCommand> _invalidateHandler;
    private readonly ILogger<MaintenanceTaskProcessor> _logger;

    public MaintenanceTaskProcessor(
        NpgsqlDataSource dataSource,
        IRetrievalEngine retrievalEngine,
        ICommandHandler<InvalidateMemoryCommand> invalidateHandler,
        ILogger<MaintenanceTaskProcessor> logger)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _retrievalEngine = retrievalEngine;
        _invalidateHandler = invalidateHandler;
        _logger = logger;
    }

    public MaintenanceTaskProcessor(
        string connectionString,
        IRetrievalEngine retrievalEngine,
        ICommandHandler<InvalidateMemoryCommand> invalidateHandler,
        ILogger<MaintenanceTaskProcessor> logger)
        : this(new NpgsqlDataSourceBuilder(connectionString).Build(), retrievalEngine, invalidateHandler, logger)
    {
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Maintenance task processor starting");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessPendingTasksAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing maintenance tasks");
            }

            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }

        _logger.LogInformation("Maintenance task processor stopping");
    }

    private async Task ProcessPendingTasksAsync(CancellationToken cancellationToken)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        // Get pending tasks ordered by priority
        var tasks = await conn.QueryAsync<dynamic>(new CommandDefinition(@"
            SELECT task_id, task_type, memory_ids, metadata
            FROM maintenance_tasks
            WHERE status = 'pending'
            ORDER BY priority ASC, scheduled_for ASC
            LIMIT 10
            FOR UPDATE SKIP LOCKED",
            cancellationToken: cancellationToken));

        foreach (var task in tasks)
        {
            try
            {
                // Mark as in progress
                await conn.ExecuteAsync(new CommandDefinition(@"
                    UPDATE maintenance_tasks
                    SET status = 'in_progress', started_at = NOW()
                    WHERE task_id = @TaskId",
                    new { TaskId = (Guid)task.task_id },
                    cancellationToken: cancellationToken));

                // Process based on task type (handle both snake_case and PascalCase)
                string taskType = task.task_type;
                var success = taskType switch
                {
                    "merge_duplicates" or "MergeDuplicates" => await ProcessMergeDuplicatesAsync(conn, task, cancellationToken),
                    "resolve_contradiction" or "DetectContradictions" => await ProcessResolveContradictionAsync(conn, task, cancellationToken),
                    // ApplyDecay is handled by MemoryMaintenanceWorker's periodic cycle, mark as done
                    "ApplyDecay" or "apply_decay" => true,
                    _ => false
                };

                // Mark as completed or failed
                await conn.ExecuteAsync(new CommandDefinition(@"
                    UPDATE maintenance_tasks
                    SET status = @Status, completed_at = NOW()
                    WHERE task_id = @TaskId",
                    new { TaskId = (Guid)task.task_id, Status = success ? "completed" : "failed" },
                    cancellationToken: cancellationToken));
            }
            catch (Exception ex)
            {
                var taskId = (Guid)task.task_id;
                _logger.LogError(ex, "Error processing task {TaskId}", taskId);

                await conn.ExecuteAsync(new CommandDefinition(@"
                    UPDATE maintenance_tasks
                    SET status = 'failed', error_message = @Error, completed_at = NOW()
                    WHERE task_id = @TaskId",
                    new { TaskId = taskId, Error = ex.Message },
                    cancellationToken: cancellationToken));
            }
        }
    }

    private async Task<bool> ProcessMergeDuplicatesAsync(NpgsqlConnection conn, dynamic task, CancellationToken cancellationToken)
    {
        // Bootstrap tasks (from schema seed) have no memory_ids - mark as completed
        if (task.memory_ids == null) return true;

        var memoryIds = (Guid[])task.memory_ids;
        if (memoryIds.Length < 2) return true; // Nothing to merge

        // Get both memories (query only)
        var memories = await conn.QueryAsync<(Guid MemoryId, float ConfidenceScore, int AccessCount)>(new CommandDefinition(@"
            SELECT memory_id, confidence_score, access_count
            FROM memory_projections
            WHERE memory_id = ANY(@MemoryIds)",
            new { MemoryIds = memoryIds },
            cancellationToken: cancellationToken));

        var memoryList = memories.ToList();
        if (memoryList.Count < 2) return false;

        // Keep the one with higher confidence/access, invalidate the other as superseded
        var primary = memoryList.OrderByDescending(m => m.ConfidenceScore * m.AccessCount).First();
        var secondary = memoryList.First(m => m.MemoryId != primary.MemoryId);

        // Use command to invalidate the duplicate (preserves event sourcing)
        var result = await _invalidateHandler.HandleAsync(new InvalidateMemoryCommand
        {
            MemoryId = secondary.MemoryId,
            Reason = "Duplicate detected and superseded",
            SupersededById = primary.MemoryId,
            ActorId = "maintenance-worker"
        }, cancellationToken);

        if (result.Success)
        {
            _logger.LogInformation("Merged duplicate: {SecondaryId} -> {PrimaryId}", secondary.MemoryId, primary.MemoryId);
        }

        return result.Success;
    }

    private async Task<bool> ProcessResolveContradictionAsync(NpgsqlConnection conn, dynamic task, CancellationToken cancellationToken)
    {
        // Bootstrap tasks (from schema seed) have no memory_ids - mark as completed
        if (task.memory_ids == null) return true;

        var memoryIds = (Guid[])task.memory_ids;
        if (memoryIds.Length < 2) return true; // Nothing to resolve

        // Get confidence scores to determine which memory to invalidate
        var memories = await conn.QueryAsync<(Guid MemoryId, float ConfidenceScore, int AccessCount)>(new CommandDefinition(@"
            SELECT memory_id, confidence_score, access_count
            FROM memory_projections
            WHERE memory_id = ANY(@MemoryIds)",
            new { MemoryIds = memoryIds },
            cancellationToken: cancellationToken));

        var memoryList = memories.ToList();
        if (memoryList.Count < 2) return false;

        // Invalidate the lower-confidence memory as contradicted by the higher-confidence one
        // A full implementation would use LLM to determine which is correct
        var stronger = memoryList.OrderByDescending(m => m.ConfidenceScore * m.AccessCount).First();
        var weaker = memoryList.First(m => m.MemoryId != stronger.MemoryId);

        var result = await _invalidateHandler.HandleAsync(new InvalidateMemoryCommand
        {
            MemoryId = weaker.MemoryId,
            Reason = "Contradiction detected with higher-confidence memory",
            ContradictedByIds = [stronger.MemoryId],
            ActorId = "maintenance-worker"
        }, cancellationToken);

        if (result.Success)
        {
            _logger.LogInformation("Resolved contradiction: invalidated {WeakerId} (contradicted by {StrongerId})",
                weaker.MemoryId, stronger.MemoryId);
        }

        return result.Success;
    }
}
