using System.Security.Claims;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using SerialMemory.Core.Interfaces;
using SerialMemory.Infrastructure;

namespace SerialMemory.Api.Dashboard;

/// <summary>
/// Dashboard endpoints for Mutation Console and Time-Travel debugging.
/// FIX #3: Slider causes "Failed to fetch" - Fixed endpoint paths and tenant headers.
/// </summary>
public static class MutationsEndpoints
{
    public static void MapMutationsEndpoints(this WebApplication app, bool selfHostedMode)
    {
        var group = app.MapGroup("/api/mutations")
            .WithTags("Mutations")
            .RequireAuthorization();

        // =============================================================================
        // GET /mutations/queue - Get mutation queue for slider display
        // =============================================================================
        group.MapGet("/queue", async (
            Guid? memoryId,
            int? limit,
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            CancellationToken ct) =>
        {
            var tenantId = GetTenantId(user, selfHostedMode);

            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.SetInternalAdminWithTenantAsync(tenantId);

            var whereClause = memoryId.HasValue
                ? "AND mm.memory_id = @MemoryId"
                : "";

            var mutations = await conn.QueryAsync<MutationQueueItemDto>(
                $"""
                SELECT
                    mm.id,
                    mm.memory_id,
                    mm.sequence_number,
                    mm.mutation_type,
                    mm.actor_id,
                    mm.actor_type,
                    mm.reason,
                    mm.created_at,
                    m.content AS current_content,
                    m.layer AS current_layer
                FROM memory_mutations mm
                JOIN memories m ON mm.memory_id = m.id
                WHERE mm.tenant_id = @TenantId {whereClause}
                ORDER BY mm.sequence_number DESC
                LIMIT @Limit
                """,
                new
                {
                    TenantId = tenantId,
                    MemoryId = memoryId,
                    Limit = limit ?? 100
                });

            var maxSeq = await conn.ExecuteScalarAsync<long?>(
                "SELECT MAX(sequence_number) FROM memory_mutations WHERE tenant_id = @TenantId",
                new { TenantId = tenantId });

            var minSeq = await conn.ExecuteScalarAsync<long?>(
                "SELECT MIN(sequence_number) FROM memory_mutations WHERE tenant_id = @TenantId",
                new { TenantId = tenantId });

            return Results.Ok(new
            {
                mutations = mutations.ToList(),
                minSequence = minSeq ?? 0,
                maxSequence = maxSeq ?? 0,
                totalMutations = mutations.Count()
            });
        })
        .WithName("GetMutationQueue")
        .WithDescription("Gets mutation queue for slider display")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized);

