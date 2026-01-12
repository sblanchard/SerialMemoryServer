using SerialMemory.Core.Models;

namespace SerialMemory.Core.Interfaces;

/// <summary>
/// Service for enforcing usage quotas and billing limits.
/// Integrates kill switch, plan limits, rate limits, and credit consumption.
/// </summary>
public interface IQuotaEnforcementService
{
    /// <summary>
    /// Checks if an operation is allowed given all quota constraints.
    /// This is the main entry point - combines kill switch, budget, rate limits.
    /// </summary>
    Task<QuotaCheckResult> CheckAsync(
        string tenantId,
        string workspaceId,
        UsageEventType operation,
        int units = 1,
        CancellationToken ct = default);

    /// <summary>
    /// Records usage after an operation completes successfully.
    /// Updates usage events, rollups, and cost records.
    /// </summary>
    Task<QuotaRecordResult> RecordAsync(
        string tenantId,
        string workspaceId,
        UsageEventType operation,
        int units = 1,
        Guid? memoryId = null,
        string? userId = null,
        int? latencyMs = null,
        bool success = true,
        string? errorMessage = null,
        CancellationToken ct = default);

    /// <summary>
    /// Gets current quota status for a tenant/workspace.
    /// </summary>
    Task<QuotaStatus> GetStatusAsync(
        string tenantId,
        string workspaceId,
        CancellationToken ct = default);

    /// <summary>
    /// Checks and triggers usage alerts if thresholds are crossed.
    /// Called automatically by RecordAsync but can be called manually.
    /// </summary>
    Task<IReadOnlyList<TriggeredAlert>> CheckAlertsAsync(
        string tenantId,
        string workspaceId,
        CancellationToken ct = default);

    /// <summary>
    /// Gets the credit cost for an operation.
    /// </summary>
    decimal GetOperationCost(UsageEventType operation, int units = 1);
}

/// <summary>
/// Result of checking if an operation is allowed.
/// </summary>
public sealed record QuotaCheckResult
{
    /// <summary>
    /// Whether the operation is allowed to proceed.
    /// </summary>
    public required bool IsAllowed { get; init; }

    /// <summary>
    /// Credits that will be consumed by this operation.
    /// </summary>
    public required decimal CreditsCost { get; init; }

    /// <summary>
    /// Credits remaining in the current billing cycle.
    /// </summary>
    public required decimal CreditsRemaining { get; init; }

    /// <summary>
    /// Total credits allocated for the current billing cycle.
    /// </summary>
    public required decimal CreditsAllocated { get; init; }

    /// <summary>
    /// Current usage percentage (0-100+).
    /// </summary>
    public decimal UsagePercentage => CreditsAllocated > 0
        ? ((CreditsAllocated - CreditsRemaining) / CreditsAllocated) * 100
        : 0;

    /// <summary>
    /// Remaining rate limit requests in the current window.
    /// </summary>
    public int? RateLimitRemaining { get; init; }

    /// <summary>
    /// Rate limit window in seconds.
    /// </summary>
    public int? RateLimitWindowSeconds { get; init; }

    /// <summary>
    /// When the rate limit resets.
    /// </summary>
    public DateTimeOffset? RateLimitResetsAt { get; init; }

    /// <summary>
    /// Violation details if not allowed.
    /// </summary>
    public QuotaViolation? Violation { get; init; }

    /// <summary>
    /// Warnings that should be communicated but don't block the operation.
    /// </summary>
    public IReadOnlyList<QuotaWarning> Warnings { get; init; } = [];

    /// <summary>
    /// Creates an "allowed" result with costs.
    /// </summary>
    public static QuotaCheckResult Allowed(decimal cost, decimal remaining, decimal allocated) => new()
    {
        IsAllowed = true,
        CreditsCost = cost,
        CreditsRemaining = remaining,
        CreditsAllocated = allocated
    };

    /// <summary>
    /// Creates a "denied" result with violation.
    /// </summary>
    public static QuotaCheckResult Denied(QuotaViolation violation, decimal cost, decimal remaining, decimal allocated) => new()
    {
        IsAllowed = false,
        CreditsCost = cost,
        CreditsRemaining = remaining,
        CreditsAllocated = allocated,
        Violation = violation
    };
}

/// <summary>
/// Details about why a quota check failed.
/// </summary>
public sealed record QuotaViolation
{
    /// <summary>
    /// Type of violation.
    /// </summary>
    public required QuotaViolationType Type { get; init; }

    /// <summary>
    /// Machine-readable error code.
    /// </summary>
    public required string Code { get; init; }

    /// <summary>
    /// Human-readable message.
    /// </summary>
    public required string Message { get; init; }

    /// <summary>
    /// When the limitation will be lifted (if known).
    /// </summary>
    public DateTimeOffset? RetryAfter { get; init; }

    /// <summary>
    /// Additional details for debugging.
    /// </summary>
    public Dictionary<string, object>? Details { get; init; }
}

/// <summary>
/// Types of quota violations.
/// </summary>
public enum QuotaViolationType
{
    /// <summary>
    /// Kill switch is active.
    /// </summary>
    KillSwitch,

    /// <summary>
    /// Credit limit exceeded for billing cycle.
    /// </summary>
    CreditLimitExceeded,

    /// <summary>
    /// Rate limit exceeded.
    /// </summary>
    RateLimitExceeded,

    /// <summary>
    /// Maximum memories limit reached.
    /// </summary>
    MaxMemoriesExceeded,

    /// <summary>
    /// Maximum entities limit reached.
    /// </summary>
    MaxEntitiesExceeded,

    /// <summary>
    /// Plan is inactive or suspended.
    /// </summary>
    PlanInactive,

    /// <summary>
    /// Billing issue (payment failed).
    /// </summary>
    BillingIssue,

