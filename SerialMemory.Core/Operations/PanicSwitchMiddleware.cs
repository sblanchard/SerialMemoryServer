using System.Text.Json;

namespace SerialMemory.Core.Operations;

/// <summary>
/// Middleware that enforces panic switches (operator kill switches).
/// Must be added early in the pipeline to block requests before any processing.
/// </summary>
public static class PanicSwitchMiddleware
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    /// <summary>
    /// Returns true if the request should be blocked due to panic switches.
    /// </summary>
    public static (bool blocked, int statusCode, string message) CheckPanicSwitches(
        OperationalConfig config,
        string httpMethod,
        string path)
    {
        // MAINTENANCE_MODE blocks everything except health checks
        if (config.MaintenanceMode && !IsHealthCheckPath(path))
        {
            return (true, 503, "Service is in maintenance mode");
        }

        // READ_ONLY_MODE blocks all writes
        if (config.ReadOnlyMode && IsWriteOperation(httpMethod, path))
        {
            return (true, 503, "Service is in read-only mode");
        }

        // DISABLE_WRITES blocks write operations
        if (config.DisableWrites && IsWriteOperation(httpMethod, path))
        {
            return (true, 503, "Write operations are disabled");
        }

        // DISABLE_SIGNUPS blocks signup/registration endpoints
        if (config.DisableSignups && IsSignupPath(path))
        {
            return (true, 503, "Signups are disabled");
        }

        return (false, 0, "");
    }

    /// <summary>
    /// Creates a JSON error response for panic switch blocks.
    /// </summary>
    public static string CreateErrorResponse(string message)
    {
        var response = new
        {
            error = new
            {
                code = "service_unavailable",
                message,
                panicSwitch = true
            }
        };

        return JsonSerializer.Serialize(response, JsonOptions);
    }

    private static bool IsHealthCheckPath(string path)
    {
        return path.StartsWith("/health", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("/healthz", StringComparison.OrdinalIgnoreCase) ||
               path.Equals("/live", StringComparison.OrdinalIgnoreCase) ||
               path.Equals("/ready", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWriteOperation(string method, string path)
    {
        // POST, PUT, PATCH, DELETE are write operations
        var writeMethod = method switch
        {
            "POST" => true,
            "PUT" => true,
            "PATCH" => true,
            "DELETE" => true,
            _ => false
        };

        if (!writeMethod) return false;

        // Exclude health checks and auth endpoints from write blocking
        if (IsHealthCheckPath(path)) return false;

        return true;
    }

    private static bool IsSignupPath(string path)
    {
        return path.Contains("/signup", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("/register", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("/onboard", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// MCP-specific panic switch enforcement for STDIO protocol.
/// </summary>
public static class McpPanicSwitch
{
    /// <summary>
    /// Returns true if the tool call should be blocked due to panic switches.
    /// </summary>
    public static (bool blocked, string message) CheckToolCall(OperationalConfig config, string toolName)
    {
        if (config.MaintenanceMode)
        {
            return (true, "Service is in maintenance mode - all operations blocked");
        }

        // Write tools are blocked in read-only mode or when writes disabled
        if ((config.ReadOnlyMode || config.DisableWrites) && IsWriteTool(toolName))
        {
            return (true, $"Write operations are disabled - {toolName} blocked");
        }

        return (false, "");
    }

    /// <summary>
    /// List of tools that perform write operations.
    /// </summary>
    private static readonly HashSet<string> WriteTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "memory_ingest",
        "memory_update",
        "memory_delete",
        "memory_merge",
        "memory_split",
        "memory_decay",
        "memory_reinforce",
        "memory_expire",
        "set_user_persona",
        "import_from_core",
        "crawl_relationships",
        "reembed_memories"
    };

    private static bool IsWriteTool(string toolName)
    {
        return WriteTools.Contains(toolName);
    }

    /// <summary>
    /// Creates an MCP error response for panic switch blocks.
    /// </summary>
    public static object CreateBlockedResponse(string message)
    {
        return new
        {
            content = new[]
            {
                new
                {
                    type = "text",
                    text = $"🚫 Operation Blocked: {message}\n\nThis is an operator-enforced restriction. Contact your administrator."
                }
            },
            isError = true
        };
    }
}