        // =============================================================================
        // GET /mutations/replay - Replay mutations to a specific sequence (FIX #3)
        // =============================================================================
        group.MapGet("/replay", async (
            [FromQuery] long toSeq,
            [FromQuery] Guid? memoryId,
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            CancellationToken ct) =>
        {
            var tenantId = GetTenantId(user, selfHostedMode);

            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.SetInternalAdminWithTenantAsync(tenantId);

            if (memoryId.HasValue)
            {
                // Replay single memory to target sequence
                var state = await conn.QueryFirstOrDefaultAsync<string>(
                    "SELECT replay_mutations_to_seq(@MemoryId, @TargetSeq)::text",
                    new { MemoryId = memoryId.Value, TargetSeq = toSeq });

                if (state == null)
                    return Results.NotFound(new { error = "not_found", message = "Memory not found" });

                return Results.Ok(new
                {
                    memoryId = memoryId.Value,
                    targetSequence = toSeq,
                    state = JsonSerializer.Deserialize<object>(state)
                });
            }
            else
            {
                // Get all memory states at target sequence
                var states = await conn.QueryAsync<MemoryStateAtSeqDto>(
                    """
                    SELECT DISTINCT ON (mm.memory_id)
                        mm.memory_id,
                        mm.sequence_number,
                        mm.new_content AS content,
                        mm.new_metadata AS metadata,
                        mm.created_at AS state_at
                    FROM memory_mutations mm
                    WHERE mm.tenant_id = @TenantId
                        AND mm.sequence_number <= @TargetSeq
                    ORDER BY mm.memory_id, mm.sequence_number DESC
                    """,
                    new { TenantId = tenantId, TargetSeq = toSeq });

                return Results.Ok(new
                {
                    targetSequence = toSeq,
                    memoriesCount = states.Count(),
                    states = states.ToList()
                });
            }
        })
        .WithName("ReplayMutations")
        .WithDescription("Replays mutations to a specific sequence number")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status404NotFound);

        // =============================================================================
        // GET /mutations/state - Get memory state at specific sequence (FIX #3)
        // =============================================================================
        group.MapGet("/state", async (
            [FromQuery] long seq,
            [FromQuery] Guid? memoryId,
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            CancellationToken ct) =>
        {
            var tenantId = GetTenantId(user, selfHostedMode);

            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.SetInternalAdminWithTenantAsync(tenantId);

            // Check if memory_mutations table exists
            var tableExists = await conn.ExecuteScalarAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM information_schema.tables WHERE table_name = 'memory_mutations')");

            if (!tableExists)
            {
                return Results.Ok(new
                {
                    error = "no_mutations",
                    message = "Mutation tracking not yet initialized. Run the comprehensive fixes migration.",
                    sequence = seq,
                    state = (object?)null
                });
            }

            if (memoryId.HasValue)
            {
                // Get specific memory state at sequence
                var snapshot = await conn.QueryFirstOrDefaultAsync<MemorySnapshotDto>(
                    """
                    SELECT id, memory_id, sequence_number, state, snapshot_type, created_at
                    FROM memory_states
                    WHERE memory_id = @MemoryId AND tenant_id = @TenantId
                        AND sequence_number <= @Seq
                    ORDER BY sequence_number DESC
                    LIMIT 1
                    """,
                    new { MemoryId = memoryId.Value, TenantId = tenantId, Seq = seq });

                if (snapshot == null)
                {
                    // Fall back to initial memory state
                    var memory = await conn.QueryFirstOrDefaultAsync<InitialMemoryDto>(
                        """
                        SELECT id, content, layer, confidence, metadata, created_at
                        FROM memories
                        WHERE id = @MemoryId AND tenant_id = @TenantId
                        """,
                        new { MemoryId = memoryId.Value, TenantId = tenantId });

                    if (memory == null)
                        return Results.NotFound(new { error = "not_found", message = "Memory not found" });

                    return Results.Ok(new
                    {
                        memoryId = memoryId.Value,
                        sequence = 0,
                        snapshotType = "initial",
                        state = new
                        {
                            id = memory.id,
                            content = memory.content,
                            layer = memory.layer,
                            confidence = memory.confidence,
                            metadata = memory.metadata
                        }
                    });
                }

                return Results.Ok(new
                {
                    memoryId = memoryId.Value,
                    sequence = snapshot.sequence_number,
                    snapshotType = snapshot.snapshot_type,
                    state = snapshot.state
                });
            }
            else
            {
                // Get mutation at exact sequence
                var mutation = await conn.QueryFirstOrDefaultAsync<MutationDetailDto>(
                    """
                    SELECT
                        mm.id,
                        mm.memory_id,
                        mm.sequence_number,
                        mm.mutation_type,
                        mm.old_content,
                        mm.new_content,
                        mm.old_metadata,
                        mm.new_metadata,
                        mm.diff,
                        mm.actor_id,
                        mm.actor_type,
                        mm.reason,
                        mm.created_at
                    FROM memory_mutations mm
                    WHERE mm.tenant_id = @TenantId AND mm.sequence_number = @Seq
                    """,
                    new { TenantId = tenantId, Seq = seq });

                if (mutation == null)
                    return Results.NotFound(new { error = "not_found", message = $"No mutation at sequence {seq}" });

                return Results.Ok(mutation);
            }
        })
        .WithName("GetMutationState")
        .WithDescription("Gets memory state at a specific sequence number")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status404NotFound);

        // =============================================================================
        // GET /mutations/{memoryId}/history - Get mutation history for a memory
        // =============================================================================
        group.MapGet("/{memoryId:guid}/history", async (
            Guid memoryId,
            int? limit,
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            CancellationToken ct) =>
        {
            var tenantId = GetTenantId(user, selfHostedMode);

            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.SetInternalAdminWithTenantAsync(tenantId);

            // Verify memory exists
            var exists = await conn.ExecuteScalarAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM memories WHERE id = @MemoryId AND tenant_id = @TenantId)",
                new { MemoryId = memoryId, TenantId = tenantId });

            if (!exists)
                return Results.NotFound(new { error = "not_found", message = "Memory not found" });

            var mutations = await conn.QueryAsync<MutationHistoryItemDto>(
                """
                SELECT
                    mm.id,
                    mm.sequence_number,
                    mm.mutation_type,
                    mm.old_content,
                    mm.new_content,
                    mm.diff,
                    mm.actor_id,
                    mm.actor_type,
                    mm.reason,
                    mm.created_at
                FROM memory_mutations mm
                WHERE mm.memory_id = @MemoryId AND mm.tenant_id = @TenantId
                ORDER BY mm.sequence_number ASC
                LIMIT @Limit
                """,
                new { MemoryId = memoryId, TenantId = tenantId, Limit = limit ?? 100 });

            return Results.Ok(new
            {
                memoryId,
                mutations = mutations.ToList(),
                mutationCount = mutations.Count()
            });
        })
        .WithName("GetMutationHistory")
        .WithDescription("Gets mutation history for a specific memory")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status404NotFound);

        // =============================================================================
        // POST /mutations/snapshot - Create a state snapshot
        // =============================================================================
        group.MapPost("/snapshot", async (
            [FromBody] CreateSnapshotRequest request,
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            CancellationToken ct) =>
        {
            var tenantId = GetTenantId(user, selfHostedMode);

            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.SetInternalAdminWithTenantAsync(tenantId);

            // Get current memory state
            var memory = await conn.QueryFirstOrDefaultAsync<dynamic>(
                """
                SELECT id, content, layer, confidence, metadata, created_at, updated_at
                FROM memories
                WHERE id = @MemoryId AND tenant_id = @TenantId
                """,
                new { MemoryId = request.MemoryId, TenantId = tenantId });

            if (memory == null)
                return Results.NotFound(new { error = "not_found", message = "Memory not found" });

            // Get current max sequence
            var maxSeq = await conn.ExecuteScalarAsync<long?>(
                "SELECT MAX(sequence_number) FROM memory_mutations WHERE memory_id = @MemoryId AND tenant_id = @TenantId",
                new { MemoryId = request.MemoryId, TenantId = tenantId }) ?? 0;

            // Create snapshot
            var snapshotId = Guid.CreateVersion7();
            var state = JsonSerializer.Serialize(new
            {
                id = (Guid)memory.id,
                content = (string)memory.content,
                layer = (string)memory.layer,
                confidence = (decimal)memory.confidence,
                metadata = memory.metadata
            });

            await conn.ExecuteAsync(
                """
                INSERT INTO memory_states (id, tenant_id, memory_id, sequence_number, state, snapshot_type, created_at)
                VALUES (@Id, @TenantId, @MemoryId, @Seq, @State::jsonb, @Type, NOW())
                """,
                new
                {
                    Id = snapshotId,
                    TenantId = tenantId,
                    MemoryId = request.MemoryId,
                    Seq = maxSeq,
                    State = state,
                    Type = request.SnapshotType ?? "manual"
                });

            return Results.Created($"/mutations/state?seq={maxSeq}&memoryId={request.MemoryId}", new
            {
                snapshotId,
                memoryId = request.MemoryId,
                sequence = maxSeq,
                message = "Snapshot created successfully"
            });
        })
        .WithName("CreateSnapshot")
        .WithDescription("Creates a state snapshot for a memory")
        .Produces(StatusCodes.Status201Created)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status404NotFound);

        // =============================================================================
        // GET /mutations/diff - Get diff between two sequences
        // =============================================================================
        group.MapGet("/diff", async (
            [FromQuery] long fromSeq,
            [FromQuery] long toSeq,
            [FromQuery] Guid? memoryId,
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            CancellationToken ct) =>
        {
            var tenantId = GetTenantId(user, selfHostedMode);

            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.SetInternalAdminWithTenantAsync(tenantId);

            var whereClause = memoryId.HasValue
                ? "AND mm.memory_id = @MemoryId"
                : "";

            var mutations = await conn.QueryAsync<MutationDiffItemDto>(
                $"""
                SELECT
                    mm.id,
                    mm.memory_id,
                    mm.sequence_number,
                    mm.mutation_type,
                    mm.old_content,
                    mm.new_content,
                    mm.diff,
                    mm.created_at
                FROM memory_mutations mm
                WHERE mm.tenant_id = @TenantId
                    AND mm.sequence_number > @FromSeq
                    AND mm.sequence_number <= @ToSeq
                    {whereClause}
                ORDER BY mm.sequence_number ASC
                """,
                new
                {
                    TenantId = tenantId,
                    FromSeq = fromSeq,
                    ToSeq = toSeq,
                    MemoryId = memoryId
                });

            return Results.Ok(new
            {
                fromSequence = fromSeq,
                toSequence = toSeq,
                memoryId,
                mutations = mutations.ToList(),
                mutationCount = mutations.Count()
            });
        })
        .WithName("GetMutationDiff")
        .WithDescription("Gets mutations between two sequence numbers")
        .Produces(StatusCodes.Status200OK)
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

