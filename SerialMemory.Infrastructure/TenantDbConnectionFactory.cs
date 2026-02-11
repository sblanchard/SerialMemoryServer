using System.Data;
using Dapper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using SerialMemory.Core.Interfaces;

namespace SerialMemory.Infrastructure;

/// <summary>
/// Factory for creating tenant-scoped database connections.
/// Ensures RLS enforcement by setting app.tenant_id on every connection.
/// </summary>
public sealed class TenantDbConnectionFactory : ITenantDbConnectionFactory
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ITenantContext _tenantContext;
    private readonly ILogger<TenantDbConnectionFactory> _logger;

    /// <summary>
    /// Creates a new connection factory with the specified data source and tenant context.
    /// </summary>
    /// <param name="dataSource">Pre-configured NpgsqlDataSource with pgvector support</param>
    /// <param name="tenantContext">Current tenant context for resolving tenant ID</param>
    /// <param name="logger">Optional logger</param>
    public TenantDbConnectionFactory(NpgsqlDataSource dataSource, ITenantContext tenantContext, ILogger<TenantDbConnectionFactory>? logger = null)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _tenantContext = tenantContext ?? throw new ArgumentNullException(nameof(tenantContext));
        _logger = logger ?? NullLogger<TenantDbConnectionFactory>.Instance;
    }

    /// <summary>
    /// Creates a new connection factory from a connection string.
    /// </summary>
    /// <param name="connectionString">PostgreSQL connection string</param>
    /// <param name="tenantContext">Current tenant context for resolving tenant ID</param>
    /// <param name="logger">Optional logger</param>
    public TenantDbConnectionFactory(string connectionString, ITenantContext tenantContext, ILogger<TenantDbConnectionFactory>? logger = null)
    {
        if (string.IsNullOrEmpty(connectionString))
            throw new ArgumentNullException(nameof(connectionString));

        _tenantContext = tenantContext ?? throw new ArgumentNullException(nameof(tenantContext));
        _logger = logger ?? NullLogger<TenantDbConnectionFactory>.Instance;

        var builder = new NpgsqlDataSourceBuilder(connectionString);
        builder.UseVector();
        _dataSource = builder.Build();
    }

    /// <summary>
    /// The Self-Hosted tenant ID (Guid.Empty / all zeros).
    /// This is a valid tenant ID for self-hosted deployments.
    /// </summary>
    public static readonly Guid SelfHostedTenantId = Guid.Empty;

    /// <inheritdoc />
    public async Task<IDbConnection> OpenAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        // Note: Guid.Empty (00000000-0000-0000-0000-000000000000) is valid for the Self-Hosted tenant
        _logger.LogDebug("Opening connection for tenant {TenantId}", tenantId);

        var connection = _dataSource.CreateConnection();

        try
        {
            await connection.OpenAsync(cancellationToken);

            // CRITICAL: Set tenant + workspace context in a SINGLE roundtrip
            // This enables RLS policies to filter by tenant and workspace
            var workspaceId = _tenantContext.WorkspaceId;
            _logger.LogDebug("Setting RLS context: tenant={TenantId}, workspace={WorkspaceId}", tenantId, workspaceId);
            await connection.ExecuteAsync(
                "SELECT set_tenant_context(@TenantId); SELECT set_workspace_context(@WorkspaceId)",
                new { TenantId = tenantId, WorkspaceId = workspaceId });

            _logger.LogDebug("Connection opened with tenant={TenantId}, workspace={WorkspaceId}", tenantId, workspaceId);
            return connection;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open connection for tenant {TenantId}", tenantId);
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <inheritdoc />
    public Task<IDbConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("OpenAsync called with TenantContext.TenantId={TenantId}", _tenantContext.TenantId);

        if (string.IsNullOrEmpty(_tenantContext.TenantId))
        {
            _logger.LogError("Tenant context is not set. TenantId is null or empty.");
            throw new InvalidOperationException("Tenant context is not set. Cannot open connection without tenant ID.");
        }

        if (!Guid.TryParse(_tenantContext.TenantId, out var tenantId))
        {
            _logger.LogError("Invalid tenant ID format: {TenantId}", _tenantContext.TenantId);
            throw new InvalidOperationException($"Invalid tenant ID format: {_tenantContext.TenantId}");
        }

        return OpenAsync(tenantId, cancellationToken);
    }

    /// <summary>
    /// Opens a connection with tenant context set, returning NpgsqlConnection for pgvector operations.
    /// </summary>
    public async Task<NpgsqlConnection> OpenNpgsqlAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var connection = await OpenAsync(tenantId, cancellationToken);
        return (NpgsqlConnection)connection;
    }

    /// <summary>
    /// Opens a connection with tenant context set, returning NpgsqlConnection for pgvector operations.
    /// </summary>
    public async Task<NpgsqlConnection> OpenNpgsqlAsync(CancellationToken cancellationToken = default)
    {
        var connection = await OpenAsync(cancellationToken);
        return (NpgsqlConnection)connection;
    }
}
