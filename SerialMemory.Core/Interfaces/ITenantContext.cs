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

    /// <summary>
    /// Whether the tenant is in lab mode (sandbox/testing environment).
    /// Lab mode tenants have access to experimental features and power tools in SaaS.
    /// </summary>
    bool IsLabMode { get; }

    /// <summary>
    /// Whether the tenant is allowed to use power mode features.
    /// In SaaS: requires explicit opt-in via tenant settings.
    /// In SelfHosted: true by default unless globally disabled.
    /// </summary>
    bool AllowPowerMode { get; }

    /// <summary>
    /// Scopes granted to the current API key or session.
    /// </summary>
    IReadOnlyList<string> Scopes { get; }
}

/// <summary>
/// Mutable tenant context for setting values at runtime.
/// </summary>
public interface IMutableTenantContext : ITenantContext
{
    /// <summary>
    /// Sets the tenant context values.
    /// </summary>
    void SetContext(
        string tenantId,
        string workspaceId,
        string? userId = null,
        Guid? sessionId = null,
        bool isLabMode = false,
        bool allowPowerMode = false,
        IReadOnlyList<string>? scopes = null);

    /// <summary>
    /// Clears the current tenant context.
    /// </summary>
    void Clear();
}
