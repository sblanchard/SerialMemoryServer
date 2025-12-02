using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using SerialMemory.Core.Interfaces;

namespace SerialMemory.Infrastructure;

/// <summary>
/// Admin service for operator observability endpoints.
/// </summary>
public sealed class AdminService : IAdminService
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<AdminService> _logger;

    public AdminService(NpgsqlDataSource dataSource, ILogger<AdminService> logger)
    {
        _dataSource = dataSource;
        _logger = logger;
    }

    public AdminService(string connectionString, ILogger<AdminService> logger)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        _dataSource = builder.Build();
        _logger = logger;
    }

    public async Task<TenantListResult> ListTenantsAsync(
        TenantStatusFilter? status,
        int limit,
        int offset,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        var statusFilter = status switch
        {
            TenantStatusFilter.Active => "AND t.status = 'active'",
            TenantStatusFilter.PendingDeletion => "AND t.status = 'pending_deletion'",
            TenantStatusFilter.Blocked => "AND t.status = 'blocked'",
            _ => ""
        };

        // Get total count
        var countSql = $"""

                                    SELECT COUNT(*) FROM tenants t
                                    WHERE 1=1 {statusFilter}
                        """;

        var totalCount = await connection.ExecuteScalarAsync<int>(countSql);

        // Get tenants with summary info
        var sql = $"""

                               SELECT
                                   t.id,
                                   t.name,
                                   t.slug,
                                   COALESCE(ts.plan, 'free') as plan,
                                   t.status,
                                   t.created_at,
                                   (SELECT MAX(created_at) FROM memories WHERE tenant_id = t.id) as last_activity_at,
                                   (SELECT COUNT(*) FROM tenant_users WHERE tenant_id = t.id) as user_count,
                                   (SELECT COUNT(*) FROM memories WHERE tenant_id = t.id AND is_active = true) as memory_count,
                                   COALESCE(bc.credits_used, 0) as credits_used_this_cycle
                               FROM tenants t
                               LEFT JOIN tenant_settings ts ON ts.tenant_id = t.id
                               LEFT JOIN billing_cycles bc ON bc.tenant_id = t.id
                                   AND bc.cycle_start <= NOW() AND bc.cycle_end > NOW()
                               WHERE 1=1 {statusFilter}
                               ORDER BY t.created_at DESC
                               LIMIT @Limit OFFSET @Offset
                   """;

        var tenants = await connection.QueryAsync<TenantSummary>(sql, new { Limit = limit, Offset = offset });

        return new TenantListResult
        {
            Tenants = tenants.ToList(),
            TotalCount = totalCount,
            Limit = limit,
            Offset = offset
        };
    }

    public async Task<TenantHealthResult?> GetTenantHealthAsync(
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        // Get basic tenant info
        const string tenantSql = """

                                             SELECT t.id, t.name, t.status
                                             FROM tenants t
                                             WHERE t.id = @TenantId
                                 """;

        var tenant = await connection.QuerySingleOrDefaultAsync<dynamic>(tenantSql, new { TenantId = tenantId });
        if (tenant == null) return null;

        // Isolation check - verify tenant_id consistency
        const string isolationSql = """

                                                SELECT
                                                    (SELECT COUNT(*) FROM memories WHERE tenant_id != @TenantId AND id IN
                                                        (SELECT memory_id FROM memory_entities WHERE entity_id IN
                                                            (SELECT id FROM entities WHERE tenant_id = @TenantId))) as cross_tenant_references
                                                
                                    """;

        var crossRefs = await connection.ExecuteScalarAsync<int>(isolationSql, new { TenantId = tenantId });
        var isolationCheck = new IsolationCheckResult
        {
            Status = crossRefs == 0 ? "healthy" : "warning",
            Message = crossRefs > 0 ? $"Found {crossRefs} potential cross-tenant references" : null,
            LastCheckedAt = DateTimeOffset.UtcNow
        };

        // Error rate (from usage events)
        const string errorSql = """

                                            SELECT
                                                COUNT(*) as total_requests,
                                                SUM(CASE WHEN status_code >= 400 THEN 1 ELSE 0 END) as error_count
                                            FROM usage_events
                                            WHERE tenant_id = @TenantId
                                              AND timestamp > NOW() - INTERVAL '24 hours'
                                """;

        var errorStats = await connection.QuerySingleOrDefaultAsync<dynamic>(errorSql, new { TenantId = tenantId });
        var totalReqs = (int)(errorStats?.total_requests ?? 0);
        var errorCount = (int)(errorStats?.error_count ?? 0);

        var errorRate = new ErrorRateResult
        {
            TotalRequests = totalReqs,
            ErrorCount = errorCount,
            ErrorRate = totalReqs > 0 ? (decimal)errorCount / totalReqs : 0,
            Period = "last_24h"
        };

        // Quota usage
        const string quotaSql = """

                                            SELECT
                                                COALESCE(bc.credits_used, 0) as credits_used,
                                                COALESCE(ts.credits_per_cycle, 100) as credits_limit,
                                                (SELECT COUNT(*) FROM memories WHERE tenant_id = @TenantId AND is_active = true) as memories_count,
                                                ts.max_memories,
                                                (SELECT COUNT(*) FROM entities WHERE tenant_id = @TenantId) as entities_count,
                                                ts.max_entities
                                            FROM tenants t
                                            LEFT JOIN tenant_settings ts ON ts.tenant_id = t.id
                                            LEFT JOIN billing_cycles bc ON bc.tenant_id = t.id
                                                AND bc.cycle_start <= NOW() AND bc.cycle_end > NOW()
                                            WHERE t.id = @TenantId
                                """;

        var quota = await connection.QuerySingleOrDefaultAsync<dynamic>(quotaSql, new { TenantId = tenantId });

        var quotaUsage = new QuotaUsageResult
        {
            CreditsUsed = (decimal)(quota?.credits_used ?? 0),
            CreditsLimit = (decimal)(quota?.credits_limit ?? 100),
            PercentUsed = quota?.credits_limit > 0
                ? (decimal)(quota?.credits_used ?? 0) / (decimal)(quota?.credits_limit ?? 0) * 100
                : 0,
            MemoriesCount = (int)(quota?.memories_count ?? 0),
            MemoriesLimit = (int?)quota?.max_memories,
            EntitiesCount = (int)(quota?.entities_count ?? 0),
            EntitiesLimit = (int?)quota?.max_entities
        };

        // Abuse flags (simplified - check for high error rate, high usage, etc.)
        var abuseFlags = new List<AbuseFlag>();

        if (errorRate.ErrorRate > 0.5m)
        {
            abuseFlags.Add(new AbuseFlag
            {
                Type = "high_error_rate",
                Message = $"Error rate {errorRate.ErrorRate:P0} exceeds threshold",
                FlaggedAt = DateTimeOffset.UtcNow,
                Severity = errorRate.ErrorRate > 0.8m ? "critical" : "warning"
            });
        }

        if (quotaUsage.PercentUsed > 95)
        {
            abuseFlags.Add(new AbuseFlag
            {
                Type = "quota_exceeded",
                Message = $"Usage at {quotaUsage.PercentUsed:F0}% of quota",
                FlaggedAt = DateTimeOffset.UtcNow,
                Severity = "critical"
            });
        }

        // Last self-audit (from admin_audit_log)
        var auditSql = """

                                   SELECT MAX(created_at) FROM admin_audit_log
                                   WHERE tenant_id = @TenantId AND action_type = 'self_audit'
                       """;

        var lastAudit = await connection.ExecuteScalarAsync<DateTimeOffset?>(auditSql, new { TenantId = tenantId });

        return new TenantHealthResult
        {
            TenantId = tenantId,
            TenantName = (string)tenant.name,
            Status = (string)tenant.status,
            IsolationCheck = isolationCheck,
            RecentErrorRate = errorRate,
            QuotaUsage = quotaUsage,
            AbuseFlags = abuseFlags,
            LastSelfAuditAt = lastAudit
        };
    }

    public async Task<SystemHealthResult> GetSystemHealthAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        // Database health check
        ComponentHealth dbHealth;
        try
        {
            await connection.ExecuteScalarAsync<int>("SELECT 1");
            var dbStats = await connection.QuerySingleAsync<dynamic>("""

                                                                                     SELECT
                                                                                         pg_database_size(current_database()) as db_size,
                                                                                         (SELECT COUNT(*) FROM pg_stat_activity WHERE state = 'active') as active_connections
                                                                                 
                                                                     """);

            dbHealth = new ComponentHealth
            {
                Status = "healthy",
                LastCheckedAt = DateTimeOffset.UtcNow,
                Details = new Dictionary<string, object>
                {
                    ["database_size_bytes"] = (long)dbStats.db_size,
                    ["active_connections"] = (int)dbStats.active_connections
                }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Database health check failed");
            dbHealth = new ComponentHealth
            {
                Status = "unhealthy",
                Message = ex.Message,
                LastCheckedAt = DateTimeOffset.UtcNow
            };
        }

        // Rate limiter health (check recent rejections from usage_events)
        const string rateLimitSql = """

                                                SELECT COUNT(*) FROM usage_events
                                                WHERE status_code = 429
                                                  AND timestamp > NOW() - INTERVAL '1 hour'
                                    """;

        var recentRejections = await connection.ExecuteScalarAsync<int>(rateLimitSql);

        var rateLimiterHealth = new RateLimiterHealth
        {
            RecentRejections = recentRejections,
            Period = "last_1h",
            PressurePercent = Math.Min(100, recentRejections / 10.0m) // Arbitrary scale
        };

        // System stats
        const string statsSql = """

                                            SELECT
                                                (SELECT COUNT(*) FROM tenants) as total_tenants,
                                                (SELECT COUNT(*) FROM tenants WHERE status = 'active') as active_tenants,
                                                (SELECT COUNT(*) FROM memories WHERE is_active = true) as total_memories,
                                                (SELECT COUNT(*) FROM entities) as total_entities,
                                                COALESCE((SELECT SUM(credits_consumed) FROM usage_events
                                                    WHERE timestamp > CURRENT_DATE), 0) as credits_today
                                """;

        var stats = await connection.QuerySingleAsync<dynamic>(statsSql);

        var systemStats = new SystemStats
        {
            TotalTenants = (int)(stats.total_tenants ?? 0),
            ActiveTenants = (int)(stats.active_tenants ?? 0),
            TotalMemories = (long)(stats.total_memories ?? 0),
            TotalEntities = (long)(stats.total_entities ?? 0),
            TotalCreditsUsedToday = (decimal)(stats.credits_today ?? 0)
        };

        // Determine overall status
        var overallStatus = "healthy";
        if (dbHealth.Status != "healthy") overallStatus = "unhealthy";
        else if (rateLimiterHealth.PressurePercent > 80) overallStatus = "degraded";

        return new SystemHealthResult
        {
            Status = overallStatus,
            Timestamp = DateTimeOffset.UtcNow,
            Database = dbHealth,
            Queue = null, // Would check RabbitMQ if enabled
            Worker = null, // Would check worker heartbeats
            RateLimiter = rateLimiterHealth,
            Stats = systemStats
        };
    }

    public async Task<IReadOnlyList<TenantSearchResult>> SearchTenantsAsync(
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        var searchPattern = $"%{query}%";

        const string sql = """
            SELECT
                t.id AS TenantId,
                t.name AS Name,
                t.slug AS Slug,
                tu.email AS Email,
                t.status AS Status,
                t.created_at AS CreatedAt
            FROM tenants t
            LEFT JOIN tenant_users tu ON tu.tenant_id = t.id AND tu.role = 'owner'
            WHERE t.name ILIKE @Pattern
               OR t.slug ILIKE @Pattern
               OR tu.email ILIKE @Pattern
            ORDER BY t.created_at DESC
            LIMIT @Limit
            """;

        var results = await connection.QueryAsync<TenantSearchResult>(
            sql,
            new { Pattern = searchPattern, Limit = limit });

        return results.ToList();
    }
}
