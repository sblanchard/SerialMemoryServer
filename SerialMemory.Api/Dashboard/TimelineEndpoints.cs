using System.Security.Claims;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using SerialMemory.Core.Interfaces;
using SerialMemory.Infrastructure;

namespace SerialMemory.Api.Dashboard;

/// <summary>
/// Dashboard endpoints for Memory Timeline and Time Travel.
/// FIX #6: Time travel not working - Fixed tenant headers and sequence ordering.
/// </summary>
public static class TimelineEndpoints
{
    public static void MapTimelineEndpoints(this WebApplication app, bool selfHostedMode)
    {
        var group = app.MapGroup("/timeline")
            .WithTags("Timeline")
            .RequireAuthorization();

        // =============================================================================
        // GET /timeline/{memoryId} - Get timeline for a memory (FIX #6)
        // =============================================================================
        group.MapGet("/{memoryId:guid}", async (
            Guid memoryId,
            DateTimeOffset? from,
            DateTimeOffset? to,
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            CancellationToken ct) =>
        {
            var tenantId = GetTenantId(user, selfHostedMode);

            await using var conn = await dataSource.OpenConnectionAsync(ct);

            // CRITICAL FIX: Set internal_admin to bypass RLS
            await conn.SetInternalAdminWithTenantAsync(tenantId);

            // Verify memory exists and belongs to tenant
            var memoryExists = await conn.ExecuteScalarAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM memories WHERE id = @MemoryId AND tenant_id = @TenantId)",
                new { MemoryId = memoryId, TenantId = tenantId });

            if (!memoryExists)
                return Results.NotFound(new { error = "not_found", message = "Memory not found" });

            // Build date filters - CRITICAL FIX: Use proper parameter binding
            var dateFilters = new List<string>();
            if (from.HasValue)
                dateFilters.Add("mt.occurred_at >= @FromTime");
            if (to.HasValue)
                dateFilters.Add("mt.occurred_at <= @ToTime");

            var whereClause = dateFilters.Count > 0
                ? " AND " + string.Join(" AND ", dateFilters)
                : "";

            // Get timeline entries - CRITICAL FIX: Order by version_number ASC (not DESC)
            var timeline = await conn.QueryAsync<TimelineEntryDto>(
                $"""
                SELECT
                    mt.id,
                    mt.event_type,
                    mt.event_data,
                    mt.version_number,
                    mt.previous_entry_id,
                    mt.actor_id,
                    mt.occurred_at,
                    mt.created_at
                FROM memory_timeline mt
                WHERE mt.memory_id = @MemoryId AND mt.tenant_id = @TenantId
                    {whereClause}
                ORDER BY mt.version_number ASC, mt.occurred_at ASC
                """,
                new
                {
                    MemoryId = memoryId,
                    TenantId = tenantId,
                    FromTime = from,
                    ToTime = to
                });

            // Get current memory state
            var currentState = await conn.QueryFirstOrDefaultAsync<CurrentMemoryStateDto>(
                """
                SELECT id, content, layer, confidence, source, created_at, updated_at
                FROM memories
                WHERE id = @MemoryId AND tenant_id = @TenantId
                """,
                new { MemoryId = memoryId, TenantId = tenantId });

            var timelineList = timeline.ToList();

            return Results.Ok(new
            {
                memoryId,
                currentState,
                timeline = timelineList,
                totalVersions = timelineList.Count,
                dateRange = new
                {
                    from,
                    to,
                    earliest = timelineList.FirstOrDefault()?.occurred_at,
                    latest = timelineList.LastOrDefault()?.occurred_at
                }
            });
        })
        .WithName("GetMemoryTimeline")
        .WithDescription("Gets timeline entries for a memory with time range filtering")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status404NotFound);

        // =============================================================================
        // GET /timeline/{memoryId}/state - Get memory state at a specific point in time
        // =============================================================================
        group.MapGet("/{memoryId:guid}/state", async (
            Guid memoryId,
            [FromQuery] DateTimeOffset at,
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            CancellationToken ct) =>
        {
            var tenantId = GetTenantId(user, selfHostedMode);

            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.SetInternalAdminWithTenantAsync(tenantId);

            // Get the timeline entry at or before the specified time
            var entry = await conn.QueryFirstOrDefaultAsync<TimelineStateDto>(
                """
                SELECT
                    mt.id,
                    mt.event_type,
                    mt.event_data,
                    mt.version_number,
                    mt.occurred_at
                FROM memory_timeline mt
                WHERE mt.memory_id = @MemoryId AND mt.tenant_id = @TenantId
                    AND mt.occurred_at <= @AtTime
                ORDER BY mt.occurred_at DESC, mt.version_number DESC
                LIMIT 1
                """,
                new { MemoryId = memoryId, TenantId = tenantId, AtTime = at });

            if (entry == null)
            {
                // No timeline entry found - return initial state from memory creation
                var initialState = await conn.QueryFirstOrDefaultAsync<InitialStateDto>(
                    """
                    SELECT id, content, layer, confidence, created_at
                    FROM memories
                    WHERE id = @MemoryId AND tenant_id = @TenantId AND created_at <= @AtTime
                    """,
                    new { MemoryId = memoryId, TenantId = tenantId, AtTime = at });

                if (initialState == null)
                    return Results.NotFound(new { error = "not_found", message = "No state found at the specified time" });

                return Results.Ok(new
                {
                    memoryId,
                    requestedAt = at,
                    actualAt = initialState.created_at,
                    version = 0,
                    eventType = "created",
                    state = initialState
                });
            }

            return Results.Ok(new
            {
                memoryId,
                requestedAt = at,
                actualAt = entry.occurred_at,
                version = entry.version_number,
                eventType = entry.event_type,
                state = entry.event_data
            });
        })
        .WithName("GetMemoryStateAtTime")
        .WithDescription("Gets memory state at a specific point in time")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status404NotFound);

        // =============================================================================
        // GET /timeline/{memoryId}/versions - Get all versions of a memory
        // =============================================================================
        group.MapGet("/{memoryId:guid}/versions", async (
            Guid memoryId,
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            CancellationToken ct) =>
        {
            var tenantId = GetTenantId(user, selfHostedMode);

            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.SetInternalAdminWithTenantAsync(tenantId);

            var versions = await conn.QueryAsync<VersionDto>(
                """
                SELECT
                    mt.version_number,
                    mt.event_type,
                    mt.actor_id,
                    mt.occurred_at,
                    CASE
                        WHEN mt.event_data->>'content' IS NOT NULL
                        THEN LEFT(mt.event_data->>'content', 100)
                        ELSE NULL
                    END AS content_preview
                FROM memory_timeline mt
                WHERE mt.memory_id = @MemoryId AND mt.tenant_id = @TenantId
                ORDER BY mt.version_number ASC
                """,
                new { MemoryId = memoryId, TenantId = tenantId });

            var versionList = versions.ToList();

            return Results.Ok(new
            {
                memoryId,
                versions = versionList,
                totalVersions = versionList.Count,
                latestVersion = versionList.LastOrDefault()?.version_number ?? 0
            });
        })
        .WithName("GetMemoryVersions")
        .WithDescription("Gets all versions of a memory")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized);

        // =============================================================================
        // POST /timeline/{memoryId}/restore - Restore memory to a specific version
        // =============================================================================
        group.MapPost("/{memoryId:guid}/restore", async (
            Guid memoryId,
            [FromBody] RestoreVersionRequest request,
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            [FromServices] ITimelineService? timelineService,
            CancellationToken ct) =>
        {
            var tenantId = GetTenantId(user, selfHostedMode);
            var userId = GetUserId(user);

            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.SetInternalAdminWithTenantAsync(tenantId);

            // Get the state at the target version
            var targetState = await conn.QueryFirstOrDefaultAsync<dynamic>(
                """
                SELECT event_data, version_number, occurred_at
                FROM memory_timeline
                WHERE memory_id = @MemoryId AND tenant_id = @TenantId
                    AND version_number = @TargetVersion
                """,
                new { MemoryId = memoryId, TenantId = tenantId, TargetVersion = request.TargetVersion });

            if (targetState == null)
                return Results.NotFound(new { error = "not_found", message = $"Version {request.TargetVersion} not found" });

            // Get current version
            var currentVersion = await conn.ExecuteScalarAsync<int>(
                """
                SELECT COALESCE(MAX(version_number), 0)
                FROM memory_timeline
                WHERE memory_id = @MemoryId AND tenant_id = @TenantId
                """,
                new { MemoryId = memoryId, TenantId = tenantId });

            // Create new timeline entry for the restore
            var newVersion = currentVersion + 1;
            var restoreEntryId = Guid.CreateVersion7();

            await conn.ExecuteAsync(
                """
                INSERT INTO memory_timeline (
                    id, tenant_id, memory_id, event_type, event_data,
                    version_number, actor_id, occurred_at
                ) VALUES (
                    @Id, @TenantId, @MemoryId, 'restored', @EventData,
                    @Version, @ActorId, NOW()
                )
                """,
                new
                {
                    Id = restoreEntryId,
                    TenantId = tenantId,
                    MemoryId = memoryId,
                    EventData = targetState.event_data,
                    Version = newVersion,
                    ActorId = userId
                });

            // Update the actual memory if event_data contains content
            var eventData = targetState.event_data as System.Text.Json.JsonElement?;
            if (eventData.HasValue && eventData.Value.TryGetProperty("content", out var contentProp))
            {
                await conn.ExecuteAsync(
                    """
                    UPDATE memories
                    SET content = @Content, updated_at = NOW()
                    WHERE id = @MemoryId AND tenant_id = @TenantId
                    """,
                    new { Content = contentProp.GetString(), MemoryId = memoryId, TenantId = tenantId });
            }

            return Results.Ok(new
            {
                memoryId,
                restoredFromVersion = request.TargetVersion,
                newVersion,
                restoredAt = DateTimeOffset.UtcNow,
                message = $"Memory restored to version {request.TargetVersion}"
            });
        })
        .WithName("RestoreMemoryVersion")
        .WithDescription("Restores a memory to a specific version")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status404NotFound);

        // =============================================================================
        // GET /timeline/{memoryId}/diff - Get diff between two versions
        // =============================================================================
        group.MapGet("/{memoryId:guid}/diff", async (
            Guid memoryId,
            [FromQuery] int fromVersion,
            [FromQuery] int toVersion,
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            CancellationToken ct) =>
        {
            var tenantId = GetTenantId(user, selfHostedMode);

            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.SetInternalAdminWithTenantAsync(tenantId);

            var fromState = await conn.QueryFirstOrDefaultAsync<dynamic>(
                """
                SELECT event_data, version_number, event_type, occurred_at
                FROM memory_timeline
                WHERE memory_id = @MemoryId AND tenant_id = @TenantId
                    AND version_number = @Version
                """,
                new { MemoryId = memoryId, TenantId = tenantId, Version = fromVersion });

            var toState = await conn.QueryFirstOrDefaultAsync<dynamic>(
                """
                SELECT event_data, version_number, event_type, occurred_at
                FROM memory_timeline
                WHERE memory_id = @MemoryId AND tenant_id = @TenantId
                    AND version_number = @Version
                """,
                new { MemoryId = memoryId, TenantId = tenantId, Version = toVersion });

            if (fromState == null || toState == null)
                return Results.NotFound(new { error = "not_found", message = "One or both versions not found" });

            return Results.Ok(new
            {
                memoryId,
                fromVersion = new
                {
                    version = fromVersion,
                    eventType = (string)fromState.event_type,
                    occurredAt = (DateTimeOffset)fromState.occurred_at,
                    data = fromState.event_data
                },
                toVersion = new
                {
                    version = toVersion,
                    eventType = (string)toState.event_type,
                    occurredAt = (DateTimeOffset)toState.occurred_at,
                    data = toState.event_data
                }
            });
        })
        .WithName("GetVersionDiff")
        .WithDescription("Gets diff between two versions of a memory")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status404NotFound);
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

