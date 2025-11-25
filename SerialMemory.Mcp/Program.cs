using System.Text.Json;
using System.Text.Json.Nodes;
using SerialMemory.Core.Interfaces;
using SerialMemory.Infrastructure;
using StackExchange.Redis;

// MCP STDIO Server for Serial Memory
// Implements Model Context Protocol over STDIN/STDOUT
// Backed by Redis + PostgreSQL for persistent context storage

var redisConn = ConnectionMultiplexer.Connect(Environment.GetEnvironmentVariable("REDIS_CONN") ?? "localhost:6379");
IContextStore store = new RedisContextStore(redisConn);

// Read JSON-RPC requests from STDIN, write responses to STDOUT
await using var stdin = Console.OpenStandardInput();
await using var stdout = Console.OpenStandardOutput();
using var reader = new StreamReader(stdin);
await using var writer = new StreamWriter(stdout) { AutoFlush = true };

var options = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = false
};

await writer.WriteLineAsync(JsonSerializer.Serialize(new
{
    jsonrpc = "2.0",
    method = "initialized",
    @params = new { }
}, options));

while (!reader.EndOfStream)
{
    var line = await reader.ReadLineAsync();
    if (string.IsNullOrWhiteSpace(line)) continue;

    try
    {
        var request = JsonNode.Parse(line);
        if (request == null) continue;

        var id = request["id"];
        var method = request["method"]?.GetValue<string>();
        var @params = request["params"];

        object? result = method switch
        {
            "initialize" => HandleInitialize(),
            "tools/list" => HandleToolsList(),
            "resources/list" => HandleResourcesList(),
            "resources/read" => await HandleResourcesRead(@params),
            "tools/call" => await HandleToolsCall(@params),
            _ => null
        };

        if (result != null)
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = id?.GetValue<object>(),
                result
            }, options));
        }
    }
    catch (Exception ex)
    {
        await Console.Error.WriteLineAsync($"[MCP Error] {ex.Message}");
    }
}

// MCP Protocol Handlers
object HandleInitialize()
{
    return new
    {
        protocolVersion = "2024-11-05",
        serverInfo = new
        {
            name = "serial-memory-server",
            version = "1.0.0"
        },
        capabilities = new
        {
            tools = new { },
            resources = new { }
        }
    };
}

object HandleToolsList()
{
    return new
    {
        tools = new object[]
        {
            new
            {
                name = "set_context",
                description = "Store or update a context value by key",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        key = new { type = "string", description = "The context key" },
                        value = new { type = "string", description = "The context value to store" }
                    },
                    required = new[] { "key", "value" }
                }
            },
            new
            {
                name = "get_context",
                description = "Retrieve a context value by key",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        key = new { type = "string", description = "The context key" }
                    },
                    required = new[] { "key" }
                }
            },
            new
            {
                name = "delete_context",
                description = "Delete a context entry by key",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        key = new { type = "string", description = "The context key to delete" }
                    },
                    required = new[] { "key" }
                }
            },
            new
            {
                name = "list_contexts",
                description = "List all available context keys",
                inputSchema = new
                {
                    type = "object",
                    properties = new { }
                }
            }
        }
    };
}

object HandleResourcesList()
{
    return new
    {
        resources = new object[]
        {
            new
            {
                uri = "context://all",
                name = "All Contexts",
                description = "List of all context keys",
                mimeType = "application/json"
            }
        }
    };
}

async Task<object> HandleResourcesRead(JsonNode? @params)
{
    var uri = @params?["uri"]?.GetValue<string>();

    if (uri == "context://all")
    {
        var keys = await store.ListKeysAsync();
        return new
        {
            contents = new[]
            {
                new
                {
                    uri = "context://all",
                    mimeType = "application/json",
                    text = JsonSerializer.Serialize(new { keys })
                }
            }
        };
    }

    throw new Exception($"Unknown resource URI: {uri}");
}

async Task<object> HandleToolsCall(JsonNode? @params)
{
    var toolName = @params?["name"]?.GetValue<string>();
    var arguments = @params?["arguments"];

    switch (toolName)
    {
        case "set_context":
        {
            var key = arguments?["key"]?.GetValue<string>() ?? throw new Exception("Missing key");
            var value = arguments?["value"]?.GetValue<string>() ?? throw new Exception("Missing value");
            await store.SetAsync(key, value);
            return new
            {
                content = new[]
                {
                    new
                    {
                        type = "text",
                        text = $"Context '{key}' set successfully"
                    }
                }
            };
        }

        case "get_context":
        {
            var key = arguments?["key"]?.GetValue<string>() ?? throw new Exception("Missing key");
            var value = await store.GetAsync(key);
            if (value == null)
            {
                return new
                {
                    content = new[]
                    {
                        new
                        {
                            type = "text",
                            text = $"Context '{key}' not found"
                        }
                    }
                };
            }
            return new
            {
                content = new[]
                {
                    new
                    {
                        type = "text",
                        text = value
                    }
                }
            };
        }

        case "delete_context":
        {
            var key = arguments?["key"]?.GetValue<string>() ?? throw new Exception("Missing key");
            await store.DeleteAsync(key);
            return new
            {
                content = new[]
                {
                    new
                    {
                        type = "text",
                        text = $"Context '{key}' deleted successfully"
                    }
                }
            };
        }

        case "list_contexts":
        {
            var keys = await store.ListKeysAsync();
            var keyList = keys.ToList();
            return new
            {
                content = new[]
                {
                    new
                    {
                        type = "text",
                        text = JsonSerializer.Serialize(new { keys = keyList, count = keyList.Count })
                    }
                }
            };
        }

        default:
            throw new Exception($"Unknown tool: {toolName}");
    }
}
