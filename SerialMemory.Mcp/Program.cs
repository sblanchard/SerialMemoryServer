using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using SerialMemory.Core.Interfaces;
using SerialMemory.Core.Models;
using SerialMemory.Core.Services;
using SerialMemory.Infrastructure;
using SerialMemory.ML;
using SerialMemory.Mcp;
using SerialMemory.Mcp.Tools;
using SerialMemory.EventSourcing.Store;
using static SerialMemory.Mcp.McpResponseHelpers;

// MCP STDIO Server for Serial Memory - CORE-like Temporal Knowledge Graph
// Implements Model Context Protocol over STDIN/STDOUT
// Backed by PostgreSQL + pgvector for semantic search

#region Configuration

// Try multiple config sources - env vars may not be passed by MCP client
var configuration = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
    .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: true, reloadOnChange: false)
    .AddEnvironmentVariables()
    .Build();

var postgresHost = configuration["POSTGRES_HOST"] ?? "localhost";
var postgresPort = configuration["POSTGRES_PORT"] ?? "5432";
var postgresUser = configuration["POSTGRES_USER"] ?? "postgres";
var postgresPassword = configuration["POSTGRES_PASSWORD"] ?? "postgres";
var postgresDb = configuration["POSTGRES_DB"] ?? "contextdb";

// Embedding service configuration - OpenAI or Ollama
var openAiApiKey = configuration["OPENAI_API_KEY"] ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
var openAiEmbedModel = configuration["OPENAI_EMBED_MODEL"] ?? Environment.GetEnvironmentVariable("OPENAI_EMBED_MODEL") ?? "text-embedding-3-small";

// Ollama fallback
var ollamaUrl = configuration["OLLAMA_URL"] ?? "http://localhost:11434";
var ollamaModel = configuration["OLLAMA_MODEL"] ?? "nomic-embed-text";
var ollamaEmbeddingDim = int.TryParse(configuration["OLLAMA_EMBEDDING_DIM"], out var dim) ? dim : 768;

// Entity extraction service configuration
var disableOllamaEntity = configuration["DISABLE_OLLAMA_ENTITY"]?.ToLowerInvariant() is "true" or "1" or "yes";
var ollamaEntityUrl = disableOllamaEntity ? null : (configuration["OLLAMA_ENTITY_URL"] ?? ollamaUrl);
var ollamaEntityModel = configuration["OLLAMA_ENTITY_MODEL"] ?? "phi3";
var extractionServiceUrl = configuration["EXTRACTION_SERVICE_URL"];
DebugFileLogger.Log("MCP", $"Entity extraction: disableOllama={disableOllamaEntity}, ollamaEntityUrl={ollamaEntityUrl ?? "null"}");

// API key for SaaS authentication (required)
var apiKey = configuration["SERIALMEMORY_API_KEY"];

var connectionString = $"Host={postgresHost};Port={postgresPort};Database={postgresDb};Username={postgresUser};Password={postgresPassword}";

// Create a shared NpgsqlDataSource with vector type handler for reembed operations
var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
dataSourceBuilder.UseVector();
var vectorDataSource = dataSourceBuilder.Build();

// Configure logging
using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
    builder.SetMinimumLevel(LogLevel.Debug);
});
var logger = loggerFactory.CreateLogger("SerialMemory.Mcp");

// Use file logger for debugging MCP
DebugFileLogger.Clear();
DebugFileLogger.Log("MCP", $"=== MCP Server Starting ===");
DebugFileLogger.Log("MCP", $"Log file: {DebugFileLogger.GetLogFilePath()}");

// Log all environment variables to debug
DebugFileLogger.Log("MCP", "--- Environment Variables ---");
foreach (var key in new[] { "SERIALMEMORY_API_KEY", "POSTGRES_HOST", "POSTGRES_PORT", "POSTGRES_USER", "POSTGRES_DB", "OLLAMA_BASE_URL" })
{
    var value = Environment.GetEnvironmentVariable(key);
    DebugFileLogger.Log("MCP", $"  {key}={value ?? "(null)"}");
}
DebugFileLogger.Log("MCP", "--- End Environment Variables ---");

#endregion

#region Service Initialization

logger.LogInformation("Initializing SerialMemory MCP Server");
logger.LogInformation("Database: {Host}:{Port}/{Database}", postgresHost, postgresPort, postgresDb);

// Authenticate with API key to get tenant ID
DebugFileLogger.Log("MCP", $"Checking API key environment variable...");
if (string.IsNullOrEmpty(apiKey))
{
    DebugFileLogger.Log("MCP", "ERROR: SERIALMEMORY_API_KEY is not set");
    logger.LogError("SERIALMEMORY_API_KEY environment variable is required");
    await Console.Error.WriteLineAsync("[MCP Error] SERIALMEMORY_API_KEY environment variable is required");
    Environment.Exit(1);
    return;
}
DebugFileLogger.Log("MCP", "API key found: [redacted]");

