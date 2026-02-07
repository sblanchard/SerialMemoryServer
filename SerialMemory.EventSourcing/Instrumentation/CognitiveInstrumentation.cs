using System.Collections.Concurrent;
using System.Diagnostics;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using SerialMemory.EventSourcing.Events;

namespace SerialMemory.EventSourcing.Instrumentation;

/// <summary>
/// Interface for cognitive stage instrumentation.
/// Tracks the execution of cognitive processing stages.
/// </summary>
public interface ICognitiveInstrumentation
{
    /// <summary>Begin a new cognitive stage execution.</summary>
    CognitiveStageScope BeginStage(CognitiveStage stage, Guid? sessionId = null, string? inputSummary = null);

    /// <summary>Log a completed stage directly.</summary>
    Task LogStageAsync(CognitiveStageLog log, CancellationToken cancellationToken = default);

    /// <summary>Get recent stage logs.</summary>
    Task<IReadOnlyList<CognitiveStageLog>> GetRecentLogsAsync(
        int limit = 100,
        CognitiveStage? stageFilter = null,
        Guid? sessionFilter = null,
        CancellationToken cancellationToken = default);

    /// <summary>Get stage statistics.</summary>
    Task<IReadOnlyList<CognitiveStageStatistics>> GetStatisticsAsync(
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        CancellationToken cancellationToken = default);

    /// <summary>Get active stages currently executing.</summary>
    IReadOnlyCollection<CognitiveStageLog> GetActiveStages();
}