// DTOs for Timeline endpoints
public sealed class TimelineEntryDto
{
    public Guid id { get; set; }
    public string event_type { get; set; } = "";
    public object? event_data { get; set; }
    public int version_number { get; set; }
    public Guid? previous_entry_id { get; set; }
    public string? actor_id { get; set; }
    public DateTimeOffset occurred_at { get; set; }
    public DateTimeOffset created_at { get; set; }
}

public sealed class CurrentMemoryStateDto
{
    public Guid id { get; set; }
    public string content { get; set; } = "";
    public string? layer { get; set; }
    public decimal confidence { get; set; }
    public string? source { get; set; }
    public DateTimeOffset created_at { get; set; }
    public DateTimeOffset? updated_at { get; set; }
}

public sealed class TimelineStateDto
{
    public Guid id { get; set; }
    public string event_type { get; set; } = "";
    public object? event_data { get; set; }
    public int version_number { get; set; }
    public DateTimeOffset occurred_at { get; set; }
}

public sealed class InitialStateDto
{
    public Guid id { get; set; }
    public string content { get; set; } = "";
    public string? layer { get; set; }
    public decimal confidence { get; set; }
    public DateTimeOffset created_at { get; set; }
}

public sealed class VersionDto
{
    public int version_number { get; set; }
    public string event_type { get; set; } = "";
    public string? actor_id { get; set; }
    public DateTimeOffset occurred_at { get; set; }
    public string? content_preview { get; set; }
}

public sealed record RestoreVersionRequest(int TargetVersion);
