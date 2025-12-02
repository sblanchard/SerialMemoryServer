using System.Security.Claims;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using SerialMemory.Core.Interfaces;

namespace SerialMemory.Api.Dashboard;

/// <summary>
/// Dashboard endpoints for Events, Timeline, Mind Health, and Replay.
/// These power the Recent Events, Time-Travel, and Mind Health dashboard features.
/// </summary>
public static class EventsEndpoints
{
    public static void MapEventsEndpoints(this WebApplication app, bool selfHostedMode)
    {
        var group = app.MapGroup("/events")
            .WithTags("Events")
            .RequireAuthorization();

        // =============================================================================
        // GET /events/recent - Get recent events (powers "Recent Events" dashboard)
        // =============================================================================
        group.MapGet("/recent", async (
            int? limit,
            int? offset,
            string? category,
            string? eventType,
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            CancellationToken ct) =>
        {
            var tenantId = GetTenantId(user, selfHostedMode);

            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @TenantId, false)",
                new { TenantId = tenantId.ToString() });

            var events = await conn.QueryAsync<RecentEventDto>(
                """
                SELECT * FROM get_recent_events(@TenantId, @Limit, @Offset, @Category, @EventType)
                """,
                new
                {
                    TenantId = tenantId,
                    Limit = limit ?? 50,
                    Offset = offset ?? 0,
                    Category = category,
                    EventType = eventType
                });

            return Results.Ok(new
            {
                events = events.ToList(),
                limit = limit ?? 50,
                offset = offset ?? 0
            });
        })
        .WithName("GetRecentEvents")
        .WithDescription("Gets recent events for the tenant (powers Recent Events dashboard)")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized);

        // =============================================================================
        // GET /events/memory/{memoryId} - Get events for a specific memory
        // =============================================================================
        group.MapGet("/memory/{memoryId:guid}", async (
            Guid memoryId,
            int? limit,
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            CancellationToken ct) =>
        {
            var tenantId = GetTenantId(user, selfHostedMode);

            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @TenantId, false)",
                new { TenantId = tenantId.ToString() });

            var events = await conn.QueryAsync<RecentEventDto>(
                """
                SELECT id, seq, event_type, memory_id, actor, payload, category, severity, created_at
                FROM event_log
                WHERE tenant_id = @TenantId AND memory_id = @MemoryId
                ORDER BY seq DESC
                LIMIT @Limit
                """,
                new { TenantId = tenantId, MemoryId = memoryId, Limit = limit ?? 100 });

            return Results.Ok(events.ToList());
        })
        .WithName("GetMemoryEvents")
        .WithDescription("Gets all events for a specific memory")
        .Produces<IReadOnlyList<RecentEventDto>>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized);

        // =============================================================================
        // GET /events/timeline/{memoryId} - Get timeline snapshots for replay
        // =============================================================================
        group.MapGet("/timeline/{memoryId:guid}", async (
            Guid memoryId,
            DateTimeOffset? from,
            DateTimeOffset? to,
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            CancellationToken ct) =>
        {
            var tenantId = GetTenantId(user, selfHostedMode);

            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @TenantId, false)",
                new { TenantId = tenantId.ToString() });

            var snapshots = await conn.QueryAsync<TimelineSnapshotDto>(
                """
                SELECT * FROM get_memory_timeline(@TenantId, @MemoryId, @FromTime, @ToTime)
                """,
                new
                {
                    TenantId = tenantId,
                    MemoryId = memoryId,
                    FromTime = from,
                    ToTime = to
                });

            return Results.Ok(new
            {
                memory_id = memoryId,
                snapshots = snapshots.ToList()
            });
        })
        .WithName("EventsGetMemoryTimeline")
        .WithDescription("Gets timeline snapshots for memory replay/time-travel")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized);

        // =============================================================================
        // POST /events/timeline/{memoryId}/replay - Replay memory to a point in time
        // =============================================================================
        group.MapPost("/timeline/{memoryId:guid}/replay", async (
            Guid memoryId,
            [FromBody] ReplayRequest request,
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            CancellationToken ct) =>
        {
            var tenantId = GetTenantId(user, selfHostedMode);

            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @TenantId, false)",
                new { TenantId = tenantId.ToString() });

            // Get the snapshot at or before the requested time
            var snapshot = await conn.QueryFirstOrDefaultAsync<TimelineSnapshotDto>(
                """
                SELECT id, snapshot_at, event_type, content, layer, confidence_score, metadata
                FROM memory_timeline
                WHERE tenant_id = @TenantId AND memory_id = @MemoryId
                  AND snapshot_at <= @ReplayAt
                ORDER BY snapshot_at DESC
                LIMIT 1
                """,
                new { TenantId = tenantId, MemoryId = memoryId, ReplayAt = request.ReplayAt });

            if (snapshot == null)
                return Results.NotFound(new { error = "not_found", message = "No snapshot found for the specified time" });

            return Results.Ok(new
            {
                memory_id = memoryId,
                replayed_at = request.ReplayAt,
                snapshot
            });
        })
        .WithName("ReplayMemory")
        .WithDescription("Replays a memory to a specific point in time")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status404NotFound);

        // =============================================================================
        // GET /events/mind-health - Get current mind health metrics
        // =============================================================================
        group.MapGet("/mind-health", async (
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            CancellationToken ct) =>
        {
            var tenantId = GetTenantId(user, selfHostedMode);

            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @TenantId, false)",
                new { TenantId = tenantId.ToString() });

            var health = await conn.QueryFirstOrDefaultAsync<MindHealthDto>(
                """
                SELECT id, computed_at, total_memories, active_memories, decayed_memories,
                       contradictions_detected, hallucinations_flagged,
                       l0_count, l1_count, l2_count, l3_count, l4_count,
                       avg_confidence, min_confidence,
                       entity_count, relationship_count, orphan_entity_count,
                       events_last_hour, events_last_day,
                       health_score, health_status, metadata
                FROM mind_health
                WHERE tenant_id = @TenantId
                ORDER BY computed_at DESC
                LIMIT 1
                """,
                new { TenantId = tenantId });

            if (health == null)
            {
                // Return default health if none computed yet
                return Results.Ok(new MindHealthDto
                {
                    health_score = 100,
                    health_status = "healthy",
                    computed_at = DateTimeOffset.UtcNow,
                    total_memories = 0,
                    active_memories = 0
                });
            }

            return Results.Ok(health);
        })
        .WithName("EventsGetMindHealth")
        .WithDescription("Gets the current mind health metrics for the tenant")
        .Produces<MindHealthDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized);

        // =============================================================================
        // POST /events/mind-health/compute - Trigger mind health computation
        // =============================================================================
        group.MapPost("/mind-health/compute", async (
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            IEventWriter eventWriter,
            CancellationToken ct) =>
        {
            var tenantId = GetTenantId(user, selfHostedMode);

            var healthId = await eventWriter.ComputeMindHealthAsync(tenantId, ct);

            return Results.Ok(new { health_id = healthId, message = "Mind health computed successfully" });
        })
        .WithName("EventsComputeMindHealth")
        .WithDescription("Triggers a fresh computation of mind health metrics")
        .RequireAuthorization("Admin")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized);

        // =============================================================================
        // GET /events/conflicts - Get unresolved conflicts
        // =============================================================================
        group.MapGet("/conflicts", async (
            bool? includeResolved,
            int? limit,
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            CancellationToken ct) =>
        {
            var tenantId = GetTenantId(user, selfHostedMode);

            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @TenantId, false)",
                new { TenantId = tenantId.ToString() });

            var whereClause = includeResolved == true ? "" : "AND resolved = FALSE";

            var conflicts = await conn.QueryAsync<ConflictResultDto>(
                $"""
                SELECT id, scan_id, memory_a_id, memory_b_id, similarity_score,
                       contradiction_type, explanation, resolved, resolved_at,
                       resolved_by, resolution_action, created_at
                FROM conflict_results
                WHERE tenant_id = @TenantId {whereClause}
                ORDER BY created_at DESC
                LIMIT @Limit
                """,
                new { TenantId = tenantId, Limit = limit ?? 50 });

            return Results.Ok(new
            {
                conflicts = conflicts.ToList(),
                unresolved_count = conflicts.Count(c => !c.resolved)
            });
        })
        .WithName("GetConflicts")
        .WithDescription("Gets detected conflicts/contradictions")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized);

        // =============================================================================
        // GET /events/branches - Get shadow branches
        // =============================================================================
        group.MapGet("/branches", async (
            string? status,
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            CancellationToken ct) =>
        {
            var tenantId = GetTenantId(user, selfHostedMode);

            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @TenantId, false)",
                new { TenantId = tenantId.ToString() });

            var whereClause = string.IsNullOrEmpty(status) ? "" : "AND status = @Status";

            var branches = await conn.QueryAsync<BranchMetadataDto>(
                $"""
                SELECT id, branch_name, description, source_branch, created_by, created_at,
                       merged_at, merged_by, status, memory_count, entity_count
                FROM branch_metadata
                WHERE tenant_id = @TenantId {whereClause}
                ORDER BY created_at DESC
                """,
                new { TenantId = tenantId, Status = status });

            return Results.Ok(branches.ToList());
        })
        .WithName("GetBranches")
        .WithDescription("Gets shadow branches")
        .Produces<IReadOnlyList<BranchMetadataDto>>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized);

        // =============================================================================
        // POST /events/branches - Create a new shadow branch
        // =============================================================================
        group.MapPost("/branches", async (
            [FromBody] CreateBranchRequest request,
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            IEventWriter eventWriter,
            CancellationToken ct) =>
        {
            var tenantId = GetTenantId(user, selfHostedMode);
            var userId = GetUserId(user);

            await using var conn = await dataSource.OpenConnectionAsync(ct);

            // Set internal admin for RLS bypass
            await conn.ExecuteAsync("SELECT set_config('app.role', 'internal_admin', false)");
            await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @TenantId, false)",
                new { TenantId = tenantId.ToString() });

            // Create branch
            var branchId = Guid.CreateVersion7();
            await conn.ExecuteAsync(
                """
                INSERT INTO branch_metadata (id, tenant_id, branch_name, description, source_branch, created_by)
                VALUES (@Id, @TenantId, @BranchName, @Description, @SourceBranch, @CreatedBy)
                """,
                new
                {
                    Id = branchId,
                    TenantId = tenantId,
                    BranchName = request.BranchName,
                    Description = request.Description,
                    SourceBranch = request.SourceBranch ?? "main",
                    CreatedBy = userId
                });

            // Emit BranchCreated event
            await eventWriter.EmitBranchCreatedAsync(
                tenantId, branchId, request.BranchName,
                request.SourceBranch ?? "main", request.Description,
                userId, ct);

            return Results.Created($"/events/branches/{branchId}", new
            {
                id = branchId,
                branch_name = request.BranchName,
                status = "active",
                message = "Branch created successfully"
            });
        })
        .WithName("EventsCreateBranch")
        .WithDescription("Creates a new shadow branch")
        .RequireAuthorization("Admin")
        .Produces(StatusCodes.Status201Created)
        .Produces(StatusCodes.Status401Unauthorized);

        // =============================================================================
        // GET /events/stats - Get event statistics
        // =============================================================================
        group.MapGet("/stats", async (
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            CancellationToken ct) =>
        {
            var tenantId = GetTenantId(user, selfHostedMode);

            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @TenantId, false)",
                new { TenantId = tenantId.ToString() });

            var stats = await conn.QueryFirstOrDefaultAsync<EventStatsDto>(
                """
                SELECT
                    COUNT(*) AS total_events,
                    COUNT(*) FILTER (WHERE created_at > NOW() - INTERVAL '1 hour') AS events_last_hour,
                    COUNT(*) FILTER (WHERE created_at > NOW() - INTERVAL '24 hours') AS events_last_day,
                    COUNT(*) FILTER (WHERE created_at > NOW() - INTERVAL '7 days') AS events_last_week,
                    COUNT(DISTINCT memory_id) FILTER (WHERE memory_id IS NOT NULL) AS unique_memories,
                    COUNT(DISTINCT event_type) AS unique_event_types,
                    MAX(created_at) AS last_event_at
                FROM event_log
                WHERE tenant_id = @TenantId
                """,
                new { TenantId = tenantId });

            return Results.Ok(stats ?? new EventStatsDto());
        })
        .WithName("GetEventStats")
        .WithDescription("Gets event statistics for the tenant")
        .Produces<EventStatsDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized);
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