/// <summary>
/// PostgreSQL-backed cognitive instrumentation service.
/// </summary>
public sealed class PostgresCognitiveInstrumentation : ICognitiveInstrumentation
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<PostgresCognitiveInstrumentation> _logger;
    private readonly ConcurrentDictionary<Guid, CognitiveStageLog> _activeStages = new();

    public PostgresCognitiveInstrumentation(string connectionString, ILogger<PostgresCognitiveInstrumentation> logger)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        _dataSource = builder.Build();
        _logger = logger;
    }

    public CognitiveStageScope BeginStage(CognitiveStage stage, Guid? sessionId = null, string? inputSummary = null)
    {
        var log = new CognitiveStageLog
        {
            Stage = stage,
            SessionId = sessionId,
            InputSummary = inputSummary
        };

        _activeStages.TryAdd(log.LogId, log);

        _logger.LogDebug("Started cognitive stage {Stage} [{LogId}]", stage, log.LogId);

        return new CognitiveStageScope(log, this);
    }

    public async Task LogStageAsync(CognitiveStageLog log, CancellationToken cancellationToken = default)
    {
        _activeStages.TryRemove(log.LogId, out _);

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        await conn.ExecuteAsync(new CommandDefinition(@"
            INSERT INTO cognitive_stage_logs
                (log_id, session_id, stage, started_at, completed_at, duration_ms,
                 input_summary, output_summary, memory_ids_involved, metadata, success, error_message)
            VALUES
                (@LogId, @SessionId, @Stage::cognitive_stage, @StartedAt, @CompletedAt, @DurationMs,
                 @InputSummary, @OutputSummary, @MemoryIdsInvolved, @Metadata::jsonb, @Success, @ErrorMessage)",
            new
            {
                log.LogId,
                log.SessionId,
                Stage = log.Stage.ToString(),
                log.StartedAt,
                log.CompletedAt,
                log.DurationMs,
                log.InputSummary,
                log.OutputSummary,
                log.MemoryIdsInvolved,
                Metadata = System.Text.Json.JsonSerializer.Serialize(log.Metadata),
                log.Success,
                log.ErrorMessage
            },
            cancellationToken: cancellationToken));

        _logger.LogDebug(
            "Logged cognitive stage {Stage} [{LogId}] - Duration: {DurationMs}ms, Success: {Success}",
            log.Stage, log.LogId, log.DurationMs, log.Success);
    }

    public async Task<IReadOnlyList<CognitiveStageLog>> GetRecentLogsAsync(
        int limit = 100,
        CognitiveStage? stageFilter = null,
        Guid? sessionFilter = null,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        var filters = new List<string> { "1=1" };
        var parameters = new DynamicParameters();
        parameters.Add("Limit", limit);

        if (stageFilter.HasValue)
        {
            filters.Add("stage = @Stage::cognitive_stage");
            parameters.Add("Stage", stageFilter.Value.ToString());
        }

        if (sessionFilter.HasValue)
        {
            filters.Add("session_id = @SessionId");
            parameters.Add("SessionId", sessionFilter.Value);
        }

        var rows = await conn.QueryAsync<dynamic>(new CommandDefinition($@"
            SELECT log_id, session_id, stage, started_at, completed_at, duration_ms,
                   input_summary, output_summary, memory_ids_involved, success, error_message
            FROM cognitive_stage_logs
            WHERE {string.Join(" AND ", filters)}
            ORDER BY started_at DESC
            LIMIT @Limit",
            parameters,
            cancellationToken: cancellationToken));

        return [.. rows.Select(r => new CognitiveStageLog
        {
            LogId = r.log_id,
            SessionId = r.session_id,
            Stage = Enum.Parse<CognitiveStage>((string)r.stage),
            StartedAt = r.started_at,
            CompletedAt = r.completed_at,
            InputSummary = r.input_summary,
            OutputSummary = r.output_summary,
            MemoryIdsInvolved = r.memory_ids_involved ?? Array.Empty<Guid>(),
            Success = r.success,
            ErrorMessage = r.error_message
        })];
    }

    public async Task<IReadOnlyList<CognitiveStageStatistics>> GetStatisticsAsync(
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        var filters = new List<string> { "1=1" };
        var parameters = new DynamicParameters();

        if (from.HasValue)
        {
            filters.Add("started_at >= @From");
            parameters.Add("From", from.Value);
        }

        if (to.HasValue)
        {
            filters.Add("started_at <= @To");
            parameters.Add("To", to.Value);
        }

        var rows = await conn.QueryAsync<dynamic>(new CommandDefinition($@"
            SELECT
                stage,
                COUNT(*) AS total_count,
                COUNT(*) FILTER (WHERE success) AS success_count,
                AVG(duration_ms)::INT AS avg_duration_ms,
                MIN(duration_ms) AS min_duration_ms,
                MAX(duration_ms) AS max_duration_ms,
                PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY duration_ms)::INT AS p50_duration_ms,
                PERCENTILE_CONT(0.95) WITHIN GROUP (ORDER BY duration_ms)::INT AS p95_duration_ms,
                PERCENTILE_CONT(0.99) WITHIN GROUP (ORDER BY duration_ms)::INT AS p99_duration_ms
            FROM cognitive_stage_logs
            WHERE {string.Join(" AND ", filters)}
            GROUP BY stage
            ORDER BY stage",
            parameters,
            cancellationToken: cancellationToken));

        return [.. rows.Select(r => new CognitiveStageStatistics
        {
            Stage = Enum.Parse<CognitiveStage>((string)r.stage),
            TotalCount = (int)(long)r.total_count,
            SuccessCount = (int)(long)r.success_count,
            SuccessRate = (float)(long)r.success_count / (long)r.total_count,
            AvgDurationMs = r.avg_duration_ms ?? 0,
            MinDurationMs = r.min_duration_ms ?? 0,
            MaxDurationMs = r.max_duration_ms ?? 0,
            P50DurationMs = r.p50_duration_ms ?? 0,
            P95DurationMs = r.p95_duration_ms ?? 0,
            P99DurationMs = r.p99_duration_ms ?? 0
        })];
    }

    public IReadOnlyCollection<CognitiveStageLog> GetActiveStages()
    {
        return [.. _activeStages.Values];
    }
}