var jwtOptions = JwtAuthenticationOptions.FromEnvironment();
var authService = new JwtAuthenticationService(connectionString, jwtOptions);

DebugFileLogger.Log("MCP", "Calling ValidateApiKeyAsync...");
logger.LogInformation("Authenticating with API key...");
var authResult = await authService.ValidateApiKeyAsync(apiKey);
DebugFileLogger.Log("MCP", $"Auth result: IsValid={authResult.IsValid}, TenantId={authResult.TenantId}, Error={authResult.ErrorMessage}");

if (!authResult.IsValid)
{
    logger.LogError("API key authentication failed: {Error}", authResult.ErrorMessage);
    await Console.Error.WriteLineAsync($"[MCP Error] API key authentication failed: {authResult.ErrorMessage}");
    Environment.Exit(1);
    return;
}

var tenantId = authResult.TenantId!.Value.ToString();
var userId = authResult.UserId;
logger.LogInformation("Authenticated as tenant {TenantId} (user: {UserId})", tenantId, userId);
DebugFileLogger.Log("MCP", $"Authenticated tenantId={tenantId}, userId={userId}");

// Initialize services with mutable tenant context (supports workspace switching)
var tenantContext = new TenantContext();
tenantContext.SetContext(tenantId, "default", userId);
DebugFileLogger.Log("MCP", $"Created TenantContext: TenantId={tenantContext.TenantId}, WorkspaceId={tenantContext.WorkspaceId}");
var tenantConnectionFactory = new TenantDbConnectionFactory(vectorDataSource, tenantContext);
IKnowledgeGraphStore store = new PostgresKnowledgeGraphStore(tenantConnectionFactory, tenantContext);
DebugFileLogger.Log("MCP", $"Created PostgresKnowledgeGraphStore (shared DataSource)");


// Create embedding service - OpenAI preferred, Ollama fallback
IEmbeddingService embeddingService;
if (!string.IsNullOrEmpty(openAiApiKey))
{
    var openAiClient = new OpenAiClient(
        apiKey: openAiApiKey,
        chatModel: "gpt-5-nano-2025-08-07",  // Not used for embeddings
        embedModel: openAiEmbedModel,
        embeddingDimension: 1536);
    embeddingService = openAiClient;
    logger.LogInformation("Using OpenAI embedding service: {Model} (dim=1536)", openAiEmbedModel);
    DebugFileLogger.Log("MCP", $"Using OpenAI for embeddings: {openAiEmbedModel}");
}
else
{
    embeddingService = new OllamaEmbeddingService(ollamaUrl, ollamaModel, ollamaEmbeddingDim);
    logger.LogInformation("Using local Ollama embedding service: {Model} at {Url} (dim={Dim})", ollamaModel, ollamaUrl, ollamaEmbeddingDim);
    DebugFileLogger.Log("MCP", $"Using Ollama for embeddings: {ollamaModel} (dim={ollamaEmbeddingDim})");
}

// Wrap embedding service with LRU cache to avoid re-embedding identical queries
embeddingService = new SerialMemory.ML.CachedEmbeddingService(embeddingService);
DebugFileLogger.Log("MCP", "Wrapped embedding service with LRU cache (512 entries, 5min sliding expiration)");

// Create entity extraction service (OpenAI > Ollama > HTTP > Pattern-based)
var openAiEntityModel = configuration["OPENAI_ENTITY_MODEL"] ?? Environment.GetEnvironmentVariable("OPENAI_ENTITY_MODEL");
IEntityExtractionService entityService = EntityExtractionServiceFactory.Create(
    openAiApiKey: openAiApiKey,
    openAiEntityModel: openAiEntityModel,
    ollamaUrl: ollamaEntityUrl,
    ollamaModel: ollamaEntityModel,
    httpServiceUrl: extractionServiceUrl);

logger.LogInformation("Entity extraction service: {Type}", entityService.GetType().Name);

var kgService = new KnowledgeGraphService(store, embeddingService, entityService);

// Initialize event store for lifecycle operations
IEventStore eventStore = new PostgresEventStore(vectorDataSource, loggerFactory.CreateLogger<PostgresEventStore>());

// Initialize tool handlers (gateway-registered)
var lifecycleTools = new MemoryLifecycleTools(eventStore, embeddingService, entityService, store, logger);
var observabilityTools = new MemoryObservabilityTools(eventStore, vectorDataSource, logger);
var safetyTools = new MemorySafetyTools(eventStore, embeddingService, vectorDataSource, logger);
var exportTools = new MemoryExportTools(eventStore, vectorDataSource, logger);