// DTOs for Events endpoints
public sealed class RecentEventDto
{
    public Guid id { get; set; }
    public long seq { get; set; }
    public string event_type { get; set; } = "";
    public Guid? memory_id { get; set; }
    public string actor { get; set; } = "";
    public object? payload { get; set; }
    public string category { get; set; } = "";
    public string severity { get; set; } = "";
    public DateTimeOffset created_at { get; set; }
}

public sealed class TimelineSnapshotDto
{
    public Guid id { get; set; }
    public DateTimeOffset snapshot_at { get; set; }
    public string event_type { get; set; } = "";
    public string content { get; set; } = "";
    public string layer { get; set; } = "";
    public decimal confidence_score { get; set; }
    public object? metadata { get; set; }
}

public sealed class MindHealthDto
{
    public Guid? id { get; set; }
    public DateTimeOffset computed_at { get; set; }
    public int total_memories { get; set; }
    public int active_memories { get; set; }
    public int decayed_memories { get; set; }
    public int contradictions_detected { get; set; }
    public int hallucinations_flagged { get; set; }
    public int l0_count { get; set; }
    public int l1_count { get; set; }
    public int l2_count { get; set; }
    public int l3_count { get; set; }
    public int l4_count { get; set; }
    public decimal avg_confidence { get; set; }
    public decimal min_confidence { get; set; }
    public int entity_count { get; set; }
    public int relationship_count { get; set; }
    public int orphan_entity_count { get; set; }
    public int events_last_hour { get; set; }
    public int events_last_day { get; set; }
    public int health_score { get; set; }
    public string health_status { get; set; } = "";
    public object? metadata { get; set; }
}

