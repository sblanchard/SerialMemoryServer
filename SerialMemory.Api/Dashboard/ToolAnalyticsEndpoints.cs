using System.Security.Claims;
using Dapper;
using Npgsql;
using SerialMemory.Infrastructure;

namespace SerialMemory.Api.Dashboard;

/// <summary>
/// Dashboard endpoints for Tool Analytics — MCP tool usage stats and category breakdowns.
/// </summary>
public static class ToolAnalyticsEndpoints
{
    /// <summary>
    /// Maps tool name → lazy-MCP category for grouping.
    /// </summary>
    private static readonly Dictionary<string, string> ToolCategoryMap = new()
    {
        // Core
        ["memory_search"] = "core",
        ["memory_ingest"] = "core",
        ["memory_multi_hop_search"] = "core",
        ["memory_about_user"] = "core",
        // Lifecycle
        ["memory_update"] = "lifecycle",
        ["memory_delete"] = "lifecycle",
        ["memory_merge"] = "lifecycle",
        ["memory_split"] = "lifecycle",
        ["memory_decay"] = "lifecycle",
        ["memory_reinforce"] = "lifecycle",
        ["memory_expire"] = "lifecycle",
        ["memory_supersede"] = "lifecycle",
        // Observability
        ["memory_trace"] = "observability",
        ["memory_lineage"] = "observability",
        ["memory_explain"] = "observability",
        ["memory_conflicts"] = "observability",
        // Safety
        ["detect_contradictions"] = "safety",
        ["detect_hallucinations"] = "safety",
        ["verify_memory_integrity"] = "safety",
        ["scan_loops"] = "safety",
        // Export
        ["export_workspace"] = "export",
        ["export_memories"] = "export",
        ["export_graph"] = "export",
        ["export_user_profile"] = "export",
        ["export_markdown"] = "export",
        // Reasoning
        ["engineering_analyze"] = "reasoning",
        ["engineering_visualize"] = "reasoning",
        ["engineering_reason"] = "reasoning",
        // Session
        ["initialise_conversation_session"] = "session",
        ["end_conversation_session"] = "session",
        ["instantiate_context"] = "session",
        // Admin
        ["set_user_persona"] = "admin",
        ["get_integrations"] = "admin",
        ["import_from_core"] = "admin",
        ["crawl_relationships"] = "admin",
        ["get_graph_statistics"] = "admin",
        ["get_model_info"] = "admin",
        ["reembed_memories"] = "admin",
    };

