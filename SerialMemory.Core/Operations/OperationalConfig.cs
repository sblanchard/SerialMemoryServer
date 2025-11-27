namespace SerialMemory.Core.Operations;

/// <summary>
/// Operational configuration for production hardening.
/// All values are read from environment variables.
/// </summary>
public sealed class OperationalConfig
{
    // Panic Switches (operator kill switches)
    public bool DisableWrites { get; init; }
    public bool DisableSignups { get; init; }
    public bool ReadOnlyMode { get; init; }
    public bool MaintenanceMode { get; init; }

    // Health Check Settings
    public int HealthCheckTimeoutSeconds { get; init; } = 5;
    public int DatabaseHealthCheckTimeoutSeconds { get; init; } = 3;

    // Observability Settings
    public bool EnableStructuredLogging { get; init; } = true;
    public bool EnableRequestCorrelation { get; init; } = true;
    public string LogLevel { get; init; } = "Information";

    // Rate Limiting (operator override)
    public int? GlobalRateLimitPerMinute { get; init; }
    public int? TenantRateLimitPerMinute { get; init; }

    /// <summary>
    /// Creates config from environment variables.
    /// </summary>
    public static OperationalConfig FromEnvironment()
    {
        return new OperationalConfig
        {
            // Panic Switches
            DisableWrites = GetBoolEnv("DISABLE_WRITES"),
            DisableSignups = GetBoolEnv("DISABLE_SIGNUPS"),
            ReadOnlyMode = GetBoolEnv("READ_ONLY_MODE"),
            MaintenanceMode = GetBoolEnv("MAINTENANCE_MODE"),

            // Health Check Settings
            HealthCheckTimeoutSeconds = GetIntEnv("HEALTH_CHECK_TIMEOUT_SECONDS", 5),
            DatabaseHealthCheckTimeoutSeconds = GetIntEnv("DB_HEALTH_CHECK_TIMEOUT_SECONDS", 3),

            // Observability
            EnableStructuredLogging = GetBoolEnv("ENABLE_STRUCTURED_LOGGING", defaultValue: true),
            EnableRequestCorrelation = GetBoolEnv("ENABLE_REQUEST_CORRELATION", defaultValue: true),
            LogLevel = Environment.GetEnvironmentVariable("LOG_LEVEL") ?? "Information",

            // Rate Limiting
            GlobalRateLimitPerMinute = GetNullableIntEnv("GLOBAL_RATE_LIMIT_PER_MINUTE"),
            TenantRateLimitPerMinute = GetNullableIntEnv("TENANT_RATE_LIMIT_PER_MINUTE"),
        };
    }

    private static bool GetBoolEnv(string name, bool defaultValue = false)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrEmpty(value)) return defaultValue;
        return value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("1", StringComparison.Ordinal) ||
               value.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    private static int GetIntEnv(string name, int defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return int.TryParse(value, out var result) ? result : defaultValue;
    }

    private static int? GetNullableIntEnv(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return int.TryParse(value, out var result) ? result : null;
    }

    /// <summary>
    /// Returns true if any panic switch is active.
    /// </summary>
    public bool HasActivePanicSwitch => DisableWrites || DisableSignups || ReadOnlyMode || MaintenanceMode;

    /// <summary>
    /// Gets a summary of active panic switches for logging/status.
    /// </summary>
    public string GetPanicSwitchStatus()
    {
        var active = new List<string>();
        if (DisableWrites) active.Add("DISABLE_WRITES");
        if (DisableSignups) active.Add("DISABLE_SIGNUPS");
        if (ReadOnlyMode) active.Add("READ_ONLY_MODE");
        if (MaintenanceMode) active.Add("MAINTENANCE_MODE");

        return active.Count > 0
            ? $"PANIC SWITCHES ACTIVE: {string.Join(", ", active)}"
            : "No panic switches active";
    }
}
