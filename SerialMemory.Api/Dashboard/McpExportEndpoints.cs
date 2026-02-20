using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using SerialMemory.Infrastructure;

namespace SerialMemory.Api.Dashboard;

/// <summary>
/// MCP-facing lightweight export endpoints for workspace, memories, graph, user-profile, and markdown.
/// </summary>
public static class McpExportEndpoints
{
    public static void MapMcpExportEndpoints(this WebApplication app, bool selfHostedMode)
    {
        var group = app.MapGroup("/api/export")
            .WithTags("MCP Export")
            .RequireAuthorization();

        // =============================================================================
        // POST /export/workspace - Full workspace export
        // =============================================================================
        group.MapPost("/workspace", async (
            [FromBody] McpExportRequest? request,
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            CancellationToken ct) =>
        {
            var tenantId = GetTenantId(user, selfHostedMode);
            var workspaceId = request?.WorkspaceId ?? "default";

            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.SetInternalAdminWithTenantAsync(tenantId);

            try
            {
                var memories = (await conn.QueryAsync<dynamic>(
                    """
                    SELECT id, content, source, created_at, metadata
                    FROM memories
                    WHERE tenant_id = @TenantId AND workspace_id = @WorkspaceId
                    ORDER BY created_at DESC LIMIT 10000
                    """,
                    new { TenantId = tenantId, WorkspaceId = workspaceId })).ToList();

                var entities = (await conn.QueryAsync<dynamic>(
                    """
                    SELECT id, name, entity_type, canonical_name, created_at
                    FROM entities WHERE tenant_id = @TenantId
                    ORDER BY name LIMIT 10000
                    """,
                    new { TenantId = tenantId })).ToList();

                var relationships = (await conn.QueryAsync<dynamic>(
                    """
                    SELECT id, source_entity_id, target_entity_id, relationship_type, confidence, created_at
                    FROM entity_relationships WHERE tenant_id = @TenantId
                    LIMIT 10000
                    """,
                    new { TenantId = tenantId })).ToList();

                var persona = await conn.QueryFirstOrDefaultAsync<dynamic>(
                    """
                    SELECT preferences, skills, background, goals
                    FROM user_personas WHERE tenant_id = @TenantId AND workspace_id = @WorkspaceId
                    LIMIT 1
                    """,
                    new { TenantId = tenantId, WorkspaceId = workspaceId });

                var export = new
                {
                    exported_at = DateTimeOffset.UtcNow,
                    workspace_id = workspaceId,
                    memory_count = memories.Count,
                    entity_count = entities.Count,
                    relationship_count = relationships.Count,
                    memories,
                    entities,
                    relationships,
                    user_persona = persona
                };

                var json = JsonSerializer.Serialize(export, new JsonSerializerOptions { WriteIndented = true });
                return Results.Ok(new
                {
                    content = new[] { new { type = "text", text = json } }
                });
            }
            catch (PostgresException ex) when (ex.SqlState is "42P01" or "42703")
            {
                return Results.Ok(new
                {
                    content = new[] { new { type = "text", text = "Export failed: some tables not available." } },
                    isError = true
                });
            }
        })
        .WithName("McpExportWorkspace")
        .WithDescription("Full workspace export including memories, entities, relationships, and persona");

        // =============================================================================
        // POST /export/memories - Filtered memory export
        // =============================================================================
        group.MapPost("/memories", async (
            [FromBody] McpExportRequest? request,
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            CancellationToken ct) =>
        {
            var tenantId = GetTenantId(user, selfHostedMode);
            var workspaceId = request?.WorkspaceId ?? "default";
            var effectiveLimit = Math.Clamp(request?.Limit ?? 1000, 1, 10000);

            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.SetInternalAdminWithTenantAsync(tenantId);

            try
            {
                var memories = (await conn.QueryAsync<dynamic>(
                    """
                    SELECT id, content, source, created_at, updated_at, metadata
                    FROM memories
                    WHERE tenant_id = @TenantId AND workspace_id = @WorkspaceId
                    ORDER BY created_at DESC
                    LIMIT @Limit
                    """,
                    new { TenantId = tenantId, WorkspaceId = workspaceId, Limit = effectiveLimit })).ToList();

                var export = new
                {
                    exported_at = DateTimeOffset.UtcNow,
                    workspace_id = workspaceId,
                    memory_count = memories.Count,
                    memories
                };

                var json = JsonSerializer.Serialize(export, new JsonSerializerOptions { WriteIndented = true });
                return Results.Ok(new
                {
                    content = new[] { new { type = "text", text = json } }
                });
            }
            catch (PostgresException ex) when (ex.SqlState is "42P01" or "42703")
            {
                return Results.Ok(new
                {
                    content = new[] { new { type = "text", text = "Export failed: memories table not available." } },
                    isError = true
                });
            }
        })
        .WithName("McpExportMemories")
        .WithDescription("Filtered memory export");

        // =============================================================================
        // POST /export/graph - Knowledge graph export (entities + relationships)
        // =============================================================================
        group.MapPost("/graph", async (
            [FromBody] McpExportRequest? request,
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            CancellationToken ct) =>
        {
            var tenantId = GetTenantId(user, selfHostedMode);

            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.SetInternalAdminWithTenantAsync(tenantId);

            try
            {
                var entities = (await conn.QueryAsync<dynamic>(
                    """
                    SELECT id, name, entity_type, canonical_name, created_at
                    FROM entities WHERE tenant_id = @TenantId
                    ORDER BY name LIMIT 10000
                    """,
                    new { TenantId = tenantId })).ToList();

                var relationships = (await conn.QueryAsync<dynamic>(
                    """
                    SELECT id, source_entity_id, target_entity_id, relationship_type, confidence, created_at
                    FROM entity_relationships WHERE tenant_id = @TenantId
                    LIMIT 10000
                    """,
                    new { TenantId = tenantId })).ToList();

                var export = new
                {
                    exported_at = DateTimeOffset.UtcNow,
                    entity_count = entities.Count,
                    relationship_count = relationships.Count,
                    entities,
                    relationships
                };

                var json = JsonSerializer.Serialize(export, new JsonSerializerOptions { WriteIndented = true });
                return Results.Ok(new
                {
                    content = new[] { new { type = "text", text = json } }
                });
            }
            catch (PostgresException ex) when (ex.SqlState is "42P01" or "42703")
            {
                return Results.Ok(new
                {
                    content = new[] { new { type = "text", text = "Export failed: graph tables not available." } },
                    isError = true
                });
            }
        })
        .WithName("McpExportGraph")
        .WithDescription("Knowledge graph export (entities + relationships)");

        // =============================================================================
        // POST /export/user-profile - User persona + statistics
        // =============================================================================
        group.MapPost("/user-profile", async (
            [FromBody] McpExportRequest? request,
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            CancellationToken ct) =>
        {
            var tenantId = GetTenantId(user, selfHostedMode);
            var workspaceId = request?.WorkspaceId ?? "default";

            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.SetInternalAdminWithTenantAsync(tenantId);

            try
            {
                var persona = await conn.QueryFirstOrDefaultAsync<dynamic>(
                    """
                    SELECT preferences, skills, background, goals
                    FROM user_personas WHERE tenant_id = @TenantId AND workspace_id = @WorkspaceId
                    LIMIT 1
                    """,
                    new { TenantId = tenantId, WorkspaceId = workspaceId });

                var memoryCount = await conn.ExecuteScalarAsync<long>(
                    "SELECT COUNT(*) FROM memories WHERE tenant_id = @TenantId AND workspace_id = @WorkspaceId",
                    new { TenantId = tenantId, WorkspaceId = workspaceId });

                var entityCount = await conn.ExecuteScalarAsync<long>(
                    "SELECT COUNT(*) FROM entities WHERE tenant_id = @TenantId",
                    new { TenantId = tenantId });

                var export = new
                {
                    exported_at = DateTimeOffset.UtcNow,
                    workspace_id = workspaceId,
                    user_persona = persona,
                    statistics = new
                    {
                        memory_count = memoryCount,
                        entity_count = entityCount
                    }
                };

                var json = JsonSerializer.Serialize(export, new JsonSerializerOptions { WriteIndented = true });
                return Results.Ok(new
                {
                    content = new[] { new { type = "text", text = json } }
                });
            }
            catch (PostgresException ex) when (ex.SqlState is "42P01" or "42703")
            {
                return Results.Ok(new
                {
                    content = new[] { new { type = "text", text = "Export failed: persona table not available." } },
                    isError = true
                });
            }
        })
        .WithName("McpExportUserProfile")
        .WithDescription("User persona and statistics export");

        // =============================================================================
        // POST /export/markdown - Obsidian-compatible markdown vault
        // =============================================================================
        group.MapPost("/markdown", async (
            [FromBody] McpExportRequest? request,
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            CancellationToken ct) =>
        {
            var tenantId = GetTenantId(user, selfHostedMode);
            var workspaceId = request?.WorkspaceId ?? "default";
            var effectiveLimit = Math.Clamp(request?.Limit ?? 500, 1, 5000);

            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.SetInternalAdminWithTenantAsync(tenantId);

            try
            {
                var memories = await conn.QueryAsync<MarkdownMemoryDto>(
                    """
                    SELECT id, content, source, created_at
                    FROM memories
                    WHERE tenant_id = @TenantId AND workspace_id = @WorkspaceId
                    ORDER BY created_at DESC
                    LIMIT @Limit
                    """,
                    new { TenantId = tenantId, WorkspaceId = workspaceId, Limit = effectiveLimit });

                var entities = await conn.QueryAsync<MarkdownEntityDto>(
                    """
                    SELECT DISTINCT e.name, e.entity_type
                    FROM entities e
                    JOIN memory_entities me ON me.entity_id = e.id
                    JOIN memories m ON m.id = me.memory_id
                    WHERE m.tenant_id = @TenantId AND m.workspace_id = @WorkspaceId
                    ORDER BY e.name
                    """,
                    new { TenantId = tenantId, WorkspaceId = workspaceId });

                var sb = new StringBuilder();
                sb.AppendLine("# SerialMemory Export");
                sb.AppendLine($"\nExported: {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm} UTC");
                sb.AppendLine($"Workspace: {workspaceId}");
                sb.AppendLine();

                // Entities index
                var entityList = entities.ToList();
                if (entityList.Count > 0)
                {
                    sb.AppendLine("## Entities");
                    sb.AppendLine();
                    foreach (var entity in entityList)
                    {
                        sb.AppendLine($"- [[{entity.name}]] ({entity.entity_type})");
                    }
                    sb.AppendLine();
                }

                // Memories
                sb.AppendLine("## Memories");
                sb.AppendLine();
                foreach (var m in memories)
                {
                    sb.AppendLine($"### {m.created_at:yyyy-MM-dd HH:mm}");
                    sb.AppendLine($"Source: {m.source ?? "unknown"}");
                    sb.AppendLine();
                    sb.AppendLine(m.content);
                    sb.AppendLine();
                    sb.AppendLine("---");
                    sb.AppendLine();
                }

                return Results.Ok(new
                {
                    content = new[] { new { type = "text", text = sb.ToString() } }
                });
            }
            catch (PostgresException ex) when (ex.SqlState is "42P01" or "42703")
            {
                return Results.Ok(new
                {
                    content = new[] { new { type = "text", text = "Export failed: tables not available." } },
                    isError = true
                });
            }
        })
        .WithName("McpExportMarkdown")
        .WithDescription("Obsidian-compatible markdown vault export");
    }

    private static Guid GetTenantId(ClaimsPrincipal user, bool selfHosted)
        => DashboardHelpers.GetTenantId(user, selfHosted);
}

// DTOs
public sealed record McpExportRequest(
    string? WorkspaceId = null,
    int? Limit = null);

public sealed class MarkdownMemoryDto
{
    public Guid id { get; set; }
    public string content { get; set; } = "";
    public string? source { get; set; }
    public DateTime created_at { get; set; }
}

public sealed class MarkdownEntityDto
{
    public string name { get; set; } = "";
    public string entity_type { get; set; } = "";
}
