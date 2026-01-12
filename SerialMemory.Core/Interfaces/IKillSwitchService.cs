namespace SerialMemory.Core.Interfaces;

/// <summary>
/// Service for managing kill switches at global, tenant, and API key levels.
/// Combines signals from emergency_cutoffs, tenant_settings, and deployment mode.
/// </summary>
public interface IKillSwitchService
{
    /// <summary>
    /// Gets the effective kill switch state for a tenant/API key combination.
    /// Results are cached for 5-10 seconds per tenant to avoid DB churn.
    /// </summary>
    Task<KillSwitchState> GetStateAsync(
        string? tenantId,
        string? apiKeyId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Enables global kill switch (affects all tenants).
    /// </summary>
    Task EnableGlobalKillSwitchAsync(
        string reason,
        string triggeredBy,
        TimeSpan? duration = null,
        CancellationToken ct = default);

    /// <summary>
    /// Disables global kill switch.
    /// </summary>
    Task DisableGlobalKillSwitchAsync(
        string reason,
        string liftedBy,
        CancellationToken ct = default);

    /// <summary>
    /// Enables global read-only mode.
    /// </summary>
    Task EnableGlobalReadOnlyAsync(
        string reason,
        string triggeredBy,
        TimeSpan? duration = null,
        CancellationToken ct = default);

    /// <summary>
    /// Disables global read-only mode.
    /// </summary>
    Task DisableGlobalReadOnlyAsync(
        string reason,
        string liftedBy,
        CancellationToken ct = default);

    /// <summary>
    /// Enables tenant kill switch (disables all operations for tenant).
    /// </summary>
    Task EnableTenantKillSwitchAsync(
        string tenantId,
        string reason,
        string triggeredBy,
        TimeSpan? duration = null,
        CancellationToken ct = default);

    /// <summary>
    /// Disables tenant kill switch.
    /// </summary>
    Task DisableTenantKillSwitchAsync(
        string tenantId,
        string reason,
        string liftedBy,
        CancellationToken ct = default);

    /// <summary>
    /// Sets tenant to read-only mode.
    /// </summary>
    Task EnableTenantReadOnlyAsync(
        string tenantId,
        string reason,
        string triggeredBy,
        TimeSpan? duration = null,
        CancellationToken ct = default);

    /// <summary>
    /// Disables tenant read-only mode.
    /// </summary>
    Task DisableTenantReadOnlyAsync(
        string tenantId,
        string reason,
        string liftedBy,
        CancellationToken ct = default);

    /// <summary>
    /// Sets tenant to no-ingest mode (blocks only memory ingestion).
    /// </summary>
    Task EnableTenantNoIngestAsync(
        string tenantId,
        string reason,
        string triggeredBy,
        TimeSpan? duration = null,
        CancellationToken ct = default);

    /// <summary>
    /// Disables tenant no-ingest mode.
    /// </summary>
    Task DisableTenantNoIngestAsync(
        string tenantId,
        string reason,
        string liftedBy,
        CancellationToken ct = default);

    /// <summary>
    /// Disables a specific API key.
    /// </summary>
    Task DisableApiKeyAsync(
        string apiKeyId,
        string reason,
        string disabledBy,
        CancellationToken ct = default);

    /// <summary>
    /// Re-enables a specific API key.
    /// </summary>
    Task EnableApiKeyAsync(
        string apiKeyId,
        string reason,
        string enabledBy,
        CancellationToken ct = default);

    /// <summary>
    /// Gets all active kill switch states for admin dashboard.
    /// </summary>
    Task<KillSwitchDashboardState> GetDashboardStateAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets recent kill switch actions for audit trail.
    /// </summary>
    Task<IReadOnlyList<KillSwitchAction>> GetRecentActionsAsync(
        int limit = 50,
        CancellationToken ct = default);

    /// <summary>
    /// Invalidates cache for a specific tenant.
    /// </summary>
    void InvalidateCache(string? tenantId = null);
}

/// <summary>
/// Effective kill switch state for a request.
/// </summary>
public sealed record KillSwitchState
{
    /// <summary>
    /// Global kill switch is active - reject ALL requests.
    /// </summary>
    public bool GlobalOff { get; init; }

    /// <summary>
    /// Global read-only mode - allow GET, block write verbs.
    /// </summary>
    public bool GlobalReadOnly { get; init; }

    /// <summary>
    /// Tenant-specific kill switch - reject all requests for this tenant.
    /// </summary>
    public bool TenantOff { get; init; }

