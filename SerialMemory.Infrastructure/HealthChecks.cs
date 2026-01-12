using Npgsql;
using SerialMemory.Core.Operations;
using StackExchange.Redis;

namespace SerialMemory.Infrastructure;

/// <summary>
/// Database connectivity health check.
/// </summary>
public sealed class DatabaseHealthCheck(string connectionString, int timeoutSeconds = 3) : IHealthCheck
{
    public string Name => "database";

    public async Task<HealthCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync(cts.Token);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1";
            cmd.CommandTimeout = timeoutSeconds;

            await cmd.ExecuteScalarAsync(cts.Token);

            sw.Stop();

            return new HealthCheckResult
            {
                Name = Name,
                Status = "healthy",
                DurationMs = sw.ElapsedMilliseconds,
                Message = "Database connection successful"
            };
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            return new HealthCheckResult
            {
                Name = Name,
                Status = "unhealthy",
                DurationMs = sw.ElapsedMilliseconds,
                Error = $"Database health check timed out after {timeoutSeconds}s"
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new HealthCheckResult
            {
                Name = Name,
                Status = "unhealthy",
                DurationMs = sw.ElapsedMilliseconds,
                Error = ex.Message
            };
        }
    }
}

/// <summary>
/// RLS (Row-Level Security) guardrail health check.
/// Verifies that the require_tenant_context() function is installed and working.
/// </summary>
public sealed class RlsHealthCheck(string connectionString, int timeoutSeconds = 3) : IHealthCheck
{
    public string Name => "rls_guardrail";

    public async Task<HealthCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync(cts.Token);

            // Check 1: Verify require_tenant_context function exists
            await using var checkFuncCmd = conn.CreateCommand();
            checkFuncCmd.CommandText = @"
                SELECT EXISTS (
                    SELECT 1 FROM pg_proc
                    WHERE proname = 'require_tenant_context'
                )";
            checkFuncCmd.CommandTimeout = timeoutSeconds;

            var funcExists = (bool?)await checkFuncCmd.ExecuteScalarAsync(cts.Token) ?? false;

            if (!funcExists)
            {
                sw.Stop();
                return new HealthCheckResult
                {
                    Name = Name,
                    Status = "unhealthy",
                    DurationMs = sw.ElapsedMilliseconds,
                    Error = "require_tenant_context() function not found. Run migration scripts."
                };
            }

            // Check 2: Verify RLS is enabled on memories table
            await using var checkRlsCmd = conn.CreateCommand();
            checkRlsCmd.CommandText = @"
                SELECT relrowsecurity FROM pg_class
                WHERE relname = 'memories'";
            checkRlsCmd.CommandTimeout = timeoutSeconds;

            var rlsEnabled = (bool?)await checkRlsCmd.ExecuteScalarAsync(cts.Token) ?? false;

            if (!rlsEnabled)
            {
                sw.Stop();
                return new HealthCheckResult
                {
                    Name = Name,
                    Status = "unhealthy",
                    DurationMs = sw.ElapsedMilliseconds,
                    Error = "RLS not enabled on memories table. Run migration scripts."
                };
            }

            // Check 3: Verify guardrail actually works (should fail without tenant context)
            try
            {
                await using var testCmd = conn.CreateCommand();
                testCmd.CommandText = "SELECT require_tenant_context()";
                testCmd.CommandTimeout = timeoutSeconds;
                await testCmd.ExecuteScalarAsync(cts.Token);

                // If we get here, guardrail FAILED (should have raised exception)
                sw.Stop();
                return new HealthCheckResult
                {
                    Name = Name,
                    Status = "unhealthy",
                    DurationMs = sw.ElapsedMilliseconds,
                    Error = "RLS guardrail not enforcing - require_tenant_context() should fail without context"
                };
            }
            catch (PostgresException ex) when (ex.Message.Contains("tenant_id"))
            {
                // Expected! The guardrail works.
                sw.Stop();
                return new HealthCheckResult
                {
                    Name = Name,
                    Status = "healthy",
                    DurationMs = sw.ElapsedMilliseconds,
                    Message = "RLS guardrail is active and enforcing tenant isolation"
                };
            }
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            return new HealthCheckResult
            {
                Name = Name,
                Status = "unhealthy",
                DurationMs = sw.ElapsedMilliseconds,
                Error = $"RLS health check timed out after {timeoutSeconds}s"
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new HealthCheckResult
            {
                Name = Name,
                Status = "unhealthy",
                DurationMs = sw.ElapsedMilliseconds,
                Error = ex.Message
            };
        }
    }
}

/// <summary>
/// Redis connectivity health check.
/// </summary>
public sealed class RedisHealthCheck(string connectionString, int timeoutSeconds = 3) : IHealthCheck
{
    public string Name => "redis";

    public async Task<HealthCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            await using var redis = await ConnectionMultiplexer.ConnectAsync(
                connectionString,
                options => options.AbortOnConnectFail = true);

            var db = redis.GetDatabase();
            await db.PingAsync();

            sw.Stop();

            return new HealthCheckResult
            {
                Name = Name,
                Status = "healthy",
                DurationMs = sw.ElapsedMilliseconds,
                Message = "Redis connection successful"
            };
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            return new HealthCheckResult
            {
                Name = Name,
                Status = "unhealthy",
                DurationMs = sw.ElapsedMilliseconds,
                Error = $"Redis health check timed out after {timeoutSeconds}s"
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new HealthCheckResult
            {
                Name = Name,
                Status = "unhealthy",
                DurationMs = sw.ElapsedMilliseconds,
                Error = ex.Message
            };
        }
    }
}
