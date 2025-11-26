using SerialMemory.Core.Models;

namespace SerialMemory.Core.Interfaces;

/// <summary>
/// Interface for usage metering service.
/// All logging is non-blocking to prevent usage tracking from affecting core functionality.
/// </summary>
public interface IUsageService
{
    /// <summary>
    /// The tenant ID for this service instance.
    /// </summary>
    string TenantId { get; }

    /// <summary>
    /// The workspace ID for this service instance.
    /// </summary>
    string WorkspaceId { get; }

    /// <summary>
    /// Records a usage event asynchronously (fire-and-forget, non-blocking).
    /// </summary>
    void TrackUsage(
        UsageEventType eventType,
        Guid? memoryId = null,
        string? userId = null,
        Guid? sessionId = null,
        int? latencyMs = null,
        bool success = true,
        string? errorMessage = null,
        Dictionary<string, object>? metadata = null);

    /// <summary>
    /// Gets the current billing cycle for the configured tenant/workspace.
    /// </summary>
    Task<BillingCycle?> GetCurrentBillingCycleAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the current billing cycle for a specific tenant/workspace.
    /// </summary>
    Task<BillingCycle?> GetCurrentBillingCycleAsync(
        string tenantId,
        string workspaceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets usage statistics for the configured tenant/workspace.
    /// </summary>
    Task<IReadOnlyList<UsageDailyRollup>> GetUsageStatsAsync(
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets usage statistics for a specific tenant/workspace.
    /// </summary>
    Task<IReadOnlyList<UsageDailyRollup>> GetUsageStatsAsync(
        DateOnly from,
        DateOnly to,
        string tenantId,
        string workspaceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets recent usage events for the configured tenant/workspace.
    /// </summary>
    Task<IReadOnlyList<UsageEvent>> GetRecentEventsAsync(
        int limit = 100,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets recent usage events for a specific tenant/workspace.
    /// </summary>
    Task<IReadOnlyList<UsageEvent>> GetRecentEventsAsync(
        int limit,
        string tenantId,
        string workspaceId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Factory for creating tenant-scoped usage services.
/// </summary>
public interface IUsageServiceFactory
{
    /// <summary>
    /// Creates a usage service for the specified tenant/workspace.
    /// </summary>
    IUsageService CreateForTenant(string tenantId, string workspaceId);

    /// <summary>
    /// Creates a usage service using the current tenant context.
    /// </summary>
    IUsageService CreateFromContext(ITenantContext context);
}
