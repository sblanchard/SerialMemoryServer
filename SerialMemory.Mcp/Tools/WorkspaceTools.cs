using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Npgsql;
using SerialMemory.Core.Interfaces;
using SerialMemory.Core.Models;

namespace SerialMemory.Mcp.Tools;

/// <summary>
/// Workspace management tool handlers: create, list, switch workspaces.
/// </summary>
public sealed partial class WorkspaceTools
{
    private readonly IKnowledgeGraphStore _store;
    private readonly IMutableTenantContext _tenantContext;
    private readonly ILogger _logger;

    private const string WorkspaceSchemaNotAvailable =
        "Workspace schema not available. Run migrate_workspace_scoping.sql to enable workspace operations.";

    [GeneratedRegex(@"^[a-z0-9][a-z0-9\-_]{0,62}$")]
    private static partial Regex WorkspaceSlugPattern();

    public WorkspaceTools(IKnowledgeGraphStore store, IMutableTenantContext tenantContext, ILogger logger)
    {
        _store = store;
        _tenantContext = tenantContext;
        _logger = logger;
    }

    private static void ValidateWorkspaceSlug(string name)
    {
        if (!WorkspaceSlugPattern().IsMatch(name))
            throw new ArgumentException(
                "Workspace name must be 1-63 lowercase alphanumeric characters, hyphens, or underscores, starting with a letter or digit");
    }

    public async Task<object> HandleWorkspaceCreate(JsonNode? arguments)
    {
        var name = arguments?["name"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrEmpty(name))
            throw new ArgumentException("name is required");

        ValidateWorkspaceSlug(name);

        var displayName = arguments?["display_name"]?.GetValue<string>()?.Trim();
        var description = arguments?["description"]?.GetValue<string>()?.Trim();

        var workspace = new Workspace
        {
            WorkspaceId = name,
            DisplayName = displayName ?? name,
            Description = description
        };

        try
        {
            var id = await _store.CreateWorkspaceAsync(workspace);
            _logger.LogInformation("Workspace created: {Name} (id={Id})", name, id);

            return CreateTextResponse(
                $"Workspace created successfully!\n\n" +
                $"ID: {id}\n" +
                $"Workspace: {name}\n" +
                $"Display Name: {displayName ?? name}\n" +
                $"Description: {description ?? "(none)"}\n\n" +
                $"Use `workspace_switch` to activate this workspace.");
        }
        catch (PostgresException ex) when (ex.SqlState is "42P01" or "42703")
        {
            _logger.LogWarning("workspace_create skipped: {Message}", ex.MessageText);
            return CreateErrorResponse(WorkspaceSchemaNotAvailable);
        }
    }

    public async Task<object> HandleWorkspaceList(JsonNode? arguments)
    {
        var limit = Math.Clamp(arguments?["limit"]?.GetValue<int>() ?? 50, 1, 100);

        try
        {
            var workspaces = await _store.GetWorkspacesAsync(limit);

            if (workspaces.Count == 0)
                return CreateTextResponse("No workspaces found. Use `workspace_create` to create one.");

            var currentWorkspace = _tenantContext.WorkspaceId;

            var text = $"## Workspaces ({workspaces.Count})\n\n" +
                $"Active: **{currentWorkspace}**\n\n" +
                string.Join("\n", workspaces.Select(w =>
                    $"- **{w.WorkspaceId}**{(w.WorkspaceId == currentWorkspace ? " (active)" : "")} — " +
                    $"{w.DisplayName ?? w.WorkspaceId}" +
                    $"{(w.Description != null ? $": {w.Description}" : "")}" +
                    $" (created: {w.CreatedAt:yyyy-MM-dd})"));

            return CreateTextResponse(text);
        }
        catch (PostgresException ex) when (ex.SqlState is "42P01" or "42703")
        {
            _logger.LogWarning("workspace_list skipped: {Message}", ex.MessageText);
            return CreateErrorResponse(WorkspaceSchemaNotAvailable);
        }
    }

    public async Task<object> HandleWorkspaceSwitch(JsonNode? arguments)
    {
        var workspaceId = arguments?["workspace_id"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrEmpty(workspaceId))
            throw new ArgumentException("workspace_id is required");

        try
        {
            // Allow "default" without existence check
            if (workspaceId != "default")
            {
                var existing = await _store.GetWorkspaceBySlugAsync(workspaceId);
                if (existing == null)
                    throw new ArgumentException($"Workspace '{workspaceId}' does not exist. Use workspace_create first.");
            }

            var previousWorkspace = _tenantContext.WorkspaceId;

            _tenantContext.SetContext(
                _tenantContext.TenantId,
                workspaceId,
                _tenantContext.UserId,
                _tenantContext.UserEmail,
                _tenantContext.UserRole,
                _tenantContext.SessionId,
                _tenantContext.IsLabMode,
                _tenantContext.AllowPowerMode,
                _tenantContext.IsRootAdmin,
                _tenantContext.Scopes);

            _logger.LogInformation("Workspace switched: {Previous} -> {New}", previousWorkspace, workspaceId);

            return CreateTextResponse(
                $"Workspace switched: {previousWorkspace} -> **{workspaceId}**\n\n" +
                $"All subsequent operations will be scoped to workspace '{workspaceId}'.");
        }
        catch (PostgresException ex) when (ex.SqlState is "42P01" or "42703")
        {
            _logger.LogWarning("workspace_switch skipped: {Message}", ex.MessageText);
            return CreateErrorResponse(WorkspaceSchemaNotAvailable);
        }
    }

    private static object CreateTextResponse(string text) => new
    {
        content = new[] { new { type = "text", text } }
    };

    private static object CreateErrorResponse(string message) => new
    {
        content = new[] { new { type = "text", text = $"Error: {message}" } },
        isError = true
    };
}
