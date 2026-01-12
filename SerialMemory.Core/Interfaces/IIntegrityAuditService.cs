namespace SerialMemory.Core.Interfaces;

/// <summary>
/// Service for logging and querying integrity audit entries.
/// Provides complete audit trail for all memory integrity operations.
/// </summary>
public interface IIntegrityAuditService
{
    /// <summary>
    /// Logs an integrity audit entry.
    /// </summary>
    Task LogAsync(IntegrityAuditEntry entry, CancellationToken ct = default);

    /// <summary>
    /// Gets audit entries for a tenant with optional filters.
    /// </summary>
    Task<IntegrityPagedResult<IntegrityAuditEntry>> GetAuditEntriesAsync(
        IntegrityAuditQueryOptions options,
        CancellationToken ct = default);

    /// <summary>
    /// Gets audit entries for a specific memory.
    /// </summary>
    Task<IReadOnlyList<IntegrityAuditEntry>> GetMemoryAuditTrailAsync(
        Guid memoryId,
        CancellationToken ct = default);

    /// <summary>
    /// Gets tenant privacy/integrity settings.
    /// </summary>
    Task<TenantIntegritySettings> GetSettingsAsync(CancellationToken ct = default);

    /// <summary>
    /// Updates tenant privacy/integrity settings.
    /// </summary>
    Task<TenantIntegritySettings> UpdateSettingsAsync(
        TenantIntegritySettingsUpdate update,
        CancellationToken ct = default);

    /// <summary>
    /// Checks if audit logging is enabled for the tenant.
    /// </summary>
    Task<bool> IsAuditEnabledAsync(CancellationToken ct = default);
}

/// <summary>
/// A single integrity audit log entry.
/// </summary>
public sealed record IntegrityAuditEntry
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid TenantId { get; init; }
    public string? UserId { get; init; }
    public IntegrityAuditAction Action { get; init; }
    public Guid? MemoryId { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public string? IpAddress { get; init; }
    public string? UserAgent { get; init; }
    public bool? IntegrityValid { get; init; }
    public Dictionary<string, object>? Details { get; init; }
    public Guid? RequestId { get; init; }
}

/// <summary>
/// Types of auditable integrity actions.
/// </summary>
public enum IntegrityAuditAction
{
    // Memory operations
    MemoryCreated,
    MemoryRead,
    MemoryUpdated,
    MemoryDeleted,
    MemorySearched,
    MemoryExported,

    // Integrity operations
    IntegrityComputed,
    IntegrityVerified,
    IntegrityFailed,
    IntegrityRepaired,
    ChainVerified,
    ChainFailed,

    // Privacy operations
    SettingsRead,
    SettingsUpdated,
    AuditLogQueried,

    // System
    BatchVerificationStarted,
    BatchVerificationCompleted
}

/// <summary>
/// Query options for fetching audit entries.
/// </summary>
public sealed record IntegrityAuditQueryOptions
{
    public IntegrityAuditAction? Action { get; init; }
    public string? UserId { get; init; }
    public Guid? MemoryId { get; init; }
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    public bool? IntegrityValid { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 50;
    public string SortBy { get; init; } = "timestamp";
    public bool SortDescending { get; init; } = true;
}

/// <summary>
/// Tenant integrity settings.
/// </summary>
public sealed record TenantIntegritySettings
{
    public Guid TenantId { get; init; }
    public bool EnablePrivacyMode { get; init; }
    public bool EnableHashVerification { get; init; } = true;
    public bool EnableAuditLogs { get; init; } = true;
    public string HashAlgorithm { get; init; } = "SHA256";
    public int AutoVerifyIntervalHours { get; init; } = 24;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>
/// Update request for integrity settings.
/// </summary>
public sealed record TenantIntegritySettingsUpdate
{
    public bool? EnablePrivacyMode { get; init; }
    public bool? EnableHashVerification { get; init; }
    public bool? EnableAuditLogs { get; init; }
    public string? HashAlgorithm { get; init; }
    public int? AutoVerifyIntervalHours { get; init; }
}

/// <summary>
/// Generic paged result for integrity audit.
/// </summary>
public sealed record IntegrityPagedResult<T>
{
    public IReadOnlyList<T> Items { get; init; } = [];
    public int TotalCount { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }
    public int TotalPages => (int)Math.Ceiling((double)TotalCount / PageSize);
    public bool HasNextPage => Page < TotalPages;
    public bool HasPreviousPage => Page > 1;
}
