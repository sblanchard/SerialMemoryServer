namespace SerialMemory.Core.Interfaces;

/// <summary>
/// Provides tenant context for multi-tenant operations.
/// Automatically injected from MCP auth/session layer.
/// </summary>
public interface ITenantContext
{
    /// <summary>
    /// The tenant identifier. Must not be null or empty.
    /// </summary>
    string TenantId { get; }

    /// <summary>
    /// The workspace identifier within the tenant.
    /// </summary>
    string WorkspaceId { get; }

    /// <summary>
    /// Optional user identifier from the session.
    /// </summary>
    string? UserId { get; }

    /// <summary>
    /// Optional session identifier.
    /// </summary>
    Guid? SessionId { get; }
}

/// <summary>
/// Mutable tenant context for setting values at runtime.
/// </summary>
public interface IMutableTenantContext : ITenantContext
{
    /// <summary>
    /// Sets the tenant context values.
    /// </summary>
    void SetContext(string tenantId, string workspaceId, string? userId = null, Guid? sessionId = null);

    /// <summary>
    /// Clears the current tenant context.
    /// </summary>
    void Clear();
}
