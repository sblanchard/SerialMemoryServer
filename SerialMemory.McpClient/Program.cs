using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace SerialMemory.McpClient;

/// <summary>
/// Docker-first MCP Client for SerialMemory.
/// Zero-parameter startup with interactive setup on first run.
/// Proxies safe MCP operations to a remote SerialMemory backend.
/// </summary>
public static class Program
{
    private const string ConfigPath = "/data/config.json";
    private const string DefaultServerUrl = "https://serialmemory.serialcoder.com";

    // Machine-specific encryption key derived from container ID
    private static readonly byte[] EncryptionKey = DeriveEncryptionKey();

    // Blocked tools (security)
    private static readonly HashSet<string> BlockedTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "execute_shell", "filesystem_write", "filesystem_delete",
        "raw_sql", "admin_reset", "bulk_delete"
    };

    // Allowed tools for developer mode
    private static readonly HashSet<string> AllowedTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "memory_search", "memory_ingest", "memory_about_user", "set_user_persona",
        "initialise_conversation_session", "end_conversation_session",
        "memory_multi_hop_search", "get_integrations", "import_from_core",
        "crawl_relationships", "get_graph_statistics", "get_model_info",
        "reembed_memories", "memory_update", "memory_delete", "memory_merge",
        "memory_split", "memory_decay", "memory_reinforce", "memory_expire",
        "memory_trace", "memory_lineage", "memory_explain", "memory_conflicts",
        "detect_contradictions", "detect_hallucinations", "verify_memory_integrity",
        "scan_loops", "export_workspace", "export_memories", "export_graph",
        "export_user_profile", "engineering_analyze", "engineering_visualize",
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
    private static McpConfig? _config;

    public static async Task Main(string[] args)
    {
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
        });
        _logger = loggerFactory.CreateLogger("McpClient");

        // Ensure /data directory exists
        EnsureDataDirectory();

        // Load or create config
        _config = await LoadOrCreateConfigAsync();
        if (_config == null)
        {
            Environment.Exit(1);
            return;
        }

        // Initialize HTTP client (never log API key)
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(_config.ServerUrl),
            Timeout = TimeSpan.FromSeconds(120)
        };
        _httpClient.DefaultRequestHeaders.Add("X-API-Key", DecryptApiKey(_config.EncryptedApiKey));
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "SerialMemory-McpClient/1.0");

        _logger.LogInformation("SerialMemory MCP Client starting");
        _logger.LogInformation("Server: {Url}", _config.ServerUrl);

        // Run MCP protocol over STDIO
        await RunStdioProtocolAsync();
    }

    private static void EnsureDataDirectory()
    {
        var dataDir = Path.GetDirectoryName(ConfigPath);
        if (!string.IsNullOrEmpty(dataDir) && !Directory.Exists(dataDir))
        {
            Directory.CreateDirectory(dataDir);
        }
    }

    private static async Task<McpConfig?> LoadOrCreateConfigAsync()
    {
        // Check if config exists
        if (File.Exists(ConfigPath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(ConfigPath);
                var config = JsonSerializer.Deserialize<McpConfig>(json, JsonOptions);

                if (config != null && !string.IsNullOrEmpty(config.EncryptedApiKey))
                {
                    _logger?.LogInformation("Loaded configuration from {Path}", ConfigPath);
                    return config;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to load config, will prompt for new setup");
            }
        }

        // First run - interactive setup required
        return await RunInteractiveSetupAsync();
    }

    private static async Task<McpConfig?> RunInteractiveSetupAsync()
    {
        // Check if we have a TTY
        if (!Console.IsInputRedirected || !Environment.UserInteractive)
        {
            // For Docker, check if stdin is a terminal
            try
            {
                // Try to read - if this fails, we don't have a TTY
                Console.Write("");
            }
            catch
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine("═══════════════════════════════════════════════════════════════");
                Console.Error.WriteLine("  ERROR: Interactive terminal required on first run");
                Console.Error.WriteLine("═══════════════════════════════════════════════════════════════");
                Console.Error.WriteLine();
                Console.Error.WriteLine("  This container requires an interactive terminal for initial setup.");
                Console.Error.WriteLine();
                Console.Error.WriteLine("  Run with: docker run -it serialcoder/serialmemory-mcp");
                Console.Error.WriteLine();
                Console.Error.WriteLine("  After setup, the container can run non-interactively.");
                Console.Error.WriteLine();
                return null;
            }
        }

        Console.Error.WriteLine();
        Console.Error.WriteLine("╔═══════════════════════════════════════════════════════════════╗");
        Console.Error.WriteLine("║         SerialMemory MCP Client - First Time Setup           ║");
        Console.Error.WriteLine("╚═══════════════════════════════════════════════════════════════╝");
        Console.Error.WriteLine();

        // Prompt for API key (secure - no echo)
        Console.Error.Write("  Enter your SerialMemory API Key: ");
        var apiKey = ReadSecureInput();
        Console.Error.WriteLine();

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Console.Error.WriteLine("  ERROR: API key is required.");
            return null;
        }

        // Prompt for server URL
        Console.Error.WriteLine();
        Console.Error.Write($"  Server URL (press Enter for default [{DefaultServerUrl}]): ");
        var serverUrl = Console.ReadLine()?.Trim();

        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            serverUrl = DefaultServerUrl;
        }

        // Validate URL
        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out _))
        {
            Console.Error.WriteLine("  ERROR: Invalid URL format.");
            return null;
        }

        // Create config with encrypted API key
        var config = new McpConfig
        {
            ServerUrl = serverUrl,
            EncryptedApiKey = EncryptApiKey(apiKey),
            CreatedAt = DateTimeOffset.UtcNow
        };

        // Save config
        try
        {
            var json = JsonSerializer.Serialize(config, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            await File.WriteAllTextAsync(ConfigPath, json);

            Console.Error.WriteLine();
            Console.Error.WriteLine("  ✓ Configuration saved successfully!");
            Console.Error.WriteLine();
            Console.Error.WriteLine("  You can now run this container without -it flag.");
            Console.Error.WriteLine();

            return config;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  ERROR: Failed to save configuration: {ex.Message}");
            return null;
        }
    }

    private static string ReadSecureInput()
    {
        var input = new StringBuilder();

        while (true)
        {
            var keyInfo = Console.ReadKey(intercept: true);

            if (keyInfo.Key == ConsoleKey.Enter)
                break;

            if (keyInfo.Key == ConsoleKey.Backspace && input.Length > 0)
            {
                input.Remove(input.Length - 1, 1);
                Console.Error.Write("\b \b");
            }
            else if (!char.IsControl(keyInfo.KeyChar))
            {
                input.Append(keyInfo.KeyChar);
                Console.Error.Write("*");
            }
        }

        return input.ToString();
    }

    private static byte[] DeriveEncryptionKey()
    {
        // Use machine-specific data to derive key
        var machineId = Environment.MachineName +
                       Environment.GetEnvironmentVariable("HOSTNAME") +
                       "serialmemory-mcp-client-v1";

        using var sha256 = SHA256.Create();
        return sha256.ComputeHash(Encoding.UTF8.GetBytes(machineId));
    }

    private static string EncryptApiKey(string apiKey)
    {
        using var aes = Aes.Create();
        aes.Key = EncryptionKey;
        aes.GenerateIV();

        using var encryptor = aes.CreateEncryptor();
        var plainBytes = Encoding.UTF8.GetBytes(apiKey);
        var cipherBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

        // Prepend IV to cipher text
        var result = new byte[aes.IV.Length + cipherBytes.Length];
        aes.IV.CopyTo(result, 0);
        cipherBytes.CopyTo(result, aes.IV.Length);

        return Convert.ToBase64String(result);
    }

    private static string DecryptApiKey(string encryptedApiKey)
    {
        var data = Convert.FromBase64String(encryptedApiKey);

        using var aes = Aes.Create();
        aes.Key = EncryptionKey;

        // Extract IV from beginning
        var iv = new byte[16];
        var cipher = new byte[data.Length - 16];
        Array.Copy(data, 0, iv, 0, 16);
        Array.Copy(data, 16, cipher, 0, cipher.Length);

        aes.IV = iv;

        using var decryptor = aes.CreateDecryptor();
        var plainBytes = decryptor.TransformFinalBlock(cipher, 0, cipher.Length);

        return Encoding.UTF8.GetString(plainBytes);
    }

    private static async Task RunStdioProtocolAsync()
    {
        using var stdin = Console.OpenStandardInput();
        using var stdout = Console.OpenStandardOutput();
        using var reader = new StreamReader(stdin);
        using var writer = new StreamWriter(stdout) { AutoFlush = true };

        _logger?.LogInformation("MCP Client ready");

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
                    Error = new McpError { Code = -32603, Message = $"Internal error: {ex.Message}" }
                };
                await writer.WriteLineAsync(JsonSerializer.Serialize(errorResponse, JsonOptions));
            }
        }
    }

    private static async Task<McpResponse> HandleRequestAsync(McpRequest request)
    {
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

    private static McpResponse HandleInitialize(McpRequest request) => new()
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
            serverInfo = new { name = "serialmemory-mcp-client", version = "1.0.0" }
        }
    };

    private static McpResponse HandleToolsList(McpRequest request) => new()
    {
        Jsonrpc = "2.0",
        Id = request.Id,
        Result = new
        {
            tools = AllowedTools.Select(name => new
            {
                name,
                description = GetToolDescription(name),
                inputSchema = GetToolInputSchema(name)
            }).ToList()
        }
    };

    private static async Task<McpResponse> HandleToolCallAsync(McpRequest request)
    {
        var toolName = request.Params?.GetProperty("name").GetString();
        var arguments = request.Params?.GetProperty("arguments");

        if (string.IsNullOrEmpty(toolName))
            return ErrorResponse(request.Id, -32602, "Tool name is required");

        if (BlockedTools.Contains(toolName))
            return ErrorResponse(request.Id, -32600, $"Tool '{toolName}' is blocked");

        if (!AllowedTools.Contains(toolName))
            return ErrorResponse(request.Id, -32602, $"Unknown tool: {toolName}");

        try
        {
            var endpoint = GetToolEndpoint(toolName);
            var method = GetToolHttpMethod(toolName);

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
                    Encoding.UTF8,
                    "application/json");
                response = await _httpClient!.PostAsync(endpoint, content);
            }

            var responseBody = await response.Content.ReadAsStringAsync();

            return response.IsSuccessStatusCode
                ? new McpResponse
                {
                    Jsonrpc = "2.0",
                    Id = request.Id,
                    Result = new { content = new[] { new { type = "text", text = responseBody } } }
                }
                : ErrorResponse(request.Id, (int)response.StatusCode, $"Backend error: {responseBody}");
        }
        catch (Exception ex)
        {
            return ErrorResponse(request.Id, -32603, ex.Message);
        }
    }

    private static McpResponse HandleResourcesList(McpRequest request) => new()
    {
        Jsonrpc = "2.0",
        Id = request.Id,
        Result = new
        {
            resources = new[]
            {
                new { uri = "memory://recent", name = "Recent Memories", mimeType = "application/json" },
                new { uri = "memory://sessions", name = "Conversation Sessions", mimeType = "application/json" }
            }
        }
    };

    private static async Task<McpResponse> HandleResourceReadAsync(McpRequest request)
    {
        var uri = request.Params?.GetProperty("uri").GetString();
        if (string.IsNullOrEmpty(uri))
            return ErrorResponse(request.Id, -32602, "Resource URI is required");

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
                Result = new { contents = new[] { new { uri, mimeType = "application/json", text = content } } }
            };
        }
        catch (Exception ex)
        {
            return ErrorResponse(request.Id, -32603, ex.Message);
        }
    }

    private static McpResponse HandlePromptsList(McpRequest request) => new()
    {
        Jsonrpc = "2.0",
        Id = request.Id,
        Result = new { prompts = Array.Empty<object>() }
    };

    private static McpResponse ErrorResponse(object? id, int code, string message) => new()
    {
        Jsonrpc = "2.0",
        Id = id,
        Error = new McpError { Code = code, Message = message }
    };

    private static string GetToolEndpoint(string toolName) => $"/mcp/{toolName}";

    private static HttpMethod GetToolHttpMethod(string toolName) => toolName switch
    {
        "memory_about_user" or "get_integrations" or "get_graph_statistics" or "get_model_info" => HttpMethod.Get,
        _ => HttpMethod.Post
    };

    private static string BuildQueryString(JsonElement? arguments)
    {
        if (arguments == null || arguments.Value.ValueKind != JsonValueKind.Object)
            return "";

        var pairs = arguments.Value.EnumerateObject()
            .Select(prop => (prop.Name, Value: prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString(),
                JsonValueKind.Number => prop.Value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null
            }))
            .Where(x => x.Value != null)
            .Select(x => $"{Uri.EscapeDataString(x.Name)}={Uri.EscapeDataString(x.Value!)}");

        var query = string.Join("&", pairs);
        return string.IsNullOrEmpty(query) ? "" : "?" + query;
    }

    private static string GetToolDescription(string toolName) => toolName switch
    {
        "memory_search" => "Search for relevant memories using semantic search.",
        "memory_ingest" => "Add a new memory to the knowledge graph.",
        "memory_about_user" => "Retrieve user persona information.",
        "set_user_persona" => "Set or update a user persona attribute.",
        "memory_multi_hop_search" => "Perform multi-hop reasoning through the knowledge graph.",
        "get_graph_statistics" => "Get statistics about the knowledge graph.",
        "detect_contradictions" => "Find memories that contradict each other.",
        "detect_hallucinations" => "Flag potential hallucinations.",
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

// Configuration model
public sealed class McpConfig
{
    [JsonPropertyName("serverUrl")]
    public string ServerUrl { get; set; } = "";

    [JsonPropertyName("encryptedApiKey")]
    public string EncryptedApiKey { get; set; } = "";

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; }
}

// MCP Protocol Types
public sealed class McpRequest
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

public sealed class McpResponse
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

public sealed class McpError
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
}
