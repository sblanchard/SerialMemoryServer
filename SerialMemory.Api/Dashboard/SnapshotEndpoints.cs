using System.Security.Claims;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using SerialMemory.Infrastructure;

namespace SerialMemory.Api.Dashboard;

/// <summary>
/// MCP-facing snapshot endpoints: list, create, load workspace snapshots.
/// </summary>
public static class SnapshotEndpoints
{
    public static void MapSnapshotEndpoints(this WebApplication app, bool selfHostedMode)
    {
        var group = app.MapGroup("/api/snapshots")
            .WithTags("Snapshots")
            .RequireAuthorization();

        // =============================================================================
        // GET /snapshots - List snapshots for a workspace
        // =============================================================================
        group.MapGet("", async (
            string? workspace_id,
            int? limit,
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            CancellationToken ct) =>
        {
            var tenantId = GetTenantId(user, selfHostedMode);
            var workspaceId = workspace_id ?? "default";
            var effectiveLimit = Math.Clamp(limit ?? 20, 1, 100);

            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.SetInternalAdminWithTenantAsync(tenantId);

            try
            {
                var snapshots = await conn.QueryAsync<SnapshotDto>(
                    """
                    SELECT id, workspace_id, snapshot_name, state_data::text AS state_data_json, created_at
                    FROM workspace_snapshots
                    WHERE workspace_id = @WorkspaceId
                    ORDER BY created_at DESC
                    LIMIT @Limit
                    """,
                    new { WorkspaceId = workspaceId, Limit = effectiveLimit });

                var list = snapshots.ToList();

                if (list.Count == 0)
                {
                    return Results.Ok(new
                    {
                        content = new[]
                        {
                            new { type = "text", text = $"No snapshots found for workspace '{workspaceId}'." }
                        }
                    });
                }

                var text = $"## Snapshots for '{workspaceId}' ({list.Count})\n\n" +
                    string.Join("\n", list.Select(s =>
                    {
                        var stateInfo = TryParseStateData(s.state_data_json);
                        return $"- **{s.snapshot_name}** (created: {s.created_at:yyyy-MM-dd HH:mm})" +
                            $" — {stateInfo.memoryCount} memories, {stateInfo.entityCount} entities" +
                            $"{(stateInfo.goal != null ? $", goal: {stateInfo.goal}" : "")}";
                    }));

                return Results.Ok(new
                {
                    content = new[] { new { type = "text", text } }
                });
            }
            catch (PostgresException ex) when (ex.SqlState is "42P01" or "42703")
            {
                return Results.Ok(new
                {
                    content = new[] { new { type = "text", text = "Snapshot schema not available." } },
                    isError = true
                });
            }
        })
        .WithName("ListSnapshots")
        .WithDescription("Lists snapshots for a workspace");

        // =============================================================================
        // POST /snapshots - Create a new snapshot
        // =============================================================================
        group.MapPost("", async (
            [FromBody] CreateWorkspaceSnapshotRequest request,
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            CancellationToken ct) =>
        {
            var tenantId = GetTenantId(user, selfHostedMode);

            if (string.IsNullOrWhiteSpace(request.SnapshotName))
                return Results.BadRequest(new { error = "validation_error", message = "snapshot_name is required" });

            var workspaceId = request.WorkspaceId ?? "default";

            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.SetInternalAdminWithTenantAsync(tenantId);

            try
            {
                // Gather current state for the workspace
                var recentMemoryIds = (await conn.QueryAsync<Guid>(
                    """
                    SELECT id FROM memories
                    WHERE tenant_id = @TenantId AND workspace_id = @WorkspaceId
                    ORDER BY created_at DESC LIMIT 10
                    """,
                    new { TenantId = tenantId, WorkspaceId = workspaceId })).ToList();

                var activeEntityNames = (await conn.QueryAsync<string>(
                    """
                    SELECT DISTINCT e.name FROM entities e
                    JOIN memory_entities me ON me.entity_id = e.id
                    JOIN memories m ON m.id = me.memory_id
                    WHERE m.tenant_id = @TenantId AND m.workspace_id = @WorkspaceId
                    ORDER BY e.name LIMIT 20
                    """,
                    new { TenantId = tenantId, WorkspaceId = workspaceId })).ToList();

                var stateData = new
                {
                    session_id = request.SessionId,
                    goal = request.Goal,
                    constraints = request.Constraints,
                    memory = request.Memory,
                    recent_memory_ids = recentMemoryIds,
                    active_entity_names = activeEntityNames,
                    custom_metadata = request.Metadata
                };

                var id = Guid.CreateVersion7();
                await conn.ExecuteAsync(
                    """
                    INSERT INTO workspace_snapshots (id, tenant_id, workspace_id, snapshot_name, state_data, created_at)
                    VALUES (@Id, @TenantId, @WorkspaceId, @SnapshotName, @StateData::jsonb, NOW())
                    """,
                    new
                    {
                        Id = id,
                        TenantId = tenantId,
                        WorkspaceId = workspaceId,
                        SnapshotName = request.SnapshotName,
                        StateData = JsonSerializer.Serialize(stateData)
                    });

                return Results.Ok(new
                {
                    content = new[]
                    {
                        new
                        {
                            type = "text",
                            text = $"Snapshot created successfully!\n\n" +
                                $"ID: {id}\nWorkspace: {workspaceId}\n" +
                                $"Name: {request.SnapshotName}\n" +
                                $"Memories captured: {recentMemoryIds.Count}\n" +
                                $"Entities captured: {activeEntityNames.Count}"
                        }
                    }
                });
            }
            catch (PostgresException ex) when (ex.SqlState == "23505")
            {
                return Results.Conflict(new { error = "duplicate", message = $"Snapshot '{request.SnapshotName}' already exists in workspace '{workspaceId}'" });
            }
            catch (PostgresException ex) when (ex.SqlState is "42P01" or "42703")
            {
                return Results.Ok(new
                {
                    content = new[] { new { type = "text", text = "Snapshot schema not available." } },
                    isError = true
                });
            }
        })
        .WithName("CreateWorkspaceSnapshot")
        .WithDescription("Creates a new workspace snapshot");

        // =============================================================================
        // GET /snapshots/load - Load a snapshot by name
        // =============================================================================
        group.MapGet("/load", async (
            string? snapshot_name,
            string? workspace_id,
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            CancellationToken ct) =>
        {
            var tenantId = GetTenantId(user, selfHostedMode);

            if (string.IsNullOrWhiteSpace(snapshot_name))
                return Results.BadRequest(new { error = "validation_error", message = "snapshot_name is required" });

            var workspaceId = workspace_id ?? "default";

            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.SetInternalAdminWithTenantAsync(tenantId);

            try
            {
                var snapshot = await conn.QueryFirstOrDefaultAsync<SnapshotDto>(
                    """
                    SELECT id, workspace_id, snapshot_name, state_data::text AS state_data_json, created_at
                    FROM workspace_snapshots
                    WHERE workspace_id = @WorkspaceId AND snapshot_name = @SnapshotName
                    """,
                    new { WorkspaceId = workspaceId, SnapshotName = snapshot_name });

                if (snapshot == null)
                    return Results.NotFound(new
                    {
                        error = "not_found",
                        message = $"Snapshot '{snapshot_name}' not found in workspace '{workspaceId}'"
                    });

                var text = $"## Snapshot Loaded: {snapshot.snapshot_name}\n\n" +
                    $"Workspace: {workspaceId}\nCreated: {snapshot.created_at:yyyy-MM-dd HH:mm}\n\n";

                // Parse and display state data
                if (!string.IsNullOrEmpty(snapshot.state_data_json))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(snapshot.state_data_json);
                        var root = doc.RootElement;

                        if (root.TryGetProperty("goal", out var goal) && goal.ValueKind == JsonValueKind.String)
                            text += $"**Goal:** {goal.GetString()}\n";
                        if (root.TryGetProperty("constraints", out var constraints) && constraints.ValueKind == JsonValueKind.String)
                            text += $"**Constraints:** {constraints.GetString()}\n";
                        if (root.TryGetProperty("memory", out var memory) && memory.ValueKind == JsonValueKind.String)
                            text += $"**Memory:** {memory.GetString()}\n";
                        if (root.TryGetProperty("session_id", out var sessionId) && sessionId.ValueKind == JsonValueKind.String)
                            text += $"**Session:** {sessionId.GetString()}\n";

                        if (root.TryGetProperty("active_entity_names", out var entities) && entities.ValueKind == JsonValueKind.Array)
                        {
                            var names = entities.EnumerateArray().Select(e => e.GetString()).Where(n => n != null).ToList();
                            if (names.Count > 0)
                                text += $"\n**Active Entities:** {string.Join(", ", names)}\n";
                        }

                        if (root.TryGetProperty("recent_memory_ids", out var memIds) && memIds.ValueKind == JsonValueKind.Array)
                        {
                            var ids = memIds.EnumerateArray().Select(e => e.GetString()).Where(id => id != null).Take(5).ToList();
                            if (ids.Count > 0)
                                text += $"\n**Recent Memory IDs ({ids.Count}):**\n" +
                                    string.Join("\n", ids.Select(id => $"  - {id}"));
                        }
                    }
                    catch (JsonException)
                    {
                        text += $"\n**State Data (raw):**\n```json\n{snapshot.state_data_json}\n```";
                    }
                }

                return Results.Ok(new
                {
                    content = new[] { new { type = "text", text } }
                });
            }
            catch (PostgresException ex) when (ex.SqlState is "42P01" or "42703")
            {
                return Results.Ok(new
                {
                    content = new[] { new { type = "text", text = "Snapshot schema not available." } },
                    isError = true
                });
            }
        })
        .WithName("LoadSnapshot")
        .WithDescription("Loads a snapshot by name and workspace");
    }

    private static (int memoryCount, int entityCount, string? goal) TryParseStateData(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return (0, 0, null);

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var memoryCount = root.TryGetProperty("recent_memory_ids", out var mIds) && mIds.ValueKind == JsonValueKind.Array
                ? mIds.GetArrayLength() : 0;
            var entityCount = root.TryGetProperty("active_entity_names", out var eNames) && eNames.ValueKind == JsonValueKind.Array
                ? eNames.GetArrayLength() : 0;
            var goal = root.TryGetProperty("goal", out var g) && g.ValueKind == JsonValueKind.String
                ? g.GetString() : null;

            return (memoryCount, entityCount, goal);
        }
        catch
        {
            return (0, 0, null);
        }
    }

    private static Guid GetTenantId(ClaimsPrincipal user, bool selfHosted)
        => DashboardHelpers.GetTenantId(user, selfHosted);
}

// DTOs
public sealed record CreateWorkspaceSnapshotRequest(
    string? SnapshotName = null,
    string? WorkspaceId = null,
    string? SessionId = null,
    string? Goal = null,
    string? Constraints = null,
    string? Memory = null,
    Dictionary<string, object>? Metadata = null);

public sealed class SnapshotDto
{
    public Guid id { get; set; }
    public string workspace_id { get; set; } = "";
    public string snapshot_name { get; set; } = "";
    public string? state_data_json { get; set; }
    public DateTime created_at { get; set; }
}
