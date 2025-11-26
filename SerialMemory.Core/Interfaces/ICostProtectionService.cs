namespace SerialMemory.Core.Interfaces;

/// <summary>
/// Service for protecting against cost blow-ups.
/// Implements budget ceilings and emergency cutoff systems.
/// </summary>
public interface ICostProtectionService
{
    /// <summary>
    /// Checks if an operation can proceed given budget constraints.
    /// </summary>
    Task<BudgetCheckResult> CheckBudgetAsync(
        string tenantId,
        decimal estimatedCost,
        CancellationToken ct = default);

    /// <summary>
    /// Records cost consumption for a tenant.
    /// </summary>
    Task RecordCostAsync(
        string tenantId,
        decimal cost,
        string operation,
        CancellationToken ct = default);

    /// <summary>
    /// Triggers emergency cutoff for a tenant.
    /// </summary>
    Task TriggerEmergencyCutoffAsync(
        string tenantId,
        string reason,
        TimeSpan? duration = null,
        CancellationToken ct = default);

    /// <summary>
    /// Lifts emergency cutoff for a tenant.
    /// </summary>
    Task LiftEmergencyCutoffAsync(
        string tenantId,
        string reason,
        CancellationToken ct = default);

    /// <summary>
    /// Gets the current budget status for a tenant.
    /// </summary>
    Task<BudgetStatus> GetBudgetStatusAsync(
        string tenantId,
        CancellationToken ct = default);

    /// <summary>
    /// Sets a budget alert threshold for a tenant.
    /// </summary>
    Task SetAlertThresholdAsync(
        string tenantId,
        BudgetAlertThreshold threshold,
        CancellationToken ct = default);

    /// <summary>
    /// Gets tenants approaching or exceeding budget limits.
    /// </summary>
    Task<IReadOnlyList<TenantBudgetAlert>> GetBudgetAlertsAsync(
        CancellationToken ct = default);
}

/// <summary>
/// Result of a budget check.
/// </summary>
public sealed record BudgetCheckResult
{
    public required bool IsAllowed { get; init; }
    public required decimal CurrentUsage { get; init; }
    public required decimal BudgetCeiling { get; init; }
    public required decimal EstimatedCost { get; init; }
    public BudgetViolation? Violation { get; init; }

    public decimal RemainingBudget => BudgetCeiling - CurrentUsage;
    public decimal UsagePercentage => BudgetCeiling > 0 ? (CurrentUsage / BudgetCeiling) * 100 : 0;
}

/// <summary>
/// A budget violation.
/// </summary>
public sealed record BudgetViolation
{
    public required BudgetViolationType Type { get; init; }
    public required string Code { get; init; }
    public required string Message { get; init; }
    public DateTimeOffset? ResumesAt { get; init; }
}

/// <summary>
/// Types of budget violations.
/// </summary>
public enum BudgetViolationType
{
    /// <summary>
    /// Hard budget ceiling exceeded.
    /// </summary>
    CeilingExceeded,

    /// <summary>
    /// Soft budget warning threshold reached.
    /// </summary>
    WarningThreshold,

    /// <summary>
    /// Emergency cutoff is active.
    /// </summary>
    EmergencyCutoff,

    /// <summary>
    /// Suspected abuse pattern detected.
    /// </summary>
    AbuseDetected,

    /// <summary>
    /// Billing issue (payment failed, etc.).
    /// </summary>
    BillingIssue
}

/// <summary>
/// Current budget status for a tenant.
/// </summary>
public sealed record BudgetStatus
{
    public required string TenantId { get; init; }
    public required decimal CurrentUsage { get; init; }
    public required decimal BudgetCeiling { get; init; }
    public required decimal RemainingBudget { get; init; }
    public required decimal UsagePercentage { get; init; }
    public required bool IsEmergencyCutoffActive { get; init; }
    public DateTimeOffset? EmergencyCutoffEndsAt { get; init; }
    public string? EmergencyCutoffReason { get; init; }
    public required BudgetPeriod CurrentPeriod { get; init; }
    public IReadOnlyList<BudgetAlertThreshold>? ActiveAlerts { get; init; }
}

/// <summary>
/// Budget period information.
/// </summary>
public sealed record BudgetPeriod
{
    public required DateTimeOffset Start { get; init; }
    public required DateTimeOffset End { get; init; }
    public required int DaysRemaining { get; init; }
}

/// <summary>
/// Budget alert threshold configuration.
/// </summary>
public sealed record BudgetAlertThreshold
{
    public required decimal Percentage { get; init; }
    public required string AlertType { get; init; }
    public required bool Enabled { get; init; }
    public string? NotificationEmail { get; init; }
    public string? WebhookUrl { get; init; }
}

/// <summary>
/// A tenant budget alert.
/// </summary>
public sealed record TenantBudgetAlert
{
    public required string TenantId { get; init; }
    public required string TenantName { get; init; }
    public required decimal UsagePercentage { get; init; }
    public required decimal CurrentUsage { get; init; }
    public required decimal BudgetCeiling { get; init; }
    public required BudgetAlertSeverity Severity { get; init; }
    public required string Message { get; init; }
}

/// <summary>
/// Alert severity levels.
/// </summary>
public enum BudgetAlertSeverity
{
    Info,      // 50% usage
    Warning,   // 75% usage
    Critical,  // 90% usage
    Emergency  // 100%+ usage or cutoff
}