    /// <summary>
    /// Tenant read-only mode - allow GET, block write verbs.
    /// </summary>
    public bool TenantReadOnly { get; init; }

    /// <summary>
    /// Tenant no-ingest mode - block only ingest endpoints.
    /// </summary>
    public bool TenantNoIngest { get; init; }

    /// <summary>
    /// Specific API key is disabled.
    /// </summary>
    public bool ApiKeyDisabled { get; init; }

    /// <summary>
    /// Human-readable reason for the active kill switch.
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>
    /// When the kill switch expires (null = indefinite).
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>
    /// Whether any blocking kill switch is active.
    /// </summary>
    public bool IsBlocked => GlobalOff || TenantOff || ApiKeyDisabled;

    /// <summary>
    /// Whether read-only mode is active (global or tenant).
    /// </summary>
    public bool IsReadOnly => GlobalReadOnly || TenantReadOnly;

    /// <summary>
    /// Whether ingest operations are blocked.
    /// </summary>
    public bool IsIngestBlocked => IsBlocked || TenantNoIngest;

    /// <summary>
    /// Gets the most specific active reason.
    /// </summary>
    public string GetEffectiveReason()
    {
        if (GlobalOff) return Reason ?? "Service globally disabled";
        if (TenantOff) return Reason ?? "Tenant disabled";
        if (ApiKeyDisabled) return Reason ?? "API key disabled";
        if (GlobalReadOnly) return Reason ?? "Service in read-only mode";
        if (TenantReadOnly) return Reason ?? "Tenant in read-only mode";
        if (TenantNoIngest) return Reason ?? "Memory ingestion disabled";
        return "OK";
    }

    /// <summary>
    /// Gets the error code for the active kill switch.
    /// </summary>
    public string GetErrorCode()
    {
        if (GlobalOff) return "GLOBAL_SERVICE_DISABLED";
        if (TenantOff) return "TENANT_DISABLED";
        if (ApiKeyDisabled) return "API_KEY_DISABLED";
        if (GlobalReadOnly) return "GLOBAL_READ_ONLY";
        if (TenantReadOnly) return "TENANT_READ_ONLY";
        if (TenantNoIngest) return "INGEST_DISABLED";
        return "OK";
    }

    /// <summary>
    /// Creates an "all clear" state.
    /// </summary>
    public static KillSwitchState AllClear => new();
}

/// <summary>
/// Kill switch state for admin dashboard.
/// </summary>
public sealed record KillSwitchDashboardState
{
    public required bool GlobalKillSwitchActive { get; init; }
    public required bool GlobalReadOnlyActive { get; init; }
    public string? GlobalReason { get; init; }
    public DateTimeOffset? GlobalExpiresAt { get; init; }
    public required IReadOnlyList<TenantKillSwitchInfo> TenantKillSwitches { get; init; }
    public required IReadOnlyList<DisabledApiKeyInfo> DisabledApiKeys { get; init; }
}

/// <summary>
/// Information about a tenant kill switch.
/// </summary>
public sealed record TenantKillSwitchInfo
{
    public required string TenantId { get; init; }
    public required string TenantName { get; init; }
    public required KillSwitchMode Mode { get; init; }
    public required string Reason { get; init; }
    public required string TriggeredBy { get; init; }
    public required DateTimeOffset TriggeredAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
}

/// <summary>
/// Kill switch mode for a tenant.
/// </summary>
public enum KillSwitchMode
{
    Off,        // Completely disabled
    ReadOnly,   // Read-only access
    NoIngest    // No memory ingestion
}

/// <summary>
/// Information about a disabled API key.
/// </summary>
public sealed record DisabledApiKeyInfo
{
    public required string ApiKeyId { get; init; }
    public required string TenantId { get; init; }
    public required string KeyPrefix { get; init; }
    public required string Reason { get; init; }
    public required string DisabledBy { get; init; }
    public required DateTimeOffset DisabledAt { get; init; }
}

/// <summary>
/// A kill switch action for audit trail.
/// </summary>
public sealed record KillSwitchAction
{
    public required Guid Id { get; init; }
    public required string ActionType { get; init; }
    public required string TargetType { get; init; }
    public string? TargetId { get; init; }
    public required string Reason { get; init; }
    public required string PerformedBy { get; init; }
    public required DateTimeOffset PerformedAt { get; init; }
    public TimeSpan? Duration { get; init; }
    public Dictionary<string, object>? Metadata { get; init; }
}