// Initialize engineering reasoning and visualization services
IEngineeringReasoningService reasoningService = new SerialMemory.Infrastructure.Services.EngineeringReasoningService(store);
IGraphVisualizationService visualizationService = new SerialMemory.Infrastructure.Services.GraphVisualizationService(store, reasoningService);
IReasoningModelFactory modelFactory = new SerialMemory.Infrastructure.Services.DefaultReasoningModelFactory(store, loggerFactory);
IMultiModelReasoningService multiModelService = new SerialMemory.Infrastructure.Services.MultiModelReasoningService(
    store, reasoningService, modelFactory, loggerFactory.CreateLogger<SerialMemory.Infrastructure.Services.MultiModelReasoningService>());
var reasoningTools = new EngineeringReasoningTools(reasoningService, visualizationService, multiModelService, logger);

// Session state (encapsulated for safe override/restore)
var sessionState = new McpSessionState();

// AsyncLocal for tool-specific metadata (e.g., dedup tracking)
var toolMetadataContext = new AsyncLocal<Dictionary<string, object>?>();

// Initialize extracted tool handler classes
var coreToolHandlers = new CoreToolHandlers(kgService, sessionState, toolMetadataContext);
var userProfileHandlers = new UserProfileToolHandlers(kgService, logger);
var goalHandlers = new GoalToolHandlers(kgService, logger);
var adminHandlers = new AdminToolHandlers(
    kgService, store, embeddingService, entityService, vectorDataSource,
    new EmbeddingModelConfig(openAiApiKey, openAiEmbedModel, ollamaModel, ollamaUrl), logger);

// Initialize workspace and snapshot tools
var workspaceTools = new WorkspaceTools(store, tenantContext, logger);
var snapshotTools = new SnapshotTools(store, tenantContext, logger, () => sessionState.CurrentSessionId);

// Initialize two-tool gateway and register all categorized tools
var gateway = new ToolGateway();

// Helper: register a category of tools from definitions and a handler dispatch dictionary
static void RegisterToolCategory(
    ToolGateway gw, string category, object[] tools,
    Dictionary<string, Func<JsonNode?, Task<object>>> handlers)
{
    foreach (var schema in tools)
    {
        var toolName = (string)((dynamic)schema).name;
        if (!handlers.TryGetValue(toolName, out var handler))
            throw new InvalidOperationException($"No handler registered for {category} tool: {toolName}");
        gw.Register(category, toolName, schema, handler);
    }
}

RegisterToolCategory(gateway, "lifecycle", ToolDefinitions.GetLifecycleTools(), new()
{
    ["memory_update"] = args => lifecycleTools.HandleMemoryUpdate(args),
    ["memory_delete"] = args => lifecycleTools.HandleMemoryDelete(args),
    ["memory_merge"] = args => lifecycleTools.HandleMemoryMerge(args),
    ["memory_split"] = args => lifecycleTools.HandleMemorySplit(args),
    ["memory_decay"] = args => lifecycleTools.HandleMemoryDecay(args),
    ["memory_reinforce"] = args => lifecycleTools.HandleMemoryReinforce(args),
    ["memory_expire"] = args => lifecycleTools.HandleMemoryExpire(args),
    ["memory_supersede"] = args => lifecycleTools.HandleMemorySupersede(args),
});

RegisterToolCategory(gateway, "observability", ToolDefinitions.GetObservabilityTools(), new()
{
    ["memory_trace"] = args => observabilityTools.HandleMemoryTrace(args),
    ["memory_lineage"] = args => observabilityTools.HandleMemoryLineage(args),
    ["memory_explain"] = args => observabilityTools.HandleMemoryExplain(args),
    ["memory_conflicts"] = args => observabilityTools.HandleMemoryConflicts(args),
});

RegisterToolCategory(gateway, "safety", ToolDefinitions.GetSafetyTools(), new()
{
    ["detect_contradictions"] = args => safetyTools.HandleDetectContradictions(args),
    ["detect_hallucinations"] = args => safetyTools.HandleDetectHallucinations(args),
    ["verify_memory_integrity"] = args => safetyTools.HandleVerifyIntegrity(args),
    ["scan_loops"] = args => safetyTools.HandleScanLoops(args),
});

RegisterToolCategory(gateway, "export", ToolDefinitions.GetExportTools(), new()
{
    ["export_workspace"] = args => exportTools.HandleExportWorkspace(args),
    ["export_memories"] = args => exportTools.HandleExportMemories(args),
    ["export_graph"] = args => exportTools.HandleExportGraph(args),
    ["export_user_profile"] = args => exportTools.HandleExportUserProfile(args),
    ["export_markdown"] = args => exportTools.HandleExportMarkdown(args),
});

RegisterToolCategory(gateway, "reasoning", ToolDefinitions.GetReasoningTools(), new()
{
    ["engineering_analyze"] = args => reasoningTools.HandleEngineeringAnalyze(args),
    ["engineering_visualize"] = args => reasoningTools.HandleEngineeringVisualize(args),
    ["engineering_reason"] = args => reasoningTools.HandleEngineeringReason(args),
});

