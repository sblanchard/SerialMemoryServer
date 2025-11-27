namespace SerialMemory.Core.Deployment;

/// <summary>
/// Defines the deployment mode for the SerialMemory system.
/// </summary>
public enum DeploymentMode
{
    /// <summary>
    /// Public multi-tenant SaaS deployment.
    /// Strict tenant isolation, billing enforcement, feature restrictions.
    /// </summary>
    PublicSaaS,

    /// <summary>
    /// Self-hosted deployment.
    /// Single tenant or private deployment with full feature access by default.
    /// </summary>
    SelfHosted
}

/// <summary>
/// Provides deployment context information.
/// </summary>
public interface IDeploymentContext
{
    /// <summary>
    /// The current deployment mode.
    /// </summary>
    DeploymentMode Mode { get; }

    /// <summary>
    /// True if running in PublicSaaS mode.
    /// </summary>
    bool IsSaaS => Mode == DeploymentMode.PublicSaaS;

    /// <summary>
    /// True if running in SelfHosted mode.
    /// </summary>
    bool IsSelfHosted => Mode == DeploymentMode.SelfHosted;

    /// <summary>
    /// Whether quotas/billing are enabled.
    /// Always true in SaaS, configurable in SelfHosted.
    /// </summary>
    bool QuotasEnabled { get; }

    /// <summary>
    /// Whether power mode is globally disabled (safety override).
    /// </summary>
    bool PowerModeGloballyDisabled { get; }

    /// <summary>
    /// License key for self-hosted deployments (stub for future use).
    /// </summary>
    string? LicenseKey { get; }

    /// <summary>
    /// Instance identifier for telemetry/support.
    /// </summary>
    string InstanceId { get; }
}

/// <summary>
/// Default implementation of IDeploymentContext.
/// Reads configuration from environment variables.
/// </summary>
public sealed class DeploymentContext : IDeploymentContext
{
    public DeploymentMode Mode { get; }
    public bool IsSaaS => Mode == DeploymentMode.PublicSaaS;
    public bool IsSelfHosted => Mode == DeploymentMode.SelfHosted;
    public bool QuotasEnabled { get; }
    public bool PowerModeGloballyDisabled { get; }
    public string? LicenseKey { get; }
    public string InstanceId { get; }

    public DeploymentContext()
    {
        // Read deployment mode from environment
        var modeStr = Environment.GetEnvironmentVariable("SERIALMEMORY_DEPLOYMENT_MODE");
        Mode = modeStr?.ToLowerInvariant() switch
        {
            "selfhosted" or "self-hosted" or "self_hosted" => DeploymentMode.SelfHosted,
            "saas" or "publicsaas" or "public_saas" => DeploymentMode.PublicSaaS,
            _ => DeploymentMode.PublicSaaS // Default to SaaS for safety
        };

        // Quotas: always enabled in SaaS, configurable in SelfHosted
        if (Mode == DeploymentMode.PublicSaaS)
        {
            QuotasEnabled = true;
        }
        else
        {
            var quotasStr = Environment.GetEnvironmentVariable("SERIALMEMORY_ENABLE_QUOTAS");
            QuotasEnabled = quotasStr?.ToLowerInvariant() is "true" or "1" or "yes";
        }

        // Power mode can be globally disabled
        var disablePowerStr = Environment.GetEnvironmentVariable("SERIALMEMORY_DISABLE_POWER_MODE");
        PowerModeGloballyDisabled = disablePowerStr?.ToLowerInvariant() is "true" or "1" or "yes";

        // License key for self-hosted
        LicenseKey = Environment.GetEnvironmentVariable("SERIALMEMORY_LICENSE_KEY");

        // Generate or read instance ID
        InstanceId = Environment.GetEnvironmentVariable("SERIALMEMORY_INSTANCE_ID")
            ?? GenerateInstanceId();
    }

    private static string GenerateInstanceId()
    {
        // Try to read from a persistent file, otherwise generate new
        var idPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SerialMemory",
            "instance_id");

        try
        {
            if (File.Exists(idPath))
            {
                return File.ReadAllText(idPath).Trim();
            }

            var newId = $"sm-{Guid.NewGuid():N}"[..16];
            Directory.CreateDirectory(Path.GetDirectoryName(idPath)!);
            File.WriteAllText(idPath, newId);
            return newId;
        }
        catch
        {
            // Fallback to a session-based ID
            return $"sm-temp-{Guid.NewGuid():N}"[..20];
        }
    }
}
