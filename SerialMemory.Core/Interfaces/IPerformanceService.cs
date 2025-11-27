using SerialMemory.Core.Performance;

namespace SerialMemory.Core.Interfaces;

/// <summary>
/// Service for collecting and reporting performance metrics.
/// </summary>
public interface IPerformanceService
{
    /// <summary>
    /// Record a latency measurement for an operation.
    /// </summary>
    void RecordLatency(string operationName, TimeSpan duration, bool success = true, int? statusCode = null);

    /// <summary>
    /// Get current performance snapshot.
    /// </summary>
    Task<PerformanceSnapshot> GetSnapshotAsync(CancellationToken ct = default);

    /// <summary>
    /// Get latency metrics for a specific operation.
    /// </summary>
    Task<LatencySnapshot?> GetOperationLatencyAsync(string operationName, CancellationToken ct = default);

    /// <summary>
    /// Get recent slow operations.
    /// </summary>
    Task<IReadOnlyList<SlowOperationRecord>> GetSlowOperationsAsync(int limit = 100, CancellationToken ct = default);

    /// <summary>
    /// Get cache statistics.
    /// </summary>
    Task<CacheSnapshot> GetCacheStatsAsync(CancellationToken ct = default);

    /// <summary>
    /// Set slow operation threshold.
    /// </summary>
    Task SetSlowThresholdAsync(string operationName, TimeSpan threshold, CancellationToken ct = default);

    /// <summary>
    /// Reset all metrics.
    /// </summary>
    Task ResetAsync(CancellationToken ct = default);

    /// <summary>
    /// Get database connection pool stats.
    /// </summary>
    Task<DbPoolStats> GetDbPoolStatsAsync(CancellationToken ct = default);

    /// <summary>
    /// Get active operations currently in progress.
    /// </summary>
    Task<IReadOnlyList<ActiveOperation>> GetActiveOperationsAsync(CancellationToken ct = default);
}

public sealed class DbPoolStats
{
    public int ActiveConnections { get; init; }
    public int IdleConnections { get; init; }
    public int TotalConnections { get; init; }
    public int MaxPoolSize { get; init; }
    public long TotalConnectionsCreated { get; init; }
    public long TotalConnectionsDestroyed { get; init; }
    public double AvgAcquisitionTimeMs { get; init; }
}

public sealed class ActiveOperation
{
    public required string OperationId { get; init; }
    public required string OperationName { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public double ElapsedMs { get; init; }
    public string? Context { get; init; }
}
