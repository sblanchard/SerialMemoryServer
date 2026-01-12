using System.Data;

namespace SerialMemory.Core.Interfaces;

/// <summary>
/// Factory for creating database connections with internal admin privileges.
/// Used for system operations that need to bypass tenant RLS:
/// - Kill switches and emergency cutoffs
/// - Billing and usage metering
/// - Integrity checks and privacy auditing
/// - Event sourcing and mutation processing
/// - Background worker operations
///
/// IMPORTANT: Only use for internal system operations, never for user requests.
/// </summary>
public interface IInternalDbConnectionFactory
{
    /// <summary>
    /// Opens a connection with internal admin role set.
    /// This connection bypasses ALL RLS policies.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>An open IDbConnection with app.role = 'internal_admin'</returns>
    /// <remarks>
    /// WARNING: This connection has full access to ALL tenant data.
    /// Only use for cross-tenant system operations.
    /// </remarks>
    Task<IDbConnection> OpenInternalAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a connection with internal admin role AND tenant context.
    /// Use this when the worker needs elevated access but still wants
    /// to filter by tenant (e.g., processing a specific tenant's queue).
    /// </summary>
    /// <param name="tenantId">The tenant ID for context (not filtering)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>An open connection with both app.role and app.current_tenant_id set</returns>
    Task<IDbConnection> OpenInternalWithTenantAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default);
}