using SerialMemory.Core.Interfaces;

namespace SerialMemory.Core.Services;

/// <summary>
/// AsyncLocal-based tenant context for ambient tenant information.
/// Thread-safe and async-safe.
/// </summary>
public sealed class TenantContext : IMutableTenantContext
{
    private static readonly AsyncLocal<TenantContextData?> _current = new();

    public string TenantId => _current.Value?.TenantId
        ?? throw new InvalidOperationException("Tenant context not set. Call SetContext before accessing TenantId.");

    public string WorkspaceId => _current.Value?.WorkspaceId
        ?? throw new InvalidOperationException("Tenant context not set. Call SetContext before accessing WorkspaceId.");

    public string? UserId => _current.Value?.UserId;

    public Guid? SessionId => _current.Value?.SessionId;

    public void SetContext(string tenantId, string workspaceId, string? userId = null, Guid? sessionId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

        _current.Value = new TenantContextData(tenantId, workspaceId, userId, sessionId);
    }

    public void Clear()
    {
        _current.Value = null;
    }

    /// <summary>
    /// Creates a scope that automatically clears context on disposal.
    /// </summary>
    public static IDisposable CreateScope(string tenantId, string workspaceId, string? userId = null, Guid? sessionId = null)
    {
        var context = new TenantContext();
        context.SetContext(tenantId, workspaceId, userId, sessionId);
        return new TenantContextScope(context);
    }

    /// <summary>
    /// Tries to get the current tenant context without throwing.
    /// </summary>
    public static bool TryGetCurrent(out ITenantContext? context)
    {
        var data = _current.Value;
        if (data != null)
        {
            context = new TenantContext();
            return true;
        }
        context = null;
        return false;
    }

    /// <summary>
    /// Gets whether a tenant context is currently set.
    /// </summary>
    public static bool IsSet => _current.Value != null;

    private sealed record TenantContextData(
        string TenantId,
        string WorkspaceId,
        string? UserId,
        Guid? SessionId);

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
    public FixedTenantContext(string tenantId, string workspaceId, string? userId = null, Guid? sessionId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

        TenantId = tenantId;
        WorkspaceId = workspaceId;
        UserId = userId;
        SessionId = sessionId;
    }

    public string TenantId { get; }
    public string WorkspaceId { get; }
    public string? UserId { get; }
    public Guid? SessionId { get; }

    /// <summary>
    /// Creates a default self-hosted tenant context.
    /// </summary>
    public static FixedTenantContext SelfHosted => new("self", "default");
}