// Register admin tools via gateway
gateway.Register("admin", "set_user_persona", new { name = "set_user_persona" }, args => userProfileHandlers.HandleSetUserPersona(args));
gateway.Register("admin", "get_integrations", new { name = "get_integrations" }, _ => adminHandlers.HandleGetIntegrations());
gateway.Register("admin", "import_from_core", new { name = "import_from_core" }, args => adminHandlers.HandleImportFromCore(args));
gateway.Register("admin", "crawl_relationships", new { name = "crawl_relationships" }, args => adminHandlers.HandleCrawlRelationships(args));
gateway.Register("admin", "get_graph_statistics", new { name = "get_graph_statistics" }, _ => adminHandlers.HandleGetGraphStatistics());
gateway.Register("admin", "get_model_info", new { name = "get_model_info" }, _ => Task.FromResult(adminHandlers.HandleGetModelInfo()));
gateway.Register("admin", "reembed_memories", new { name = "reembed_memories" }, args => adminHandlers.HandleReembedMemories(args));

// Register session tools via gateway
gateway.Register("session", "instantiate_context", new { name = "instantiate_context" }, args => adminHandlers.HandleInstantiateContext(args));

RegisterToolCategory(gateway, "workspace", ToolDefinitions.GetWorkspaceTools(), new()
{
    ["workspace_create"] = args => workspaceTools.HandleWorkspaceCreate(args),
    ["workspace_list"] = args => workspaceTools.HandleWorkspaceList(args),
    ["workspace_switch"] = args => workspaceTools.HandleWorkspaceSwitch(args),
    ["snapshot_create"] = args => snapshotTools.HandleSnapshotCreate(args),
    ["snapshot_list"] = args => snapshotTools.HandleSnapshotList(args),
    ["snapshot_load"] = args => snapshotTools.HandleSnapshotLoad(args),
});

RegisterToolCategory(gateway, "goals", ToolDefinitions.GetGoalTools(), new()
{
    ["goal_set"] = args => goalHandlers.HandleGoalSet(args),
    ["goal_list"] = args => goalHandlers.HandleGoalList(args),
    ["goal_complete"] = args => goalHandlers.HandleGoalComplete(args),
});

// Initialize progressive disclosure tools (P0 - GAP 2)
var progressiveDisclosureTools = new ProgressiveDisclosureTools(kgService, logger);

// Initialize auto-capture tools
var autoCaptureTools = new AutoCaptureTools(kgService, logger);

// Initialize LLM service for summarization (OpenAI preferred, Ollama fallback)
ILlmService? llmService = null;
SummarizationTools? summarizationTools = null;
try
{
    if (!string.IsNullOrEmpty(openAiApiKey))
    {
        var chatModel = configuration["OPENAI_CHAT_MODEL"] ?? Environment.GetEnvironmentVariable("OPENAI_CHAT_MODEL") ?? "gpt-4o-mini";
        llmService = new OpenAiClient(
            apiKey: openAiApiKey,
            chatModel: chatModel,
            embedModel: openAiEmbedModel,
            embeddingDimension: 1536);
        logger.LogInformation("LLM service initialized: OpenAI/{Model}", chatModel);
    }
    else
    {
        var ollamaChatModel = configuration["OLLAMA_CHAT_MODEL"] ?? Environment.GetEnvironmentVariable("OLLAMA_CHAT_MODEL") ?? "qwen2.5:7b";
        llmService = new OllamaLlmService(ollamaUrl, ollamaChatModel);
        logger.LogInformation("LLM service initialized: Ollama/{Model} at {Url}", ollamaChatModel, ollamaUrl);
    }

    summarizationTools = new SummarizationTools(kgService, llmService, logger);
    logger.LogInformation("Summarization tools initialized with {Provider}", llmService.ProviderName);
}
catch (Exception ex)
{
    logger.LogWarning(ex, "Failed to initialize LLM/summarization - summarization will be disabled");
}

// Wire up session handlers now that autoCaptureTools and summarizationTools are available
var sessionToolHandlers = new SessionToolHandlers(kgService, sessionState, autoCaptureTools, summarizationTools, logger);

RegisterToolCategory(gateway, "disclosure", ToolDefinitions.GetProgressiveDisclosureTools(), new()
{
    ["memory_search_index"] = args => progressiveDisclosureTools.HandleMemorySearchIndex(args),
    ["memory_timeline"] = args => progressiveDisclosureTools.HandleMemoryTimeline(args),
    ["memory_fetch"] = args => progressiveDisclosureTools.HandleMemoryFetch(args),
});

RegisterToolCategory(gateway, "capture", ToolDefinitions.GetCaptureTools(), new()
{
    ["drain_session_captures"] = args => autoCaptureTools.HandleDrainSessionCaptures(args),
    ["capture_status"] = args => autoCaptureTools.HandleCaptureStatus(args),
});

