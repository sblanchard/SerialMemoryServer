using System.Text.Json;
using System.Text.Json.Serialization;

namespace SerialMemory.Core.Operations;

/// <summary>
/// Structured log entry for JSON logging.
/// All logs MUST use this format - no plaintext logging allowed.
/// </summary>
public sealed class StructuredLogEntry
{
    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    [JsonPropertyName("level")]
    public string Level { get; init; } = "INFO";

    [JsonPropertyName("message")]
    public string Message { get; init; } = "";

    [JsonPropertyName("correlation_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CorrelationId { get; init; }

    [JsonPropertyName("tenant_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TenantId { get; init; }

    [JsonPropertyName("api_key_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ApiKeyId { get; init; }

    [JsonPropertyName("tool_name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolName { get; init; }

    [JsonPropertyName("user_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UserId { get; init; }

    [JsonPropertyName("session_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SessionId { get; init; }

    [JsonPropertyName("duration_ms")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? DurationMs { get; init; }

    [JsonPropertyName("success")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Success { get; init; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; init; }

    [JsonPropertyName("error_type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorType { get; init; }

    [JsonPropertyName("stack_trace")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StackTrace { get; init; }

    [JsonPropertyName("request_path")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RequestPath { get; init; }

    [JsonPropertyName("http_method")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? HttpMethod { get; init; }

    [JsonPropertyName("status_code")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? StatusCode { get; init; }

    [JsonPropertyName("client_ip")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ClientIp { get; init; }

    [JsonPropertyName("service")]
    public string Service { get; init; } = "serial-memory";

    [JsonPropertyName("version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Version { get; init; }

    [JsonPropertyName("environment")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Environment { get; init; }

    [JsonPropertyName("extra")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, object>? Extra { get; init; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
}

/// <summary>
/// Thread-local context for correlation and tenant information.
/// Must be set at the start of each request/operation.
/// </summary>
public static class OperationContext
{
    private static readonly AsyncLocal<string?> _correlationId = new();
    private static readonly AsyncLocal<string?> _tenantId = new();
    private static readonly AsyncLocal<string?> _apiKeyId = new();
    private static readonly AsyncLocal<string?> _userId = new();
    private static readonly AsyncLocal<string?> _sessionId = new();
    private static readonly AsyncLocal<string?> _toolName = new();

    public static string? CorrelationId
    {
        get => _correlationId.Value;
        set => _correlationId.Value = value;
    }

    public static string? TenantId
    {
        get => _tenantId.Value;
        set => _tenantId.Value = value;
    }

    public static string? ApiKeyId
    {
        get => _apiKeyId.Value;
        set => _apiKeyId.Value = value;
    }

    public static string? UserId
    {
        get => _userId.Value;
        set => _userId.Value = value;
    }

    public static string? SessionId
    {
        get => _sessionId.Value;
        set => _sessionId.Value = value;
    }

    public static string? ToolName
    {
        get => _toolName.Value;
        set => _toolName.Value = value;
    }

    /// <summary>
    /// Generates a new correlation ID if not already set.
    /// </summary>
    public static string EnsureCorrelationId()
    {
        if (string.IsNullOrEmpty(CorrelationId))
        {
            CorrelationId = Guid.CreateVersion7().ToString();
        }
        return CorrelationId!;
    }

    /// <summary>
    /// Sets all context values at once.
    /// </summary>
    public static void Set(string? tenantId = null, string? apiKeyId = null, string? userId = null, string? sessionId = null)
    {
        TenantId = tenantId;
        ApiKeyId = apiKeyId;
        UserId = userId;
        SessionId = sessionId;
        EnsureCorrelationId();
    }

    /// <summary>
    /// Clears all context values.
    /// </summary>
    public static void Clear()
    {
        CorrelationId = null;
        TenantId = null;
        ApiKeyId = null;
        UserId = null;
        SessionId = null;
        ToolName = null;
    }

    /// <summary>
    /// Creates a StructuredLogEntry with current context values.
    /// </summary>
    public static StructuredLogEntry CreateLogEntry(string level, string message, Dictionary<string, object>? extra = null)
    {
        return new StructuredLogEntry
        {
            Level = level,
            Message = message,
            CorrelationId = CorrelationId,
            TenantId = TenantId,
            ApiKeyId = ApiKeyId,
            UserId = UserId,
            SessionId = SessionId,
            ToolName = ToolName,
            Extra = extra
        };
    }
}

/// <summary>
/// Structured logger that outputs JSON to stderr.
/// Use this for all MCP server logging.
/// </summary>
public sealed class StructuredJsonLogger
{
    private readonly string _service;
    private readonly string? _version;
    private readonly string? _environment;
    private readonly TextWriter _output;
    private readonly object _lock = new();

    public StructuredJsonLogger(
        string service = "serial-memory",
        string? version = null,
        string? environment = null,
        TextWriter? output = null)
    {
        _service = service;
        _version = version;
        _environment = environment ?? Environment.GetEnvironmentVariable("ENVIRONMENT") ?? "development";
        _output = output ?? Console.Error;
    }

    public void Log(string level, string message, Exception? exception = null, Dictionary<string, object>? extra = null)
    {
        var entry = new StructuredLogEntry
        {
            Level = level,
            Message = message,
            CorrelationId = OperationContext.CorrelationId,
            TenantId = OperationContext.TenantId,
            ApiKeyId = OperationContext.ApiKeyId,
            UserId = OperationContext.UserId,
            SessionId = OperationContext.SessionId,
            ToolName = OperationContext.ToolName,
            Service = _service,
            Version = _version,
            Environment = _environment,
            Error = exception?.Message,
            ErrorType = exception?.GetType().Name,
            StackTrace = exception?.StackTrace,
            Extra = extra
        };

        var json = entry.ToJson();

        lock (_lock)
        {
            _output.WriteLine(json);
        }
    }

    public void Debug(string message, Dictionary<string, object>? extra = null) => Log("DEBUG", message, extra: extra);
    public void Info(string message, Dictionary<string, object>? extra = null) => Log("INFO", message, extra: extra);
    public void Warn(string message, Dictionary<string, object>? extra = null) => Log("WARN", message, extra: extra);
    public void Error(string message, Exception? ex = null, Dictionary<string, object>? extra = null) => Log("ERROR", message, ex, extra);
    public void Fatal(string message, Exception? ex = null, Dictionary<string, object>? extra = null) => Log("FATAL", message, ex, extra);

    /// <summary>
    /// Logs a tool invocation with timing.
    /// </summary>
    public void LogToolCall(string toolName, long durationMs, bool success, string? error = null)
    {
        var entry = new StructuredLogEntry
        {
            Level = success ? "INFO" : "ERROR",
            Message = $"Tool call: {toolName}",
            CorrelationId = OperationContext.CorrelationId,
            TenantId = OperationContext.TenantId,
            ApiKeyId = OperationContext.ApiKeyId,
            ToolName = toolName,
            DurationMs = durationMs,
            Success = success,
            Error = error,
            Service = _service,
            Version = _version,
            Environment = _environment
        };

        var json = entry.ToJson();

        lock (_lock)
        {
            _output.WriteLine(json);
        }
    }
}
