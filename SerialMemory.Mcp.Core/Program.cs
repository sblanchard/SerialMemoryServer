using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SerialMemory.Core.Auth;
using SerialMemory.Core.Services;
using SerialMemory.Mcp.Shared;
using SerialMemory.Mcp.Shared.Tools;

// =================================================================
// SerialMemory MCP Core Server
// Essential memory operations for daily use (~12 tools, ~2k tokens)
// =================================================================

var server = new CoreMcpServer();
await server.RunAsync();

public sealed class CoreMcpServer : McpServerBase
{
    private readonly MemoryLifecycleTools _lifecycleTools;

    public CoreMcpServer() : base("serialmemory-core", "3.0.0")
    {
        _lifecycleTools = new MemoryLifecycleTools(EventStore, EmbeddingService, Logger);
        Logger.LogInformation("Core MCP server initialized (12 essential tools)");
    }

    /// <summary>
    /// Required scope for Core MCP server: serialmemory.core
    /// </summary>
    protected override string RequiredScope => Scopes.Core;

    protected override object HandleToolsList()
    {
        return new { tools = ToolDefinitions.GetCoreTools() };
    }

    protected override async Task<object> HandleToolsCall(JsonNode? @params)
    {
        // Authenticate request and set tenant context
        var authError = await AuthenticateRequestAsync(@params);
        if (authError != null)
            return authError;

        var toolName = @params?["name"]?.GetValue<string>();

        // Enforce usage limits before execution
        var usageError = await EnforceUsageLimitsAsync(toolName);
        if (usageError != null)
            return usageError;

        var arguments = @params?["arguments"];
        var sw = Stopwatch.StartNew();

        try
        {
            var result = toolName switch
            {
                "memory_search" => await HandleMemorySearch(arguments),
                "memory_ingest" => await HandleMemoryIngest(arguments),
                "memory_about_user" => await HandleMemoryAboutUser(arguments),
                "set_user_persona" => await HandleSetUserPersona(arguments),
                "memory_update" => await _lifecycleTools.HandleMemoryUpdate(arguments),
                "memory_delete" => await _lifecycleTools.HandleMemoryDelete(arguments),
                "memory_merge" => await _lifecycleTools.HandleMemoryMerge(arguments),
                "memory_split" => await _lifecycleTools.HandleMemorySplit(arguments),
                "memory_reinforce" => await _lifecycleTools.HandleMemoryReinforce(arguments),
                "memory_expire" => await _lifecycleTools.HandleMemoryExpire(arguments),
                "initialise_conversation_session" => await HandleInitialiseSession(arguments),
                "end_conversation_session" => await HandleEndSession(),
                _ => throw new Exception($"Unknown tool: {toolName}")
            };

            Logger.LogDebug("Tool {Tool} completed in {Ms}ms (tenant: {TenantId})",
                toolName, sw.ElapsedMilliseconds, CurrentAuth?.TenantId);
            return result;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error executing tool {Tool}", toolName);
            return CreateErrorResponse(ex.Message);
        }
    }

    private async Task<object> HandleMemorySearch(JsonNode? arguments)
    {
        var query = arguments?["query"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrEmpty(query))
            throw new ArgumentException("Query required");

        var modeStr = arguments?["mode"]?.GetValue<string>() ?? "hybrid";
        var limit = Math.Clamp(arguments?["limit"]?.GetValue<int>() ?? 10, 1, 100);
        var threshold = Math.Clamp(arguments?["threshold"]?.GetValue<float>() ?? 0.7f, 0f, 1f);

        var mode = modeStr.ToLowerInvariant() switch
        {
            "semantic" => SearchMode.Semantic,
            "text" => SearchMode.Text,
            _ => SearchMode.Hybrid
        };

        var results = await KgService.SearchMemoriesAsync(query, mode, limit, threshold, includeEntities: true);

        var text = $"Found {results.Count} memories:\n\n" +
            string.Join("\n\n", results.Select((r, i) =>
                $"**{i + 1}.** [{r.Id}]\n" +
                $"{r.Content[..Math.Min(150, r.Content.Length)]}{(r.Content.Length > 150 ? "..." : "")}\n" +
                $"Entities: {string.Join(", ", r.Entities.Take(3).Select(e => e.Name))}\n" +
                $"Score: {(r.Similarity > 0 ? r.Similarity : r.Rank):F3}"));

        return CreateTextResponse(text);
    }

