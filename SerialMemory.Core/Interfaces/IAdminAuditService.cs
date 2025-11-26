namespace SerialMemory.Core.Interfaces;

/// <summary>
/// Service for tamper-evident audit logging of admin tool invocations.
/// Uses SHA-256 hash chaining for integrity verification.
/// </summary>
public interface IAdminAuditService
{
    /// <summary>
    /// Logs an admin action BEFORE execution.
    /// Returns an action ID to complete after execution.
    /// </summary>
    /// <param name="tenantId">Tenant UUID.</param>
    /// <param name="userId">User who invoked the tool.</param>
    /// <param name="toolName">Name of the admin tool.</param>
    /// <param name="parameters">Tool parameters (will be hashed).</param>
    /// <param name="metadata">Optional additional metadata.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The created admin action entry with its ID.</returns>
    Task<AdminActionEntry> LogActionAsync(
        Guid tenantId,
        string userId,
        string toolName,
        object? parameters,
        Dictionary<string, object>? metadata = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Completes an admin action after execution.
    /// Updates execution time and success status.
    /// </summary>
    /// <param name="actionId">The action ID returned by LogActionAsync.</param>
    /// <param name="executionMs">Execution time in milliseconds.</param>
    /// <param name="success">Whether the tool executed successfully.</param>
    /// <param name="errorMessage">Error message if failed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task CompleteActionAsync(
        Guid actionId,
        int executionMs,
        bool success,
        string? errorMessage = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies the integrity of the admin action hash chain for a tenant.
    /// Detects tampering, missing entries, or broken chains.
    /// </summary>
    /// <param name="tenantId">Tenant UUID to verify.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Verification result with details of any issues.</returns>
    Task<AdminChainVerificationResult> VerifyChainAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets recent admin actions for a tenant.
    /// </summary>
    /// <param name="tenantId">Tenant UUID.</param>
    /// <param name="limit">Maximum number of entries to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of admin action entries.</returns>
    Task<IReadOnlyList<AdminActionEntry>> GetRecentActionsAsync(
        Guid tenantId,
        int limit = 100,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets admin actions by user.
    /// </summary>
    /// <param name="tenantId">Tenant UUID.</param>
    /// <param name="userId">User ID to filter by.</param>
    /// <param name="limit">Maximum number of entries to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of admin action entries.</returns>
    Task<IReadOnlyList<AdminActionEntry>> GetActionsByUserAsync(
        Guid tenantId,
        string userId,
        int limit = 100,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets admin actions by tool name.
    /// </summary>
    /// <param name="tenantId">Tenant UUID.</param>
    /// <param name="toolName">Tool name to filter by.</param>
    /// <param name="limit">Maximum number of entries to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of admin action entries.</returns>
    Task<IReadOnlyList<AdminActionEntry>> GetActionsByToolAsync(
        Guid tenantId,
        string toolName,
        int limit = 100,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets admin actions in a date range.
    /// </summary>
    /// <param name="tenantId">Tenant UUID.</param>
    /// <param name="from">Start of date range.</param>
    /// <param name="to">End of date range.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of admin action entries.</returns>
    Task<IReadOnlyList<AdminActionEntry>> GetActionsInRangeAsync(
        Guid tenantId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// An admin action audit log entry.
/// </summary>
public sealed record AdminActionEntry
{
    /// <summary>
    /// Unique identifier for this action.
    /// </summary>
    public required Guid Id { get; init; }

    /// <summary>
    /// Tenant that owns this action.
    /// </summary>
    public required Guid TenantId { get; init; }

    /// <summary>
    /// User who performed the action.
    /// </summary>
    public required string UserId { get; init; }

    /// <summary>
    /// Name of the admin tool invoked.
    /// </summary>
    public required string ToolName { get; init; }

    /// <summary>
    /// SHA-256 hash of the tool parameters.
    /// </summary>
    public required string ParamsHash { get; init; }

    /// <summary>
    /// When the action was logged.
    /// </summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// Hash of the previous entry in the chain.
    /// </summary>
    public required string PreviousHash { get; init; }

    /// <summary>
    /// SHA-256 hash of this entry.
    /// </summary>
    public required string Hash { get; init; }

    /// <summary>
    /// Execution time in milliseconds (set after completion).
    /// </summary>
    public int? ExecutionMs { get; init; }

    /// <summary>
    /// Whether the action succeeded (set after completion).
    /// </summary>
    public bool Success { get; init; } = true;

    /// <summary>
    /// Error message if failed (set after completion).
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Additional metadata.
    /// </summary>
    public Dictionary<string, object>? Metadata { get; init; }
}

/// <summary>
/// Result of verifying an admin action hash chain.
/// </summary>
public sealed record AdminChainVerificationResult
{
    /// <summary>
    /// Whether the entire chain is valid.
    /// </summary>
    public required bool IsValid { get; init; }

    /// <summary>
    /// Total number of entries in the chain.
    /// </summary>
    public required int TotalEntries { get; init; }

    /// <summary>
    /// Number of entries verified before first issue.
    /// </summary>
    public required int VerifiedEntries { get; init; }

    /// <summary>
    /// ID of the first broken entry (if any).
    /// </summary>
    public Guid? FirstBrokenId { get; init; }

    /// <summary>
    /// Description of the first issue found (if any).
    /// </summary>
    public string? FirstBrokenReason { get; init; }
}