    /// <summary>
    /// Emergency cutoff is active.
    /// </summary>
    EmergencyCutoff
}

/// <summary>
/// Warning about quota status (doesn't block operation).
/// </summary>
public sealed record QuotaWarning
{
    /// <summary>
    /// Warning type.
    /// </summary>
    public required QuotaWarningType Type { get; init; }

    /// <summary>
    /// Warning message.
    /// </summary>
    public required string Message { get; init; }

    /// <summary>
    /// Threshold that triggered the warning (e.g., 80 for 80%).
    /// </summary>
    public decimal? Threshold { get; init; }

    /// <summary>
    /// Current value that triggered the warning.
    /// </summary>
    public decimal? CurrentValue { get; init; }
}

/// <summary>
/// Types of quota warnings.
/// </summary>
public enum QuotaWarningType
{
    ApproachingCreditLimit,
    ApproachingRateLimit,
    ApproachingMemoryLimit,
    NearCycleEnd
}

/// <summary>
/// Result of recording usage.
/// </summary>
public sealed record QuotaRecordResult
{
    /// <summary>
    /// Whether the record was successfully written.
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    /// Credits consumed by this operation.
    /// </summary>
    public required decimal CreditsConsumed { get; init; }

    /// <summary>
    /// Credits remaining after this operation.
    /// </summary>
    public required decimal CreditsRemaining { get; init; }

    /// <summary>
    /// Current usage percentage.
    /// </summary>
    public required decimal UsagePercentage { get; init; }

    /// <summary>
    /// Alerts that were triggered by this operation.
    /// </summary>
    public IReadOnlyList<TriggeredAlert> TriggeredAlerts { get; init; } = [];

    /// <summary>
    /// Whether the tenant should be auto-throttled after this operation.
    /// </summary>
    public bool ShouldThrottle { get; init; }
}

/// <summary>
/// An alert that was triggered.
/// </summary>
public sealed record TriggeredAlert
{
    /// <summary>
    /// Alert type.
    /// </summary>
    public required string AlertType { get; init; }

    /// <summary>
    /// Threshold percentage that was crossed.
    /// </summary>
    public required int ThresholdPercent { get; init; }

    /// <summary>
    /// Current usage percentage.
    /// </summary>
    public required decimal CurrentPercent { get; init; }

    /// <summary>
    /// Alert message.
    /// </summary>
    public required string Message { get; init; }

    /// <summary>
    /// Whether an email notification was sent.
    /// </summary>
    public bool EmailSent { get; init; }

    /// <summary>
    /// Whether a webhook was triggered.
    /// </summary>
    public bool WebhookTriggered { get; init; }

    /// <summary>
    /// When the alert was triggered.
    /// </summary>
    public required DateTimeOffset TriggeredAt { get; init; }
}

/// <summary>
/// Current quota status for a tenant/workspace.
/// </summary>
public sealed record QuotaStatus
{
    /// <summary>
    /// Plan name.
    /// </summary>
    public required string PlanName { get; init; }

    /// <summary>
    /// Plan display name.
    /// </summary>
    public required string PlanDisplayName { get; init; }

    /// <summary>
    /// Credits allocated for current cycle.
    /// </summary>
    public required decimal CreditsAllocated { get; init; }

    /// <summary>
    /// Credits used in current cycle.
    /// </summary>
    public required decimal CreditsUsed { get; init; }

    /// <summary>
    /// Credits remaining in current cycle.
    /// </summary>
    public decimal CreditsRemaining => CreditsAllocated - CreditsUsed;

    /// <summary>
    /// Current usage percentage.
    /// </summary>
    public decimal UsagePercentage => CreditsAllocated > 0
        ? (CreditsUsed / CreditsAllocated) * 100
        : 0;

    /// <summary>
    /// Billing cycle start date.
    /// </summary>
    public required DateTimeOffset CycleStart { get; init; }

    /// <summary>
    /// Billing cycle end date.
    /// </summary>
    public required DateTimeOffset CycleEnd { get; init; }

    /// <summary>
    /// Days remaining in current cycle.
    /// </summary>
    public int DaysRemaining => Math.Max(0, (int)(CycleEnd - DateTimeOffset.UtcNow).TotalDays);

    /// <summary>
    /// Rate limit per minute (if applicable).
    /// </summary>
    public int? RateLimitPerMinute { get; init; }

    /// <summary>
    /// Current requests in rate limit window.
    /// </summary>
    public int? CurrentRateCount { get; init; }

    /// <summary>
    /// Maximum memories allowed (if applicable).
    /// </summary>
    public int? MaxMemories { get; init; }

    /// <summary>
    /// Current memory count.
    /// </summary>
    public int? CurrentMemories { get; init; }

    /// <summary>
    /// Maximum entities allowed (if applicable).
    /// </summary>
    public int? MaxEntities { get; init; }

    /// <summary>
    /// Current entity count.
    /// </summary>
    public int? CurrentEntities { get; init; }

    /// <summary>
    /// Whether any kill switch is active.
    /// </summary>
    public bool KillSwitchActive { get; init; }

    /// <summary>
    /// Kill switch reason (if active).
    /// </summary>
    public string? KillSwitchReason { get; init; }

    /// <summary>
    /// Active alerts for this tenant.
    /// </summary>
    public IReadOnlyList<ActiveAlert> ActiveAlerts { get; init; } = [];
}

/// <summary>
/// An active alert.
/// </summary>
public sealed record ActiveAlert
{
    public required string AlertType { get; init; }
    public required int ThresholdPercent { get; init; }
    public required string Message { get; init; }
    public required DateTimeOffset TriggeredAt { get; init; }
    public bool Acknowledged { get; init; }
}
