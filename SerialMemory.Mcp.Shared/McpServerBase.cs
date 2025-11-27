using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using SerialMemory.Core.Auth;
using SerialMemory.Core.Interfaces;
using SerialMemory.Core.Models;
using SerialMemory.Core.Services;
using SerialMemory.EventSourcing.Store;
using SerialMemory.Infrastructure;
using SerialMemory.ML;

namespace SerialMemory.Mcp.Shared;

/// <summary>
/// Base class for MCP servers providing shared configuration, services, and protocol handling.
/// Supports JWT authentication and tenant context management.
/// </summary>
public abstract class McpServerBase
{
    protected readonly ILogger Logger;
    protected readonly ILoggerFactory LoggerFactory;
    protected readonly IKnowledgeGraphStore Store;
    protected readonly IEmbeddingService EmbeddingService;
    protected readonly IEntityExtractionService EntityService;
    protected readonly KnowledgeGraphService KgService;
    protected readonly IEventStore EventStore;
    protected readonly NpgsqlDataSource VectorDataSource;
    protected readonly string ConnectionString;
    protected readonly IJwtAuthenticationService AuthService;
    protected readonly IUsageLimitService UsageLimitService;
    protected readonly IAdminAuditService AdminAuditService;
    protected readonly TenantContext TenantContext;
    protected readonly bool SelfHostedMode;

    protected readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    protected Guid? CurrentSessionId;
    protected AuthenticationResult? CurrentAuth;

    private readonly string _serverName;
    private readonly string _serverVersion;

    protected McpServerBase(string serverName, string serverVersion)
    {
        _serverName = serverName;
        _serverVersion = serverVersion;

        var configuration = new ConfigurationBuilder()
            .AddEnvironmentVariables()
            .Build();

        var postgresHost = configuration["POSTGRES_HOST"] ?? "localhost";
        var postgresPort = configuration["POSTGRES_PORT"] ?? "5432";
        var postgresUser = configuration["POSTGRES_USER"] ?? "postgres";
        var postgresPassword = configuration["POSTGRES_PASSWORD"] ?? "postgres";
        var postgresDb = configuration["POSTGRES_DB"] ?? "contextdb";

        var ollamaUrl = configuration["OLLAMA_URL"] ?? "http://localhost:11434";
        var ollamaModel = configuration["OLLAMA_MODEL"] ?? "nomic-embed-text";
        var ollamaEmbeddingDim = int.TryParse(configuration["OLLAMA_EMBEDDING_DIM"], out var dim) ? dim : 768;
        var ollamaEntityUrl = configuration["OLLAMA_ENTITY_URL"] ?? ollamaUrl;
        var ollamaEntityModel = configuration["OLLAMA_ENTITY_MODEL"] ?? "phi3";
        var extractionServiceUrl = configuration["EXTRACTION_SERVICE_URL"];

        // Authentication configuration
        SelfHostedMode = configuration["SERIALMEMORY_MODE"]?.ToLowerInvariant() == "self-hosted";
        var jwtOptions = JwtAuthenticationOptions.FromEnvironment();
        jwtOptions.SelfHostedMode = SelfHostedMode;

        ConnectionString = $"Host={postgresHost};Port={postgresPort};Database={postgresDb};Username={postgresUser};Password={postgresPassword}";

        var dataSourceBuilder = new NpgsqlDataSourceBuilder(ConnectionString);
        dataSourceBuilder.UseVector();
        VectorDataSource = dataSourceBuilder.Build();

        LoggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder =>
        {
            builder.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
            builder.SetMinimumLevel(LogLevel.Information);
        });
        Logger = LoggerFactory.CreateLogger(serverName);

        // Initialize authentication service
        AuthService = new JwtAuthenticationService(ConnectionString, jwtOptions);

        // Initialize usage limit service
        UsageLimitService = new UsageLimitService(
            ConnectionString,
            LoggerFactory.CreateLogger<UsageLimitService>());

        // Initialize admin audit service
        AdminAuditService = new AdminAuditService(
            ConnectionString,
            LoggerFactory.CreateLogger<AdminAuditService>());

        // Initialize tenant context (will be set during authentication)
        TenantContext = new TenantContext();

        // Create store with tenant context for RLS
        Store = new PostgresKnowledgeGraphStore(ConnectionString, TenantContext);
        EmbeddingService = new OllamaEmbeddingService(ollamaUrl, ollamaModel, ollamaEmbeddingDim);
        EntityService = EntityExtractionServiceFactory.Create(ollamaEntityUrl, ollamaEntityModel, extractionServiceUrl);
        KgService = new KnowledgeGraphService(Store, EmbeddingService, EntityService);
        EventStore = new PostgresEventStore(ConnectionString, LoggerFactory.CreateLogger<PostgresEventStore>());