// Register summarization tools via gateway (if LLM available)
if (summarizationTools != null)
{
    RegisterToolCategory(gateway, "summarization", ToolDefinitions.GetSummarizationTools(), new()
    {
        ["summarize_session"] = args => summarizationTools.HandleSummarizeSession(args),
        ["summarize_context"] = args => summarizationTools.HandleSummarizeContext(args),
    });
}

// Initialize usage service (non-blocking metering) - use authenticated tenant context
var usageLogger = loggerFactory.CreateLogger<UsageService>();
using var usageService = new UsageService(connectionString, usageLogger, tenantId: tenantId, workspaceId: tenantContext.WorkspaceId);
logger.LogInformation("Usage metering service initialized for tenant {TenantId}", tenantId);

// Initialize usage tracker
var usageTracker = new McpToolUsageTracker(usageService, sessionState, logger);

logger.LogInformation("Services initialized successfully (v2.3 with lifecycle, observability, safety, export, reasoning, multi-model reasoning, usage metering)");

#endregion

#region MCP Protocol Loop

var jsonOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
};

// Build core tool definitions once — shared between HandleToolsList and HandleGetToolsInCategory
var coreTools = CoreToolDefinitions.GetCoreTools();

// Read JSON-RPC requests from STDIN, write responses to STDOUT
await using var stdin = Console.OpenStandardInput();
await using var stdout = Console.OpenStandardOutput();
using var reader = new StreamReader(stdin);
await using var writer = new StreamWriter(stdout) { AutoFlush = true };

while (await reader.ReadLineAsync() is { } line)
{
    if (string.IsNullOrWhiteSpace(line)) continue;

    try
    {
        var request = JsonNode.Parse(line);
        if (request == null) continue;

        var id = request["id"];
        var method = request["method"]?.GetValue<string>();
        var @params = request["params"];

        logger.LogDebug("Received request: {Method}", method);

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
            }, jsonOptions);

            await writer.WriteLineAsync(response);
            logger.LogDebug("Sent response for: {Method}", method);
        }
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Error processing request");
        await Console.Error.WriteLineAsync($"[MCP Error] {ex.Message}");
    }
}

#endregion

#region Protocol Handlers

object HandleInitialize() => new
{
    protocolVersion = "2024-11-05",
    serverInfo = new { name = "serialmemory-server", version = "2.1.0" },
    capabilities = new { tools = new { }, resources = new { } }
};

object HandleToolsList()
{
    var lazyMcpEnabled = configuration["LAZY_MCP_ENABLED"]?.ToLowerInvariant() is not ("false" or "0" or "no");

    if (lazyMcpEnabled)
    {
        var lazyToolNames = new HashSet<string>
        {
            "memory_search", "memory_ingest", "memory_multi_hop_search", "memory_about_user",
            "initialise_conversation_session", "end_conversation_session",
            "memory_search_index", "memory_timeline", "memory_fetch"
        };
        var lazyTools = coreTools.Where(t => lazyToolNames.Contains(((dynamic)t).name)).ToArray();
        var pdTools = ToolDefinitions.GetProgressiveDisclosureTools()
            .Where(t => lazyToolNames.Contains(((dynamic)t).name)).ToArray();
        var gatewayTools = ToolDefinitions.GetGatewayTools();
        return new { tools = lazyTools.Concat(pdTools).Concat(gatewayTools).ToArray() };
    }

    return new
    {
        tools = coreTools
            .Concat(ToolDefinitions.GetLifecycleTools())
            .Concat(ToolDefinitions.GetObservabilityTools())
            .Concat(ToolDefinitions.GetSafetyTools())
            .Concat(ToolDefinitions.GetExportTools())
            .Concat(ToolDefinitions.GetReasoningTools())
            .Concat(ToolDefinitions.GetWorkspaceTools())
            .ToArray()
    };
}

object HandleResourcesList() => new
{
    resources = new object[]
    {
        new { uri = "memory://recent", name = "Recent Memories", description = "List of recently added memories", mimeType = "application/json" },
        new { uri = "memory://sessions", name = "Conversation Sessions", description = "List of recent conversation sessions", mimeType = "application/json" }
    }
};

async Task<object> HandleResourcesRead(JsonNode? @params)
{
    var uri = @params?["uri"]?.GetValue<string>();

    if (uri == "memory://recent")
    {
        var results = await kgService.GetRecentMemoriesAsync(20, includeEntities: true);
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
                        id = r.Id,
                        content = r.Content,
                        created_at = r.CreatedAt.ToString("O"),
                        source = r.Source,
                        entities = r.Entities.Select(e => new { name = e.Name, type = e.Type })
                    }), jsonOptions)
                }
            }
        };
    }

    if (uri == "memory://sessions")
    {
        var sessions = await kgService.GetRecentSessionsAsync(20);
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
                        id = s.Id,
                        session_name = s.SessionName,
                        started_at = s.StartedAt.ToString("O"),
                        ended_at = s.EndedAt?.ToString("O"),
                        client_type = s.ClientType
                    }), jsonOptions)
                }
            }
        };
    }

    throw new Exception($"Unknown resource URI: {uri}");
}