    public static void MapToolAnalyticsEndpoints(this WebApplication app, bool selfHostedMode)
    {
        var group = app.MapGroup("/api/tools")
            .WithTags("Tool Analytics")
            .RequireAuthorization();

        // =============================================================================
        // GET /tools/stats - Aggregate tool usage stats
        // =============================================================================
        group.MapGet("/stats", async (
            int? days,
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            CancellationToken ct) =>
        {
            var tenantId = GetTenantId(user, selfHostedMode);
            var periodDays = Math.Clamp(days ?? 7, 1, 365);

            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.SetInternalAdminWithTenantAsync(tenantId);

            var stats = await conn.QueryFirstOrDefaultAsync<ToolStatsDto>(
                """
                SELECT
                    COUNT(*) AS total_calls,
                    COUNT(DISTINCT tool_name) AS unique_tools,
                    AVG((metadata->>'latency_ms')::numeric) AS avg_latency_ms,
                    SUM(CASE WHEN NOT success THEN 1 ELSE 0 END) AS error_count,
                    CASE WHEN COUNT(*) > 0
                         THEN ROUND(SUM(CASE WHEN NOT success THEN 1 ELSE 0 END)::numeric / COUNT(*)::numeric * 100, 1)
                         ELSE 0 END AS error_rate_pct,
                    COUNT(*) FILTER (WHERE created_at > NOW() - INTERVAL '1 day') AS calls_today,
                    COUNT(*) FILTER (WHERE created_at > NOW() - INTERVAL '7 days') AS calls_7d,
                    COUNT(*) FILTER (WHERE created_at > NOW() - INTERVAL '30 days') AS calls_30d
                FROM usage_events
                WHERE tenant_id = @TenantId
                    AND created_at > NOW() - make_interval(days => @Days)
                """,
                new { TenantId = tenantId, Days = periodDays });

            return Results.Ok(stats ?? new ToolStatsDto());
        })
        .WithName("GetToolStats")
        .WithDescription("Get aggregate tool usage statistics")
        .Produces<ToolStatsDto>(StatusCodes.Status200OK);

        // =============================================================================
        // GET /tools/top - Top tools by call count
        // =============================================================================
        group.MapGet("/top", async (
            int? days,
            int? limit,
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            CancellationToken ct) =>
        {
            var tenantId = GetTenantId(user, selfHostedMode);
            var periodDays = Math.Clamp(days ?? 7, 1, 365);

            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.SetInternalAdminWithTenantAsync(tenantId);

            var tools = await conn.QueryAsync<TopToolDto>(
                """
                SELECT
                    tool_name,
                    COUNT(*) AS calls,
                    AVG((metadata->>'latency_ms')::numeric) AS avg_latency_ms,
                    SUM(CASE WHEN NOT success THEN 1 ELSE 0 END) AS errors,
                    MAX(created_at) AS last_used_at
                FROM usage_events
                WHERE tenant_id = @TenantId
                    AND created_at > NOW() - make_interval(days => @Days)
                GROUP BY tool_name
                ORDER BY calls DESC
                LIMIT @Limit
                """,
                new { TenantId = tenantId, Days = periodDays, Limit = Math.Clamp(limit ?? 10, 1, 50) });

            // Enrich with category info
            var result = tools.Select(t => new
            {
                t.tool_name,
                category = ToolCategoryMap.GetValueOrDefault(t.tool_name, "other"),
                t.calls,
                t.avg_latency_ms,
                t.errors,
                t.last_used_at
            });

            return Results.Ok(new { tools = result.ToList() });
        })
        .WithName("GetTopTools")
        .WithDescription("Get top tools by call count")
        .Produces(StatusCodes.Status200OK);

        // =============================================================================
        // GET /tools/categories - Calls grouped by lazy-MCP category
        // =============================================================================
        group.MapGet("/categories", async (
            int? days,
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            CancellationToken ct) =>
        {
            var tenantId = GetTenantId(user, selfHostedMode);
            var periodDays = Math.Clamp(days ?? 7, 1, 365);

            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.SetInternalAdminWithTenantAsync(tenantId);

            var toolCounts = await conn.QueryAsync<ToolCountDto>(
                """
                SELECT tool_name, COUNT(*) AS calls
                FROM usage_events
                WHERE tenant_id = @TenantId
                    AND created_at > NOW() - make_interval(days => @Days)
                GROUP BY tool_name
                """,
                new { TenantId = tenantId, Days = periodDays });

            // Aggregate by category
            var categories = toolCounts
                .GroupBy(t => ToolCategoryMap.GetValueOrDefault(t.tool_name, "other"))
                .Select(g => new
                {
                    category = g.Key,
                    calls = g.Sum(t => t.calls),
                    tool_count = g.Count()
                })
                .OrderByDescending(c => c.calls)
                .ToList();

            return Results.Ok(new { categories });
        })
        .WithName("GetToolCategories")
        .WithDescription("Get tool usage grouped by lazy-MCP category")
        .Produces(StatusCodes.Status200OK);

        // =============================================================================
        // GET /tools/recent - Recent tool call log
        // =============================================================================
        group.MapGet("/recent", async (
            int? limit,
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            CancellationToken ct) =>
        {
            var tenantId = GetTenantId(user, selfHostedMode);

            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.SetInternalAdminWithTenantAsync(tenantId);

            var events = await conn.QueryAsync<RecentToolCallDto>(
                """
                SELECT
                    id,
                    tool_name,
                    credits_used,
                    success,
                    (metadata->>'latency_ms')::int AS latency_ms,
                    metadata->>'error' AS error_message,
                    created_at
                FROM usage_events
                WHERE tenant_id = @TenantId
                ORDER BY created_at DESC
                LIMIT @Limit
                """,
                new { TenantId = tenantId, Limit = Math.Clamp(limit ?? 50, 1, 200) });

            return Results.Ok(new { events = events.ToList() });
        })
        .WithName("GetRecentToolCalls")
        .WithDescription("Get recent tool call log")
        .Produces(StatusCodes.Status200OK);
    }

    private static Guid GetTenantId(ClaimsPrincipal user, bool selfHosted)
        => DashboardHelpers.GetTenantId(user, selfHosted);
}

// DTOs
public sealed class ToolStatsDto
{
    public long total_calls { get; set; }
    public int unique_tools { get; set; }
    public decimal? avg_latency_ms { get; set; }
    public long error_count { get; set; }
    public decimal error_rate_pct { get; set; }
    public long calls_today { get; set; }
    public long calls_7d { get; set; }
    public long calls_30d { get; set; }
}

public sealed class TopToolDto
{
    public string tool_name { get; set; } = "";
    public long calls { get; set; }
    public decimal? avg_latency_ms { get; set; }
    public long errors { get; set; }
    public DateTimeOffset? last_used_at { get; set; }
}

public sealed class ToolCountDto
{
    public string tool_name { get; set; } = "";
    public long calls { get; set; }
}

public sealed class RecentToolCallDto
{
    public Guid id { get; set; }
    public string tool_name { get; set; } = "";
    public decimal credits_used { get; set; }
    public bool success { get; set; }
    public int? latency_ms { get; set; }
    public string? error_message { get; set; }
    public DateTimeOffset created_at { get; set; }
}