public sealed class ConflictResultDto
{
    public Guid id { get; set; }
    public Guid scan_id { get; set; }
    public Guid memory_a_id { get; set; }
    public Guid memory_b_id { get; set; }
    public decimal similarity_score { get; set; }
    public string contradiction_type { get; set; } = "";
    public string? explanation { get; set; }
    public bool resolved { get; set; }
    public DateTimeOffset? resolved_at { get; set; }
    public string? resolved_by { get; set; }
    public string? resolution_action { get; set; }
    public DateTimeOffset created_at { get; set; }
}

public sealed class BranchMetadataDto
{
    public Guid id { get; set; }
    public string branch_name { get; set; } = "";
    public string? description { get; set; }
    public string source_branch { get; set; } = "";
    public string created_by { get; set; } = "";
    public DateTimeOffset created_at { get; set; }
    public DateTimeOffset? merged_at { get; set; }
    public string? merged_by { get; set; }
    public string status { get; set; } = "";
    public int memory_count { get; set; }
    public int entity_count { get; set; }
}

public sealed class EventStatsDto
{
    public long total_events { get; set; }
    public long events_last_hour { get; set; }
    public long events_last_day { get; set; }
    public long events_last_week { get; set; }
    public long unique_memories { get; set; }
    public long unique_event_types { get; set; }
    public DateTimeOffset? last_event_at { get; set; }
}

public sealed record ReplayRequest(DateTimeOffset ReplayAt);
public sealed record CreateBranchRequest(string BranchName, string? Description = null, string? SourceBranch = null);