#endregion

#region Tool Dispatch

async Task<object> HandleToolsCall(JsonNode? @params)
{
    var toolName = @params?["name"]?.GetValue<string>();
    var arguments = @params?["arguments"];
    var sw = Stopwatch.StartNew();
    var success = true;
    string? errorMessage = null;
    var trackedToolName = toolName;

    // Reset metadata context for this tool call
    toolMetadataContext.Value = null;

    // --- Context envelope extraction ---
    string? savedWorkspaceId = null;
    McpSessionState.SessionOverride? sessionOverride = null;

    try
    {
        if (arguments?["context"] is JsonNode contextNode)
        {
            var callContext = new CallContext
            {
                WorkspaceId = contextNode["workspace_id"]?.GetValue<string>()?.Trim(),
                SessionId = contextNode["session_id"]?.GetValue<string>()?.Trim(),
                Memory = contextNode["memory"]?.GetValue<string>()?.Trim(),
                Goal = contextNode["goal"]?.GetValue<string>()?.Trim(),
                Constraints = contextNode["constraints"]?.GetValue<string>()?.Trim()
            };

            // Override workspace if provided
            if (!string.IsNullOrEmpty(callContext.WorkspaceId))
            {
                savedWorkspaceId = tenantContext.WorkspaceId;
                tenantContext.SetContext(
                    tenantContext.TenantId,
                    callContext.WorkspaceId,
                    tenantContext.UserId,
                    tenantContext.UserEmail,
                    tenantContext.UserRole,
                    tenantContext.SessionId,
                    tenantContext.IsLabMode,
                    tenantContext.AllowPowerMode,
                    tenantContext.IsRootAdmin,
                    tenantContext.Scopes);
                logger.LogDebug("Context envelope: workspace overridden to {WorkspaceId}", callContext.WorkspaceId);
            }

            // Override session if provided
            if (!string.IsNullOrEmpty(callContext.SessionId) && Guid.TryParse(callContext.SessionId, out var overrideSessionId))
            {
                sessionOverride = sessionState.OverrideSession(overrideSessionId);
                logger.LogDebug("Context envelope: session overridden to {SessionId}", callContext.SessionId);
            }

            // Store memory/goal/constraints as session metadata if session is active
            if (sessionState.CurrentSessionId.HasValue && (callContext.Memory != null || callContext.Goal != null || callContext.Constraints != null))
            {
                var currentSid = sessionState.CurrentSessionId.Value;
                var sessionMeta = new Dictionary<string, object>();
                if (callContext.Memory != null) sessionMeta["memory"] = callContext.Memory;
                if (callContext.Goal != null) sessionMeta["goal"] = callContext.Goal;
                if (callContext.Constraints != null) sessionMeta["constraints"] = callContext.Constraints;
                _ = Task.Run(async () =>
                {
                    try { await store.UpdateSessionMetadataAsync(currentSid, sessionMeta); }
                    catch (Exception ex) { logger.LogWarning(ex, "Failed to update session metadata"); }
                });
            }
        }

        // --- Tool dispatch ---
        var result = toolName switch
        {
            // Core tools
            "memory_search" => await coreToolHandlers.HandleMemorySearch(arguments),
            "memory_ingest" => await coreToolHandlers.HandleMemoryIngest(arguments),
            "memory_about_user" => await userProfileHandlers.HandleMemoryAboutUser(arguments),
            "initialise_conversation_session" => await sessionToolHandlers.HandleInitialiseSession(arguments),
            "end_conversation_session" => await sessionToolHandlers.HandleEndSession(),
            "memory_multi_hop_search" => await coreToolHandlers.HandleMultiHopSearch(arguments),

            // Progressive disclosure tools (P0 - GAP 2)
            "memory_search_index" => await progressiveDisclosureTools.HandleMemorySearchIndex(arguments),
            "memory_timeline" => await progressiveDisclosureTools.HandleMemoryTimeline(arguments),
            "memory_fetch" => await progressiveDisclosureTools.HandleMemoryFetch(arguments),

            // Gateway meta-tools
            "get_tools" => gateway.HandleGetTools(arguments),
            "use_tool" => await HandleUseToolViaGateway(arguments),

            // Legacy meta-tools (kept for backward compat)
            "get_tools_in_category" => HandleGetToolsInCategory(arguments),
            "execute_tool" => await HandleExecuteTool(arguments),

            // Fallback: try gateway for any registered tool
            _ => await HandleViaGatewayFallback(toolName!, arguments)
        };

        return result;
    }
    catch (Exception ex)
    {
        success = false;
        errorMessage = ex.Message;
        logger.LogError(ex, "Error executing tool {ToolName}", toolName);
        return CreateErrorResponse(ex.Message);
    }
    finally
    {
        sw.Stop();

        // Restore workspace if overridden
        if (savedWorkspaceId != null)
        {
            tenantContext.SetContext(
                tenantContext.TenantId,
                savedWorkspaceId,
                tenantContext.UserId,
                tenantContext.UserEmail,
                tenantContext.UserRole,
                tenantContext.SessionId,
                tenantContext.IsLabMode,
                tenantContext.AllowPowerMode,
                tenantContext.IsRootAdmin,
                tenantContext.Scopes);
        }

        // Restore session if overridden
        sessionOverride?.Dispose();

        // Track usage (non-blocking)
        usageTracker.Track(trackedToolName, (int)sw.ElapsedMilliseconds, success, errorMessage, toolMetadataContext.Value);
    }
}

