using System.Collections.Concurrent;
using System.Diagnostics;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using SerialMemory.Core.Interfaces;
using SerialMemory.Core.Performance;

namespace SerialMemory.Infrastructure;

/// <summary>
/// Performance monitoring service with real database stats.
/// </summary>
public sealed class PostgresPerformanceService : IPerformanceService, IDisposable
{
    private readonly string _connectionString;
    private readonly ILogger<PostgresPerformanceService> _logger;
    private readonly PerformanceRegistry _registry;
    private readonly ConcurrentDictionary<string, ActiveOperationTracker> _activeOps = new();

    public PerformanceRegistry Registry => _registry;

    public PostgresPerformanceService(
        string connectionString,
        ILogger<PostgresPerformanceService> logger,
        TimeSpan? slowOperationThreshold = null)
    {
        _connectionString = connectionString;
        _logger = logger;
        _registry = new PerformanceRegistry(slowOperationThreshold ?? TimeSpan.FromMilliseconds(100));

        // Configure default thresholds
        ConfigureDefaultThresholds();
    }

    private void ConfigureDefaultThresholds()
    {
        _registry.SlowDetector.SetThreshold("memory_search", TimeSpan.FromMilliseconds(200));
        _registry.SlowDetector.SetThreshold("memory_ingest", TimeSpan.FromMilliseconds(500));
        _registry.SlowDetector.SetThreshold("embedding_generate", TimeSpan.FromMilliseconds(1000));
        _registry.SlowDetector.SetThreshold("entity_extraction", TimeSpan.FromMilliseconds(300));
        _registry.SlowDetector.SetThreshold("db_query", TimeSpan.FromMilliseconds(50));
        _registry.SlowDetector.SetThreshold("db_insert", TimeSpan.FromMilliseconds(100));
        _registry.SlowDetector.SetThreshold("cache_get", TimeSpan.FromMilliseconds(5));
        _registry.SlowDetector.SetThreshold("cache_set", TimeSpan.FromMilliseconds(10));
        _registry.SlowDetector.SetThreshold("http_request", TimeSpan.FromMilliseconds(500));
    }

    /// <inheritdoc />
    public void RecordLatency(string operationName, TimeSpan duration, bool success = true, int? statusCode = null)
    {
        var histogram = _registry.Metrics.GetHistogram(operationName);
        histogram.Record(duration);

        var counter = _registry.Metrics.GetCounter(operationName);
        if (success)
            counter.RecordSuccess();
        else
            counter.RecordFailure();

        // Check for slow operation
        _registry.SlowDetector.RecordIfSlow(operationName, duration, statusCode?.ToString());
    }

    public Task<PerformanceSnapshot> GetSnapshotAsync(CancellationToken ct = default)
    {
        return Task.FromResult(_registry.GetSnapshot());
    }

    public Task<LatencySnapshot?> GetOperationLatencyAsync(string operationName, CancellationToken ct = default)
    {
        var snapshots = _registry.Metrics.GetAllLatencySnapshots();
        return Task.FromResult(snapshots.TryGetValue(operationName, out var snapshot) ? snapshot : null);
    }

    public Task<IReadOnlyList<SlowOperationRecord>> GetSlowOperationsAsync(int limit = 100, CancellationToken ct = default)
    {
        return Task.FromResult(_registry.SlowDetector.GetRecentSlowOperations(limit));
    }

