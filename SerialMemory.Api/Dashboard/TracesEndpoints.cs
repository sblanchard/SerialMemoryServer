using System.Security.Claims;
using Dapper;
using Npgsql;
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
        var group = app.MapGroup("/api/traces")
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

            // Handle both eventsourcing schema (global_sequence, stream_id) and dashboard schema (sequence_number, memory_id)
            var events = await conn.QueryAsync<TraceEventDto>(
                $"""
                SELECT
                    COALESCE(me.id, me.event_id) AS id,
                    COALESCE(me.memory_id, me.stream_id) AS memory_id,
                    me.event_type::text AS event_type,
                    COALESCE(me.event_data, me.payload) AS event_data,
                    COALESCE(me.created_at, me.timestamp) AS created_at,
                    COALESCE(me.actor_id, me.created_by) AS actor_id,
                    COALESCE(me.sequence_number, me.global_sequence) AS sequence_number,
                    m.content AS memory_content,
                    m.layer AS memory_layer
                FROM memory_events me
                LEFT JOIN memories m ON COALESCE(me.memory_id, me.stream_id) = m.id
                WHERE (me.tenant_id = @TenantId OR me.tenant_id IS NULL)
                    AND (@Category IS NULL OR me.event_type::text LIKE @Category || '%')
                    AND (@EventType IS NULL OR me.event_type::text = @EventType)
                ORDER BY COALESCE(me.created_at, me.timestamp) {orderBy}, COALESCE(me.sequence_number, me.global_sequence) {orderBy}
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

            // Get all events for this memory in DESC order (handle both schema versions)
            var events = await conn.QueryAsync<TraceEventDto>(
                """
                SELECT
                    COALESCE(id, event_id) AS id,
                    COALESCE(memory_id, stream_id) AS memory_id,
                    event_type::text AS event_type,
                    COALESCE(event_data, payload) AS event_data,
                    COALESCE(created_at, timestamp) AS created_at,
                    COALESCE(actor_id, created_by) AS actor_id,
                    COALESCE(sequence_number, global_sequence) AS sequence_number
                FROM memory_events
                WHERE COALESCE(memory_id, stream_id) = @MemoryId
                    AND (tenant_id = @TenantId OR tenant_id IS NULL)
                ORDER BY COALESCE(created_at, timestamp) DESC, COALESCE(sequence_number, global_sequence) DESC
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

            // Get full event trace with all details (handle both schema versions)
            var trace = await conn.QueryAsync<TraceDetailDto>(
                """
                SELECT
                    COALESCE(me.id, me.event_id) AS id,
                    COALESCE(me.memory_id, me.stream_id) AS memory_id,
                    me.event_type::text AS event_type,
                    COALESCE(me.event_data, me.payload) AS event_data,
                    COALESCE(me.created_at, me.timestamp) AS created_at,
                    COALESCE(me.actor_id, me.created_by) AS actor_id,
                    COALESCE(me.sequence_number, me.global_sequence) AS sequence_number,
                    me.version,
                    LAG(COALESCE(me.event_data, me.payload)) OVER (ORDER BY COALESCE(me.sequence_number, me.global_sequence)) AS previous_event_data,
                    LEAD(COALESCE(me.event_data, me.payload)) OVER (ORDER BY COALESCE(me.sequence_number, me.global_sequence)) AS next_event_data
                FROM memory_events me
                WHERE COALESCE(me.memory_id, me.stream_id) = @MemoryId
                    AND (me.tenant_id = @TenantId OR me.tenant_id IS NULL)
                ORDER BY COALESCE(me.created_at, me.timestamp) DESC, COALESCE(me.sequence_number, me.global_sequence) DESC
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
                    SPLIT_PART(event_type::text, '_', 1) AS category,
                    COUNT(*) AS event_count,
                    MAX(COALESCE(created_at, timestamp)) AS last_event_at
                FROM memory_events
                WHERE tenant_id = @TenantId OR tenant_id IS NULL
                GROUP BY SPLIT_PART(event_type::text, '_', 1)
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
        => DashboardHelpers.GetTenantId(user, selfHosted);
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