        Logger.LogInformation("Initialized {ServerName} v{Version}", serverName, serverVersion);
        Logger.LogInformation("Database: {Host}:{Port}/{Database}", postgresHost, postgresPort, postgresDb);
        Logger.LogInformation("Mode: {Mode}", SelfHostedMode ? "self-hosted" : "saas");
    }

    /// <summary>
    /// Required scope for this MCP server. Override in derived classes.
    /// </summary>
    protected abstract string RequiredScope { get; }

    /// <summary>
    /// Authenticates the current request and sets tenant context.
    /// Both self-hosted and SaaS modes require valid API keys.
    /// Self-hosted mode skips billing/quota enforcement, not authentication.
    /// </summary>
    /// <param name="params">Request parameters that may contain authentication info.</param>
    /// <returns>Null if authentication succeeds, error response if it fails.</returns>
    protected async Task<object?> AuthenticateRequestAsync(JsonNode? @params)
    {
        // Try to get token from params or environment
        var token = @params?["_auth"]?["token"]?.GetValue<string>()
            ?? Environment.GetEnvironmentVariable("SERIALMEMORY_TOKEN");

        var apiKey = @params?["_auth"]?["api_key"]?.GetValue<string>()
            ?? Environment.GetEnvironmentVariable("SERIALMEMORY_API_KEY");

        AuthenticationResult authResult;

        if (!string.IsNullOrEmpty(token))
        {
            authResult = await AuthService.ValidateTokenAsync(token);
        }
        else if (!string.IsNullOrEmpty(apiKey))
        {
            authResult = await AuthService.ValidateApiKeyAsync(apiKey);
        }
        else
        {
            return CreateAuthErrorResponse(AuthenticationResult.MissingToken());
        }

        if (!authResult.IsValid)
        {
            return CreateAuthErrorResponse(authResult);
        }

        // Check required scope
        if (!authResult.HasScope(RequiredScope))
        {
            return CreateAuthErrorResponse(AuthenticationResult.InsufficientScope(RequiredScope));
        }

        // Set tenant context from authenticated claims
        TenantContext.SetContext(
            authResult.TenantId!.Value.ToString(),
            authResult.WorkspaceId ?? "default",
            authResult.UserId);

        CurrentAuth = authResult;
        return null;
    }

    /// <summary>
    /// Checks if the current user has admin privileges.
    /// </summary>
    protected bool IsAdmin => CurrentAuth?.IsAdmin ?? false;

    /// <summary>
    /// Checks if the current user has a specific scope.
    /// </summary>
    protected bool HasScope(string scope) => CurrentAuth?.HasScope(scope) ?? false;

    /// <summary>
    /// Creates an authentication error response.
    /// </summary>
    protected object CreateAuthErrorResponse(AuthenticationResult result) => new
    {
        content = new[]
        {
            new
            {
                type = "text",
                text = JsonSerializer.Serialize(new
                {
                    error = result.ErrorCode?.ToLowerInvariant() ?? "auth_error",
                    message = result.ErrorMessage ?? "Authentication failed"
                }, JsonOptions)
            }
        },
        isError = true
    };

    /// <summary>
    /// Maps tool names to usage event types for metering.
    /// </summary>
    protected static UsageEventType? GetUsageEventType(string? toolName) => toolName switch
    {
        "memory_ingest" => UsageEventType.MemoryIngest,
        "memory_search" => UsageEventType.MemorySearch,
        "memory_multi_hop_search" => UsageEventType.MemoryMultiHopSearch,
        "memory_update" => UsageEventType.MemoryUpdate,
        "memory_delete" => UsageEventType.MemoryDelete,
        "memory_merge" => UsageEventType.MemoryMerge,
        "memory_split" => UsageEventType.MemorySplit,
        "memory_decay" => UsageEventType.MemoryDecay,
        "memory_reinforce" => UsageEventType.MemoryReinforce,
        "memory_expire" => UsageEventType.MemoryExpire,
        "crawl_relationships" => UsageEventType.CrawlRelationships,
        "export_workspace" => UsageEventType.ExportWorkspace,
        "export_memories" => UsageEventType.ExportMemories,
        "export_graph" => UsageEventType.ExportGraph,
        "reembed_memories" => UsageEventType.ReembedMemories,
        _ => null
    };

    /// <summary>
    /// Enforces usage limits before tool execution.
    /// Returns an error response if limits are exceeded, otherwise null to proceed.
    /// </summary>
    /// <param name="toolName">The tool being called.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Error response if blocked, null if allowed.</returns>
    protected async Task<object?> EnforceUsageLimitsAsync(string? toolName, CancellationToken cancellationToken = default)
    {
        // Skip usage enforcement in self-hosted mode
        if (SelfHostedMode)
            return null;

        var eventType = GetUsageEventType(toolName);
        if (eventType == null)
            return null; // Unknown tool, allow by default

        var result = await UsageLimitService.CheckLimitsAsync(
            TenantContext.TenantId,
            TenantContext.WorkspaceId,
            eventType.Value,
            cancellationToken);

        if (!result.IsAllowed)
        {
            Logger.LogWarning(
                "Usage limit exceeded for tenant {TenantId}: {Code} - {Message}",
                TenantContext.TenantId,
                result.Violation?.Code,
                result.Violation?.Message);

            // Return structured error response
            return CreateUsageLimitErrorResponse(result);
        }

        // Record rate limit hit (for sliding window tracking)
        await UsageLimitService.RecordRateLimitHitAsync(
            TenantContext.TenantId,
            TenantContext.WorkspaceId,
            cancellationToken);

        return null; // Allowed
    }

    /// <summary>
    /// Creates a structured error response for usage limit violations.
    /// </summary>
    protected object CreateUsageLimitErrorResponse(UsageLimitCheckResult result)
    {
        var violation = result.Violation!;

        return new
        {
            content = new[]
            {
                new
                {
                    type = "text",
                    text = JsonSerializer.Serialize(new
                    {
                        error = violation.Code.ToLowerInvariant(),
                        plan = CurrentAuth?.TenantId?.ToString() ?? "unknown",
                        next_reset = violation.RetryAfter?.ToString("O"),
                        message = violation.Message,
                        details = violation.Details
                    }, JsonOptions)
                }
            },
            isError = true
        };
    }

    public async Task RunAsync()
    {
        await using var stdin = Console.OpenStandardInput();
        await using var stdout = Console.OpenStandardOutput();
        using var reader = new StreamReader(stdin);
        await using var writer = new StreamWriter(stdout) { AutoFlush = true };

        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            try
            {
                var request = JsonNode.Parse(line);
                if (request == null) continue;

                var id = request["id"];
                var method = request["method"]?.GetValue<string>();
                var @params = request["params"];

                Logger.LogDebug("Received: {Method}", method);

                object? result = method switch
                {
                    "initialize" => HandleInitialize(),
                    "notifications/initialized" => null,
                    "tools/list" => HandleToolsList(),
                    "resources/list" => HandleResourcesList(),
                    "resources/read" => await HandleResourcesRead(@params),
                    "tools/call" => await HandleToolsCall(@params),
                    _ => null
                };

                if (result != null)
                {
                    var response = JsonSerializer.Serialize(new
                    {
                        jsonrpc = "2.0",
                        id = id?.GetValue<object>(),
                        result
                    }, JsonOptions);

                    await writer.WriteLineAsync(response);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error processing request");
                await Console.Error.WriteLineAsync($"[MCP Error] {ex.Message}");
            }
        }
    }

    private object HandleInitialize() => new
    {
        protocolVersion = "2024-11-05",
        serverInfo = new { name = _serverName, version = _serverVersion },
        capabilities = new { tools = new { }, resources = new { } }
    };

    protected abstract object HandleToolsList();
    protected abstract Task<object> HandleToolsCall(JsonNode? @params);

    protected virtual object HandleResourcesList() => new
    {
        resources = new object[]
        {
            new { uri = "memory://recent", name = "Recent Memories", description = "Recently added memories", mimeType = "application/json" },
            new { uri = "memory://sessions", name = "Sessions", description = "Recent conversation sessions", mimeType = "application/json" }
        }
    };

    protected virtual async Task<object> HandleResourcesRead(JsonNode? @params)
    {
        var uri = @params?["uri"]?.GetValue<string>();

        if (uri == "memory://recent")
        {
            var results = await KgService.GetRecentMemoriesAsync(20, includeEntities: true);
            return new
            {
                contents = new[]
                {
                    new
                    {
                        uri = "memory://recent",
                        mimeType = "application/json",
                        text = JsonSerializer.Serialize(results.Select(r => new
                        {
                            id = r.Id, content = r.Content, created_at = r.CreatedAt.ToString("O"),
                            source = r.Source, entities = r.Entities.Select(e => new { name = e.Name, type = e.Type })
                        }), JsonOptions)
                    }
                }
            };
        }

        if (uri == "memory://sessions")
        {
            var sessions = await KgService.GetRecentSessionsAsync(20);
            return new
            {
                contents = new[]
                {
                    new
                    {
                        uri = "memory://sessions",
                        mimeType = "application/json",
                        text = JsonSerializer.Serialize(sessions.Select(s => new
                        {
                            id = s.Id, session_name = s.SessionName, started_at = s.StartedAt.ToString("O"),
                            ended_at = s.EndedAt?.ToString("O"), client_type = s.ClientType
                        }), JsonOptions)
                    }
                }
            };
        }

        throw new Exception($"Unknown resource URI: {uri}");
    }

    protected static object CreateTextResponse(string text) => new
    {
        content = new[] { new { type = "text", text } }
    };

    protected static object CreateErrorResponse(string message) => new
    {
        content = new[] { new { type = "text", text = $"Error: {message}" } },
        isError = true
    };
}
