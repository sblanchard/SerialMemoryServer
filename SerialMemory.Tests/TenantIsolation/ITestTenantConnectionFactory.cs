using Npgsql;

namespace SerialMemory.Tests.TenantIsolation;

/// <summary>
/// Factory for creating tenant-isolated database connections for testing.
/// Ensures each connection has the proper app.tenant_id set via RLS.
/// </summary>
public interface ITestTenantConnectionFactory
{
    /// <summary>
    /// Opens a connection with the specified tenant context set.
    /// </summary>
    Task<NpgsqlConnection> OpenAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>
    /// Opens a connection WITHOUT tenant context (for testing RLS guardrails).
    /// </summary>
    Task<NpgsqlConnection> OpenWithoutTenantContextAsync(CancellationToken ct = default);

    /// <summary>
    /// Opens a connection as the database superuser (bypasses RLS).
    /// Only for test setup/teardown and verification.
    /// </summary>
    Task<NpgsqlConnection> OpenAsSuperuserAsync(CancellationToken ct = default);
}

/// <summary>
/// Implementation of test connection factory that enforces RLS tenant isolation.
/// </summary>
public sealed class TestTenantConnectionFactory : ITestTenantConnectionFactory
{
    private readonly string _connectionString;
    private readonly string _superuserConnectionString;

    public TestTenantConnectionFactory(string connectionString, string? superuserConnectionString = null)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _superuserConnectionString = superuserConnectionString ?? connectionString;
    }

    public async Task<NpgsqlConnection> OpenAsync(Guid tenantId, CancellationToken ct = default)
    {
        var connection = new NpgsqlConnection(_connectionString);

        try
        {
            await connection.OpenAsync(ct);

            // CRITICAL: Set tenant context BEFORE any queries
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT set_tenant_context(@tenant_id)";
            cmd.Parameters.AddWithValue("tenant_id", tenantId);
            await cmd.ExecuteNonQueryAsync(ct);

            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async Task<NpgsqlConnection> OpenWithoutTenantContextAsync(CancellationToken ct = default)
    {
        var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        // Deliberately NOT setting tenant context
        return connection;
    }

    public async Task<NpgsqlConnection> OpenAsSuperuserAsync(CancellationToken ct = default)
    {
        var connection = new NpgsqlConnection(_superuserConnectionString);
        await connection.OpenAsync(ct);
        // Set bypass flag so superuser can see all data even with FORCE RLS
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT set_config('app.bypass_rls', 'true', false)";
        await cmd.ExecuteNonQueryAsync(ct);
        return connection;
    }
}
