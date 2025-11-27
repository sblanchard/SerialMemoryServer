namespace SerialMemory.Core.Interfaces;

/// <summary>
/// Service for audit-quality logging with tamper detection.
/// </summary>
public interface IAuditLogService
{
    /// <summary>
    /// Appends an audit log entry. Returns the entry with computed hash.
    /// </summary>
    Task<AuditLogEntry> AppendAsync(
        string tenantId,
        string workspaceId,
        AuditEventType eventType,
        string description,
        Dictionary<string, object>? data = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies the integrity of the audit log for a billing cycle.
    /// Returns any detected tampering or gaps.
    /// </summary>
    Task<AuditVerificationResult> VerifyIntegrityAsync(
        Guid billingCycleId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets audit log entries for a billing cycle.
    /// </summary>
    Task<IReadOnlyList<AuditLogEntry>> GetEntriesAsync(
        Guid billingCycleId,
        int? limit = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets audit log entries by date range.
    /// </summary>
    Task<IReadOnlyList<AuditLogEntry>> GetEntriesByDateRangeAsync(
        string tenantId,
        string workspaceId,
        DateTimeOffset fromDate,
        DateTimeOffset toDate,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Types of audit events.
/// </summary>
public enum AuditEventType
{
    // Usage events
    UsageEventRecorded,
    UsageLimitExceeded,
    RateLimitExceeded,

    // Plan events
    PlanSubscribed,
    PlanUpgraded,
    PlanDowngraded,
    PlanCancelled,

    // Billing events
    BillingCycleCreated,
    BillingCycleClosed,
    CreditAdjustment,
    CreditCarryOver,
    CreditExpired,

    // Admin events
    TenantCreated,
    TenantSuspended,
    TenantReactivated,
    SettingsChanged,

    // Export events
    UsageExported,

    // Security events
    AuthenticationFailed,
    UnauthorizedAccess,
    IntegrityViolation
}

/// <summary>
/// Immutable audit log entry with hash chain.
/// </summary>
public sealed class AuditLogEntry
{
    public Guid Id { get; init; }
    public long SequenceNumber { get; init; }
    public string TenantId { get; init; } = "";
    public string WorkspaceId { get; init; } = "";
    public Guid? BillingCycleId { get; init; }
    public AuditEventType EventType { get; init; }
    public string Description { get; init; } = "";
    public Dictionary<string, object>? Data { get; init; }
    public string? UserId { get; init; }
    public string? IpAddress { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public string PreviousHash { get; init; } = "";
    public string ContentHash { get; init; } = "";
}

/// <summary>
/// Result of audit log integrity verification.
/// </summary>
public sealed class AuditVerificationResult
{
    public bool IsValid { get; init; }
    public int TotalEntries { get; init; }
    public int VerifiedEntries { get; init; }
    public List<AuditIntegrityIssue> Issues { get; init; } = [];
    public DateTimeOffset VerifiedAt { get; init; } = DateTimeOffset.UtcNow;
    public string? FirstEntryHash { get; init; }
    public string? LastEntryHash { get; init; }
}

/// <summary>
/// Details about an integrity issue.
/// </summary>
public sealed class AuditIntegrityIssue
{
    public AuditIntegrityIssueType IssueType { get; init; }
    public long? SequenceNumber { get; init; }
    public Guid? EntryId { get; init; }
    public string Description { get; init; } = "";
    public string? ExpectedHash { get; init; }
    public string? ActualHash { get; init; }
}

/// <summary>
/// Types of integrity issues.
/// </summary>
public enum AuditIntegrityIssueType
{
    HashMismatch,
    SequenceGap,
    ChainBroken,
    MissingEntry,
    DuplicateSequence,
    TimestampAnomaly
}
