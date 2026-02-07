using System.Text.Json;
using System.Text.Json.Nodes;

namespace SerialMemory.Mcp.Tools;

/// <summary>
/// Two-tool gateway: get_tools discovers available tools by category,
/// use_tool dispatches to registered handlers.
/// Reduces the number of top-level MCP tools while keeping all functionality accessible.
/// </summary>
public sealed class ToolGateway
{
    private readonly Dictionary<string, Dictionary<string, ToolRegistration>> _registry = new();

    public sealed record ToolRegistration(object Schema, Func<JsonNode?, Task<object>> Handler);

    /// <summary>
    /// Register a tool under a category.
    /// </summary>
    public void Register(string category, string toolName, object schema, Func<JsonNode?, Task<object>> handler)
    {
        if (!_registry.TryGetValue(category, out var tools))
        {
            tools = new Dictionary<string, ToolRegistration>();
            _registry[category] = tools;
        }
        tools[toolName] = new ToolRegistration(schema, handler);
    }

    /// <summary>
    /// Handle get_tools: returns tool schemas by category or a category listing.
    /// </summary>
    public object HandleGetTools(JsonNode? arguments)
    {
        var category = arguments?["category"]?.GetValue<string>()?.Trim()?.ToLowerInvariant();

        if (string.IsNullOrEmpty(category))
        {
            // Return category listing with tool counts
            var text = "## Available Tool Categories\n\n";
            foreach (var (cat, tools) in _registry.OrderBy(kv => kv.Key))
            {
                text += $"- **{cat}** ({tools.Count} tools)\n";
                foreach (var (toolName, _) in tools.OrderBy(kv => kv.Key))
                {
                    text += $"  - `{toolName}`\n";
                }
            }
            text += "\nUse `get_tools` with a `category` to see full schemas, or `use_tool` to execute.";
            return CreateTextResponse(text);
        }

        if (!_registry.TryGetValue(category, out var categoryTools))
        {
            return CreateErrorResponse(
                $"Unknown category: {category}. Available: {string.Join(", ", _registry.Keys.OrderBy(k => k))}");
        }

        var schemas = categoryTools.Values.Select(t => t.Schema).ToArray();
        return CreateTextResponse(
            $"## Tools in '{category}' ({schemas.Length})\n\n" +
            $"Use `use_tool` with `tool_name` and `arguments` to execute.\n\n" +
            JsonSerializer.Serialize(schemas, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            }));
    }

    /// <summary>
    /// Handle use_tool: dispatches to the registered handler.
    /// Returns the tool name for usage tracking.
    /// </summary>
    public async Task<(object Result, string ToolName)> HandleUseTool(JsonNode? arguments)
    {
        var toolName = arguments?["tool_name"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrEmpty(toolName))
            throw new ArgumentException("tool_name is required");

        var toolArguments = arguments?["arguments"];

        // Find the tool across all categories
        foreach (var (_, tools) in _registry)
        {
            if (tools.TryGetValue(toolName, out var registration))
            {
                var result = await registration.Handler(toolArguments);
                return (result, toolName);
            }
        }

        throw new ArgumentException(
            $"Unknown tool: {toolName}. Use `get_tools` to discover available tools.");
    }

    /// <summary>
    /// Get all registered tool names for building usage tracking maps.
    /// </summary>
    public IEnumerable<string> GetAllToolNames()
    {
        return _registry.Values.SelectMany(tools => tools.Keys);
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
