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

    /// <summary>
    /// Records usage after a successful operation and consumes credits.
    /// Returns any triggered usage alerts.
    /// </summary>
    Task<UsageRecordResult> RecordUsageAsync(
        string tenantId,
        string workspaceId,
        UsageEventType eventType,
        Guid? memoryId = null,
        string? userId = null,
        Guid? sessionId = null,
        int? latencyMs = null,
        bool success = true,
        string? errorMessage = null,
        Dictionary<string, object>? metadata = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets usage alerts that should be shown to the user.
    /// </summary>
    Task<IReadOnlyList<UsageAlert>> GetActiveAlertsAsync(
        string tenantId,
        string workspaceId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of recording usage.
/// </summary>
public sealed class UsageRecordResult
{
    public bool Success { get; init; }
    public decimal CreditsConsumed { get; init; }
    public decimal CreditsRemaining { get; init; }
    public decimal UsagePercentage { get; init; }
    public IReadOnlyList<UsageAlert> TriggeredAlerts { get; init; } = [];
}

/// <summary>
/// Usage alert for threshold notifications.
/// </summary>
public sealed class UsageAlert
{
    public UsageAlertType AlertType { get; init; }
    public int ThresholdPercent { get; init; }
    public decimal CurrentPercent { get; init; }
    public string Message { get; init; } = "";
    public DateTimeOffset TriggeredAt { get; init; }
    public bool Acknowledged { get; init; }
}

/// <summary>
/// Types of usage alerts.
/// </summary>
public enum UsageAlertType
{
    /// <summary>75% of credit limit reached.</summary>
    CreditWarning75,
    /// <summary>90% of credit limit reached.</summary>
    CreditWarning90,
    /// <summary>100% of credit limit reached.</summary>
    CreditLimitReached,
    /// <summary>75% of memory limit reached.</summary>
    MemoryWarning75,
    /// <summary>90% of memory limit reached.</summary>
    MemoryWarning90,
    /// <summary>Memory limit reached.</summary>
    MemoryLimitReached
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
