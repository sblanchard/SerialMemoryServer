using System.Text.Json.Serialization;

namespace SerialMemory.Core.Operations;

/// <summary>
/// Health check result for a single check.
/// </summary>
public sealed class HealthCheckResult
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("status")]
    public string Status { get; init; } = "unhealthy";

    [JsonPropertyName("duration_ms")]
    public long DurationMs { get; init; }

    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; init; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; init; }

    public bool IsHealthy => Status == "healthy";
}

/// <summary>
/// Aggregated health status for all checks.
/// </summary>
public sealed class HealthStatus
{
    [JsonPropertyName("status")]
    public string Status { get; init; } = "unhealthy";

    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    [JsonPropertyName("version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Version { get; init; }

    [JsonPropertyName("environment")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Environment { get; init; }

    [JsonPropertyName("checks")]
    public List<HealthCheckResult> Checks { get; init; } = [];

    [JsonPropertyName("panic_switches")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PanicSwitchStatus? PanicSwitches { get; init; }

    public bool IsHealthy => Status == "healthy";
}

/// <summary>
/// Status of panic switches for health reporting.
/// </summary>
public sealed class PanicSwitchStatus
{
    [JsonPropertyName("any_active")]
    public bool AnyActive { get; init; }

    [JsonPropertyName("disable_writes")]
    public bool DisableWrites { get; init; }

    [JsonPropertyName("disable_signups")]
    public bool DisableSignups { get; init; }

    [JsonPropertyName("read_only_mode")]
    public bool ReadOnlyMode { get; init; }

    [JsonPropertyName("maintenance_mode")]
    public bool MaintenanceMode { get; init; }
}

/// <summary>
/// Interface for health check implementations.
/// </summary>
public interface IHealthCheck
{
    string Name { get; }
    Task<HealthCheckResult> CheckAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Liveness check - verifies the process is running.
/// Always returns healthy (if we can execute code, we're alive).
/// </summary>
public sealed class LivenessCheck : IHealthCheck
{
    public string Name => "liveness";

    public Task<HealthCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new HealthCheckResult
        {
            Name = Name,
            Status = "healthy",
            DurationMs = 0,
            Message = "Process is running"
        });
    }
}

// Database and Redis health checks are in SerialMemory.Infrastructure.HealthChecks

/// <summary>
/// Health check coordinator that runs all registered checks.
/// </summary>
public sealed class HealthCheckService
{
    private readonly List<IHealthCheck> _checks = [];
    private readonly OperationalConfig _config;
    private readonly string? _version;
    private readonly string? _environment;

    public HealthCheckService(OperationalConfig config, string? version = null, string? environment = null)
    {
        _config = config;
        _version = version;
        _environment = environment ?? Environment.GetEnvironmentVariable("ENVIRONMENT") ?? "development";
    }

    public void AddCheck(IHealthCheck check) => _checks.Add(check);

    /// <summary>
    /// Runs all health checks and returns aggregated status.
    /// </summary>
    public async Task<HealthStatus> CheckAllAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<HealthCheckResult>();

        foreach (var check in _checks)
        {
            try
            {
                var result = await check.CheckAsync(cancellationToken);
                results.Add(result);
            }
            catch (Exception ex)
            {
                results.Add(new HealthCheckResult
                {
                    Name = check.Name,
                    Status = "unhealthy",
                    Error = ex.Message
                });
            }
        }

        var allHealthy = results.All(r => r.IsHealthy);

        return new HealthStatus
        {
            Status = allHealthy ? "healthy" : "unhealthy",
            Version = _version,
            Environment = _environment,
            Checks = results,
            PanicSwitches = new PanicSwitchStatus
            {
                AnyActive = _config.HasActivePanicSwitch,
                DisableWrites = _config.DisableWrites,
                DisableSignups = _config.DisableSignups,
                ReadOnlyMode = _config.ReadOnlyMode,
                MaintenanceMode = _config.MaintenanceMode
            }
        };
    }

    /// <summary>
    /// Runs readiness checks (all checks that determine if service can accept traffic).
    /// </summary>
    public async Task<HealthStatus> CheckReadyAsync(CancellationToken cancellationToken = default)
    {
        return await CheckAllAsync(cancellationToken);
    }

    /// <summary>
    /// Runs liveness check only (is process alive).
    /// </summary>
    public async Task<HealthStatus> CheckLiveAsync(CancellationToken cancellationToken = default)
    {
        var livenessCheck = _checks.FirstOrDefault(c => c.Name == "liveness") ?? new LivenessCheck();
        var result = await livenessCheck.CheckAsync(cancellationToken);

        return new HealthStatus
        {
            Status = result.IsHealthy ? "healthy" : "unhealthy",
            Version = _version,
            Environment = _environment,
            Checks = [result]
        };
    }

    /// <summary>
    /// Runs a specific check by name.
    /// </summary>
    public async Task<HealthCheckResult?> CheckByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        var check = _checks.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        return check != null ? await check.CheckAsync(cancellationToken) : null;
    }
}