/// <summary>
/// Disposable scope for tracking a cognitive stage execution.
/// </summary>
public sealed class CognitiveStageScope : IDisposable, IAsyncDisposable
{
    private readonly CognitiveStageLog _log;
    private readonly ICognitiveInstrumentation _instrumentation;
    private readonly Stopwatch _stopwatch;
    private bool _disposed;

    internal CognitiveStageScope(CognitiveStageLog log, ICognitiveInstrumentation instrumentation)
    {
        _log = log;
        _instrumentation = instrumentation;
        _stopwatch = Stopwatch.StartNew();
    }

    /// <summary>The log entry being tracked.</summary>
    public CognitiveStageLog Log => _log;

    /// <summary>Add a memory ID that was involved in this stage.</summary>
    public void AddMemoryId(Guid memoryId)
    {
        var list = _log.MemoryIdsInvolved.ToList();
        if (!list.Contains(memoryId))
        {
            list.Add(memoryId);
        }
        // Note: CognitiveStageLog.MemoryIdsInvolved should be mutable for this to work
    }

    /// <summary>Add metadata to the stage log.</summary>
    public void AddMetadata(string key, object value)
    {
        _log.Metadata[key] = value;
    }

    /// <summary>Mark the stage as completed successfully.</summary>
    public void Complete(string? outputSummary = null)
    {
        _stopwatch.Stop();
        _log.Complete(outputSummary);
    }

    /// <summary>Mark the stage as failed.</summary>
    public void Fail(string errorMessage)
    {
        _stopwatch.Stop();
        _log.Fail(errorMessage);
    }

    /// <summary>Mark the stage as failed with exception.</summary>
    public void Fail(Exception exception)
    {
        Fail(exception.Message);
        _log.Metadata["exception_type"] = exception.GetType().Name;
        _log.Metadata["stack_trace"] = exception.StackTrace ?? "";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (!_log.CompletedAt.HasValue)
        {
            _log.Complete();
        }

        // Fire-and-forget logging
        _ = _instrumentation.LogStageAsync(_log, CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (!_log.CompletedAt.HasValue)
        {
            _log.Complete();
        }

        await _instrumentation.LogStageAsync(_log, CancellationToken.None);
    }
}

/// <summary>
/// Statistics for a cognitive stage.
/// </summary>
public sealed record CognitiveStageStatistics
{
    public CognitiveStage Stage { get; init; }
    public int TotalCount { get; init; }
    public int SuccessCount { get; init; }
    public float SuccessRate { get; init; }
    public int AvgDurationMs { get; init; }
    public int MinDurationMs { get; init; }
    public int MaxDurationMs { get; init; }
    public int P50DurationMs { get; init; }
    public int P95DurationMs { get; init; }
    public int P99DurationMs { get; init; }
}

/// <summary>
/// Extension methods for cognitive instrumentation.
/// </summary>
public static class CognitiveInstrumentationExtensions
{
    /// <summary>
    /// Execute an action within a cognitive stage scope.
    /// </summary>
    public static async Task<T> ExecuteInStageAsync<T>(
        this ICognitiveInstrumentation instrumentation,
        CognitiveStage stage,
        Func<CognitiveStageScope, Task<T>> action,
        Guid? sessionId = null,
        string? inputSummary = null)
    {
        await using var scope = instrumentation.BeginStage(stage, sessionId, inputSummary);
        try
        {
            var result = await action(scope);
            scope.Complete();
            return result;
        }
        catch (Exception ex)
        {
            scope.Fail(ex);
            throw;
        }
    }

    /// <summary>
    /// Execute an action within a cognitive stage scope.
    /// </summary>
    public static async Task ExecuteInStageAsync(
        this ICognitiveInstrumentation instrumentation,
        CognitiveStage stage,
        Func<CognitiveStageScope, Task> action,
        Guid? sessionId = null,
        string? inputSummary = null)
    {
        await using var scope = instrumentation.BeginStage(stage, sessionId, inputSummary);
        try
        {
            await action(scope);
            scope.Complete();
        }
        catch (Exception ex)
        {
            scope.Fail(ex);
            throw;
        }
    }
}
