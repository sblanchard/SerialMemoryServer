namespace SerialMemory.Core.Interfaces;

/// <summary>
/// Provider for self-host admin metrics with clear demo mode detection.
/// When real telemetry is unavailable, returns demo data with explicit flag.
/// </summary>
public interface ISelfHostMetricsProvider
{
    /// <summary>
    /// Get comprehensive self-host metrics for the admin dashboard.
    /// Returns demo data with IsDemoMode=true if telemetry is not configured.
    /// </summary>
    Task<SelfHostMetricsResult> GetMetricsAsync(CancellationToken ct = default);

    /// <summary>
    /// Get current load status for real-time metrics display.
    /// </summary>
    Task<SelfHostLoadStatus> GetCurrentLoadAsync(CancellationToken ct = default);

    /// <summary>
    /// Check if the metrics provider is configured for live data.
    /// </summary>
    bool IsLiveMode { get; }
}

/// <summary>
/// Comprehensive self-host metrics result with demo mode flag.
/// </summary>
public sealed record SelfHostMetricsResult
{
    /// <summary>
    /// True if this is demo/sample data, not live system data.
    /// UI MUST show a clear indicator when this is true.
    /// </summary>
    public required bool IsDemoMode { get; init; }

    /// <summary>
    /// Reason why demo mode is active (if applicable).
    /// </summary>
    public string? DemoModeReason { get; init; }

    /// <summary>
    /// True if there was an error fetching metrics.
    /// </summary>
    public bool HasError { get; init; }

    /// <summary>
    /// Error message if metrics fetch failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Scale prediction and recommendations.
    /// </summary>
    public SelfHostScalePrediction ScalePrediction { get; init; } = new();

    /// <summary>
    /// API server metrics.
    /// </summary>
    public SelfHostApiMetrics Api { get; init; } = new();

    /// <summary>
    /// Background worker metrics.
    /// </summary>
    public SelfHostWorkerMetrics Workers { get; init; } = new();

    /// <summary>
    /// Database metrics.
    /// </summary>
    public SelfHostDatabaseMetrics Database { get; init; } = new();

    /// <summary>
    /// Traffic and load metrics.
    /// </summary>
    public SelfHostTrafficMetrics Traffic { get; init; } = new();

    /// <summary>
    /// When these metrics were collected.
    /// </summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Current load status for real-time updates.
/// </summary>
public sealed record SelfHostLoadStatus
{
    public required bool IsDemoMode { get; init; }
    public bool HasError { get; init; }
    public string? ErrorMessage { get; init; }

    public double CurrentRps { get; init; }
    public double P95LatencyMs { get; init; }
    public int PendingTasks { get; init; }
    public int ActiveConnections { get; init; }
    public double DbConnectionUtilization { get; init; }

    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Scale prediction and recommendations.
/// </summary>
public sealed record SelfHostScalePrediction
{
    public int CurrentApiInstances { get; init; } = 1;
    public int RecommendedApiInstances { get; init; } = 1;
    public string ApiScaleIndicator { get; init; } = "stable"; // "stable", "scale_up", "scale_down"
    public string ApiReason { get; init; } = "Load within normal parameters";

    public int CurrentWorkerInstances { get; init; } = 1;
    public int RecommendedWorkerInstances { get; init; } = 1;
    public string WorkerScaleIndicator { get; init; } = "stable";
    public string WorkerReason { get; init; } = "Queue depth normal";

    public string DbRiskLevel { get; init; } = "Low"; // "Low", "Medium", "High"
    public double DbConnectionUtilization { get; init; }
    public string DbSuggestion { get; init; } = "Database capacity adequate";
}

/// <summary>
/// API server metrics.
/// </summary>
public sealed record SelfHostApiMetrics
{
    public int InstanceCount { get; init; } = 1;
    public double CurrentRps { get; init; }
    public double AvgRps15Min { get; init; }
    public double PeakRps15Min { get; init; }
    public double P50LatencyMs { get; init; }
    public double P95LatencyMs { get; init; }
    public double P99LatencyMs { get; init; }
    public long RequestsLastHour { get; init; }
    public long ErrorsLastHour { get; init; }
    public double ErrorRate { get; init; }
}

/// <summary>
/// Background worker metrics.
/// </summary>
public sealed record SelfHostWorkerMetrics
{
    public int InstanceCount { get; init; } = 1;
    public int PendingTasks { get; init; }
    public int RunningJobs { get; init; }
    public int FailedLastHour { get; init; }
    public int CompletedLastHour { get; init; }
    public double AvgProcessingTimeMs { get; init; }
}

/// <summary>
/// Database metrics.
/// </summary>
public sealed record SelfHostDatabaseMetrics
{
    public int ActiveConnections { get; init; }
    public int IdleConnections { get; init; }
    public int TotalConnections { get; init; }
    public int MaxConnections { get; init; } = 100;
    public double ConnectionUtilization { get; init; }
    public long DatabaseSizeBytes { get; init; }
    public double CacheHitRatio { get; init; }
    public string Status { get; init; } = "connected";
}

/// <summary>
/// Traffic and load metrics.
/// </summary>
public sealed record SelfHostTrafficMetrics
{
    public double CurrentRps { get; init; }
    public double AvgRps { get; init; }
    public double PeakRps { get; init; }
    public string TrendDirection { get; init; } = "stable"; // "increasing", "stable", "decreasing"
    public double GrowthRate { get; init; }
}

/// <summary>
/// Configuration for self-host metrics mode.
/// </summary>
public sealed class SelfHostMetricsConfig
{
    /// <summary>
    /// Metrics mode: "live", "demo", or "auto" (auto-detect).
    /// </summary>
    public string Mode { get; set; } = "live";

    /// <summary>
    /// Prometheus URL for metrics scraping (optional).
    /// </summary>
    public string? PrometheusUrl { get; set; }

    /// <summary>
    /// Timeout for metrics fetch operations.
    /// </summary>
    public TimeSpan FetchTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How often to refresh cached metrics.
    /// </summary>
    public TimeSpan CacheExpiry { get; set; } = TimeSpan.FromSeconds(30);
}
