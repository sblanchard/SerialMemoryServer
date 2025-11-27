using System.Data;

namespace SerialMemory.Core.Interfaces;

/// <summary>
/// Factory for creating tenant-scoped database connections.
/// All connections returned have app.tenant_id set for RLS enforcement.
/// </summary>
public interface ITenantDbConnectionFactory
{
    /// <summary>
    /// Opens a connection with tenant context set for RLS.
    /// </summary>
    /// <param name="tenantId">The tenant ID to scope the connection to</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>An open IDbConnection with app.tenant_id set</returns>
    /// <remarks>
    /// This method MUST:
    /// 1. Open the connection
    /// 2. Execute SET app.tenant_id = @tenantId
    /// 3. Return the connection ready for Dapper queries
    ///
    /// RLS policies will automatically filter all queries to the specified tenant.
    /// </remarks>
    Task<IDbConnection> OpenAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a connection using the current ITenantContext.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>An open IDbConnection with app.tenant_id set from ITenantContext</returns>
    Task<IDbConnection> OpenAsync(CancellationToken cancellationToken = default);
}