// DTOs for Mutations endpoints
public sealed class MutationQueueItemDto
{
    public Guid id { get; set; }
    public Guid memory_id { get; set; }
    public long sequence_number { get; set; }
    public string mutation_type { get; set; } = "";
    public string? actor_id { get; set; }
    public string? actor_type { get; set; }
    public string? reason { get; set; }
    public DateTimeOffset created_at { get; set; }
    public string? current_content { get; set; }
    public string? current_layer { get; set; }
}

public sealed class MemoryStateAtSeqDto
{
    public Guid memory_id { get; set; }
    public long sequence_number { get; set; }
    public string? content { get; set; }
    public object? metadata { get; set; }
    public DateTimeOffset state_at { get; set; }
}

public sealed class MemorySnapshotDto
{
    public Guid id { get; set; }
    public Guid memory_id { get; set; }
    public long sequence_number { get; set; }
    public object? state { get; set; }
    public string? snapshot_type { get; set; }
    public DateTimeOffset created_at { get; set; }
}

public sealed class InitialMemoryDto
{
    public Guid id { get; set; }
    public string content { get; set; } = "";
    public string? layer { get; set; }
    public decimal confidence { get; set; }
    public object? metadata { get; set; }
    public DateTimeOffset created_at { get; set; }
}

public sealed class MutationDetailDto
{
    public Guid id { get; set; }
    public Guid memory_id { get; set; }
    public long sequence_number { get; set; }
    public string mutation_type { get; set; } = "";
    public string? old_content { get; set; }
    public string? new_content { get; set; }
    public object? old_metadata { get; set; }
    public object? new_metadata { get; set; }
    public object? diff { get; set; }
    public string? actor_id { get; set; }
    public string? actor_type { get; set; }
    public string? reason { get; set; }
    public DateTimeOffset created_at { get; set; }
}

public sealed class MutationHistoryItemDto
{
    public Guid id { get; set; }
    public long sequence_number { get; set; }
    public string mutation_type { get; set; } = "";
    public string? old_content { get; set; }
    public string? new_content { get; set; }
    public object? diff { get; set; }
    public string? actor_id { get; set; }
    public string? actor_type { get; set; }
    public string? reason { get; set; }
    public DateTimeOffset created_at { get; set; }
}

public sealed class MutationDiffItemDto
{
    public Guid id { get; set; }
    public Guid memory_id { get; set; }
    public long sequence_number { get; set; }
    public string mutation_type { get; set; } = "";
    public string? old_content { get; set; }
    public string? new_content { get; set; }
    public object? diff { get; set; }
    public DateTimeOffset created_at { get; set; }
}

public sealed record CreateSnapshotRequest(Guid MemoryId, string? SnapshotType = null);
