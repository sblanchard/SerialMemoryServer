using System.Security.Claims;
using Dapper;
using Npgsql;
using SerialMemory.Infrastructure;

namespace SerialMemory.Api.Dashboard;

/// <summary>
/// Dashboard endpoints for Dedup & Supersede — duplicate detection stats, supersede history, merge history.
/// </summary>
public static class DedupEndpoints
{
    public static void MapDedupEndpoints(this WebApplication app, bool selfHostedMode)
    {
        var group = app.MapGroup("/api/dedup")
            .WithTags("Dedup & Supersede")
            .RequireAuthorization();

        // =============================================================================
        // GET /dedup/stats - Dedup & supersede overview stats
        // =============================================================================
        group.MapGet("/stats", async (
            int? days,
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            CancellationToken ct) =>
        {
            var tenantId = GetTenantId(user, selfHostedMode);
            var periodDays = Math.Clamp(days ?? 30, 1, 365);

            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.SetInternalAdminWithTenantAsync(tenantId);

            // Count dedup events from usage_events metadata
            var dedupStats = await conn.QueryFirstOrDefaultAsync<DedupStatsRawDto>(
                """
                SELECT
                    COUNT(*) FILTER (WHERE metadata->>'dedup_mode' IS NOT NULL AND metadata->>'dedup_detected' = 'true') AS dupes_detected,
                    COUNT(*) FILTER (WHERE metadata->>'dedup_mode' = 'skip' AND metadata->>'dedup_detected' = 'true') AS dupes_skipped,
                    COUNT(*) FILTER (WHERE metadata->>'dedup_mode' = 'append' AND metadata->>'dedup_detected' = 'true') AS dupes_appended,
                    COUNT(*) FILTER (WHERE metadata->>'dedup_mode' = 'warn' AND metadata->>'dedup_detected' = 'true') AS dupes_warned
                FROM usage_events
                WHERE tenant_id = @TenantId
                    AND tool_name = 'memory_ingest'
                    AND created_at > NOW() - make_interval(days => @Days)
                """,
                new { TenantId = tenantId.ToString(), Days = periodDays });

            // Count supersede and merge events from event store
            var eventStats = await conn.QueryFirstOrDefaultAsync<EventStatsRawDto>(
                """
                SELECT
                    COUNT(*) FILTER (WHERE event_type::text = 'MemoryInvalidated'
                        AND (event_data::jsonb->>'supersededById') IS NOT NULL) AS supersede_count,
                    COUNT(*) FILTER (WHERE event_type::text = 'MemoryMerged') AS merge_count
                FROM memory_events
                WHERE tenant_id = @TenantId
                    AND created_at > NOW() - make_interval(days => @Days)
                """,
                new { TenantId = tenantId.ToString(), Days = periodDays });

            return Results.Ok(new
            {
                period_days = periodDays,
                dupes_detected = dedupStats?.dupes_detected ?? 0,
                dupes_skipped = dedupStats?.dupes_skipped ?? 0,
                dupes_appended = dedupStats?.dupes_appended ?? 0,
                dupes_warned = dedupStats?.dupes_warned ?? 0,
                supersede_count = eventStats?.supersede_count ?? 0,
                merge_count = eventStats?.merge_count ?? 0
            });
        })
        .WithName("GetDedupStats")
        .WithDescription("Get dedup and supersede statistics")
        .Produces(StatusCodes.Status200OK);

        // =============================================================================
        // GET /dedup/supersede-history - Supersede chain history
        // =============================================================================
        group.MapGet("/supersede-history", async (
            int? limit,
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            CancellationToken ct) =>
        {
            var tenantId = GetTenantId(user, selfHostedMode);

            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.SetInternalAdminWithTenantAsync(tenantId);

            var history = await conn.QueryAsync<SupersedeHistoryDto>(
                """
                SELECT
                    me.event_id AS event_id,
                    me.stream_id AS old_memory_id,
                    (me.event_data::jsonb->>'supersededById')::uuid AS new_memory_id,
                    me.event_data::jsonb->>'reason' AS reason,
                    me.created_by AS actor_id,
                    me.created_at AS superseded_at,
                    LEFT(m_old.content, 120) AS old_content_preview,
                    LEFT(m_new.content, 120) AS new_content_preview
                FROM memory_events me
                LEFT JOIN memories m_old ON me.stream_id = m_old.id
                LEFT JOIN memories m_new ON (me.event_data::jsonb->>'supersededById')::uuid = m_new.id
                WHERE me.event_type::text = 'MemoryInvalidated'
                    AND (me.event_data::jsonb->>'supersededById') IS NOT NULL
                    AND me.tenant_id = @TenantId
                ORDER BY me.created_at DESC
                LIMIT @Limit
                """,
                new { TenantId = tenantId.ToString(), Limit = Math.Clamp(limit ?? 20, 1, 100) });

            return Results.Ok(new { history = history.ToList() });
        })
        .WithName("GetSupersedeHistory")
        .WithDescription("Get memory supersede history")
        .Produces(StatusCodes.Status200OK);

        // =============================================================================
        // GET /dedup/merge-history - Merge event history
        // =============================================================================
        group.MapGet("/merge-history", async (
            int? limit,
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            CancellationToken ct) =>
        {
            var tenantId = GetTenantId(user, selfHostedMode);

            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.SetInternalAdminWithTenantAsync(tenantId);

            var history = await conn.QueryAsync<MergeHistoryDto>(
                """
                SELECT
                    me.event_id AS event_id,
                    me.stream_id AS merged_memory_id,
                    me.event_data::jsonb->>'strategy' AS strategy,
                    me.created_by AS actor_id,
                    me.created_at AS merged_at,
                    LEFT(m.content, 120) AS content_preview
                FROM memory_events me
                LEFT JOIN memories m ON me.stream_id = m.id
                WHERE me.event_type::text = 'MemoryMerged'
                    AND me.tenant_id = @TenantId
                ORDER BY me.created_at DESC
                LIMIT @Limit
                """,
                new { TenantId = tenantId.ToString(), Limit = Math.Clamp(limit ?? 20, 1, 100) });

            return Results.Ok(new { history = history.ToList() });
        })
        .WithName("GetMergeHistory")
        .WithDescription("Get memory merge history")
        .Produces(StatusCodes.Status200OK);
    }

    private static Guid GetTenantId(ClaimsPrincipal user, bool selfHosted)
        => DashboardHelpers.GetTenantId(user, selfHosted);
}

// DTOs
public sealed class DedupStatsRawDto
{
    public long dupes_detected { get; set; }
    public long dupes_skipped { get; set; }
    public long dupes_appended { get; set; }
    public long dupes_warned { get; set; }
}

public sealed class EventStatsRawDto
{
    public long supersede_count { get; set; }
    public long merge_count { get; set; }
}

public sealed class SupersedeHistoryDto
{
    public Guid event_id { get; set; }
    public Guid old_memory_id { get; set; }
    public Guid? new_memory_id { get; set; }
    public string? reason { get; set; }
    public string? actor_id { get; set; }
    public DateTimeOffset superseded_at { get; set; }
    public string? old_content_preview { get; set; }
    public string? new_content_preview { get; set; }
}

public sealed class MergeHistoryDto
{
    public Guid event_id { get; set; }
    public Guid merged_memory_id { get; set; }
    public string? strategy { get; set; }
    public string? actor_id { get; set; }
    public DateTimeOffset merged_at { get; set; }
    public string? content_preview { get; set; }
}
