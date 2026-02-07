using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Npgsql;
using SerialMemory.Core.Interfaces;
using SerialMemory.Core.Models;

namespace SerialMemory.Mcp.Tools;

/// <summary>
/// Workspace snapshot tool handlers: create, list, load snapshots.
/// </summary>
public sealed class SnapshotTools
{
    private readonly IKnowledgeGraphStore _store;
    private readonly ITenantContext _tenantContext;
    private readonly ILogger _logger;
    private readonly Func<Guid?> _getCurrentSessionId;

    private const string SnapshotSchemaNotAvailable =
        "Snapshot schema not available. Run migrate_workspace_scoping.sql to enable snapshot operations.";

    public SnapshotTools(
        IKnowledgeGraphStore store,
        ITenantContext tenantContext,
        ILogger logger,
        Func<Guid?> getCurrentSessionId)
    {
        _store = store;
        _tenantContext = tenantContext;
        _logger = logger;
        _getCurrentSessionId = getCurrentSessionId;
    }

    public async Task<object> HandleSnapshotCreate(JsonNode? arguments)
    {
        var snapshotName = arguments?["snapshot_name"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrEmpty(snapshotName))
            throw new ArgumentException("snapshot_name is required");

        var workspaceId = _tenantContext.WorkspaceId;

        try
        {
            // Gather current state
            var recentMemories = await _store.GetRecentMemoriesAsync(10);
            var recentMemoryIds = recentMemories.Select(m => m.Id).ToList();

            var entities = await _store.GetAllEntitiesAsync(20);
            var activeEntityNames = entities.Select(e => e.Name).ToList();

            // Parse custom metadata if provided
            Dictionary<string, object>? customMetadata = null;
            if (arguments?["metadata"] is JsonNode metadataNode)
            {
                customMetadata = JsonSerializer.Deserialize<Dictionary<string, object>>(metadataNode.ToJsonString());
            }

            var stateData = new WorkspaceStateData
            {
                SessionId = _getCurrentSessionId()?.ToString(),
                Goal = arguments?["goal"]?.GetValue<string>()?.Trim(),
                Constraints = arguments?["constraints"]?.GetValue<string>()?.Trim(),
                Memory = arguments?["memory"]?.GetValue<string>()?.Trim(),
                RecentMemoryIds = recentMemoryIds,
                ActiveEntityNames = activeEntityNames,
                CustomMetadata = customMetadata
            };

            var snapshot = new WorkspaceSnapshot
            {
                WorkspaceId = workspaceId,
                SnapshotName = snapshotName,
                StateData = stateData
            };

            var id = await _store.CreateSnapshotAsync(snapshot);
            _logger.LogInformation("Snapshot created: {Name} for workspace {Workspace} (id={Id})",
                snapshotName, workspaceId, id);

            return CreateTextResponse(
                $"Snapshot created successfully!\n\n" +
                $"ID: {id}\n" +
                $"Workspace: {workspaceId}\n" +
                $"Name: {snapshotName}\n" +
                $"Memories captured: {recentMemoryIds.Count}\n" +
                $"Entities captured: {activeEntityNames.Count}");
        }
        catch (PostgresException ex) when (ex.SqlState is "42P01" or "42703")
        {
            _logger.LogWarning("snapshot_create skipped: {Message}", ex.MessageText);
            return CreateErrorResponse(SnapshotSchemaNotAvailable);
        }
    }

    public async Task<object> HandleSnapshotList(JsonNode? arguments)
    {
        var workspaceId = arguments?["workspace_id"]?.GetValue<string>()?.Trim() ?? _tenantContext.WorkspaceId;
        var limit = Math.Clamp(arguments?["limit"]?.GetValue<int>() ?? 20, 1, 100);

        try
        {
            var snapshots = await _store.GetSnapshotsAsync(workspaceId, limit);

            if (snapshots.Count == 0)
                return CreateTextResponse($"No snapshots found for workspace '{workspaceId}'.");

            var text = $"## Snapshots for '{workspaceId}' ({snapshots.Count})\n\n" +
                string.Join("\n", snapshots.Select(s =>
                    $"- **{s.SnapshotName}** (created: {s.CreatedAt:yyyy-MM-dd HH:mm})" +
                    $" — {s.StateData.RecentMemoryIds.Count} memories, {s.StateData.ActiveEntityNames.Count} entities" +
                    $"{(s.StateData.Goal != null ? $", goal: {s.StateData.Goal}" : "")}"));

            return CreateTextResponse(text);
        }
        catch (PostgresException ex) when (ex.SqlState is "42P01" or "42703")
        {
            _logger.LogWarning("snapshot_list skipped: {Message}", ex.MessageText);
            return CreateErrorResponse(SnapshotSchemaNotAvailable);
        }
    }

    public async Task<object> HandleSnapshotLoad(JsonNode? arguments)
    {
        var snapshotName = arguments?["snapshot_name"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrEmpty(snapshotName))
            throw new ArgumentException("snapshot_name is required");

        var workspaceId = arguments?["workspace_id"]?.GetValue<string>()?.Trim() ?? _tenantContext.WorkspaceId;

        try
        {
            var snapshot = await _store.GetSnapshotByNameAsync(workspaceId, snapshotName);
            if (snapshot == null)
                throw new ArgumentException($"Snapshot '{snapshotName}' not found in workspace '{workspaceId}'");

            var state = snapshot.StateData;
            var text = $"## Snapshot Loaded: {snapshotName}\n\n" +
                $"Workspace: {workspaceId}\n" +
                $"Created: {snapshot.CreatedAt:yyyy-MM-dd HH:mm}\n\n";

            if (state.Goal != null)
                text += $"**Goal:** {state.Goal}\n";
            if (state.Constraints != null)
                text += $"**Constraints:** {state.Constraints}\n";
            if (state.Memory != null)
                text += $"**Memory:** {state.Memory}\n";
            if (state.SessionId != null)
                text += $"**Session:** {state.SessionId}\n";

            if (state.ActiveEntityNames.Count > 0)
                text += $"\n**Active Entities:** {string.Join(", ", state.ActiveEntityNames)}\n";

            if (state.RecentMemoryIds.Count > 0)
                text += $"\n**Recent Memory IDs ({state.RecentMemoryIds.Count}):**\n" +
                    string.Join("\n", state.RecentMemoryIds.Take(5).Select(id => $"  - {id}"));

            if (state.CustomMetadata is { Count: > 0 })
            {
                text += $"\n\n**Custom Metadata:**\n```json\n" +
                    JsonSerializer.Serialize(state.CustomMetadata, new JsonSerializerOptions { WriteIndented = true }) +
                    "\n```";
            }

            _logger.LogInformation("Snapshot loaded: {Name} for workspace {Workspace}", snapshotName, workspaceId);

            return CreateTextResponse(text);
        }
        catch (PostgresException ex) when (ex.SqlState is "42P01" or "42703")
        {
            _logger.LogWarning("snapshot_load skipped: {Message}", ex.MessageText);
            return CreateErrorResponse(SnapshotSchemaNotAvailable);
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
