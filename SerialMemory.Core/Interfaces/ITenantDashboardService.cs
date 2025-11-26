namespace SerialMemory.Core.Interfaces;

/// <summary>
/// Service for tenant dashboard operations.
/// Provides tenant info, usage stats, plan details, export, and deletion.
/// </summary>
public interface ITenantDashboardService
{
    /// <summary>
    /// Gets current user information within the tenant context.
    /// </summary>
    Task<UserInfoResult> GetCurrentUserAsync(
        Guid tenantId,
        string userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets current cycle usage for a tenant/workspace.
    /// </summary>
    Task<TenantUsageResult> GetTenantUsageAsync(
        Guid tenantId,
        string workspaceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets plan details for a tenant.
    /// </summary>
    Task<TenantPlanResult> GetTenantPlanAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Requests a data export for a tenant.
    /// </summary>
    Task<ExportRequestResult> RequestExportAsync(
        Guid tenantId,
        string userId,
        ExportRequestOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the status of an export request.
    /// </summary>
    Task<ExportStatusResult?> GetExportStatusAsync(
        Guid tenantId,
        Guid exportId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Requests tenant deletion (owner only).
    /// </summary>
    Task<DeletionRequestResult> RequestDeletionAsync(
        Guid tenantId,
        string userId,
        string confirmationPhrase,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the status of a deletion request.
    /// </summary>
    Task<DeletionStatusResult?> GetDeletionStatusAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels a pending deletion request.
    /// </summary>
    Task<bool> CancelDeletionAsync(
        Guid tenantId,
        string userId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Result for GET /me endpoint.
/// </summary>
public sealed record UserInfoResult
{
    public required string UserId { get; init; }
    public required Guid TenantId { get; init; }
    public required string TenantName { get; init; }
    public required string TenantSlug { get; init; }
    public required string Role { get; init; }
    public required IReadOnlyList<string> Scopes { get; init; }
    public required IReadOnlyList<WorkspaceInfo> Workspaces { get; init; }
    public DateTimeOffset? JoinedAt { get; init; }
}

public sealed record WorkspaceInfo
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required string Slug { get; init; }
    public required bool IsDefault { get; init; }
}

/// <summary>
/// Result for GET /tenant/usage endpoint.
/// </summary>
public sealed record TenantUsageResult
{
    public required Guid TenantId { get; init; }
    public required string WorkspaceId { get; init; }
    public required decimal CreditsUsed { get; init; }
    public required decimal CreditsAllocated { get; init; }
    public required decimal CreditsRemaining { get; init; }
    public required DateTimeOffset CycleStart { get; init; }
    public required DateTimeOffset CycleEnd { get; init; }
    public required int DaysRemaining { get; init; }
    public required IReadOnlyList<UsageBreakdown> BreakdownByType { get; init; }
    public required IReadOnlyList<DailyUsage> Last7Days { get; init; }
}

public sealed record UsageBreakdown
{
    public required string EventType { get; init; }
    public required int Count { get; init; }
    public required decimal Credits { get; init; }
}

public sealed record DailyUsage
{
    public required DateOnly Date { get; init; }
    public required int EventCount { get; init; }
    public required decimal CreditsUsed { get; init; }
}

/// <summary>
/// Result for GET /tenant/plan endpoint.
/// </summary>
public sealed record TenantPlanResult
{
    public required Guid TenantId { get; init; }
    public required string PlanName { get; init; }
    public required string DisplayName { get; init; }
    public required decimal CreditsPerCycle { get; init; }
    public required int CycleDays { get; init; }
    public int? MaxMemories { get; init; }
    public int? MaxEntities { get; init; }
    public int? MaxWorkspaces { get; init; }
    public int? MaxUsers { get; init; }
    public required IReadOnlyList<string> Features { get; init; }
    public required PlanLimitsStatus LimitsStatus { get; init; }
}

public sealed record PlanLimitsStatus
{
    public required int CurrentMemories { get; init; }
    public required int CurrentEntities { get; init; }
    public required int CurrentWorkspaces { get; init; }
    public required int CurrentUsers { get; init; }
}

/// <summary>
/// Options for export request.
/// </summary>
public sealed record ExportRequestOptions
{
    public bool IncludeMemories { get; init; } = true;
    public bool IncludeEntities { get; init; } = true;
    public bool IncludeRelationships { get; init; } = true;
    public bool IncludeEvents { get; init; } = false;
    public bool IncludePersona { get; init; } = true;
    public string Format { get; init; } = "json";
    public bool Compress { get; init; } = true;
}

/// <summary>
/// Result for POST /tenant/export endpoint.
/// </summary>
public sealed record ExportRequestResult
{
    public required Guid ExportId { get; init; }
    public required string Status { get; init; }
    public required DateTimeOffset RequestedAt { get; init; }
    public DateTimeOffset? EstimatedCompletion { get; init; }
}

/// <summary>
/// Result for GET /tenant/export/{id} status check.
/// </summary>
public sealed record ExportStatusResult
{
    public required Guid ExportId { get; init; }
    public required string Status { get; init; }
    public required DateTimeOffset RequestedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string? DownloadUrl { get; init; }
    public DateTimeOffset? DownloadExpiresAt { get; init; }
    public long? FileSizeBytes { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Result for DELETE /tenant endpoint.
/// </summary>
public sealed record DeletionRequestResult
{
    public required Guid DeletionId { get; init; }
    public required string Status { get; init; }
    public required DateTimeOffset RequestedAt { get; init; }
    public required DateTimeOffset ScheduledFor { get; init; }
    public required string Message { get; init; }
}

/// <summary>
/// Result for GET /tenant/deletion status check.
/// </summary>
public sealed record DeletionStatusResult
{
    public required Guid DeletionId { get; init; }
    public required string Status { get; init; }
    public required DateTimeOffset RequestedAt { get; init; }
    public required DateTimeOffset ScheduledFor { get; init; }
    public DateTimeOffset? CancelledAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string? CancellationReason { get; init; }
}
