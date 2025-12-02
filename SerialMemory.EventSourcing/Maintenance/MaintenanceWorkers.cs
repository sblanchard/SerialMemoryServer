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
    private readonly ICommandHandler<ApplyDecayCommand> _decayHandler;
    private readonly ICommandHandler<ArchiveMemoryCommand> _archiveHandler;
    private readonly ICommandHandler<ReinforceMemoryCommand> _reinforceHandler;
    private readonly ILogger<MemoryMaintenanceWorker> _logger;
    private readonly MaintenanceConfig _config;

    public MemoryMaintenanceWorker(
        string connectionString,
        IRetrievalEngine retrievalEngine,
        ICommandHandler<ApplyDecayCommand> decayHandler,
        ICommandHandler<ArchiveMemoryCommand> archiveHandler,
        ICommandHandler<ReinforceMemoryCommand> reinforceHandler,
        ILogger<MemoryMaintenanceWorker> logger,
        MaintenanceConfig? config = null)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        _dataSource = builder.Build();
        _retrievalEngine = retrievalEngine;
        _decayHandler = decayHandler;
        _archiveHandler = archiveHandler;
        _reinforceHandler = reinforceHandler;
        _logger = logger;
        _config = config ?? MaintenanceConfig.Default;
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
        // Find memories that need decay applied (query only, no mutation)
        var memoriesNeedingDecay = await conn.QueryAsync<Guid>(new CommandDefinition(@"
            SELECT memory_id
            FROM memory_projections
            WHERE is_active = TRUE
                AND confidence_score > 0.01
                AND last_reinforced_at < NOW() - INTERVAL '1 day'
            LIMIT 1000",
            cancellationToken: cancellationToken));

        var decayedCount = 0;
        foreach (var memoryId in memoriesNeedingDecay)
        {
            var result = await _decayHandler.HandleAsync(new ApplyDecayCommand
            {
                MemoryId = memoryId,
                ActorId = "maintenance-worker"
            }, cancellationToken);

            if (result.Success)
                decayedCount++;
        }

        return decayedCount;
    }

    private async Task<int> ArchiveColdMemoriesAsync(NpgsqlConnection conn, CancellationToken cancellationToken)
    {
        // Find memories that should be archived (query only, no mutation)
        var memoriesForArchive = await conn.QueryAsync<(Guid MemoryId, int AccessCount, DateTimeOffset LastAccessedAt)>(new CommandDefinition(@"
            SELECT memory_id, access_count, last_accessed_at
            FROM memory_projections
            WHERE is_active = TRUE
                AND is_archived = FALSE
                AND confidence_score < @ArchiveThreshold
                AND access_count < @MinAccessCount
                AND last_accessed_at < NOW() - @ColdPeriod::INTERVAL
                AND layer NOT IN ('L3_KNOWLEDGE', 'L4_HEURISTIC')
            LIMIT 500",
            new
            {
                ArchiveThreshold = _config.ArchiveConfidenceThreshold,
                MinAccessCount = _config.MinAccessCountForRetention,
                ColdPeriod = $"{_config.ColdPeriodDays} days"
            },
            cancellationToken: cancellationToken));

        var archivedCount = 0;
        foreach (var memory in memoriesForArchive)
        {
            var result = await _archiveHandler.HandleAsync(new ArchiveMemoryCommand
            {
                MemoryId = memory.MemoryId,
                Reason = "Cold memory: low confidence and access count",
                AccessCount = memory.AccessCount,
                LastAccessedAt = memory.LastAccessedAt,
                ActorId = "maintenance-worker"
            }, cancellationToken);

            if (result.Success)
                archivedCount++;
        }

        return archivedCount;
    }

    private async Task<int> ReinforceStableMemoriesAsync(NpgsqlConnection conn, CancellationToken cancellationToken)
    {
        // Find memories that should be reinforced (query only, no mutation)
        var memoriesForReinforce = await conn.QueryAsync<(Guid MemoryId, float ConfidenceScore)>(new CommandDefinition(@"
            SELECT memory_id, confidence_score
            FROM memory_projections
            WHERE is_active = TRUE
                AND confidence_score > @MinConfidence
                AND access_count > @MinAccessCount
                AND last_reinforced_at < NOW() - @ReinforceInterval::INTERVAL
                AND validated_by IS NOT NULL
            LIMIT 500",
            new
            {
                MinConfidence = _config.ReinforceMinConfidence,
                MinAccessCount = _config.ReinforceMinAccessCount,
                ReinforceInterval = $"{_config.ReinforceIntervalDays} days"
            },
            cancellationToken: cancellationToken));

        var reinforcedCount = 0;
        foreach (var memory in memoriesForReinforce)
        {
            var result = await _reinforceHandler.HandleAsync(new ReinforceMemoryCommand
            {
                MemoryId = memory.MemoryId,
                NewConfidence = Math.Min(1.0f, memory.ConfidenceScore * 1.1f),
                Source = "auto-reinforce:stable-memory",
                ActorId = "maintenance-worker"
            }, cancellationToken);

            if (result.Success)
                reinforcedCount++;
        }

        return reinforcedCount;
    }

    private async Task<int> DetectDuplicatesAsync(NpgsqlConnection conn, CancellationToken cancellationToken)
    {
        // Find potential duplicates using vector similarity
        var duplicates = await conn.QueryAsync<dynamic>(new CommandDefinition(@"
            SELECT
                a.memory_id AS memory_a_id,
                b.memory_id AS memory_b_id,
                1 - (a.embedding <=> b.embedding) AS similarity
            FROM memory_projections a
            JOIN memory_projections b ON a.memory_id < b.memory_id
            WHERE a.is_active = TRUE
                AND b.is_active = TRUE
                AND 1 - (a.embedding <=> b.embedding) > @Threshold
            LIMIT 100",
            new { Threshold = _config.DuplicateSimilarityThreshold },
            cancellationToken: cancellationToken));

        var duplicatesList = duplicates.ToList();

        // Log potential duplicates as maintenance tasks
        foreach (var dup in duplicatesList)
        {
            await conn.ExecuteAsync(new CommandDefinition(@"
                INSERT INTO maintenance_tasks (task_id, task_type, status, priority, memory_ids, scheduled_for, metadata)
                VALUES (@TaskId, 'merge_duplicates', 'pending', 2, @MemoryIds, NOW(), @Metadata::jsonb)
                ON CONFLICT DO NOTHING",
                new
                {
                    TaskId = Guid.CreateVersion7(),
                    MemoryIds = new[] { (Guid)dup.memory_a_id, (Guid)dup.memory_b_id },
                    Metadata = JsonSerializer.Serialize(new { similarity = (float)dup.similarity })
                },
                cancellationToken: cancellationToken));
        }

        return duplicatesList.Count;
    }

    private async Task<int> DetectContradictionsAsync(NpgsqlConnection conn, CancellationToken cancellationToken)
    {
        // Find potential contradictions:
        // - Memories with similar content but marked as contradicting each other
        // - Or memories with opposite sentiment about same entities
        // This is a simplified version - full implementation would use semantic analysis
        var contradictions = await conn.QueryAsync<dynamic>(new CommandDefinition(@"
            SELECT DISTINCT
                a.memory_id AS memory_a_id,
                b.memory_id AS memory_b_id
            FROM memory_projections a
            JOIN memory_projections b ON a.memory_id != b.memory_id
            WHERE a.is_active = TRUE
                AND b.is_active = TRUE
                AND a.memory_id < b.memory_id
                AND (
                    -- Check if they already reference each other as contradictions
                    a.memory_id = ANY(b.contradiction_ids)
                    OR b.memory_id = ANY(a.contradiction_ids)
                    -- Or have high semantic similarity but different layers (potential contradiction)
                    OR (
                        1 - (a.embedding <=> b.embedding) > 0.85
                        AND a.layer != b.layer
                        AND (a.layer IN ('L3_KNOWLEDGE', 'L4_HEURISTIC') OR b.layer IN ('L3_KNOWLEDGE', 'L4_HEURISTIC'))
                    )
                )
            LIMIT 50",
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
                    MemoryIds = new[] { (Guid)contr.memory_a_id, (Guid)contr.memory_b_id }
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
        string connectionString,
        IRetrievalEngine retrievalEngine,
        ICommandHandler<InvalidateMemoryCommand> invalidateHandler,
        ILogger<MaintenanceTaskProcessor> logger)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        _dataSource = builder.Build();
        _retrievalEngine = retrievalEngine;
        _invalidateHandler = invalidateHandler;
        _logger = logger;
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
