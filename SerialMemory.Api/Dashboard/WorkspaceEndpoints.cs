using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using SerialMemory.Infrastructure;

namespace SerialMemory.Api.Dashboard;

/// <summary>
/// MCP-facing workspace endpoints: list, create, switch workspaces.
/// </summary>
public static class WorkspaceEndpoints
{
    public static void MapWorkspaceEndpoints(this WebApplication app, bool selfHostedMode)
    {
        var group = app.MapGroup("/api/workspaces")
            .WithTags("Workspaces")
            .RequireAuthorization();

        // =============================================================================
        // GET /workspaces - List all workspaces
        // =============================================================================
        group.MapGet("", async (
            int? limit,
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            CancellationToken ct) =>
        {
            var tenantId = GetTenantId(user, selfHostedMode);
            var effectiveLimit = Math.Clamp(limit ?? 50, 1, 100);

            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.SetInternalAdminWithTenantAsync(tenantId);

            try
            {
                var workspaces = await conn.QueryAsync<WorkspaceDto>(
                    """
                    SELECT id, workspace_id, display_name, description, created_at, updated_at
                    FROM workspaces
                    ORDER BY created_at DESC
                    LIMIT @Limit
                    """,
                    new { Limit = effectiveLimit });

                var list = workspaces.ToList();
                return Results.Ok(new
                {
                    content = new[]
                    {
                        new
                        {
                            type = "text",
                            text = list.Count == 0
                                ? "No workspaces found. Use `workspace_create` to create one."
                                : $"## Workspaces ({list.Count})\n\n" +
                                  string.Join("\n", list.Select(w =>
                                      $"- **{w.workspace_id}** — {w.display_name ?? w.workspace_id}" +
                                      $"{(w.description != null ? $": {w.description}" : "")}" +
                                      $" (created: {w.created_at:yyyy-MM-dd})"))
                        }
                    }
                });
            }
            catch (PostgresException ex) when (ex.SqlState is "42P01" or "42703")
            {
                return Results.Ok(new
                {
                    content = new[] { new { type = "text", text = "Workspace schema not available." } },
                    isError = true
                });
            }
        })
        .WithName("ListWorkspaces")
        .WithDescription("Lists all workspaces for the tenant");

        // =============================================================================
        // POST /workspaces - Create a new workspace
        // =============================================================================
        group.MapPost("", async (
            [FromBody] CreateWorkspaceRequest request,
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            CancellationToken ct) =>
        {
            var tenantId = GetTenantId(user, selfHostedMode);

            if (string.IsNullOrWhiteSpace(request.Name))
                return Results.BadRequest(new { error = "validation_error", message = "name is required" });

            if (!Regex.IsMatch(request.Name, @"^[a-z0-9][a-z0-9\-_]{0,62}$"))
                return Results.BadRequest(new { error = "validation_error", message = "name must be 1-63 lowercase alphanumeric characters, hyphens, or underscores" });

            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.SetInternalAdminWithTenantAsync(tenantId);

            try
            {
                var id = Guid.CreateVersion7();
                var now = DateTime.UtcNow;

                await conn.ExecuteAsync(
                    """
                    INSERT INTO workspaces (id, tenant_id, workspace_id, display_name, description, metadata, created_at, updated_at)
                    VALUES (@Id, @TenantId, @WorkspaceId, @DisplayName, @Description, @Metadata::jsonb, @CreatedAt, @UpdatedAt)
                    """,
                    new
                    {
                        Id = id,
                        TenantId = tenantId,
                        WorkspaceId = request.Name,
                        DisplayName = request.DisplayName ?? request.Name,
                        Description = request.Description,
                        Metadata = request.Metadata != null
                            ? JsonSerializer.Serialize(request.Metadata)
                            : null,
                        CreatedAt = now,
                        UpdatedAt = now
                    });

                return Results.Ok(new
                {
                    content = new[]
                    {
                        new
                        {
                            type = "text",
                            text = $"Workspace created successfully!\n\nID: {id}\nWorkspace: {request.Name}\n" +
                                   $"Display Name: {request.DisplayName ?? request.Name}\n" +
                                   $"Description: {request.Description ?? "(none)"}\n\n" +
                                   "Use `workspace_switch` to activate this workspace."
                        }
                    }
                });
            }
            catch (PostgresException ex) when (ex.SqlState is "42P01" or "42703")
            {
                return Results.Ok(new
                {
                    content = new[] { new { type = "text", text = "Workspace schema not available." } },
                    isError = true
                });
            }
            catch (PostgresException ex) when (ex.SqlState == "23505")
            {
                return Results.Conflict(new { error = "duplicate", message = $"Workspace '{request.Name}' already exists" });
            }
        })
        .WithName("CreateWorkspace")
        .WithDescription("Creates a new workspace");

        // =============================================================================
        // POST /workspaces/switch - Switch active workspace
        // =============================================================================
        group.MapPost("/switch", async (
            [FromBody] SwitchWorkspaceRequest request,
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            CancellationToken ct) =>
        {
            var tenantId = GetTenantId(user, selfHostedMode);

            if (string.IsNullOrWhiteSpace(request.WorkspaceId))
                return Results.BadRequest(new { error = "validation_error", message = "workspace_id is required" });

            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.SetInternalAdminWithTenantAsync(tenantId);

            try
            {
                // Allow "default" without existence check
                if (request.WorkspaceId != "default")
                {
                    var exists = await conn.ExecuteScalarAsync<bool>(
                        "SELECT EXISTS(SELECT 1 FROM workspaces WHERE workspace_id = @WorkspaceId AND tenant_id = @TenantId)",
                        new { request.WorkspaceId, TenantId = tenantId });

                    if (!exists)
                        return Results.NotFound(new
                        {
                            error = "not_found",
                            message = $"Workspace '{request.WorkspaceId}' does not exist. Use workspace_create first."
                        });
                }

                return Results.Ok(new
                {
                    content = new[]
                    {
                        new
                        {
                            type = "text",
                            text = $"Workspace switched to **{request.WorkspaceId}**\n\n" +
                                   $"All subsequent operations will be scoped to workspace '{request.WorkspaceId}'."
                        }
                    }
                });
            }
            catch (PostgresException ex) when (ex.SqlState is "42P01" or "42703")
            {
                return Results.Ok(new
                {
                    content = new[] { new { type = "text", text = "Workspace schema not available." } },
                    isError = true
                });
            }
        })
        .WithName("SwitchWorkspace")
        .WithDescription("Validates and switches the active workspace");
    }

    private static Guid GetTenantId(ClaimsPrincipal user, bool selfHosted)
        => DashboardHelpers.GetTenantId(user, selfHosted);
}

// DTOs
public sealed record CreateWorkspaceRequest(
    string? Name = null,
    string? DisplayName = null,
    string? Description = null,
    Dictionary<string, object>? Metadata = null);

public sealed record SwitchWorkspaceRequest(string? WorkspaceId = null);

public sealed class WorkspaceDto
{
    public Guid id { get; set; }
    public string workspace_id { get; set; } = "";
    public string? display_name { get; set; }
    public string? description { get; set; }
    public DateTime created_at { get; set; }
    public DateTime updated_at { get; set; }
}
