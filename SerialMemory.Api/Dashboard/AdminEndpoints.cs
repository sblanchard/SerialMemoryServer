using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Claims;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using SerialMemory.Core.Interfaces;
using SerialMemory.Infrastructure;

namespace SerialMemory.Api.Dashboard;

/// <summary>
/// Dashboard endpoints for Admin Status and System Metrics.
/// FIX #11: Self-host admin stats MUST WORK IN SAAS mode.
/// Admin (owner with is_root_admin = true) can see system stats.
/// </summary>
public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this WebApplication app, bool selfHostedMode)
    {
        var group = app.MapGroup("/admin")
            .WithTags("Admin")
            .RequireAuthorization();

        // =============================================================================
        // GET /admin/status - Get system status (FIX #11)
        // =============================================================================
        group.MapGet("/status", async (
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            [FromServices] IAdminStatusService? adminStatusService,
            CancellationToken ct) =>
        {
            var tenantId = GetTenantId(user, selfHostedMode);
            var userId = GetUserId(user);

            // Verify admin access
            var isAdmin = await VerifyAdminAccessAsync(dataSource, tenantId, userId, selfHostedMode, ct);
            if (!isAdmin)
                return Results.Forbid();

            await using var conn = await dataSource.OpenConnectionAsync(ct);

            // CRITICAL FIX: Set internal_admin role to bypass tenant filters
            await conn.ExecuteAsync("SELECT set_config('app.role', 'internal_admin', false)");

            // Use admin service if available
            if (adminStatusService != null)
            {
                var status = await adminStatusService.GetSystemStatusAsync(ct);
                return Results.Ok(status);
            }

            // Manual status collection
            var process = Process.GetCurrentProcess();
            var gcInfo = GC.GetGCMemoryInfo();

            // Get runtime info
            var runtimeInfo = new
            {
                machineName = Environment.MachineName,
                osVersion = RuntimeInformation.OSDescription,
                processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                frameworkVersion = RuntimeInformation.FrameworkDescription,
                processId = process.Id,
                uptime = (DateTime.UtcNow - process.StartTime.ToUniversalTime()).TotalSeconds,
                startTime = process.StartTime.ToUniversalTime()
            };

            // Get CPU usage (approximate)
            var cpuTime = process.TotalProcessorTime;
            var cpuUsage = cpuTime.TotalMilliseconds / (Environment.ProcessorCount * 1000);

            // Get memory info
            var memoryInfo = new
            {
                workingSetBytes = process.WorkingSet64,
                workingSetMb = process.WorkingSet64 / (1024.0 * 1024.0),
                privateMemoryBytes = process.PrivateMemorySize64,
                privateMemoryMb = process.PrivateMemorySize64 / (1024.0 * 1024.0),
                gcTotalMemory = GC.GetTotalMemory(false),
                gcTotalMemoryMb = GC.GetTotalMemory(false) / (1024.0 * 1024.0),
                heapSizeBytes = gcInfo.HeapSizeBytes,
                heapSizeMb = gcInfo.HeapSizeBytes / (1024.0 * 1024.0),
                gen0Collections = GC.CollectionCount(0),
                gen1Collections = GC.CollectionCount(1),
                gen2Collections = GC.CollectionCount(2)
            };

            // Get database status
            var dbStatus = await GetDatabaseStatusAsync(conn);

            // Get API stats (last hour)
            var apiStats = await conn.QueryFirstOrDefaultAsync<ApiStatsDto>(
                """
                SELECT
                    COUNT(*) AS requests_last_hour,
                    COUNT(*) FILTER (WHERE severity = 'error') AS errors_last_hour,
                    AVG(CASE WHEN (payload->>'duration_ms')::float IS NOT NULL
                        THEN (payload->>'duration_ms')::float ELSE NULL END) AS avg_duration_ms
                FROM event_log
                WHERE created_at > NOW() - INTERVAL '1 hour'
                    AND event_type LIKE 'api_%'
                """) ?? new ApiStatsDto();

            return Results.Ok(new
            {
                status = "healthy",
                selfHostedMode,
                runtime = runtimeInfo,
                cpu = new
                {
                    usagePercent = Math.Round(cpuUsage, 2),
                    processorCount = Environment.ProcessorCount
                },
                memory = memoryInfo,
                database = dbStatus,
                api = apiStats,
                timestamp = DateTimeOffset.UtcNow
            });
        })
        .WithName("GetAdminStatus")
        .WithDescription("Gets system status and metrics (admin only)")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);

        // =============================================================================
        // GET /admin/metrics - Get detailed metrics stream
        // =============================================================================
        group.MapGet("/metrics", async (
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            CancellationToken ct) =>
        {
            var tenantId = GetTenantId(user, selfHostedMode);
            var userId = GetUserId(user);

            var isAdmin = await VerifyAdminAccessAsync(dataSource, tenantId, userId, selfHostedMode, ct);
            if (!isAdmin)
                return Results.Forbid();

            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.ExecuteAsync("SELECT set_config('app.role', 'internal_admin', false)");

            // Get comprehensive metrics
            var metrics = await conn.QueryFirstOrDefaultAsync<SystemMetricsDto>(
                """
                SELECT
                    (SELECT COUNT(*) FROM memories) AS total_memories,
                    (SELECT COUNT(*) FROM entities) AS total_entities,
                    (SELECT COUNT(*) FROM entity_relationships) AS total_relationships,
                    (SELECT COUNT(*) FROM tenants) AS total_tenants,
                    (SELECT COUNT(*) FROM tenant_users) AS total_users,
                    (SELECT COUNT(*) FROM tenant_api_keys WHERE revoked_at IS NULL) AS active_api_keys,
                    (SELECT COUNT(*) FROM conversation_sessions WHERE ended_at IS NULL) AS active_sessions,
                    (SELECT COUNT(*) FROM memories WHERE created_at > NOW() - INTERVAL '24 hours') AS memories_24h,
                    (SELECT COUNT(*) FROM event_log WHERE created_at > NOW() - INTERVAL '1 hour') AS events_1h,
                    (SELECT pg_database_size(current_database())) AS database_size_bytes
                """) ?? new SystemMetricsDto();

            return Results.Ok(metrics);
        })
        .WithName("GetAdminMetrics")
        .WithDescription("Gets detailed system metrics (admin only)")
        .Produces<SystemMetricsDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);

        // =============================================================================
        // GET /admin/tenants - List all tenants (root admin only)
        // =============================================================================
        group.MapGet("/tenants", async (
            int? limit,
            int? offset,
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            CancellationToken ct) =>
        {
            var tenantId = GetTenantId(user, selfHostedMode);
            var userId = GetUserId(user);

            var isRootAdmin = await VerifyRootAdminAsync(dataSource, userId, ct);
            if (!isRootAdmin)
                return Results.Forbid();

            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.ExecuteAsync("SELECT set_config('app.role', 'internal_admin', false)");

            var tenants = await conn.QueryAsync<TenantListItemDto>(
                """
                SELECT
                    t.id,
                    t.name,
                    t.slug,
                    t.status,
                    t.created_at AS createdAt,
                    ts.plan,
                    (SELECT COUNT(*) FROM memories WHERE tenant_id = t.id) AS memoryCount,
                    (SELECT COUNT(*) FROM tenant_users WHERE tenant_id = t.id) AS userCount
                FROM tenants t
                LEFT JOIN tenant_settings ts ON t.id = ts.tenant_id
                ORDER BY t.created_at DESC
                LIMIT @Limit
                OFFSET @Offset
                """,
                new { Limit = limit ?? 50, Offset = offset ?? 0 });

            var totalCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM tenants");

            return Results.Ok(new
            {
                tenants = tenants.ToList(),
                total = totalCount,
                limit = limit ?? 50,
                offset = offset ?? 0
            });
        })
        .WithName("ListTenants")
        .WithDescription("Lists all tenants (root admin only)")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);

        // =============================================================================
        // GET /admin/health - Health check endpoint
        // =============================================================================
        group.MapGet("/health", async (
            NpgsqlDataSource dataSource,
            CancellationToken ct) =>
        {
            try
            {
                await using var conn = await dataSource.OpenConnectionAsync(ct);
                var result = await conn.ExecuteScalarAsync<int>("SELECT 1");

                return Results.Ok(new
                {
                    status = "healthy",
                    database = "connected",
                    timestamp = DateTimeOffset.UtcNow
                });
            }
            catch (Exception ex)
            {
                return Results.Ok(new
                {
                    status = "degraded",
                    database = "error",
                    error = ex.Message,
                    timestamp = DateTimeOffset.UtcNow
                });
            }
        })
        .WithName("AdminHealthCheck")
        .WithDescription("Health check endpoint")
        .AllowAnonymous()
        .Produces(StatusCodes.Status200OK);

        // =============================================================================
        // POST /admin/maintenance/gc - Trigger garbage collection
        // =============================================================================
        group.MapPost("/maintenance/gc", async (
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            CancellationToken ct) =>
        {
            var tenantId = GetTenantId(user, selfHostedMode);
            var userId = GetUserId(user);

            var isAdmin = await VerifyAdminAccessAsync(dataSource, tenantId, userId, selfHostedMode, ct);
            if (!isAdmin)
                return Results.Forbid();

            var beforeMemory = GC.GetTotalMemory(false);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var afterMemory = GC.GetTotalMemory(true);

            return Results.Ok(new
            {
                message = "Garbage collection completed",
                beforeMb = beforeMemory / (1024.0 * 1024.0),
                afterMb = afterMemory / (1024.0 * 1024.0),
                freedMb = (beforeMemory - afterMemory) / (1024.0 * 1024.0)
            });
        })
        .WithName("TriggerGC")
        .WithDescription("Triggers garbage collection (admin only)")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status403Forbidden);
    }

    private static async Task<bool> VerifyAdminAccessAsync(
        NpgsqlDataSource dataSource,
        Guid tenantId,
        string userId,
        bool selfHostedMode,
        CancellationToken ct)
    {
        // In self-hosted mode, all authenticated users are admins
        if (selfHostedMode)
            return true;

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync("SELECT set_config('app.role', 'internal_admin', false)");

        // Check if user is owner of their tenant
        var isOwner = await conn.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS(
                SELECT 1 FROM tenant_users
                WHERE tenant_id = @TenantId AND user_id = @UserId AND role = 'owner'
            )
            """,
            new { TenantId = tenantId, UserId = userId });

        return isOwner;
    }

    private static async Task<bool> VerifyRootAdminAsync(
        NpgsqlDataSource dataSource,
        string userId,
        CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync("SELECT set_config('app.role', 'internal_admin', false)");

        // Root admin is owner of the first tenant ever created
        var isRootAdmin = await conn.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS(
                SELECT 1 FROM tenant_users tu
                JOIN tenants t ON tu.tenant_id = t.id
                WHERE tu.user_id = @UserId
                    AND tu.role = 'owner'
                    AND t.created_at = (SELECT MIN(created_at) FROM tenants)
            )
            """,
            new { UserId = userId });

        return isRootAdmin;
    }

    private static async Task<DatabaseStatusDto> GetDatabaseStatusAsync(NpgsqlConnection conn)
    {
        try
        {
            var dbSize = await conn.ExecuteScalarAsync<long>(
                "SELECT pg_database_size(current_database())");

            var connectionCount = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM pg_stat_activity WHERE datname = current_database()");

            var version = await conn.ExecuteScalarAsync<string>("SELECT version()") ?? "unknown";

            return new DatabaseStatusDto
            {
                status = "connected",
                sizeBytes = dbSize,
                sizeMb = dbSize / (1024.0 * 1024.0),
                activeConnections = connectionCount,
                version = version.Split(' ')[0..2] is var parts ? string.Join(" ", parts) : "PostgreSQL"
            };
        }
        catch (Exception ex)
        {
            return new DatabaseStatusDto
            {
                status = "error",
                error = ex.Message
            };
        }
    }

    private static Guid GetTenantId(ClaimsPrincipal user, bool selfHosted)
    {
        if (selfHosted)
            return Guid.Parse("00000000-0000-0000-0000-000000000000");

        var tenantIdClaim = user.FindFirst("tenant_id")?.Value
            ?? throw new UnauthorizedAccessException("Missing tenant_id claim");

        return Guid.Parse(tenantIdClaim);
    }

    private static string GetUserId(ClaimsPrincipal user)
    {
        return user.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? user.FindFirst("sub")?.Value
            ?? "system";
    }
}

// DTOs for Admin endpoints
public sealed class DatabaseStatusDto
{
    public string status { get; set; } = "";
    public long sizeBytes { get; set; }
    public double sizeMb { get; set; }
    public int activeConnections { get; set; }
    public string? version { get; set; }
    public string? error { get; set; }
}

public sealed class ApiStatsDto
{
    public long requests_last_hour { get; set; }
    public long errors_last_hour { get; set; }
    public double? avg_duration_ms { get; set; }
}

public sealed class SystemMetricsDto
{
    public long total_memories { get; set; }
    public long total_entities { get; set; }
    public long total_relationships { get; set; }
    public long total_tenants { get; set; }
    public long total_users { get; set; }
    public long active_api_keys { get; set; }
    public long active_sessions { get; set; }
    public long memories_24h { get; set; }
    public long events_1h { get; set; }
    public long database_size_bytes { get; set; }
}

public sealed class TenantListItemDto
{
    public Guid id { get; set; }
    public string name { get; set; } = "";
    public string slug { get; set; } = "";
    public string status { get; set; } = "";
    public DateTimeOffset createdAt { get; set; }
    public string? plan { get; set; }
    public long memoryCount { get; set; }
    public long userCount { get; set; }
}
