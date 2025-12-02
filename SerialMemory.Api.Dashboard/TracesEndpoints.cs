using System.Security.Claims;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using SerialMemory.Core.Interfaces;
using SerialMemory.Infrastructure;

namespace SerialMemory.Api.Dashboard;

/// <summary>
/// Dashboard endpoints for Memory Traces and Events.
/// FIX #1: Events returned in DESC order by default.
/// </summary>
public static class TracesEndpoints
{
    public static void MapTracesEndpoints(this WebApplication app, bool selfHostedMode)
    {
        var group = app.MapGroup("/traces")
            .WithTags("Traces")
            .RequireAuthorization();

        // =============================================================================
        // GET /traces/events - Get recent events in DESC order (FIX #1)
        // =============================================================================
        group.MapGet("/events", async (
            int? limit,
            int? offset,
            string? category,
            string? eventType,
            string? sortOrder,
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            CancellationToken ct) =>
        {
            var tenantId = GetTenantId(user, selfHostedMode);

            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.SetInternalAdminWithTenantAsync(tenantId);

            // CRITICAL FIX: Always DESC order by default, frontend should NOT re-sort
            var orderBy = (sortOrder?.ToLowerInvariant() == "asc") ? "ASC" : "DESC";

            var events = await conn.QueryAsync<TraceEventDto>(
                $"""
                SELECT
                    me.id,
                    me.memory_id,
                    me.event_type,
                    me.event_data,
                    me.created_at,
                    me.actor_id,
                    me.sequence_number,
                    m.content AS memory_content,
                    m.layer AS memory_layer
                FROM memory_events me
                LEFT JOIN memories m ON me.memory_id = m.id
                WHERE me.tenant_id = @TenantId
                    AND (@Category IS NULL OR me.event_type LIKE @Category || '%')
                    AND (@EventType IS NULL OR me.event_type = @EventType)
                ORDER BY me.created_at {orderBy}, me.sequence_number {orderBy}
                LIMIT @Limit
                OFFSET @Offset
                """,
                new
                {
                    TenantId = tenantId,
                    Category = category,
                    EventType = eventType,
                    Limit = limit ?? 100,
                    Offset = offset ?? 0
                });

            // Get total count for pagination
            var totalCount = await conn.ExecuteScalarAsync<int>(
                """
                SELECT COUNT(*)
                FROM memory_events
                WHERE tenant_id = @TenantId
                    AND (@Category IS NULL OR event_type LIKE @Category || '%')
                    AND (@EventType IS NULL OR event_type = @EventType)
                """,
                new { TenantId = tenantId, Category = category, EventType = eventType });

            return Results.Ok(new
            {
                events = events.ToList(),
                total = totalCount,
                limit = limit ?? 100,
                offset = offset ?? 0,
                sortOrder = orderBy.ToLowerInvariant()
            });
        })
        .WithName("GetTraceEvents")
        .WithDescription("Gets memory events in DESC order by default")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized);