    private async Task<object> HandleMemoryIngest(JsonNode? arguments)
    {
        var content = arguments?["content"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrEmpty(content))
            throw new ArgumentException("Content required");
        if (content.Length > 100000)
            throw new ArgumentException("Content exceeds 100k chars");

        var source = arguments?["source"]?.GetValue<string>()?.Trim();
        var extractEntities = arguments?["extract_entities"]?.GetValue<bool>() ?? true;

        var result = await KgService.IngestMemoryAsync(content, source, CurrentSessionId, null, extractEntities);

        return CreateTextResponse(
            $"Memory ingested.\n" +
            $"ID: {result.MemoryId}\n" +
            $"Entities: {result.EntitiesCreated}\n" +
            $"Relationships: {result.RelationshipsCreated}");
    }

    private async Task<object> HandleMemoryAboutUser(JsonNode? arguments)
    {
        var userId = arguments?["user_id"]?.GetValue<string>() ?? "default_user";
        var persona = await KgService.GetUserPersonaAsync(userId);

        if (persona.Count == 0)
            return CreateTextResponse($"No persona for user: {userId}");

        var text = $"User: {userId}\n\n";
        foreach (var (attrType, attributes) in persona)
        {
            text += $"**{char.ToUpper(attrType[0]) + attrType[1..]}:**\n";
            foreach (var (key, valueData) in attributes)
            {
                if (valueData is Dictionary<string, object> vd)
                    text += $"  - {key}: {vd.GetValueOrDefault("value", "N/A")}\n";
            }
        }

        return CreateTextResponse(text);
    }

    private async Task<object> HandleSetUserPersona(JsonNode? arguments)
    {
        var attrType = arguments?["attribute_type"]?.GetValue<string>()?.Trim();
        var attrKey = arguments?["attribute_key"]?.GetValue<string>()?.Trim();
        var attrValue = arguments?["attribute_value"]?.GetValue<string>()?.Trim();

        if (string.IsNullOrEmpty(attrType) || string.IsNullOrEmpty(attrKey) || string.IsNullOrEmpty(attrValue))
            throw new ArgumentException("attribute_type, attribute_key, attribute_value required");

        var validTypes = new[] { "preference", "skill", "goal", "background" };
        if (!validTypes.Contains(attrType.ToLowerInvariant()))
            throw new ArgumentException($"attribute_type must be: {string.Join(", ", validTypes)}");

        var confidence = Math.Clamp(arguments?["confidence"]?.GetValue<float>() ?? 1.0f, 0f, 1f);
        var userId = arguments?["user_id"]?.GetValue<string>()?.Trim() ?? "default_user";

        await KgService.SetUserPersonaAttributeAsync(attrType, attrKey, attrValue, confidence, userId);

        return CreateTextResponse($"Set {attrType}/{attrKey} = {attrValue}");
    }

    private async Task<object> HandleInitialiseSession(JsonNode? arguments)
    {
        var sessionName = arguments?["session_name"]?.GetValue<string>();
        var clientType = arguments?["client_type"]?.GetValue<string>();

        CurrentSessionId = await KgService.CreateConversationSessionAsync(sessionName, clientType, null);

        return CreateTextResponse($"Session started: {CurrentSessionId}");
    }

    private async Task<object> HandleEndSession()
    {
        if (!CurrentSessionId.HasValue)
            return CreateTextResponse("No active session.");

        await KgService.EndConversationSessionAsync(CurrentSessionId.Value);
        var oldSession = CurrentSessionId;
        CurrentSessionId = null;

        return CreateTextResponse($"Session ended: {oldSession}");
    }
}
