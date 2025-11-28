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

    public Guid? SessionId => _data?.SessionId;

    public bool IsLabMode => _data?.IsLabMode ?? false;

    public bool AllowPowerMode => _data?.AllowPowerMode ?? false;

    public IReadOnlyList<string> Scopes => _data?.Scopes ?? Array.Empty<string>();

    public void SetContext(
        string tenantId,
        string workspaceId,
        string? userId = null,
        Guid? sessionId = null,
        bool isLabMode = false,
        bool allowPowerMode = false,
        IReadOnlyList<string>? scopes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

        _data = new TenantContextData(
            tenantId,
            workspaceId,
            userId,
            sessionId,
            isLabMode,
            allowPowerMode,
            scopes ?? Array.Empty<string>());
    }

    public void Clear()
    {
        _data = null;
    }

    /// <summary>
    /// Creates a scope that automatically clears context on disposal.
    /// </summary>
    public static IDisposable CreateScope(
        string tenantId,
        string workspaceId,
        string? userId = null,
        Guid? sessionId = null,
        bool isLabMode = false,
        bool allowPowerMode = false,
        IReadOnlyList<string>? scopes = null)
    {
        var context = new TenantContext();
        context.SetContext(tenantId, workspaceId, userId, sessionId, isLabMode, allowPowerMode, scopes);
        return new TenantContextScope(context);
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
        Guid? SessionId,
        bool IsLabMode,
        bool AllowPowerMode,
        IReadOnlyList<string> Scopes);

    private sealed class TenantContextScope : IDisposable
    {
        private readonly TenantContext _context;

        public TenantContextScope(TenantContext context)
        {
            _context = context;
        }

        public void Dispose()
        {
            _context.Clear();
        }
    }
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
        Guid? sessionId = null,
        bool isLabMode = false,
        bool allowPowerMode = false,
        IReadOnlyList<string>? scopes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

        TenantId = tenantId;
        WorkspaceId = workspaceId;
        UserId = userId;
        SessionId = sessionId;
        IsLabMode = isLabMode;
        AllowPowerMode = allowPowerMode;
        Scopes = scopes ?? Array.Empty<string>();
    }

    public string TenantId { get; }
    public string WorkspaceId { get; }
    public string? UserId { get; }
    public Guid? SessionId { get; }
    public bool IsLabMode { get; }
    public bool AllowPowerMode { get; }
    public IReadOnlyList<string> Scopes { get; }

    /// <summary>
    /// Creates a default self-hosted tenant context with full power mode access.
    /// </summary>
    public static FixedTenantContext SelfHosted => new(
        "00000000-0000-0000-0000-000000000000",
        "default",
        isLabMode: true,
        allowPowerMode: true,
        scopes: new[] { "serialmemory.core", "serialmemory.admin", "serialmemory.export", "serialmemory.delete", "serialmemory.power" });
}
