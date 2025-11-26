using SerialMemory.Core.Models;

namespace SerialMemory.Core.Interfaces;

/// <summary>
/// Service for managing tenant plans and subscriptions.
/// </summary>
public interface IPlanService
{
    /// <summary>
    /// Gets all available plans.
    /// </summary>
    Task<IReadOnlyList<TenantPlan>> GetAvailablePlansAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a specific plan by name.
    /// </summary>
    Task<TenantPlan?> GetPlanByNameAsync(
        string planName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the current plan for a tenant.
    /// </summary>
    Task<TenantSubscription?> GetSubscriptionAsync(
        string tenantId,
        string workspaceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Subscribes a tenant to a plan.
    /// Creates a new billing cycle.
    /// </summary>
    Task<TenantSubscription> SubscribeAsync(
        string tenantId,
        string workspaceId,
        string planName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Upgrades a tenant to a higher plan.
    /// Pro-rates remaining credits.
    /// </summary>
    Task<PlanChangeResult> UpgradePlanAsync(
        string tenantId,
        string workspaceId,
        string newPlanName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Downgrades a tenant to a lower plan.
    /// Takes effect at the start of the next billing cycle.
    /// </summary>
    Task<PlanChangeResult> DowngradePlanAsync(
        string tenantId,
        string workspaceId,
        string newPlanName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels a tenant subscription.
    /// </summary>
    Task CancelSubscriptionAsync(
        string tenantId,
        string workspaceId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Represents a tenant's subscription to a plan.
/// </summary>
public sealed class TenantSubscription
{
    public Guid Id { get; init; }
    public string TenantId { get; init; } = "";
    public string WorkspaceId { get; init; } = "";
    public Guid PlanId { get; init; }
    public string PlanName { get; init; } = "";
    public SubscriptionStatus Status { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CancelledAt { get; init; }
    public DateTimeOffset? EffectiveEndDate { get; init; }
    public string? PendingPlanChange { get; init; }
    public DateTimeOffset? PendingChangeDate { get; init; }
    public BillingCycle? CurrentCycle { get; init; }
}

/// <summary>
/// Subscription status.
/// </summary>
public enum SubscriptionStatus
{
    Active,
    PastDue,
    Cancelled,
    Suspended,
    PendingDowngrade
}

/// <summary>
/// Result of a plan change operation.
/// </summary>
public sealed class PlanChangeResult
{
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public string OldPlanName { get; init; } = "";
    public string NewPlanName { get; init; } = "";
    public DateTimeOffset EffectiveDate { get; init; }
    public decimal? CreditAdjustment { get; init; }
    public BillingCycle? NewCycle { get; init; }
}
