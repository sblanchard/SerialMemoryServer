using SerialMemory.Core.Interfaces;

namespace SerialMemory.Core.Deployment;

/// <summary>
/// Service that determines whether quotas should be enforced based on deployment mode.
/// </summary>
public interface IQuotaEnforcementService
{
    /// <summary>
    /// Whether quota enforcement is currently active.
    /// </summary>
    bool IsEnforcing { get; }

    /// <summary>
    /// Checks if a usage limit should be enforced.
    /// Returns true if the limit should be enforced, false if it should be bypassed.
    /// </summary>
    bool ShouldEnforceLimit(string tenantId);

    /// <summary>
    /// Gets the effective quota multiplier (1.0 = normal, 999999 = unlimited).
    /// </summary>
    decimal GetEffectiveQuotaMultiplier(string tenantId);
}

/// <summary>
/// Default implementation that respects deployment mode settings.
/// </summary>
public sealed class QuotaEnforcementService : IQuotaEnforcementService
{
    private readonly IDeploymentContext _deploymentContext;

    public QuotaEnforcementService(IDeploymentContext deploymentContext)
    {
        _deploymentContext = deploymentContext;
    }

    public bool IsEnforcing => _deploymentContext.QuotasEnabled;

    public bool ShouldEnforceLimit(string tenantId)
    {
        // In SaaS mode, always enforce
        if (_deploymentContext.IsSaaS)
            return true;

        // In SelfHosted mode, only enforce if explicitly enabled
        return _deploymentContext.QuotasEnabled;
    }

    public decimal GetEffectiveQuotaMultiplier(string tenantId)
    {
        if (!ShouldEnforceLimit(tenantId))
            return 999999999m; // Effectively unlimited

        return 1.0m; // Normal quota
    }
}

/// <summary>
/// Extension methods for quota-aware operations.
/// </summary>
public static class QuotaExtensions
{
    /// <summary>
    /// Wraps an operation with quota checking.
    /// </summary>
    public static async Task<T> WithQuotaCheckAsync<T>(
        this IQuotaEnforcementService quotaService,
        string tenantId,
        decimal creditsRequired,
        Func<decimal, Task<decimal>> getAvailableCredits,
        Func<Task<T>> operation)
    {
        if (!quotaService.ShouldEnforceLimit(tenantId))
        {
            return await operation();
        }

        var available = await getAvailableCredits(quotaService.GetEffectiveQuotaMultiplier(tenantId));
        if (available < creditsRequired)
        {
            throw new InvalidOperationException(
                $"Insufficient credits. Required: {creditsRequired}, Available: {available}");
        }

        return await operation();
    }
}
