using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace SerialMemory.McpClient;

/// <summary>
/// Docker-first MCP Client for SerialMemory.
/// Proxies safe MCP operations to a remote SerialMemory backend.
/// Hard-disables dangerous capabilities.
/// </summary>
public static class Program
{
    // Environment configuration
    private static readonly string McpServerUrl = Environment.GetEnvironmentVariable("MCP_SERVER_URL")
        ?? "https://serialmemory.serialcoder.com";
    private static readonly string McpApiKey = Environment.GetEnvironmentVariable("MCP_API_KEY")
        ?? throw new InvalidOperationException("MCP_API_KEY environment variable is required");
    private static readonly string McpMode = Environment.GetEnvironmentVariable("MCP_MODE") ?? "developer";

    // Blocked endpoints (security)
    private static readonly HashSet<string> BlockedPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/api/power",
        "/api/mutations",
        "/api/admin",
        "/api/system/dangerous",
        "/api/export/full",
        "/api/delete"
    };

    // Blocked tools (security)
    private static readonly HashSet<string> BlockedTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "execute_shell",
        "filesystem_write",
        "filesystem_delete",
        "raw_sql",
        "admin_reset",
        "bulk_delete"
    };

    // Allowed tools for developer mode
    private static readonly HashSet<string> AllowedTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "memory_search",
        "memory_ingest",
        "memory_about_user",
        "set_user_persona",
        "initialise_conversation_session",
        "end_conversation_session",
        "memory_multi_hop_search",
        "get_integrations",
        "import_from_core",
        "crawl_relationships",
        "get_graph_statistics",
        "get_model_info",
        "reembed_memories",
        "memory_update",
        "memory_delete",
        "memory_merge",
        "memory_split",
        "memory_decay",
        "memory_reinforce",
        "memory_expire",
        "memory_trace",
        "memory_lineage",
        "memory_explain",
        "memory_conflicts",
        "detect_contradictions",
        "detect_hallucinations",
        "verify_memory_integrity",
        "scan_loops",
        "export_workspace",
        "export_memories",
        "export_graph",
        "export_user_profile",
        "engineering_analyze",
        "engineering_visualize",
        "engineering_reason"
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private static HttpClient? _httpClient;
    private static ILogger? _logger;

    public static async Task Main(string[] args)
    {
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
        });
        _logger = loggerFactory.CreateLogger("McpClient");

        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(McpServerUrl),
            Timeout = TimeSpan.FromSeconds(120)
        };
        _httpClient.DefaultRequestHeaders.Add("X-API-Key", McpApiKey);
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "SerialMemory-McpClient/1.0");

        _logger.LogInformation("SerialMemory MCP Client starting");
        _logger.LogInformation("Server URL: {Url}", McpServerUrl);
        _logger.LogInformation("Mode: {Mode}", McpMode);

        // Run MCP protocol over STDIO
        await RunStdioProtocolAsync();
    }

    private static async Task RunStdioProtocolAsync()
    {
        using var stdin = Console.OpenStandardInput();
        using var stdout = Console.OpenStandardOutput();
        using var reader = new StreamReader(stdin);
        using var writer = new StreamWriter(stdout) { AutoFlush = true };

        _logger?.LogInformation("MCP Client ready, waiting for requests...");

        while (true)
        {
            var line = await reader.ReadLineAsync();
            if (line == null) break;

            try
            {
                var request = JsonSerializer.Deserialize<McpRequest>(line, JsonOptions);
                if (request == null) continue;

                var response = await HandleRequestAsync(request);
                var responseJson = JsonSerializer.Serialize(response, JsonOptions);
                await writer.WriteLineAsync(responseJson);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error processing request");
                var errorResponse = new McpResponse
                {
                    Jsonrpc = "2.0",
                    Id = null,
                    Error = new McpError
                    {
                        Code = -32603,
                        Message = $"Internal error: {ex.Message}"
                    }
                };
                var errorJson = JsonSerializer.Serialize(errorResponse, JsonOptions);
                await writer.WriteLineAsync(errorJson);
            }
        }
    }

    private static async Task<McpResponse> HandleRequestAsync(McpRequest request)
    {
        _logger?.LogInformation("Received request: {Method}", request.Method);

        return request.Method switch
        {
            "initialize" => HandleInitialize(request),
            "tools/list" => HandleToolsList(request),
            "tools/call" => await HandleToolCallAsync(request),
            "resources/list" => HandleResourcesList(request),
            "resources/read" => await HandleResourceReadAsync(request),
            "prompts/list" => HandlePromptsList(request),
            _ => new McpResponse
            {
                Jsonrpc = "2.0",
                Id = request.Id,
                Error = new McpError { Code = -32601, Message = $"Method not found: {request.Method}" }
            }
        };
    }

    private static McpResponse HandleInitialize(McpRequest request)
    {
        return new McpResponse
        {
            Jsonrpc = "2.0",
            Id = request.Id,
            Result = new
            {
                protocolVersion = "2024-11-05",
                capabilities = new
                {
                    tools = new { listChanged = false },
                    resources = new { subscribe = false, listChanged = false },
                    prompts = new { listChanged = false }
                },
                serverInfo = new
                {
                    name = "serialmemory-mcp-client",
                    version = "1.0.0"
                }
            }
        };
    }

    private static McpResponse HandleToolsList(McpRequest request)
    {
        var tools = AllowedTools.Select(name => new
        {
            name,
            description = GetToolDescription(name),
            inputSchema = GetToolInputSchema(name)
        }).ToList();

        return new McpResponse
        {
            Jsonrpc = "2.0",
            Id = request.Id,
            Result = new { tools }
        };
    }

    private static async Task<McpResponse> HandleToolCallAsync(McpRequest request)
    {
        var toolName = request.Params?.GetProperty("name").GetString();
        var arguments = request.Params?.GetProperty("arguments");

        if (string.IsNullOrEmpty(toolName))
        {
            return new McpResponse
            {
                Jsonrpc = "2.0",
                Id = request.Id,
                Error = new McpError { Code = -32602, Message = "Tool name is required" }
            };
        }

        // Security check: block dangerous tools
        if (BlockedTools.Contains(toolName))
        {
            _logger?.LogWarning("Blocked tool call: {Tool}", toolName);
            return new McpResponse
            {
                Jsonrpc = "2.0",
                Id = request.Id,
                Error = new McpError { Code = -32600, Message = $"Tool '{toolName}' is not allowed in {McpMode} mode" }
            };
        }

        // Security check: only allow known tools
        if (!AllowedTools.Contains(toolName))
        {
            _logger?.LogWarning("Unknown tool: {Tool}", toolName);
            return new McpResponse
            {
                Jsonrpc = "2.0",
                Id = request.Id,
                Error = new McpError { Code = -32602, Message = $"Unknown tool: {toolName}" }
            };
        }

        try
        {
            // Proxy to backend
            var endpoint = GetToolEndpoint(toolName);
            var method = GetToolHttpMethod(toolName);

            _logger?.LogInformation("Proxying {Tool} to {Endpoint}", toolName, endpoint);

            HttpResponseMessage response;
            if (method == HttpMethod.Get)
            {
                var queryString = BuildQueryString(arguments);
                response = await _httpClient!.GetAsync($"{endpoint}{queryString}");
            }
            else
            {
                var content = new StringContent(
                    arguments?.GetRawText() ?? "{}",
                    System.Text.Encoding.UTF8,
                    "application/json");
                response = await _httpClient!.PostAsync(endpoint, content);
            }

            var responseBody = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                return new McpResponse
                {
                    Jsonrpc = "2.0",
                    Id = request.Id,
                    Result = new
                    {
                        content = new[]
                        {
                            new { type = "text", text = responseBody }
                        }
                    }
                };
            }
            else
            {
                return new McpResponse
                {
                    Jsonrpc = "2.0",
                    Id = request.Id,
                    Error = new McpError
                    {
                        Code = (int)response.StatusCode,
                        Message = $"Backend error: {responseBody}"
                    }
                };
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error calling tool {Tool}", toolName);
            return new McpResponse
            {
                Jsonrpc = "2.0",
                Id = request.Id,
                Error = new McpError { Code = -32603, Message = ex.Message }
            };
        }
    }

    private static McpResponse HandleResourcesList(McpRequest request)
    {
        var resources = new[]
        {
            new { uri = "memory://recent", name = "Recent Memories", mimeType = "application/json" },
            new { uri = "memory://sessions", name = "Conversation Sessions", mimeType = "application/json" }
        };

        return new McpResponse
        {
            Jsonrpc = "2.0",
            Id = request.Id,
            Result = new { resources }
        };
    }

    private static async Task<McpResponse> HandleResourceReadAsync(McpRequest request)
    {
        var uri = request.Params?.GetProperty("uri").GetString();

        if (string.IsNullOrEmpty(uri))
        {
            return new McpResponse
            {
                Jsonrpc = "2.0",
                Id = request.Id,
                Error = new McpError { Code = -32602, Message = "Resource URI is required" }
            };
        }

        try
        {
            var endpoint = uri switch
            {
                "memory://recent" => "/api/memories/recent",
                "memory://sessions" => "/api/sessions/recent",
                _ => throw new ArgumentException($"Unknown resource: {uri}")
            };

            var response = await _httpClient!.GetAsync(endpoint);
            var content = await response.Content.ReadAsStringAsync();

            return new McpResponse
            {
                Jsonrpc = "2.0",
                Id = request.Id,
                Result = new
                {
                    contents = new[]
                    {
                        new { uri, mimeType = "application/json", text = content }
                    }
                }
            };
        }
        catch (Exception ex)
        {
            return new McpResponse
            {
                Jsonrpc = "2.0",
                Id = request.Id,
                Error = new McpError { Code = -32603, Message = ex.Message }
            };
        }
    }

    private static McpResponse HandlePromptsList(McpRequest request)
    {
        return new McpResponse
        {
            Jsonrpc = "2.0",
            Id = request.Id,
            Result = new { prompts = Array.Empty<object>() }
        };
    }

    private static string GetToolEndpoint(string toolName) => toolName switch
    {
        "memory_search" => "/mcp/memory_search",
        "memory_ingest" => "/mcp/memory_ingest",
        "memory_about_user" => "/mcp/memory_about_user",
        "set_user_persona" => "/mcp/set_user_persona",
        "initialise_conversation_session" => "/mcp/initialise_conversation_session",
        "end_conversation_session" => "/mcp/end_conversation_session",
        "memory_multi_hop_search" => "/mcp/memory_multi_hop_search",
        "get_integrations" => "/mcp/get_integrations",
        "import_from_core" => "/mcp/import_from_core",
        "crawl_relationships" => "/mcp/crawl_relationships",
        "get_graph_statistics" => "/mcp/get_graph_statistics",
        "get_model_info" => "/mcp/get_model_info",
        "reembed_memories" => "/mcp/reembed_memories",
        "memory_update" => "/mcp/memory_update",
        "memory_delete" => "/mcp/memory_delete",
        "memory_merge" => "/mcp/memory_merge",
        "memory_split" => "/mcp/memory_split",
        "memory_decay" => "/mcp/memory_decay",
        "memory_reinforce" => "/mcp/memory_reinforce",
        "memory_expire" => "/mcp/memory_expire",
        "memory_trace" => "/mcp/memory_trace",
        "memory_lineage" => "/mcp/memory_lineage",
        "memory_explain" => "/mcp/memory_explain",
        "memory_conflicts" => "/mcp/memory_conflicts",
        "detect_contradictions" => "/mcp/detect_contradictions",
        "detect_hallucinations" => "/mcp/detect_hallucinations",
        "verify_memory_integrity" => "/mcp/verify_memory_integrity",
        "scan_loops" => "/mcp/scan_loops",
        "export_workspace" => "/mcp/export_workspace",
        "export_memories" => "/mcp/export_memories",
        "export_graph" => "/mcp/export_graph",
        "export_user_profile" => "/mcp/export_user_profile",
        "engineering_analyze" => "/mcp/engineering_analyze",
        "engineering_visualize" => "/mcp/engineering_visualize",
        "engineering_reason" => "/mcp/engineering_reason",
        _ => $"/mcp/{toolName}"
    };

    private static HttpMethod GetToolHttpMethod(string toolName) => toolName switch
    {
        "memory_search" => HttpMethod.Post,
        "memory_ingest" => HttpMethod.Post,
        "memory_about_user" => HttpMethod.Get,
        "get_integrations" => HttpMethod.Get,
        "get_graph_statistics" => HttpMethod.Get,
        "get_model_info" => HttpMethod.Get,
        _ => HttpMethod.Post
    };

    private static string BuildQueryString(JsonElement? arguments)
    {
        if (arguments == null || arguments.Value.ValueKind != JsonValueKind.Object)
            return "";

        var pairs = new List<string>();
        foreach (var prop in arguments.Value.EnumerateObject())
        {
            var value = prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString(),
                JsonValueKind.Number => prop.Value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null
            };
            if (value != null)
            {
                pairs.Add($"{Uri.EscapeDataString(prop.Name)}={Uri.EscapeDataString(value)}");
            }
        }

        return pairs.Count > 0 ? "?" + string.Join("&", pairs) : "";
    }

    private static string GetToolDescription(string toolName) => toolName switch
    {
        "memory_search" => "Search for relevant memories using semantic search, full-text search, or both.",
        "memory_ingest" => "Add a new memory to the knowledge graph with automatic entity extraction.",
        "memory_about_user" => "Retrieve structured information about the user's persona.",
        "set_user_persona" => "Set or update a user persona attribute.",
        "initialise_conversation_session" => "Create a new conversation session.",
        "end_conversation_session" => "End the current conversation session.",
        "memory_multi_hop_search" => "Perform multi-hop reasoning by traversing the knowledge graph.",
        "get_integrations" => "List available integrations.",
        "get_graph_statistics" => "Get statistics about the knowledge graph.",
        "memory_update" => "Update memory content with new embedding.",
        "memory_delete" => "Soft delete a memory.",
        "memory_merge" => "Merge multiple memories into one.",
        "memory_split" => "Split a memory into multiple child memories.",
        "detect_contradictions" => "Find memories that contradict each other.",
        "detect_hallucinations" => "Flag potential hallucinations.",
        "engineering_analyze" => "Analyze the knowledge graph for engineering insights.",
        "engineering_visualize" => "Generate graph visualization data.",
        "engineering_reason" => "Run multi-model reasoning on the knowledge graph.",
        _ => $"Execute {toolName} tool"
    };

    private static object GetToolInputSchema(string toolName) => toolName switch
    {
        "memory_search" => new
        {
            type = "object",
            properties = new
            {
                query = new { type = "string", description = "Search query" },
                mode = new { type = "string", @enum = new[] { "semantic", "text", "hybrid" } },
                limit = new { type = "integer", @default = 10 }
            },
            required = new[] { "query" }
        },
        "memory_ingest" => new
        {
            type = "object",
            properties = new
            {
                content = new { type = "string", description = "Memory content" },
                source = new { type = "string", description = "Source identifier" }
            },
            required = new[] { "content" }
        },
        _ => new { type = "object", properties = new { } }
    };
}

// MCP Protocol Types
public class McpRequest
{
    [JsonPropertyName("jsonrpc")]
    public string Jsonrpc { get; set; } = "2.0";

    [JsonPropertyName("id")]
    public object? Id { get; set; }

    [JsonPropertyName("method")]
    public string Method { get; set; } = "";

    [JsonPropertyName("params")]
    public JsonElement? Params { get; set; }
}

public class McpResponse
{
    [JsonPropertyName("jsonrpc")]
    public string Jsonrpc { get; set; } = "2.0";

    [JsonPropertyName("id")]
    public object? Id { get; set; }

    [JsonPropertyName("result")]
    public object? Result { get; set; }

    [JsonPropertyName("error")]
    public McpError? Error { get; set; }
}

public class McpError
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
}
