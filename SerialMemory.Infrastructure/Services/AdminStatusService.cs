using System.Diagnostics;
using System.Runtime.InteropServices;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using SerialMemory.Core.Interfaces;

namespace SerialMemory.Infrastructure.Services;

/// <summary>
/// PostgreSQL-backed admin status service.
/// FIX #11: Self-Host Admin Stats - MUST WORK IN SAAS mode.
/// Provides system-wide metrics and status information.
/// </summary>
public sealed class AdminStatusService(
    NpgsqlDataSource dataSource,
    ILogger<AdminStatusService> logger,
    bool selfHostedMode)
    : IAdminStatusService
{
    private readonly ILogger<AdminStatusService> _logger = logger;

    public async Task<SystemStatus> GetSystemStatusAsync(CancellationToken ct = default)
    {
        var process = Process.GetCurrentProcess();
        var gcInfo = GC.GetGCMemoryInfo();

        // Runtime info
        var runtime = new RuntimeInfo
        {
            MachineName = Environment.MachineName,
            OsVersion = RuntimeInformation.OSDescription,
            ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            FrameworkVersion = RuntimeInformation.FrameworkDescription,
            ProcessId = process.Id,
            UptimeSeconds = (DateTime.UtcNow - process.StartTime.ToUniversalTime()).TotalSeconds,
            StartTime = process.StartTime.ToUniversalTime()
        };

        // CPU info
        var cpuTime = process.TotalProcessorTime;
        var cpuUsage = cpuTime.TotalMilliseconds / (Environment.ProcessorCount * 1000);

        var cpu = new CpuInfo
        {
            UsagePercent = Math.Round(cpuUsage, 2),
            ProcessorCount = Environment.ProcessorCount
        };

        // Memory info
        var memory = new MemoryInfo
        {
            WorkingSetBytes = process.WorkingSet64,
            WorkingSetMb = process.WorkingSet64 / (1024.0 * 1024.0),
            PrivateMemoryBytes = process.PrivateMemorySize64,
            PrivateMemoryMb = process.PrivateMemorySize64 / (1024.0 * 1024.0),
            GcTotalMemory = GC.GetTotalMemory(false),
            GcTotalMemoryMb = GC.GetTotalMemory(false) / (1024.0 * 1024.0),
            HeapSizeBytes = gcInfo.HeapSizeBytes,
            HeapSizeMb = gcInfo.HeapSizeBytes / (1024.0 * 1024.0),
            Gen0Collections = GC.CollectionCount(0),
            Gen1Collections = GC.CollectionCount(1),
            Gen2Collections = GC.CollectionCount(2)
        };

        // Database status
        DatabaseStatus database;
        ApiStats api;

        try
        {
            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.ExecuteAsync("SELECT set_config('app.role', 'internal_admin', false)");

            database = await GetDatabaseStatusAsync(conn);
            api = await GetApiStatsAsync(conn);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get database status");
            database = new DatabaseStatus
            {
                Status = "error",
                Error = ex.Message
            };
            api = new ApiStats();
        }

        return new SystemStatus
        {
            Status = database.Status == "connected" ? "healthy" : "degraded",
            SelfHostedMode = selfHostedMode,
            Runtime = runtime,
            Cpu = cpu,
            Memory = memory,
            Database = database,
            Api = api,
            Timestamp = DateTimeOffset.UtcNow
        };
    }

    public async Task<SystemMetrics> GetSystemMetricsAsync(CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync("SELECT set_config('app.role', 'internal_admin', false)");

        var metrics = await conn.QueryFirstOrDefaultAsync<MetricsRow>(
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
            """);

        return new SystemMetrics
        {
            TotalMemories = metrics?.total_memories ?? 0,
            TotalEntities = metrics?.total_entities ?? 0,
            TotalRelationships = metrics?.total_relationships ?? 0,
            TotalTenants = metrics?.total_tenants ?? 0,
            TotalUsers = metrics?.total_users ?? 0,
            ActiveApiKeys = metrics?.active_api_keys ?? 0,
            ActiveSessions = metrics?.active_sessions ?? 0,
            MemoriesLast24Hours = metrics?.memories_24h ?? 0,
            EventsLastHour = metrics?.events_1h ?? 0,
            DatabaseSizeBytes = metrics?.database_size_bytes ?? 0
        };
    }

    public async Task<ApiActivityStats> GetApiActivityAsync(
        int hours = 24,
        CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync("SELECT set_config('app.role', 'internal_admin', false)");

        var cutoff = DateTimeOffset.UtcNow.AddHours(-hours);

        // Overall stats
        var overallStats = await conn.QueryFirstOrDefaultAsync<ApiActivityRow>(
            """
            SELECT
                COUNT(*) AS total_requests,
                COUNT(*) FILTER (WHERE severity != 'error') AS successful_requests,
                COUNT(*) FILTER (WHERE severity = 'error') AS failed_requests,
                AVG((payload->>'duration_ms')::float) AS avg_response_time_ms,
                PERCENTILE_CONT(0.95) WITHIN GROUP (ORDER BY (payload->>'duration_ms')::float) AS p95_response_time_ms,
                PERCENTILE_CONT(0.99) WITHIN GROUP (ORDER BY (payload->>'duration_ms')::float) AS p99_response_time_ms
            FROM event_log
            WHERE created_at >= @Cutoff
              AND event_type LIKE 'api_%'
              AND (payload->>'duration_ms')::float IS NOT NULL
            """,
            new { Cutoff = cutoff });

        // Requests by endpoint
        var byEndpoint = await conn.QueryAsync<EndpointStatsRow>(
            """
            SELECT
                payload->>'endpoint' AS endpoint,
                COUNT(*) AS count
            FROM event_log
            WHERE created_at >= @Cutoff
              AND event_type LIKE 'api_%'
              AND payload->>'endpoint' IS NOT NULL
            GROUP BY payload->>'endpoint'
            ORDER BY count DESC
            LIMIT 20
            """,
            new { Cutoff = cutoff });

        // Requests by status code
        var byStatusCode = await conn.QueryAsync<StatusCodeStatsRow>(
            """
            SELECT
                (payload->>'status_code')::int AS status_code,
                COUNT(*) AS count
            FROM event_log
            WHERE created_at >= @Cutoff
              AND event_type LIKE 'api_%'
              AND (payload->>'status_code')::int IS NOT NULL
            GROUP BY (payload->>'status_code')::int
            ORDER BY count DESC
            """,
            new { Cutoff = cutoff });

        // Hourly activity
        var hourlyActivity = await conn.QueryAsync<HourlyActivityRow>(
            """
            SELECT
                DATE_TRUNC('hour', created_at) AS hour,
                COUNT(*) AS requests,
                COUNT(*) FILTER (WHERE severity = 'error') AS errors,
                AVG((payload->>'duration_ms')::float) AS avg_duration_ms
            FROM event_log
            WHERE created_at >= @Cutoff
              AND event_type LIKE 'api_%'
            GROUP BY DATE_TRUNC('hour', created_at)
            ORDER BY hour
            """,
            new { Cutoff = cutoff });

        var totalRequests = overallStats?.total_requests ?? 0;
        var successfulRequests = overallStats?.successful_requests ?? 0;

        return new ApiActivityStats
        {
            TotalRequests = totalRequests,
            SuccessfulRequests = successfulRequests,
            FailedRequests = overallStats?.failed_requests ?? 0,
            SuccessRate = totalRequests > 0 ? (double)successfulRequests / totalRequests : 0,
            AvgResponseTimeMs = overallStats?.avg_response_time_ms ?? 0,
            P95ResponseTimeMs = overallStats?.p95_response_time_ms ?? 0,
            P99ResponseTimeMs = overallStats?.p99_response_time_ms ?? 0,
            RequestsByEndpoint = byEndpoint.ToDictionary(x => x.endpoint ?? "unknown", x => x.count),
            RequestsByStatusCode = byStatusCode.ToDictionary(x => x.status_code, x => x.count),
            HourlyActivity = hourlyActivity.Select(h => new HourlyApiActivity
            {
                Hour = h.hour,
                Requests = h.requests,
                Errors = h.errors,
                AvgDurationMs = h.avg_duration_ms ?? 0
            }).ToList()
        };
    }

    public async Task<DatabaseHealth> GetDatabaseHealthAsync(CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync("SELECT set_config('app.role', 'internal_admin', false)");

        try
        {
            // Get database size
            var dbSize = await conn.ExecuteScalarAsync<long>(
                "SELECT pg_database_size(current_database())");

            // Get connection info
            var connectionInfo = await conn.QueryFirstOrDefaultAsync<ConnectionInfoRow>(
                """
                SELECT
                    (SELECT COUNT(*) FROM pg_stat_activity WHERE datname = current_database()) AS active_connections,
                    (SELECT setting::int FROM pg_settings WHERE name = 'max_connections') AS max_connections
                """);

            // Get PostgreSQL version
            var version = await conn.ExecuteScalarAsync<string>("SELECT version()") ?? "unknown";
            var pgVersion = version.Split(' ') is { Length: >= 2 } parts
                ? string.Join(" ", parts[..2])
                : "PostgreSQL";

            // Get cache hit ratio
            var cacheStats = await conn.QueryFirstOrDefaultAsync<CacheStatsRow>(
                """
                SELECT
                    ROUND(
                        CASE WHEN (blks_hit + blks_read) > 0
                        THEN blks_hit::float / (blks_hit + blks_read) * 100
                        ELSE 0 END, 2
                    ) AS cache_hit_ratio
                FROM pg_stat_database
                WHERE datname = current_database()
                """);

            // Get table sizes
            var tableSizes = await conn.QueryAsync<TableSizeRow>(
                """
                SELECT
                    relname AS table_name,
                    pg_relation_size(c.oid) AS size_bytes
                FROM pg_class c
                JOIN pg_namespace n ON n.oid = c.relnamespace
                WHERE n.nspname = 'public'
                  AND c.relkind = 'r'
                ORDER BY pg_relation_size(c.oid) DESC
                LIMIT 10
                """);

            // Get total tables and indexes size
            var totalSizes = await conn.QueryFirstOrDefaultAsync<TotalSizesRow>(
                """
                SELECT
                    SUM(pg_relation_size(c.oid)) FILTER (WHERE c.relkind = 'r') AS tables_size,
                    SUM(pg_relation_size(c.oid)) FILTER (WHERE c.relkind = 'i') AS indexes_size
                FROM pg_class c
                JOIN pg_namespace n ON n.oid = c.relnamespace
                WHERE n.nspname = 'public'
                """);

            var activeConnections = connectionInfo?.active_connections ?? 0;
            var maxConnections = connectionInfo?.max_connections ?? 100;

            return new DatabaseHealth
            {
                Status = "healthy",
                SizeBytes = dbSize,
                SizeMb = dbSize / (1024.0 * 1024.0),
                ActiveConnections = activeConnections,
                MaxConnections = maxConnections,
                ConnectionUsagePercent = maxConnections > 0 ? (double)activeConnections / maxConnections * 100 : 0,
                PostgresVersion = pgVersion,
                TotalTablesSize = totalSizes?.tables_size ?? 0,
                TotalIndexesSize = totalSizes?.indexes_size ?? 0,
                TableSizes = tableSizes.ToDictionary(t => t.table_name, t => t.size_bytes),
                ReplicationEnabled = false, // Would need additional checks
                CacheHitRatio = cacheStats?.cache_hit_ratio ?? 0
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get database health");
            return new DatabaseHealth
            {
                Status = "error"
            };
        }
    }

    private async Task<DatabaseStatus> GetDatabaseStatusAsync(NpgsqlConnection conn)
    {
        try
        {
            var dbSize = await conn.ExecuteScalarAsync<long>(
                "SELECT pg_database_size(current_database())");

            var connectionCount = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM pg_stat_activity WHERE datname = current_database()");

            var version = await conn.ExecuteScalarAsync<string>("SELECT version()") ?? "unknown";
            var pgVersion = version.Split(' ') is { Length: >= 2 } parts
                ? string.Join(" ", parts[..2])
                : "PostgreSQL";

            return new DatabaseStatus
            {
                Status = "connected",
                SizeBytes = dbSize,
                SizeMb = dbSize / (1024.0 * 1024.0),
                ActiveConnections = connectionCount,
                Version = pgVersion
            };
        }
        catch (Exception ex)
        {
            return new DatabaseStatus
            {
                Status = "error",
                Error = ex.Message
            };
        }
    }

    private async Task<ApiStats> GetApiStatsAsync(NpgsqlConnection conn)
    {
        var stats = await conn.QueryFirstOrDefaultAsync<ApiStatsRow>(
            """
            SELECT
                COUNT(*) AS requests_last_hour,
                COUNT(*) FILTER (WHERE severity = 'error') AS errors_last_hour,
                AVG(CASE WHEN (payload->>'duration_ms')::float IS NOT NULL
                    THEN (payload->>'duration_ms')::float ELSE NULL END) AS avg_duration_ms
            FROM event_log
            WHERE created_at > NOW() - INTERVAL '1 hour'
                AND event_type LIKE 'api_%'
            """);

        return new ApiStats
        {
            RequestsLastHour = stats?.requests_last_hour ?? 0,
            ErrorsLastHour = stats?.errors_last_hour ?? 0,
            AvgDurationMs = stats?.avg_duration_ms
        };
    }

    // Row types for Dapper
    private sealed class MetricsRow
    {
        public long total_memories { get; init; }
        public long total_entities { get; init; }
        public long total_relationships { get; init; }
        public long total_tenants { get; init; }
        public long total_users { get; init; }
        public long active_api_keys { get; init; }
        public long active_sessions { get; init; }
        public long memories_24h { get; init; }
        public long events_1h { get; init; }
        public long database_size_bytes { get; init; }
    }

    private sealed class ApiStatsRow
    {
        public long requests_last_hour { get; init; }
        public long errors_last_hour { get; init; }
        public double? avg_duration_ms { get; init; }
    }

    private sealed class ApiActivityRow
    {
        public long total_requests { get; init; }
        public long successful_requests { get; init; }
        public long failed_requests { get; init; }
        public double avg_response_time_ms { get; init; }
        public double p95_response_time_ms { get; init; }
        public double p99_response_time_ms { get; init; }
    }

    private sealed class EndpointStatsRow
    {
        public string? endpoint { get; init; }
        public long count { get; init; }
    }

    private sealed class StatusCodeStatsRow
    {
        public int status_code { get; init; }
        public long count { get; init; }
    }

    private sealed class HourlyActivityRow
    {
        public DateTimeOffset hour { get; init; }
        public long requests { get; init; }
        public long errors { get; init; }
        public double? avg_duration_ms { get; init; }
    }

    private sealed class ConnectionInfoRow
    {
        public int active_connections { get; init; }
        public int max_connections { get; init; }
    }

    private sealed class CacheStatsRow
    {
        public double cache_hit_ratio { get; init; }
    }

    private sealed class TableSizeRow
    {
        public string table_name { get; init; } = "";
        public long size_bytes { get; init; }
    }

    private sealed class TotalSizesRow
    {
        public long tables_size { get; init; }
        public long indexes_size { get; init; }
    }
}
