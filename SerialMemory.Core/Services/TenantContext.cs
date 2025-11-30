using SerialMemory.Core.Interfaces;

namespace SerialMemory.Core.Services;

/// <summary>
/// Scoped tenant context for ambient tenant information.
/// Uses instance state (not static AsyncLocal) for proper DI scoping.
/// </summary>
public sealed class TenantContext : IMutableTenantContext
{
    private TenantContextData? _data;

    public string TenantId => _data?.TenantId
        ?? throw new InvalidOperationException("Tenant context not set. Call SetContext before accessing TenantId.");

    public string WorkspaceId => _data?.WorkspaceId
        ?? throw new InvalidOperationException("Tenant context not set. Call SetContext before accessing WorkspaceId.");

    public string? UserId => _data?.UserId;

    public string? UserEmail => _data?.UserEmail;

    public string? UserRole => _data?.UserRole;

    public Guid? SessionId => _data?.SessionId;

    public bool IsLabMode => _data?.IsLabMode ?? false;

    public bool AllowPowerMode => _data?.AllowPowerMode ?? false;

    public bool IsRootAdmin => _data?.IsRootAdmin ?? false;

    public bool IsOwner => string.Equals(UserRole, "owner", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<string> Scopes => _data?.Scopes ?? Array.Empty<string>();

    public void SetContext(
        string tenantId,
        string workspaceId,
        string? userId = null,
        string? userEmail = null,
        string? userRole = null,
        Guid? sessionId = null,
        bool isLabMode = false,
        bool allowPowerMode = false,
        bool isRootAdmin = false,
        IReadOnlyList<string>? scopes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

        _data = new TenantContextData(
            tenantId,
            workspaceId,
            userId,
            userEmail,
            userRole,
            sessionId,
            isLabMode,
            allowPowerMode,
            isRootAdmin,
            scopes ?? Array.Empty<string>());
    }

    public void Clear()
    {
        _data = null;
    }

    /// <summary>
    /// Tries to get the current tenant context without throwing.
    /// </summary>
    public bool TryGetCurrent(out ITenantContext? context)
    {
        if (_data != null)
        {
            context = this;
            return true;
        }
        context = null;
        return false;
    }

    /// <summary>
    /// Gets whether a tenant context is currently set.
    /// </summary>
    public bool IsSet => _data != null;

    private sealed record TenantContextData(
        string TenantId,
        string WorkspaceId,
        string? UserId,
        string? UserEmail,
        string? UserRole,
        Guid? SessionId,
        bool IsLabMode,
        bool AllowPowerMode,
        bool IsRootAdmin,
        IReadOnlyList<string> Scopes);
}

/// <summary>
/// Fixed tenant context for testing or single-tenant scenarios.
/// </summary>
public sealed class FixedTenantContext : ITenantContext
{
    public FixedTenantContext(
        string tenantId,
        string workspaceId,
        string? userId = null,
        string? userEmail = null,
        string? userRole = null,
        Guid? sessionId = null,
        bool isLabMode = false,
        bool allowPowerMode = false,
        bool isRootAdmin = false,
        IReadOnlyList<string>? scopes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

        TenantId = tenantId;
        WorkspaceId = workspaceId;
        UserId = userId;
        UserEmail = userEmail;
        UserRole = userRole;
        SessionId = sessionId;
        IsLabMode = isLabMode;
        AllowPowerMode = allowPowerMode;
        IsRootAdmin = isRootAdmin;
        Scopes = scopes ?? Array.Empty<string>();
    }

    public string TenantId { get; }
    public string WorkspaceId { get; }
    public string? UserId { get; }
    public string? UserEmail { get; }
    public string? UserRole { get; }
    public Guid? SessionId { get; }
    public bool IsLabMode { get; }
    public bool AllowPowerMode { get; }
    public bool IsRootAdmin { get; }
    public bool IsOwner => string.Equals(UserRole, "owner", StringComparison.OrdinalIgnoreCase);
    public IReadOnlyList<string> Scopes { get; }

    /// <summary>
    /// Creates a default self-hosted tenant context with full power mode access.
    /// </summary>
    public static FixedTenantContext SelfHosted => new(
        "00000000-0000-0000-0000-000000000000",
        "default",
        userRole: "owner",
        isLabMode: true,
        allowPowerMode: true,
        scopes: new[] { "serialmemory.core", "serialmemory.admin", "serialmemory.export", "serialmemory.delete", "serialmemory.power" });
}
