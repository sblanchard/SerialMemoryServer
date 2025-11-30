using System.Data;
using Dapper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using SerialMemory.Core.Interfaces;

namespace SerialMemory.Infrastructure;

/// <summary>
/// Factory for creating database connections with internal admin privileges.
/// Used for system operations that need to bypass tenant RLS (kill switches,
/// billing, integrity checks, event sourcing, etc.)
///
/// IMPORTANT: Only use this for internal system operations, never for user requests.
/// </summary>
public sealed class InternalDbConnectionFactory : IInternalDbConnectionFactory
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<InternalDbConnectionFactory> _logger;

    /// <summary>
    /// Creates a new internal connection factory with the specified data source.
    /// </summary>
    public InternalDbConnectionFactory(
        NpgsqlDataSource dataSource,
        ILogger<InternalDbConnectionFactory>? logger = null)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _logger = logger ?? NullLogger<InternalDbConnectionFactory>.Instance;
    }

    /// <summary>
    /// Creates a new internal connection factory from a connection string.
    /// </summary>
    public InternalDbConnectionFactory(
        string connectionString,
        ILogger<InternalDbConnectionFactory>? logger = null)
    {
        if (string.IsNullOrEmpty(connectionString))
            throw new ArgumentNullException(nameof(connectionString));

        _logger = logger ?? NullLogger<InternalDbConnectionFactory>.Instance;

        var builder = new NpgsqlDataSourceBuilder(connectionString);
        builder.UseVector();
        _dataSource = builder.Build();
    }

    /// <inheritdoc />
    public async Task<IDbConnection> OpenInternalAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Opening internal admin connection");

        var connection = _dataSource.CreateConnection();

        try
        {
            await connection.OpenAsync(cancellationToken);

            // CRITICAL: Set internal admin role for RLS bypass
            // Use false to set for session (not just transaction) - ensures RLS bypass works across statements
            await connection.ExecuteAsync("SELECT set_config('app.role', 'internal_admin', false)");

            _logger.LogDebug("Internal admin connection opened successfully");
            return connection;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open internal admin connection");
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<IDbConnection> OpenInternalWithTenantAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Opening internal admin connection with tenant context {TenantId}", tenantId);

        var connection = _dataSource.CreateConnection();

        try
        {
            await connection.OpenAsync(cancellationToken);

            // Set BOTH internal admin role AND tenant context (both variables for compatibility)
            // This allows operations that need internal access but also tenant filtering
            // Use false to set for session (not just transaction) - ensures RLS bypass works across statements
            await connection.ExecuteAsync(
                """
                SELECT set_config('app.role', 'internal_admin', false);
                SELECT set_config('app.tenant_id', @TenantId, false);
                SELECT set_config('app.current_tenant_id', @TenantId, false);
                """,
                new { TenantId = tenantId.ToString() });

            _logger.LogDebug("Internal admin connection with tenant context opened for {TenantId}", tenantId);
            return connection;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open internal admin connection for tenant {TenantId}", tenantId);
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<NpgsqlConnection> OpenNpgsqlInternalAsync(CancellationToken cancellationToken = default)
    {
        var connection = await OpenInternalAsync(cancellationToken);
        return (NpgsqlConnection)connection;
    }

    /// <inheritdoc />
    public async Task<NpgsqlConnection> OpenNpgsqlInternalWithTenantAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        var connection = await OpenInternalWithTenantAsync(tenantId, cancellationToken);
        return (NpgsqlConnection)connection;
    }
}

/// <summary>
/// Extension methods for setting internal admin role on existing connections.
/// </summary>
public static class InternalDbConnectionExtensions
{
    /// <summary>
    /// Sets the internal admin role on an existing connection.
    /// Use this when you have a connection that needs elevated privileges.
    /// </summary>
    public static async Task SetInternalAdminRoleAsync(this IDbConnection connection)
    {
        await connection.ExecuteAsync("SELECT set_config('app.role', 'internal_admin', false)");
    }

    /// <summary>
    /// Clears the internal admin role from a connection.
    /// </summary>
    public static async Task ClearInternalAdminRoleAsync(this IDbConnection connection)
    {
        await connection.ExecuteAsync("SELECT set_config('app.role', '', false)");
    }

    /// <summary>
    /// Sets both internal admin role and tenant context on a connection.
    /// Sets BOTH app.tenant_id and app.current_tenant_id for backward compatibility.
    /// </summary>
    public static async Task SetInternalAdminWithTenantAsync(this IDbConnection connection, Guid tenantId)
    {
        await connection.ExecuteAsync(
            """
            SELECT set_config('app.role', 'internal_admin', false);
            SELECT set_config('app.tenant_id', @TenantId, false);
            SELECT set_config('app.current_tenant_id', @TenantId, false);
            """,
            new { TenantId = tenantId.ToString() });
    }

    /// <summary>
    /// Sets tenant context on a connection without internal admin role.
    /// Use this for user-scoped operations.
    /// </summary>
    public static async Task SetTenantContextAsync(this IDbConnection connection, Guid tenantId)
    {
        await connection.ExecuteAsync(
            """
            SELECT set_config('app.tenant_id', @TenantId, false);
            SELECT set_config('app.current_tenant_id', @TenantId, false);
            """,
            new { TenantId = tenantId.ToString() });
    }

    /// <summary>
    /// Sets tenant context on a connection with a specific role.
    /// </summary>
    public static async Task SetTenantContextWithRoleAsync(this IDbConnection connection, Guid tenantId, string role)
    {
        await connection.ExecuteAsync(
            """
            SELECT set_config('app.role', @Role, false);
            SELECT set_config('app.tenant_id', @TenantId, false);
            SELECT set_config('app.current_tenant_id', @TenantId, false);
            """,
            new { TenantId = tenantId.ToString(), Role = role });
    }
}