        // =============================================================================
        // GET /traces/{memoryId} - Get full trace for a specific memory
        // =============================================================================
        group.MapGet("/{memoryId:guid}", async (
            Guid memoryId,
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            CancellationToken ct) =>
        {
            var tenantId = GetTenantId(user, selfHostedMode);

            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.SetInternalAdminWithTenantAsync(tenantId);

            // Get the memory itself
            var memory = await conn.QueryFirstOrDefaultAsync<MemoryDetailDto>(
                """
                SELECT id, content, layer, confidence, source, embedding IS NOT NULL AS has_embedding,
                       created_at, updated_at, metadata
                FROM memories
                WHERE id = @MemoryId AND tenant_id = @TenantId
                """,
                new { MemoryId = memoryId, TenantId = tenantId });

            if (memory == null)
                return Results.NotFound(new { error = "not_found", message = "Memory not found" });

            // Get all events for this memory in DESC order
            var events = await conn.QueryAsync<TraceEventDto>(
                """
                SELECT id, memory_id, event_type, event_data, created_at, actor_id, sequence_number
                FROM memory_events
                WHERE memory_id = @MemoryId AND tenant_id = @TenantId
                ORDER BY created_at DESC, sequence_number DESC
                """,
                new { MemoryId = memoryId, TenantId = tenantId });

            // Get linked entities
            var entities = await conn.QueryAsync<LinkedEntityDto>(
                """
                SELECT e.id, e.name, e.entity_type, e.canonical_name
                FROM entities e
                JOIN memory_entities me ON e.id = me.entity_id
                WHERE me.memory_id = @MemoryId AND me.tenant_id = @TenantId
                """,
                new { MemoryId = memoryId, TenantId = tenantId });

            return Results.Ok(new
            {
                memory,
                events = events.ToList(),
                entities = entities.ToList(),
                eventCount = events.Count()
            });
        })
        .WithName("GetMemoryTrace")
        .WithDescription("Gets the complete trace for a specific memory")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status404NotFound);

        // =============================================================================
        // GET /traces/{memoryId}/detail - Get detailed trace with full event payloads
        // =============================================================================
        group.MapGet("/{memoryId:guid}/detail", async (
            Guid memoryId,
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            CancellationToken ct) =>
        {
            var tenantId = GetTenantId(user, selfHostedMode);

            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.SetInternalAdminWithTenantAsync(tenantId);

            // Verify memory exists and belongs to tenant
            var exists = await conn.ExecuteScalarAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM memories WHERE id = @MemoryId AND tenant_id = @TenantId)",
                new { MemoryId = memoryId, TenantId = tenantId });

            if (!exists)
                return Results.NotFound(new { error = "not_found", message = "Memory not found" });

            // Get full event trace with all details
            var trace = await conn.QueryAsync<TraceDetailDto>(
                """
                SELECT
                    me.id,
                    me.memory_id,
                    me.event_type,
                    me.event_data,
                    me.created_at,
                    me.actor_id,
                    me.sequence_number,
                    me.version,
                    LAG(me.event_data) OVER (ORDER BY me.sequence_number) AS previous_event_data,
                    LEAD(me.event_data) OVER (ORDER BY me.sequence_number) AS next_event_data
                FROM memory_events me
                WHERE me.memory_id = @MemoryId AND me.tenant_id = @TenantId
                ORDER BY me.created_at DESC, me.sequence_number DESC
                """,
                new { MemoryId = memoryId, TenantId = tenantId });

            return Results.Ok(new
            {
                memoryId,
                trace = trace.ToList(),
                traceCount = trace.Count()
            });
        })
        .WithName("GetMemoryTraceDetail")
        .WithDescription("Gets detailed trace with full event payloads")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status404NotFound);

        // =============================================================================
        // GET /traces/categories - Get event categories with counts
        // =============================================================================
        group.MapGet("/categories", async (
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            CancellationToken ct) =>
        {
            var tenantId = GetTenantId(user, selfHostedMode);

            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.SetInternalAdminWithTenantAsync(tenantId);

            var categories = await conn.QueryAsync<EventCategoryDto>(
                """
                SELECT
                    SPLIT_PART(event_type, '_', 1) AS category,
                    COUNT(*) AS event_count,
                    MAX(created_at) AS last_event_at
                FROM memory_events
                WHERE tenant_id = @TenantId
                GROUP BY SPLIT_PART(event_type, '_', 1)
                ORDER BY event_count DESC
                """,
                new { TenantId = tenantId });

            return Results.Ok(categories.ToList());
        })
        .WithName("GetEventCategories")
        .WithDescription("Gets event categories with counts")
        .Produces<IReadOnlyList<EventCategoryDto>>(StatusCodes.Status200OK)
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
}

// DTOs for Traces endpoints
public sealed class TraceEventDto
{
    public Guid id { get; set; }
    public Guid memory_id { get; set; }
    public string event_type { get; set; } = "";
    public object? event_data { get; set; }
    public DateTimeOffset created_at { get; set; }
    public string? actor_id { get; set; }
    public long sequence_number { get; set; }
    public string? memory_content { get; set; }
    public string? memory_layer { get; set; }
}

public sealed class TraceDetailDto
{
    public Guid id { get; set; }
    public Guid memory_id { get; set; }
    public string event_type { get; set; } = "";
    public object? event_data { get; set; }
    public DateTimeOffset created_at { get; set; }
    public string? actor_id { get; set; }
    public long sequence_number { get; set; }
    public int? version { get; set; }
    public object? previous_event_data { get; set; }
    public object? next_event_data { get; set; }
}

public sealed class MemoryDetailDto
{
    public Guid id { get; set; }
    public string content { get; set; } = "";
    public string layer { get; set; } = "";
    public decimal confidence { get; set; }
    public string? source { get; set; }
    public bool has_embedding { get; set; }
    public DateTimeOffset created_at { get; set; }
    public DateTimeOffset? updated_at { get; set; }
    public object? metadata { get; set; }
}

public sealed class LinkedEntityDto
{
    public Guid id { get; set; }
    public string name { get; set; } = "";
    public string entity_type { get; set; } = "";
    public string? canonical_name { get; set; }
}

public sealed class EventCategoryDto
{
    public string category { get; set; } = "";
    public long event_count { get; set; }
    public DateTimeOffset? last_event_at { get; set; }
}