async Task<object> HandleUseToolViaGateway(JsonNode? arguments)
{
    var (result, innerToolName) = await gateway.HandleUseTool(arguments);
    usageTracker.Track(innerToolName, 0, true, null);
    return result;
}

async Task<object> HandleViaGatewayFallback(string toolName, JsonNode? arguments)
{
    try
    {
        var wrappedArgs = new JsonObject
        {
            ["tool_name"] = toolName,
            ["arguments"] = arguments?.DeepClone()
        };
        var (result, _) = await gateway.HandleUseTool(wrappedArgs);
        return result;
    }
    catch (ArgumentException)
    {
        throw new Exception($"Unknown tool: {toolName}");
    }
}

object HandleGetToolsInCategory(JsonNode? arguments)
{
    var path = arguments?["path"]?.GetValue<string>()?.Trim()?.ToLowerInvariant() ?? "";

    if (string.IsNullOrEmpty(path))
    {
        var text = "## SerialMemory Tool Categories\n\n";
        foreach (var (key, info) in ToolHierarchy.CategoriesOrdered)
        {
            var toolCount = ToolHierarchy.ToolMap.Keys.Count(k => k.StartsWith(key + "."));
            text += $"- **{key}** ({toolCount} tools) — {info.Description}\n";
        }
        text += "\nUse `get_tools_in_category` with a category name to see available tools and their parameters.";
        return CreateTextResponse(text);
    }

    if (!ToolHierarchy.Categories.TryGetValue(path, out var categoryInfo))
        return CreateErrorResponse($"Unknown category: {path}. Available: {string.Join(", ", ToolHierarchy.Categories.Keys)}");

    var tools = ToolHierarchy.GetToolsForCategory(path, coreTools);
    return CreateTextResponse(
        $"## {categoryInfo.Title}\n{categoryInfo.Description}\n\n" +
        $"**{tools.Length} tools available.** Use `execute_tool` with path `{path}.<tool_name>` to execute.\n\n" +
        JsonSerializer.Serialize(tools, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        }));
}

async Task<object> HandleExecuteTool(JsonNode? arguments)
{
    var toolPath = arguments?["tool_path"]?.GetValue<string>()?.Trim()?.ToLowerInvariant();
    if (string.IsNullOrEmpty(toolPath))
        throw new ArgumentException("tool_path is required (e.g. 'lifecycle.memory_update')");

    if (toolPath is "meta.execute_tool" or "meta.get_tools_in_category")
        throw new ArgumentException("Cannot invoke meta-tools via execute_tool");

    if (!ToolHierarchy.ToolMap.TryGetValue(toolPath, out var actualToolName))
        throw new ArgumentException($"Unknown tool path: {toolPath}. Use get_tools_in_category to discover available tools.");

    var toolArguments = arguments?["arguments"];

    logger.LogInformation("→ execute_tool: {Path} → {Tool}", toolPath, actualToolName);

    var sw = Stopwatch.StartNew();
    var success = true;
    string? errorMessage = null;
    try
    {
        var result = DispatchTool(actualToolName, toolArguments);
        if (result == null)
            throw new Exception($"Unknown tool: {actualToolName}");
        return await result;
    }
    catch (Exception ex)
    {
        success = false;
        var msg = string.IsNullOrWhiteSpace(ex.Message)
            ? $"Tool '{actualToolName}' failed unexpectedly. Check server logs for details."
            : ex.Message;
        errorMessage = msg;
        logger.LogError(ex, "Error executing tool {ToolName} via execute_tool", actualToolName);
        return CreateErrorResponse(msg);
    }
    finally
    {
        sw.Stop();
        usageTracker.Track(actualToolName, (int)sw.ElapsedMilliseconds, success, errorMessage);
    }
}