    public async Task<CacheSnapshot> GetCacheStatsAsync(CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            // Query actual embedding_cache table for real stats
            // Use PascalCase aliases to match C# property names for Dapper mapping
            var stats = await conn.QueryFirstOrDefaultAsync<EmbeddingCacheStats>(@"
                SELECT
                    COUNT(*) AS ""TotalItems"",
                    COALESCE(SUM(access_count), 0) AS ""TotalHits"",
                    COUNT(*) FILTER (WHERE access_count = 0) AS ""ItemsNeverAccessed"",
                    COUNT(*) FILTER (WHERE is_compiled = true) AS ""CompiledCount"",
                    COALESCE(AVG(access_count), 0) AS ""AvgAccessCount"",
                    pg_size_pretty(pg_total_relation_size('embedding_cache')) AS ""TableSize""
                FROM embedding_cache");

            // Create a cache layer snapshot from actual data
            var embeddingLayer = new CacheLayerSnapshot
            {
                Hits = stats?.TotalHits ?? 0,
                Misses = 0, // Not tracked in DB
                Evictions = 0, // Not tracked in DB
                Writes = stats?.TotalItems ?? 0,
                CurrentSize = stats?.TotalItems ?? 0,
                HitRate = stats?.TotalItems > 0 ? (double)(stats.TotalHits) / (stats.TotalHits + stats.TotalItems) : 0
            };

            return new CacheSnapshot
            {
                Layers = new Dictionary<string, CacheLayerSnapshot>
                {
                    ["embedding_cache"] = embeddingLayer
                }
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get cache stats from database, using in-memory stats");
            return _registry.CacheMetrics.GetSnapshot();
        }
    }

    private class EmbeddingCacheStats
    {
        public long TotalItems { get; set; }
        public long TotalHits { get; set; }
        public long ItemsNeverAccessed { get; set; }
        public long CompiledCount { get; set; }
        public double AvgAccessCount { get; set; }
        public string? TableSize { get; set; }
    }

    public Task SetSlowThresholdAsync(string operationName, TimeSpan threshold, CancellationToken ct = default)
    {
        _registry.SlowDetector.SetThreshold(operationName, threshold);
        return Task.CompletedTask;
    }

    public Task ResetAsync(CancellationToken ct = default)
    {
        _registry.Reset();
        return Task.CompletedTask;
    }

    public async Task<DbPoolStats> GetDbPoolStatsAsync(CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            // Query PostgreSQL for connection stats
            // Use PascalCase aliases to match C# property names for Dapper mapping
            var stats = await conn.QueryFirstOrDefaultAsync<PgStatActivity>(@"
                SELECT
                    count(*) FILTER (WHERE state = 'active') AS ""ActiveCount"",
                    count(*) FILTER (WHERE state = 'idle') AS ""IdleCount"",
                    count(*) AS ""TotalCount""
                FROM pg_stat_activity
                WHERE datname = current_database()
                  AND pid != pg_backend_pid()");

            // Get pool settings from Npgsql
            var builder = new NpgsqlConnectionStringBuilder(_connectionString);

            return new DbPoolStats
            {
                ActiveConnections = stats?.ActiveCount ?? 0,
                IdleConnections = stats?.IdleCount ?? 0,
                TotalConnections = stats?.TotalCount ?? 0,
                MaxPoolSize = builder.MaxPoolSize,
                TotalConnectionsCreated = 0, // Would need custom tracking
                TotalConnectionsDestroyed = 0,
                AvgAcquisitionTimeMs = 0 // Would need custom tracking
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get DB pool stats");
            return new DbPoolStats();
        }
    }

    public Task<IReadOnlyList<ActiveOperation>> GetActiveOperationsAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var active = _activeOps.Values
            .Select(t => new ActiveOperation
            {
                OperationId = t.Id,
                OperationName = t.OperationName,
                StartedAt = t.StartedAt,
                ElapsedMs = (now - t.StartedAt).TotalMilliseconds,
                Context = t.Context
            })
            .OrderByDescending(o => o.ElapsedMs)
            .ToList();

        return Task.FromResult<IReadOnlyList<ActiveOperation>>(active);
    }

    /// <summary>
    /// Start tracking an operation. Returns a disposable that completes tracking.
    /// </summary>
    public IDisposable TrackOperation(string operationName, string? context = null)
    {
        var tracker = new ActiveOperationTracker(this, operationName, context);
        _activeOps[tracker.Id] = tracker;
        return tracker;
    }

    /// <summary>
    /// Measure an async operation with full tracking.
    /// </summary>
    public async Task<T> MeasureAsync<T>(string operationName, Func<Task<T>> operation, string? context = null)
    {
        using var tracker = TrackOperation(operationName, context);
        return await _registry.MeasureAsync(operationName, operation, context);
    }

    /// <summary>
    /// Measure a void async operation.
    /// </summary>
    public async Task MeasureAsync(string operationName, Func<Task> operation, string? context = null)
    {
        using var tracker = TrackOperation(operationName, context);
        await _registry.MeasureAsync(operationName, async () =>
        {
            await operation();
            return true;
        }, context);
    }

    internal void CompleteTracking(string id)
    {
        _activeOps.TryRemove(id, out _);
    }

    public void Dispose()
    {
        _activeOps.Clear();
    }

    private sealed class ActiveOperationTracker(
        PostgresPerformanceService service,
        string operationName,
        string? context)
        : IDisposable
    {
        public string Id { get; } = Guid.CreateVersion7().ToString();
        public string OperationName { get; } = operationName;
        public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
        public string? Context { get; } = context;

        public void Dispose()
        {
            service.CompleteTracking(Id);
        }
    }

    private sealed class PgStatActivity
    {
        public int ActiveCount { get; init; }
        public int IdleCount { get; init; }
        public int TotalCount { get; init; }
    }
}

/// <summary>
/// Extension methods for instrumenting operations.
/// </summary>
public static class PerformanceExtensions
{
    /// <summary>
    /// Wrap a database query with performance tracking.
    /// </summary>
    public static async Task<T> WithMetrics<T>(
        this Task<T> task,
        PerformanceRegistry registry,
        string operationName,
        string? context = null)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await task;
            sw.Stop();
            registry.Metrics.GetHistogram(operationName).Record(sw.Elapsed);
            registry.Metrics.GetCounter(operationName).RecordSuccess();
            registry.SlowDetector.RecordIfSlow(operationName, sw.Elapsed, context);
            return result;
        }
        catch
        {
            sw.Stop();
            registry.Metrics.GetHistogram(operationName).Record(sw.Elapsed);
            registry.Metrics.GetCounter(operationName).RecordFailure();
            registry.SlowDetector.RecordIfSlow(operationName, sw.Elapsed, context);
            throw;
        }
    }
}
