using SerialMemory.Core.Models;

namespace SerialMemory.Core.Interfaces;

/// <summary>
/// Service for enforcing usage limits and rate limits.
/// </summary>
public interface IUsageLimitService
{
    /// <summary>
    /// Checks if the operation can proceed given current limits.
    /// Throws UsageLimitExceededException if limits are exceeded.
    /// </summary>
    Task<UsageLimitCheckResult> CheckLimitsAsync(
        string tenantId,
        string workspaceId,
        UsageEventType eventType,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a rate-limited request for sliding window tracking.
    /// </summary>
    Task RecordRateLimitHitAsync(
        string tenantId,
        string workspaceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the current usage summary for the billing cycle.
    /// </summary>
    Task<UsageSummary> GetUsageSummaryAsync(
        string tenantId,
        string workspaceId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of a usage limit check.
/// </summary>
public sealed class UsageLimitCheckResult
{
    public bool IsAllowed { get; init; } = true;
    public decimal CreditsRemaining { get; init; }
    public decimal CreditsRequired { get; init; }
    public int? RateLimitRemaining { get; init; }
    public int? RateLimitWindow { get; init; }
    public UsageLimitViolation? Violation { get; init; }
}

/// <summary>
/// Details about a limit violation.
/// </summary>
public sealed class UsageLimitViolation
{
    public UsageLimitType LimitType { get; init; }
    public string Message { get; init; } = "";
    public string Code { get; init; } = "";
    public DateTimeOffset? RetryAfter { get; init; }
    public Dictionary<string, object>? Details { get; init; }
}

/// <summary>
/// Type of usage limit.
/// </summary>
public enum UsageLimitType
{
    /// <summary>
    /// Hard credit limit per billing cycle exceeded.
    /// </summary>
    CreditLimitExceeded,

    /// <summary>
    /// Per-minute rate limit exceeded.
    /// </summary>
    RateLimitExceeded,

    /// <summary>
    /// Maximum memories limit for plan exceeded.
    /// </summary>
    MaxMemoriesExceeded,

    /// <summary>
    /// Maximum entities limit for plan exceeded.
    /// </summary>
    MaxEntitiesExceeded,

    /// <summary>
    /// Plan is inactive or suspended.
    /// </summary>
    PlanInactive
}

/// <summary>
/// Summary of current usage for a tenant.
/// </summary>
public sealed class UsageSummary
{
    public string TenantId { get; init; } = "";
    public string WorkspaceId { get; init; } = "";
    public string PlanName { get; init; } = "";
    public decimal CreditsAllocated { get; init; }
    public decimal CreditsUsed { get; init; }
    public decimal CreditsRemaining => CreditsAllocated - CreditsUsed;
    public decimal UsagePercentage => CreditsAllocated > 0 ? (CreditsUsed / CreditsAllocated) * 100 : 0;
    public DateTimeOffset CycleStart { get; init; }
    public DateTimeOffset CycleEnd { get; init; }
    public int DaysRemaining => Math.Max(0, (int)(CycleEnd - DateTimeOffset.UtcNow).TotalDays);
    public int? RateLimitPerMinute { get; init; }
    public int? CurrentRateCount { get; init; }
    public int? MaxMemories { get; init; }
    public int? CurrentMemories { get; init; }
    public int? MaxEntities { get; init; }
    public int? CurrentEntities { get; init; }
}