Task<object>? DispatchTool(string toolName, JsonNode? arguments) => toolName switch
{
    // Core tools
    "memory_search" => coreToolHandlers.HandleMemorySearch(arguments),
    "memory_ingest" => coreToolHandlers.HandleMemoryIngest(arguments),
    "memory_about_user" => userProfileHandlers.HandleMemoryAboutUser(arguments),
    "initialise_conversation_session" => sessionToolHandlers.HandleInitialiseSession(arguments),
    "end_conversation_session" => sessionToolHandlers.HandleEndSession(),
    "memory_multi_hop_search" => coreToolHandlers.HandleMultiHopSearch(arguments),
    // Admin tools
    "get_integrations" => adminHandlers.HandleGetIntegrations(),
    "import_from_core" => adminHandlers.HandleImportFromCore(arguments),
    "set_user_persona" => userProfileHandlers.HandleSetUserPersona(arguments),
    "crawl_relationships" => adminHandlers.HandleCrawlRelationships(arguments),
    "get_graph_statistics" => adminHandlers.HandleGetGraphStatistics(),
    "get_model_info" => Task.FromResult(adminHandlers.HandleGetModelInfo()),
    "reembed_memories" => adminHandlers.HandleReembedMemories(arguments),
    "instantiate_context" => adminHandlers.HandleInstantiateContext(arguments),
    // Lifecycle tools
    "memory_update" => lifecycleTools.HandleMemoryUpdate(arguments),
    "memory_delete" => lifecycleTools.HandleMemoryDelete(arguments),
    "memory_merge" => lifecycleTools.HandleMemoryMerge(arguments),
    "memory_split" => lifecycleTools.HandleMemorySplit(arguments),
    "memory_decay" => lifecycleTools.HandleMemoryDecay(arguments),
    "memory_reinforce" => lifecycleTools.HandleMemoryReinforce(arguments),
    "memory_expire" => lifecycleTools.HandleMemoryExpire(arguments),
    "memory_supersede" => lifecycleTools.HandleMemorySupersede(arguments),
    // Observability tools
    "memory_trace" => observabilityTools.HandleMemoryTrace(arguments),
    "memory_lineage" => observabilityTools.HandleMemoryLineage(arguments),
    "memory_explain" => observabilityTools.HandleMemoryExplain(arguments),
    "memory_conflicts" => observabilityTools.HandleMemoryConflicts(arguments),
    // Safety tools
    "detect_contradictions" => safetyTools.HandleDetectContradictions(arguments),
    "detect_hallucinations" => safetyTools.HandleDetectHallucinations(arguments),
    "verify_memory_integrity" => safetyTools.HandleVerifyIntegrity(arguments),
    "scan_loops" => safetyTools.HandleScanLoops(arguments),
    // Export tools
    "export_workspace" => exportTools.HandleExportWorkspace(arguments),
    "export_memories" => exportTools.HandleExportMemories(arguments),
    "export_graph" => exportTools.HandleExportGraph(arguments),
    "export_user_profile" => exportTools.HandleExportUserProfile(arguments),
    "export_markdown" => exportTools.HandleExportMarkdown(arguments),
    // Engineering reasoning tools
    "engineering_analyze" => reasoningTools.HandleEngineeringAnalyze(arguments),
    "engineering_visualize" => reasoningTools.HandleEngineeringVisualize(arguments),
    "engineering_reason" => reasoningTools.HandleEngineeringReason(arguments),
    // Goal tools
    "goal_set" => goalHandlers.HandleGoalSet(arguments),
    "goal_list" => goalHandlers.HandleGoalList(arguments),
    "goal_complete" => goalHandlers.HandleGoalComplete(arguments),
    // Workspace tools
    "workspace_create" => workspaceTools.HandleWorkspaceCreate(arguments),
    "workspace_list" => workspaceTools.HandleWorkspaceList(arguments),
    "workspace_switch" => workspaceTools.HandleWorkspaceSwitch(arguments),
    "snapshot_create" => snapshotTools.HandleSnapshotCreate(arguments),
    "snapshot_list" => snapshotTools.HandleSnapshotList(arguments),
    "snapshot_load" => snapshotTools.HandleSnapshotLoad(arguments),
    // Progressive disclosure tools
    "memory_search_index" => progressiveDisclosureTools.HandleMemorySearchIndex(arguments),
    "memory_timeline" => progressiveDisclosureTools.HandleMemoryTimeline(arguments),
    "memory_fetch" => progressiveDisclosureTools.HandleMemoryFetch(arguments),
    // Auto-capture tools
    "drain_session_captures" => autoCaptureTools.HandleDrainSessionCaptures(arguments),
    "capture_status" => autoCaptureTools.HandleCaptureStatus(arguments),
    // Summarization tools
    "summarize_session" => summarizationTools?.HandleSummarizeSession(arguments) ?? Task.FromResult<object>(CreateTextResponse("No LLM configured for summarization.")),
    "summarize_context" => summarizationTools?.HandleSummarizeContext(arguments) ?? Task.FromResult<object>(CreateTextResponse("No LLM configured for summarization.")),
    _ => null
};

#endregion
