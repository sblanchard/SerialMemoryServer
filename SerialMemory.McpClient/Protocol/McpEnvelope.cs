using System.Text.Json;
using System.Text.Json.Serialization;

namespace SerialMemory.McpClient.Protocol;

/// <summary>
/// MCP JSON-RPC envelope for request/response framing.
/// </summary>
public sealed class McpEnvelope
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = "2.0";

    [JsonPropertyName("id")]
    public object? Id { get; set; }

    [JsonPropertyName("method")]
    public string? Method { get; set; }

    [JsonPropertyName("params")]
    public JsonElement? Params { get; set; }

    [JsonPropertyName("result")]
    public object? Result { get; set; }

    [JsonPropertyName("error")]
    public McpError? Error { get; set; }

    public bool IsRequest => Method != null;
    public bool IsResponse => Result != null || Error != null;
    public bool IsNotification => Method != null && Id == null;
}

/// <summary>
/// MCP error object per JSON-RPC 2.0 spec.
/// </summary>
public sealed class McpError
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    [JsonPropertyName("data")]
    public object? Data { get; set; }

    public static McpError ParseError(string message) => new() { Code = -32700, Message = message };
    public static McpError InvalidRequest(string message) => new() { Code = -32600, Message = message };
    public static McpError MethodNotFound(string method) => new() { Code = -32601, Message = $"Method not found: {method}" };
    public static McpError InvalidParams(string message) => new() { Code = -32602, Message = message };
    public static McpError InternalError(string message) => new() { Code = -32603, Message = message };
    public static McpError ServerError(int code, string message) => new() { Code = code, Message = message };
    public static McpError BackendUnavailable() => new() { Code = -32000, Message = "Backend service unavailable" };
}

/// <summary>
/// MCP protocol constants and utilities.
/// </summary>
public static class McpProtocol
{
    public const string ProtocolVersion = "2024-11-05";
    public const string ServerName = "serialmemory-mcp-bridge";
    public const string ServerVersion = "1.0.0";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public static McpEnvelope CreateResponse(object? id, object result) => new()
    {
        Id = id,
        Result = result
    };

    public static McpEnvelope CreateErrorResponse(object? id, McpError error) => new()
    {
        Id = id,
        Error = error
    };

    public static McpEnvelope CreateInitializeResponse(object? id) => CreateResponse(id, new
    {
        protocolVersion = ProtocolVersion,
        capabilities = new
        {
            tools = new { listChanged = false },
            resources = new { subscribe = false, listChanged = false },
            prompts = new { listChanged = false }
        },
        serverInfo = new
        {
            name = ServerName,
            version = ServerVersion
        }
    });
}
