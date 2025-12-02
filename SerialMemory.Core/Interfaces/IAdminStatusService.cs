namespace SerialMemory.Core.Interfaces;

/// <summary>
/// Service for retrieving system-wide admin status and metrics.
/// FIX #11: Self-Host Admin Stats - MUST WORK IN SAAS mode.
/// </summary>
public interface IAdminStatusService
{
    /// <summary>
    /// Get comprehensive system status including runtime, memory, CPU, and database info.
    /// </summary>
    Task<SystemStatus> GetSystemStatusAsync(CancellationToken ct = default);

    /// <summary>
    /// Get detailed system metrics for all entities.
    /// </summary>
    Task<SystemMetrics> GetSystemMetricsAsync(CancellationToken ct = default);

    /// <summary>
    /// Get recent API activity statistics.
    /// </summary>
    Task<ApiActivityStats> GetApiActivityAsync(
        int hours = 24,
        CancellationToken ct = default);

    /// <summary>
    /// Get database health and performance metrics.
    /// </summary>
    Task<DatabaseHealth> GetDatabaseHealthAsync(CancellationToken ct = default);
}

/// <summary>
/// Comprehensive system status.
/// </summary>
public sealed class SystemStatus
{
    public string Status { get; init; } = "healthy";
    public bool SelfHostedMode { get; init; }
    public RuntimeInfo Runtime { get; init; } = new();
    public CpuInfo Cpu { get; init; } = new();
    public MemoryInfo Memory { get; init; } = new();
    public DatabaseStatus Database { get; init; } = new();
    public ApiStats Api { get; init; } = new();
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Runtime information.
/// </summary>
public sealed class RuntimeInfo
{
    public string MachineName { get; init; } = "";
    public string OsVersion { get; init; } = "";
    public string ProcessArchitecture { get; init; } = "";
    public string FrameworkVersion { get; init; } = "";
    public int ProcessId { get; init; }
    public double UptimeSeconds { get; init; }
    public DateTimeOffset StartTime { get; init; }
}

/// <summary>
/// CPU information.
/// </summary>
public sealed class CpuInfo
{
    public double UsagePercent { get; init; }
    public int ProcessorCount { get; init; }
}

/// <summary>
/// Memory information.
/// </summary>
public sealed class MemoryInfo
{
    public long WorkingSetBytes { get; init; }
    public double WorkingSetMb { get; init; }
    public long PrivateMemoryBytes { get; init; }
    public double PrivateMemoryMb { get; init; }
    public long GcTotalMemory { get; init; }
    public double GcTotalMemoryMb { get; init; }
    public long HeapSizeBytes { get; init; }
    public double HeapSizeMb { get; init; }
    public int Gen0Collections { get; init; }
    public int Gen1Collections { get; init; }
    public int Gen2Collections { get; init; }
}

/// <summary>
/// Database status information.
/// </summary>
public sealed class DatabaseStatus
{
    public string Status { get; init; } = "connected";
    public long SizeBytes { get; init; }
    public double SizeMb { get; init; }
    public int ActiveConnections { get; init; }
    public string? Version { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// API statistics.
/// </summary>
public sealed class ApiStats
{
    public long RequestsLastHour { get; init; }
    public long ErrorsLastHour { get; init; }
    public double? AvgDurationMs { get; init; }
}

/// <summary>
/// Comprehensive system metrics.
/// </summary>
public sealed class SystemMetrics
{
    public long TotalMemories { get; init; }
    public long TotalEntities { get; init; }
    public long TotalRelationships { get; init; }
    public long TotalTenants { get; init; }
    public long TotalUsers { get; init; }
    public long ActiveApiKeys { get; init; }
    public long ActiveSessions { get; init; }
    public long MemoriesLast24Hours { get; init; }
    public long EventsLastHour { get; init; }
    public long DatabaseSizeBytes { get; init; }
}

/// <summary>
/// API activity statistics.
/// </summary>
public sealed class ApiActivityStats
{
    public long TotalRequests { get; init; }
    public long SuccessfulRequests { get; init; }
    public long FailedRequests { get; init; }
    public double SuccessRate { get; init; }
    public double AvgResponseTimeMs { get; init; }
    public double P95ResponseTimeMs { get; init; }
    public double P99ResponseTimeMs { get; init; }
    public Dictionary<string, long> RequestsByEndpoint { get; init; } = new();
    public Dictionary<int, long> RequestsByStatusCode { get; init; } = new();
    public List<HourlyApiActivity> HourlyActivity { get; init; } = [];
}

/// <summary>
/// Hourly API activity breakdown.
/// </summary>
public sealed class HourlyApiActivity
{
    public DateTimeOffset Hour { get; init; }
    public long Requests { get; init; }
    public long Errors { get; init; }
    public double AvgDurationMs { get; init; }
}

/// <summary>
/// Database health information.
/// </summary>
public sealed class DatabaseHealth
{
    public string Status { get; init; } = "healthy";
    public long SizeBytes { get; init; }
    public double SizeMb { get; init; }
    public int ActiveConnections { get; init; }
    public int MaxConnections { get; init; }
    public double ConnectionUsagePercent { get; init; }
    public string PostgresVersion { get; init; } = "";
    public long TotalTablesSize { get; init; }
    public long TotalIndexesSize { get; init; }
    public Dictionary<string, long> TableSizes { get; init; } = new();
    public bool ReplicationEnabled { get; init; }
    public double CacheHitRatio { get; init; }
    public long TransactionsPerSecond { get; init; }
}
