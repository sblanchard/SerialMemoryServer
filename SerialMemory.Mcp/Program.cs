using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using Pgvector;
using SerialMemory.Core.Interfaces;
using SerialMemory.Core.Models;
using SerialMemory.Core.Services;
using SerialMemory.Infrastructure;
using SerialMemory.ML;
using SerialMemory.Mcp.Tools;
using SerialMemory.EventSourcing.Store;

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
// Option 1: Ollama (recommended) - uses local LLM for accurate extraction
// Option 2: HTTP service (legacy) - set EXTRACTION_SERVICE_URL
// Option 3: Pattern-based (default fallback, no external dependencies)
// Set DISABLE_OLLAMA_ENTITY=true to skip local Ollama for entity extraction (useful when using cloud embeddings)
var disableOllamaEntity = configuration["DISABLE_OLLAMA_ENTITY"]?.ToLowerInvariant() is "true" or "1" or "yes";
var ollamaEntityUrl = disableOllamaEntity ? null : (configuration["OLLAMA_ENTITY_URL"] ?? ollamaUrl);
var ollamaEntityModel = configuration["OLLAMA_ENTITY_MODEL"] ?? "phi3"; // phi3 is good for extraction
var extractionServiceUrl = configuration["EXTRACTION_SERVICE_URL"];     // Legacy HTTP service
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
DebugFileLogger.Log("MCP", $"API key found: {apiKey[..8]}***");

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
IKnowledgeGraphStore store = new PostgresKnowledgeGraphStore(connectionString, tenantContext);
DebugFileLogger.Log("MCP", $"Created PostgresKnowledgeGraphStore");


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
IEventStore eventStore = new PostgresEventStore(connectionString, loggerFactory.CreateLogger<PostgresEventStore>());

// Initialize tool handlers
var lifecycleTools = new MemoryLifecycleTools(eventStore, embeddingService, entityService, store, logger);
var observabilityTools = new MemoryObservabilityTools(eventStore, connectionString, logger);
var safetyTools = new MemorySafetyTools(eventStore, embeddingService, connectionString, logger);
var exportTools = new MemoryExportTools(eventStore, connectionString, logger);

// Initialize engineering reasoning and visualization services
IEngineeringReasoningService reasoningService = new SerialMemory.Infrastructure.Services.EngineeringReasoningService(store);
IGraphVisualizationService visualizationService = new SerialMemory.Infrastructure.Services.GraphVisualizationService(store, reasoningService);
IReasoningModelFactory modelFactory = new SerialMemory.Infrastructure.Services.DefaultReasoningModelFactory(store, loggerFactory);
IMultiModelReasoningService multiModelService = new SerialMemory.Infrastructure.Services.MultiModelReasoningService(
    store, reasoningService, modelFactory, loggerFactory.CreateLogger<SerialMemory.Infrastructure.Services.MultiModelReasoningService>());
var reasoningTools = new EngineeringReasoningTools(reasoningService, visualizationService, multiModelService, logger);

// Session state (declared early so snapshot tools can reference it via closure)
Guid? currentSessionId = null;

// Initialize workspace and snapshot tools
var workspaceTools = new WorkspaceTools(store, tenantContext, logger);
var snapshotTools = new SnapshotTools(store, tenantContext, logger, () => currentSessionId);

// Initialize two-tool gateway and register all categorized tools
var gateway = new ToolGateway();

// Register lifecycle tools
foreach (var schema in ToolDefinitions.GetLifecycleTools())
{
    var name = ((dynamic)schema).name;
    gateway.Register("lifecycle", (string)name, schema, name switch
    {
        "memory_update" => (args) => lifecycleTools.HandleMemoryUpdate(args),
        "memory_delete" => (args) => lifecycleTools.HandleMemoryDelete(args),
        "memory_merge" => (args) => lifecycleTools.HandleMemoryMerge(args),
        "memory_split" => (args) => lifecycleTools.HandleMemorySplit(args),
        "memory_decay" => (args) => lifecycleTools.HandleMemoryDecay(args),
        "memory_reinforce" => (args) => lifecycleTools.HandleMemoryReinforce(args),
        "memory_expire" => (args) => lifecycleTools.HandleMemoryExpire(args),
        "memory_supersede" => (args) => lifecycleTools.HandleMemorySupersede(args),
        _ => throw new InvalidOperationException($"Unknown lifecycle tool: {name}")
    });
}

// Register observability tools
foreach (var schema in ToolDefinitions.GetObservabilityTools())
{
    var name = ((dynamic)schema).name;
    gateway.Register("observability", (string)name, schema, name switch
    {
        "memory_trace" => (args) => observabilityTools.HandleMemoryTrace(args),
        "memory_lineage" => (args) => observabilityTools.HandleMemoryLineage(args),
        "memory_explain" => (args) => observabilityTools.HandleMemoryExplain(args),
        "memory_conflicts" => (args) => observabilityTools.HandleMemoryConflicts(args),
        _ => throw new InvalidOperationException($"Unknown observability tool: {name}")
    });
}

// Register safety tools
foreach (var schema in ToolDefinitions.GetSafetyTools())
{
    var name = ((dynamic)schema).name;
    gateway.Register("safety", (string)name, schema, name switch
    {
        "detect_contradictions" => (args) => safetyTools.HandleDetectContradictions(args),
        "detect_hallucinations" => (args) => safetyTools.HandleDetectHallucinations(args),
        "verify_memory_integrity" => (args) => safetyTools.HandleVerifyIntegrity(args),
        "scan_loops" => (args) => safetyTools.HandleScanLoops(args),
        _ => throw new InvalidOperationException($"Unknown safety tool: {name}")
    });
}

// Register export tools
foreach (var schema in ToolDefinitions.GetExportTools())
{
    var name = ((dynamic)schema).name;
    gateway.Register("export", (string)name, schema, name switch
    {
        "export_workspace" => (args) => exportTools.HandleExportWorkspace(args),
        "export_memories" => (args) => exportTools.HandleExportMemories(args),
        "export_graph" => (args) => exportTools.HandleExportGraph(args),
        "export_user_profile" => (args) => exportTools.HandleExportUserProfile(args),
        "export_markdown" => (args) => exportTools.HandleExportMarkdown(args),
        _ => throw new InvalidOperationException($"Unknown export tool: {name}")
    });
}

// Register reasoning tools
foreach (var schema in ToolDefinitions.GetReasoningTools())
{
    var name = ((dynamic)schema).name;
    gateway.Register("reasoning", (string)name, schema, name switch
    {
        "engineering_analyze" => (args) => reasoningTools.HandleEngineeringAnalyze(args),
        "engineering_visualize" => (args) => reasoningTools.HandleEngineeringVisualize(args),
        "engineering_reason" => (args) => reasoningTools.HandleEngineeringReason(args),
        _ => throw new InvalidOperationException($"Unknown reasoning tool: {name}")
    });
}

// Register admin tools via gateway
gateway.Register("admin", "set_user_persona", new { name = "set_user_persona" }, HandleSetUserPersona);
gateway.Register("admin", "get_integrations", new { name = "get_integrations" }, _ => Task.FromResult(HandleGetIntegrations()));
gateway.Register("admin", "import_from_core", new { name = "import_from_core" }, HandleImportFromCore);
gateway.Register("admin", "crawl_relationships", new { name = "crawl_relationships" }, HandleCrawlRelationships);
gateway.Register("admin", "get_graph_statistics", new { name = "get_graph_statistics" }, _ => HandleGetGraphStatistics());
gateway.Register("admin", "get_model_info", new { name = "get_model_info" }, _ => Task.FromResult(HandleGetModelInfo()));
gateway.Register("admin", "reembed_memories", new { name = "reembed_memories" }, HandleReembedMemories);

// Register session tools via gateway
gateway.Register("session", "instantiate_context", new { name = "instantiate_context" }, HandleInstantiateContext);

// Register workspace tools via gateway
foreach (var schema in ToolDefinitions.GetWorkspaceTools())
{
    var name = ((dynamic)schema).name;
    gateway.Register("workspace", (string)name, schema, name switch
    {
        "workspace_create" => (args) => workspaceTools.HandleWorkspaceCreate(args),
        "workspace_list" => (args) => workspaceTools.HandleWorkspaceList(args),
        "workspace_switch" => (args) => workspaceTools.HandleWorkspaceSwitch(args),
        "snapshot_create" => (args) => snapshotTools.HandleSnapshotCreate(args),
        "snapshot_list" => (args) => snapshotTools.HandleSnapshotList(args),
        "snapshot_load" => (args) => snapshotTools.HandleSnapshotLoad(args),
        _ => throw new InvalidOperationException($"Unknown workspace tool: {name}")
    });
}

// Register goal tools via gateway
foreach (var schema in ToolDefinitions.GetGoalTools())
{
    var name = ((dynamic)schema).name;
    gateway.Register("goals", (string)name, schema, name switch
    {
        "goal_set" => (args) => HandleGoalSet(args),
        "goal_list" => (args) => HandleGoalList(args),
        "goal_complete" => (args) => HandleGoalComplete(args),
        _ => throw new InvalidOperationException($"Unknown goal tool: {name}")
    });
}

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

// Register auto-capture tools via gateway
foreach (var schema in ToolDefinitions.GetCaptureTools())
{
    var name = ((dynamic)schema).name;
    gateway.Register("capture", (string)name, schema, name switch
    {
        "drain_session_captures" => (args) => autoCaptureTools.HandleDrainSessionCaptures(args),
        "capture_status" => (args) => autoCaptureTools.HandleCaptureStatus(args),
        _ => throw new InvalidOperationException($"Unknown capture tool: {name}")
    });
}

// Register summarization tools via gateway (if LLM available)
if (summarizationTools != null)
{
    foreach (var schema in ToolDefinitions.GetSummarizationTools())
    {
        var name = ((dynamic)schema).name;
        gateway.Register("summarization", (string)name, schema, name switch
        {
            "summarize_session" => (args) => summarizationTools.HandleSummarizeSession(args),
            "summarize_context" => (args) => summarizationTools.HandleSummarizeContext(args),
            _ => throw new InvalidOperationException($"Unknown summarization tool: {name}")
        });
    }
}

// Initialize usage service (non-blocking metering) - use authenticated tenant context
var usageLogger = loggerFactory.CreateLogger<UsageService>();
using var usageService = new UsageService(connectionString, usageLogger, tenantId: tenantId, workspaceId: tenantContext.WorkspaceId);
logger.LogInformation("Usage metering service initialized for tenant {TenantId}", tenantId);

// AsyncLocal for tool-specific metadata (e.g., dedup tracking)
var toolMetadataContext = new AsyncLocal<Dictionary<string, object>?>();

logger.LogInformation("Services initialized successfully (v2.3 with lifecycle, observability, safety, export, reasoning, multi-model reasoning, usage metering)");

#endregion

#region MCP Protocol Handlers

var jsonOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
};

// Build core tool definitions once — shared between HandleToolsList and HandleGetToolsInCategory
var coreTools = BuildCoreTools();

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
            "notifications/initialized" => null, // Notification, no response needed
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

object HandleInitialize()
{
    return new
    {
        protocolVersion = "2024-11-05",
        serverInfo = new
        {
            name = "serialmemory-server",
            version = "2.0.0"
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
    var lazyMcpEnabled = configuration["LAZY_MCP_ENABLED"]?.ToLowerInvariant() is not ("false" or "0" or "no");

    return lazyMcpEnabled ? BuildLazyToolsList() : BuildFullToolsList();
}

object BuildLazyToolsList()
{
    // Core tools always listed directly
    var lazyToolNames = new HashSet<string>
    {
        "memory_search", "memory_ingest", "memory_multi_hop_search", "memory_about_user",
        "initialise_conversation_session", "end_conversation_session"
    };
    var lazyTools = coreTools.Where(t => lazyToolNames.Contains(((dynamic)t).name)).ToArray();
    // Add gateway meta-tools
    var gatewayTools = ToolDefinitions.GetGatewayTools();
    var allTools = lazyTools.Concat(gatewayTools).ToArray();
    return new { tools = allTools };
}

object BuildFullToolsList()
{
    var allTools = coreTools
        .Concat(ToolDefinitions.GetLifecycleTools())
        .Concat(ToolDefinitions.GetObservabilityTools())
        .Concat(ToolDefinitions.GetSafetyTools())
        .Concat(ToolDefinitions.GetExportTools())
        .Concat(ToolDefinitions.GetReasoningTools())
        .Concat(ToolDefinitions.GetWorkspaceTools())
        .ToArray();
    return new { tools = allTools };
}

object[] BuildCoreTools() => new object[]
{
            // memory_search
            new
            {
                name = "memory_search",
                description = "Search for relevant memories using semantic search, full-text search, or both. Returns memories with entities and temporal context.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        query = new { type = "string", description = "Search query (natural language)" },
                        mode = new { type = "string", @enum = new[] { "semantic", "text", "hybrid" }, @default = "hybrid", description = "Search mode" },
                        limit = new { type = "integer", @default = 10, description = "Maximum results to return" },
                        threshold = new { type = "number", @default = 0.7, description = "Minimum similarity threshold (0.0-1.0)" },
                        include_entities = new { type = "boolean", @default = true, description = "Include linked entities" },
                        memory_type = new { type = "string", @enum = new[] { "error", "decision", "pattern", "learning", "knowledge", "session_summary", "auto_capture" }, description = "Filter by memory type (omit for all types)" }
                    },
                    required = new[] { "query" }
                }
            },
            // memory_ingest
            new
            {
                name = "memory_ingest",
                description = "Add a new memory (episode) to the knowledge graph. Automatically extracts entities, relationships, and generates embeddings.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        content = new { type = "string", description = "Memory content to store" },
                        source = new { type = "string", description = "Source of the memory (e.g., 'claude-desktop', 'cursor')" },
                        metadata = new { type = "object", description = "Additional metadata (tags, importance, etc.)" },
                        extract_entities = new { type = "boolean", @default = true, description = "Whether to extract entities and relationships" },
                        dedup_mode = new { type = "string", @enum = new[] { "warn", "skip", "append", "off" }, @default = "warn", description = "Dedup mode: warn (create+report), skip (reject if dup), append (merge into existing), off (no check)" },
                        dedup_threshold = new { type = "number", @default = 0.85, description = "Similarity threshold for duplicate detection (0.0-1.0)" },
                        memory_type = new { type = "string", @enum = new[] { "error", "decision", "pattern", "learning", "knowledge", "session_summary", "auto_capture" }, @default = "knowledge", description = "Memory type for categorization and filtered retrieval" }
                    },
                    required = new[] { "content" }
                }
            },
            // memory_about_user
            new
            {
                name = "memory_about_user",
                description = "Retrieve structured information about the user's persona, preferences, skills, goals, and background.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        user_id = new { type = "string", @default = "default_user", description = "User identifier" }
                    }
                }
            },
            // initialise_conversation_session
            new
            {
                name = "initialise_conversation_session",
                description = "Create a new conversation session to track context across interactions.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        session_name = new { type = "string", description = "Optional session name/title" },
                        client_type = new { type = "string", description = "Client type (e.g., 'claude-desktop', 'cursor')" },
                        metadata = new { type = "object", description = "Additional session metadata" }
                    }
                }
            },
            // end_conversation_session
            new
            {
                name = "end_conversation_session",
                description = "End the current conversation session.",
                inputSchema = new
                {
                    type = "object",
                    properties = new { }
                }
            },
            // memory_multi_hop_search
            new
            {
                name = "memory_multi_hop_search",
                description = "Perform multi-hop reasoning by traversing the knowledge graph. Finds initial memories, then follows entity relationships.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        query = new { type = "string", description = "Initial search query" },
                        hops = new { type = "integer", @default = 2, description = "Number of relationship hops to traverse" },
                        max_results_per_hop = new { type = "integer", @default = 5, description = "Maximum results per hop" }
                    },
                    required = new[] { "query" }
                }
            },
            // get_integrations
            new
            {
                name = "get_integrations",
                description = "List available integrations (external tools/APIs).",
                inputSchema = new
                {
                    type = "object",
                    properties = new { }
                }
            },
            // import_from_core
            new
            {
                name = "import_from_core",
                description = "Import entities, relations, and observations from CORE MCP export format. Provide JSON with 'entities' array (each with name, entityType, observations[]) and 'relations' array (each with from, to, relationType).",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        data = new
                        {
                            type = "object",
                            description = "CORE export data with 'entities' and 'relations' arrays",
                            properties = new
                            {
                                entities = new
                                {
                                    type = "array",
                                    items = new
                                    {
                                        type = "object",
                                        properties = new
                                        {
                                            name = new { type = "string" },
                                            entityType = new { type = "string" },
                                            observations = new { type = "array", items = new { type = "string" } }
                                        },
                                        required = new[] { "name" }
                                    }
                                },
                                relations = new
                                {
                                    type = "array",
                                    items = new
                                    {
                                        type = "object",
                                        properties = new
                                        {
                                            from = new { type = "string" },
                                            to = new { type = "string" },
                                            relationType = new { type = "string" }
                                        },
                                        required = new[] { "from", "to", "relationType" }
                                    }
                                }
                            }
                        },
                        source = new { type = "string", @default = "core-import", description = "Source identifier for imported data" }
                    },
                    required = new[] { "data" }
                }
            },
            // set_user_persona
            new
            {
                name = "set_user_persona",
                description = "Set or update a user persona attribute (preference, skill, goal, background).",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        attribute_type = new { type = "string", description = "Type: preference, skill, goal, background" },
                        attribute_key = new { type = "string", description = "Attribute name (e.g., 'programming_language')" },
                        attribute_value = new { type = "string", description = "Attribute value" },
                        confidence = new { type = "number", @default = 1.0, description = "Confidence score (0.0-1.0)" },
                        user_id = new { type = "string", @default = "default_user", description = "User identifier" }
                    },
                    required = new[] { "attribute_type", "attribute_key", "attribute_value" }
                }
            },
            // crawl_relationships
            new
            {
                name = "crawl_relationships",
                description = "Crawl existing memories to extract entities and relationships. Useful for populating the knowledge graph from memories that were ingested without entity extraction.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        batch_size = new { type = "integer", @default = 100, description = "Number of memories to process" },
                        force_reprocess = new { type = "boolean", @default = false, description = "Reprocess memories that already have entities" }
                    }
                }
            },
            // get_graph_statistics
            new
            {
                name = "get_graph_statistics",
                description = "Get statistics about the knowledge graph including entity and relationship counts by type.",
                inputSchema = new
                {
                    type = "object",
                    properties = new { }
                }
            },
            // get_model_info
            new
            {
                name = "get_model_info",
                description = "Get information about the current embedding model (name, dimensions, supported models, export instructions).",
                inputSchema = new
                {
                    type = "object",
                    properties = new { }
                }
            },
            // reembed_memories
            new
            {
                name = "reembed_memories",
                description = "Re-generate embeddings for memories. Use after switching to a different embedding model. By default only re-embeds memories with null embeddings.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        force_all = new { type = "boolean", @default = false, description = "Re-embed ALL memories, not just those with null embeddings" },
                        batch_size = new { type = "integer", @default = 100, description = "Number of memories to process" }
                    }
                }
            },
            // instantiate_context
            new
            {
                name = "instantiate_context",
                description = "Retrieve and summarize memories from the previous day(s) to continue where you left off. Use at the start of a new session to get context from prior work. Optionally filter by project or subject for relevant context only.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        project_or_subject = new { type = "string", description = "Optional project name or subject to filter memories (e.g., 'FlexPilot', 'waterfall rendering'). Uses semantic search to find relevant memories." },
                        days_back = new { type = "integer", @default = 3, description = "Number of days to look back (default: 3)" },
                        limit = new { type = "integer", @default = 50, description = "Maximum memories to retrieve" },
                        include_entities = new { type = "boolean", @default = true, description = "Include linked entities and relationships" }
                    }
                }
            }
    };

object HandleResourcesList()
{
    return new
    {
        resources = new object[]
        {
            new
            {
                uri = "memory://recent",
                name = "Recent Memories",
                description = "List of recently added memories",
                mimeType = "application/json"
            },
            new
            {
                uri = "memory://sessions",
                name = "Conversation Sessions",
                description = "List of recent conversation sessions",
                mimeType = "application/json"
            }
        }
    };
}

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
    // Every tool call may include an optional "context" envelope to override workspace/session
    string? savedWorkspaceId = null;
    Guid? savedSessionId = null;
    CallContext? callContext = null;

    try
    {
        if (arguments?["context"] is JsonNode contextNode)
        {
            callContext = new CallContext
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
                savedSessionId = currentSessionId;
                currentSessionId = overrideSessionId;
                logger.LogDebug("Context envelope: session overridden to {SessionId}", callContext.SessionId);
            }

            // Store memory/goal/constraints as session metadata if session is active
            if (currentSessionId.HasValue && (callContext.Memory != null || callContext.Goal != null || callContext.Constraints != null))
            {
                var sessionMeta = new Dictionary<string, object>();
                if (callContext.Memory != null) sessionMeta["memory"] = callContext.Memory;
                if (callContext.Goal != null) sessionMeta["goal"] = callContext.Goal;
                if (callContext.Constraints != null) sessionMeta["constraints"] = callContext.Constraints;
                _ = Task.Run(async () =>
                {
                    try { await store.UpdateSessionMetadataAsync(currentSessionId.Value, sessionMeta); }
                    catch (Exception ex) { logger.LogWarning(ex, "Failed to update session metadata"); }
                });
            }
        }

        // --- Tool dispatch ---
        var result = toolName switch
        {
            // Core tools (always direct)
            "memory_search" => await HandleMemorySearch(arguments),
            "memory_ingest" => await HandleMemoryIngest(arguments),
            "memory_about_user" => await HandleMemoryAboutUser(arguments),
            "initialise_conversation_session" => await HandleInitialiseSession(arguments),
            "end_conversation_session" => await HandleEndSession(),
            "memory_multi_hop_search" => await HandleMultiHopSearch(arguments),

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
        if (savedSessionId.HasValue)
        {
            currentSessionId = savedSessionId;
        }

        // Track usage for metered tools (non-blocking)
        TrackToolUsage(trackedToolName, (int)sw.ElapsedMilliseconds, success, errorMessage, toolMetadataContext.Value);
    }
}

async Task<object> HandleUseToolViaGateway(JsonNode? arguments)
{
    var (result, innerToolName) = await gateway.HandleUseTool(arguments);
    // Track the inner tool name for usage, not "use_tool"
    TrackToolUsage(innerToolName, 0, true, null);
    return result;
}

async Task<object> HandleViaGatewayFallback(string toolName, JsonNode? arguments)
{
    // Try to dispatch via gateway for tools registered there
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

void TrackToolUsage(string? toolName, int latencyMs, bool success, string? errorMessage, Dictionary<string, object>? metadata = null)
{
    // Map ALL MCP tools to usage event types - never miss a tool
    UsageEventType? eventType = toolName switch
    {
        // Core memory operations
        "memory_ingest" => UsageEventType.MemoryIngest,
        "memory_search" => UsageEventType.MemorySearch,
        "memory_multi_hop_search" => UsageEventType.MemoryMultiHopSearch,

        // Lifecycle operations
        "memory_update" => UsageEventType.MemoryUpdate,
        "memory_delete" => UsageEventType.MemoryDelete,
        "memory_merge" => UsageEventType.MemoryMerge,
        "memory_split" => UsageEventType.MemorySplit,
        "memory_decay" => UsageEventType.MemoryDecay,
        "memory_reinforce" => UsageEventType.MemoryReinforce,
        "memory_expire" => UsageEventType.MemoryExpire,
        "memory_supersede" => UsageEventType.MemorySupersede,

        // Graph operations
        "crawl_relationships" => UsageEventType.CrawlRelationships,
        "get_graph_statistics" => UsageEventType.GetGraphStatistics,

        // Export operations
        "export_workspace" => UsageEventType.ExportWorkspace,
        "export_memories" => UsageEventType.ExportMemories,
        "export_graph" => UsageEventType.ExportGraph,
        "export_user_profile" => UsageEventType.ExportUserProfile,
        "export_markdown" => UsageEventType.ExportMarkdown,

        // Model operations
        "reembed_memories" => UsageEventType.ReembedMemories,
        "get_model_info" => UsageEventType.GetModelInfo,

        // User/session operations
        "memory_about_user" => UsageEventType.MemoryAboutUser,
        "set_user_persona" => UsageEventType.SetUserPersona,
        "initialise_conversation_session" => UsageEventType.InitialiseSession,
        "end_conversation_session" => UsageEventType.EndSession,
        "instantiate_context" => UsageEventType.InstantiateContext,

        // Integration operations
        "get_integrations" => UsageEventType.GetIntegrations,
        "import_from_core" => UsageEventType.ImportFromCore,

        // Observability operations
        "memory_trace" => UsageEventType.MemoryTrace,
        "memory_lineage" => UsageEventType.MemoryLineage,
        "memory_explain" => UsageEventType.MemoryExplain,
        "memory_conflicts" => UsageEventType.MemoryConflicts,

        // Safety operations
        "detect_contradictions" => UsageEventType.DetectContradictions,
        "detect_hallucinations" => UsageEventType.DetectHallucinations,
        "verify_memory_integrity" => UsageEventType.VerifyMemoryIntegrity,
        "scan_loops" => UsageEventType.ScanLoops,

        // Engineering reasoning operations
        "engineering_analyze" => UsageEventType.EngineeringAnalyze,
        "engineering_visualize" => UsageEventType.EngineeringVisualize,
        "engineering_reason" => UsageEventType.EngineeringReason,

        // Meta-tool operations (lazy-MCP)
        "get_tools_in_category" => UsageEventType.GetToolsInCategory,
        "execute_tool" => UsageEventType.ExecuteTool,

        // Gateway meta-tools
        "get_tools" => UsageEventType.GetTools,
        "use_tool" => UsageEventType.UseTool,

        // Goal operations
        "goal_set" => UsageEventType.GoalSet,
        "goal_list" => UsageEventType.GoalList,
        "goal_complete" => UsageEventType.GoalComplete,

        // Workspace operations
        "workspace_create" => UsageEventType.WorkspaceCreate,
        "workspace_list" => UsageEventType.WorkspaceList,
        "workspace_switch" => UsageEventType.WorkspaceSwitch,

        // Snapshot operations
        "snapshot_create" => UsageEventType.SnapshotCreate,
        "snapshot_list" => UsageEventType.SnapshotList,
        "snapshot_load" => UsageEventType.SnapshotLoad,

        // Auto-capture operations
        "drain_session_captures" => UsageEventType.DrainSessionCaptures,
        "capture_status" => UsageEventType.CaptureStatus,

        // Summarization operations
        "summarize_session" => UsageEventType.SummarizeSession,
        "summarize_context" => UsageEventType.SummarizeContext,

        _ => null
    };

    // Track usage for all tools - fire and forget, never block
    if (eventType.HasValue)
    {
        usageService.TrackUsage(
            eventType.Value,
            sessionId: currentSessionId,
            latencyMs: latencyMs,
            success: success,
            errorMessage: errorMessage,
            metadata: metadata);
    }
    else if (!string.IsNullOrEmpty(toolName))
    {
        // Log unknown tools so we can add them
        logger.LogWarning("Unknown tool not tracked for usage: {ToolName}", toolName);
    }
}

#endregion

#region Tool Implementations

async Task<object> HandleMemorySearch(JsonNode? arguments)
{
    var query = arguments?["query"]?.GetValue<string>()?.Trim();
    if (string.IsNullOrEmpty(query))
        throw new ArgumentException("Query is required and cannot be empty");
    if (query.Length > 10000)
        throw new ArgumentException("Query exceeds maximum length of 10000 characters");

    var modeStr = arguments?["mode"]?.GetValue<string>() ?? "hybrid";
    var limit = Math.Clamp(arguments?["limit"]?.GetValue<int>() ?? 10, 1, 100);
    var threshold = Math.Clamp(arguments?["threshold"]?.GetValue<float>() ?? 0.7f, 0f, 1f);
    var includeEntities = arguments?["include_entities"]?.GetValue<bool>() ?? true;
    var memoryType = arguments?["memory_type"]?.GetValue<string>()?.Trim()?.ToLowerInvariant();

    var mode = modeStr.ToLowerInvariant() switch
    {
        "semantic" => SearchMode.Semantic,
        "text" => SearchMode.Text,
        _ => SearchMode.Hybrid
    };

    var results = await kgService.SearchMemoriesAsync(query, mode, limit, threshold, includeEntities, memoryType);

    var text = $"Found {results.Count} memories:\n\n" +
        string.Join("\n\n", results.Select((r, i) =>
            $"**Memory {i + 1}** (ID: {r.Id})\n" +
            $"Created: {r.CreatedAt:O}\n" +
            $"Content: {r.Content}\n" +
            $"Entities: {string.Join(", ", r.Entities.Select(e => e.Name))}\n" +
            $"Similarity: {(r.Similarity > 0 ? r.Similarity : r.Rank):F3}"));

    return CreateTextResponse(text);
}

async Task<object> HandleMemoryIngest(JsonNode? arguments)
{
    var content = arguments?["content"]?.GetValue<string>()?.Trim();
    if (string.IsNullOrEmpty(content))
        throw new ArgumentException("Content is required and cannot be empty");
    if (content.Length > 100000)
        throw new ArgumentException("Content exceeds maximum length of 100000 characters");

    var source = arguments?["source"]?.GetValue<string>()?.Trim();
    var extractEntities = arguments?["extract_entities"]?.GetValue<bool>() ?? true;
    var dedupMode = arguments?["dedup_mode"]?.GetValue<string>()?.Trim()?.ToLowerInvariant() ?? "warn";
    var dedupThreshold = Math.Clamp(arguments?["dedup_threshold"]?.GetValue<float>() ?? 0.85f, 0f, 1f);

    if (dedupMode is not ("warn" or "skip" or "append" or "off"))
        dedupMode = "warn";

    var memoryTypeParam = arguments?["memory_type"]?.GetValue<string>()?.Trim()?.ToLowerInvariant();
    string[] validMemoryTypes = ["error", "decision", "pattern", "learning", "knowledge", "session_summary", "auto_capture"];
    if (memoryTypeParam != null && !validMemoryTypes.Contains(memoryTypeParam))
        memoryTypeParam = "knowledge";

    Dictionary<string, object>? metadata = null;
    if (arguments?["metadata"] is JsonNode metadataNode)
    {
        metadata = JsonSerializer.Deserialize<Dictionary<string, object>>(metadataNode.ToJsonString());
    }

    var result = await kgService.IngestMemoryAsync(
        content,
        source,
        currentSessionId,
        metadata,
        extractEntities,
        dedupMode,
        dedupThreshold,
        memoryTypeParam);

    // Set dedup metadata for usage tracking
    toolMetadataContext.Value = new Dictionary<string, object>
    {
        ["dedup_mode"] = dedupMode,
        ["dedup_detected"] = result.DuplicateDetected,
        ["dedup_threshold"] = dedupThreshold,
        ["similarity"] = result.DuplicateSimilarity
    };

    var text = result.DuplicateDetected && dedupMode == "skip"
        ? $"Duplicate detected — skipped ingestion.\n\n" +
          $"Existing Memory ID: {result.DuplicateOf}\n" +
          $"Similarity: {result.DuplicateSimilarity:F3}\n"
        : result.DuplicateDetected && dedupMode == "append"
            ? $"Duplicate detected — appended to existing memory.\n\n" +
              $"Memory ID: {result.MemoryId}\n" +
              $"Similarity: {result.DuplicateSimilarity:F3}\n"
            : $"Memory ingested successfully!\n\n" +
              $"Memory ID: {result.MemoryId}\n" +
              $"Entities extracted: {result.EntitiesCreated}\n" +
              $"Relationships extracted: {result.RelationshipsCreated}\n\n" +
              $"Entities: {string.Join(", ", result.Entities.Select(e => e.Name))}\n" +
              $"Relationships: {string.Join(", ", result.Relationships.Select(r => $"{r.Source} --{r.Type}--> {r.Target}"))}";

    if (result.SimilarMemories is { Count: > 0 } && dedupMode == "warn")
    {
        text += $"\n\n**Dedup Warning:** {result.SimilarMemories.Count} similar memory(ies) found:\n";
        foreach (var dup in result.SimilarMemories)
        {
            text += $"  - {dup.MemoryId} (similarity: {dup.Similarity:F3}): {dup.ContentPreview}\n";
        }
    }

    return CreateTextResponse(text);
}

async Task<object> HandleMemoryAboutUser(JsonNode? arguments)
{
    var user = arguments?["user_id"]?.GetValue<string>() ?? "default_user";

    var persona = await kgService.GetUserPersonaAsync(user);

    if (persona.Count == 0)
    {
        return CreateTextResponse($"No persona information found for user: {user}");
    }

    var text = $"User Persona for {user}:\n\n";
    foreach (var (attrType, attributes) in persona)
    {
        text += $"**{char.ToUpper(attrType[0]) + attrType[1..]}:**\n";
        foreach (var (key, valueData) in attributes)
        {
            if (valueData is Dictionary<string, object> vd)
            {
                text += $"  - {key}: {vd.GetValueOrDefault("value", "N/A")} (confidence: {vd.GetValueOrDefault("confidence", 1.0):F2})\n";
            }
        }
        text += "\n";
    }

    return CreateTextResponse(text);
}

async Task<object> HandleInitialiseSession(JsonNode? arguments)
{
    var sessionName = arguments?["session_name"]?.GetValue<string>();
    var clientType = arguments?["client_type"]?.GetValue<string>();

    Dictionary<string, object>? metadata = null;
    if (arguments?["metadata"] is { } metadataNode)
    {
        metadata = JsonSerializer.Deserialize<Dictionary<string, object>>(metadataNode.ToJsonString());
    }

    currentSessionId = await kgService.CreateConversationSessionAsync(sessionName, clientType, metadata);

    return CreateTextResponse($"Conversation session initialized: {currentSessionId}");
}

async Task<object> HandleEndSession()
{
    if (!currentSessionId.HasValue) return CreateTextResponse("No active conversation session to end.");
    try
    {
        var sessionId = currentSessionId.Value;

        // Phase 1: Auto-drain captured session activity
        try
        {
            await autoCaptureTools.DrainInternalAsync(sessionId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Auto-capture drain failed (non-fatal)");
        }

        // Phase 3: AI summarization (if LLM available)
        try
        {
            if (summarizationTools != null)
            {
                await summarizationTools.SummarizeSessionInternalAsync(sessionId);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Session summarization failed (non-fatal)");
        }

        // End the session
        await kgService.EndConversationSessionAsync(sessionId);
        var oldSession = currentSessionId;
        currentSessionId = null;
        return CreateTextResponse($"Conversation session ended: {oldSession}");
    }
    catch (PostgresException ex) when (ex.SqlState is "42P01" or "42703")
    {
        logger.LogWarning("end_conversation_session skipped: {Message}", ex.MessageText);
        currentSessionId = null;
        return CreateErrorResponse("Session tracking schema not available. Session cleared locally.");
    }
}

async Task<object> HandleMultiHopSearch(JsonNode? arguments)
{
    var query = arguments?["query"]?.GetValue<string>()?.Trim();
    if (string.IsNullOrEmpty(query))
        throw new ArgumentException("Query is required and cannot be empty");

    var hops = Math.Clamp(arguments?["hops"]?.GetValue<int>() ?? 2, 1, 5);
    var maxResultsPerHop = Math.Clamp(arguments?["max_results_per_hop"]?.GetValue<int>() ?? 5, 1, 20);

    var result = await kgService.MultiHopSearchAsync(query, hops, maxResultsPerHop);

    var text =
        $"Multi-hop search completed ({result.Hops} hops):\n\n" +
        $"Memories found: {result.Memories.Count}\n" +
        $"Entities discovered: {result.Entities.Count}\n" +
        $"Relationships: {result.Relationships.Count}\n\n" +
        string.Join("\n\n", result.Memories.Take(5).Select((m, i) =>
            $"**Memory {i + 1}:**\n{(m.Content.Length > 200 ? m.Content[..200] + "..." : m.Content)}"));

    return CreateTextResponse(text);
}

object HandleGetIntegrations()
{
    return CreateTextResponse("No integrations configured yet.");
}

async Task<object> HandleImportFromCore(JsonNode? arguments)
{
    var dataNode = arguments?["data"] ?? throw new Exception("Missing data");
    var source = arguments?["source"]?.GetValue<string>() ?? "core-import";

    var coreData = new CoreExportData();

    // Parse entities
    if (dataNode["entities"] is JsonArray entitiesArray)
    {
        foreach (var entityNode in entitiesArray)
        {
            var entity = new CoreEntity
            {
                Name = entityNode?["name"]?.GetValue<string>() ?? "Unknown",
                EntityType = entityNode?["entityType"]?.GetValue<string>()
            };

            if (entityNode?["observations"] is JsonArray obsArray)
            {
                entity.Observations = obsArray.Select(o => o?.GetValue<string>() ?? "").ToList();
            }

            coreData.Entities.Add(entity);
        }
    }

    // Parse relations
    if (dataNode["relations"] is JsonArray relationsArray)
    {
        foreach (var relNode in relationsArray)
        {
            coreData.Relations.Add(new CoreRelation
            {
                From = relNode?["from"]?.GetValue<string>() ?? "",
                To = relNode?["to"]?.GetValue<string>() ?? "",
                RelationType = relNode?["relationType"]?.GetValue<string>() ?? "RELATED_TO"
            });
        }
    }

    var result = await kgService.ImportFromCoreAsync(coreData, source);

    var text =
        $"CORE Import completed!\n\n" +
        $"Entities imported: {result.EntitiesImported}\n" +
        $"Relations imported: {result.RelationsImported}\n" +
        $"Observations imported: {result.ObservationsImported}\n";

    if (result.Errors.Count > 0)
    {
        text += $"\nWarnings/Errors ({result.Errors.Count}):\n" +
                string.Join("\n", result.Errors.Take(10).Select(e => $"  - {e}"));
    }

    return CreateTextResponse(text);
}

async Task<object> HandleSetUserPersona(JsonNode? arguments)
{
    var attrType = arguments?["attribute_type"]?.GetValue<string>()?.Trim();
    var attrKey = arguments?["attribute_key"]?.GetValue<string>()?.Trim();
    var attrValue = arguments?["attribute_value"]?.GetValue<string>()?.Trim();

    if (string.IsNullOrEmpty(attrType))
        throw new ArgumentException("attribute_type is required");
    if (string.IsNullOrEmpty(attrKey))
        throw new ArgumentException("attribute_key is required");
    if (string.IsNullOrEmpty(attrValue))
        throw new ArgumentException("attribute_value is required");

    var validTypes = new[] { "preference", "skill", "goal", "background" };
    if (!validTypes.Contains(attrType.ToLowerInvariant()))
        throw new ArgumentException($"attribute_type must be one of: {string.Join(", ", validTypes)}");

    var confidence = Math.Clamp(arguments?["confidence"]?.GetValue<float>() ?? 1.0f, 0f, 1f);
    var user = arguments?["user_id"]?.GetValue<string>()?.Trim() ?? "default_user";

    try
    {
        await kgService.SetUserPersonaAttributeAsync(attrType, attrKey, attrValue, confidence, user);
        return CreateTextResponse($"User persona attribute set: {attrType}/{attrKey} = {attrValue} (confidence: {confidence:F2})");
    }
    catch (PostgresException ex) when (ex.SqlState is "42P01" or "42703")
    {
        logger.LogWarning("set_user_persona skipped: {Message}", ex.MessageText);
        return CreateErrorResponse("User persona schema not available. Run workspace scoping migration to enable this feature.");
    }
}

async Task<object> HandleCrawlRelationships(JsonNode? arguments)
{
    var batchSize = Math.Clamp(arguments?["batch_size"]?.GetValue<int>() ?? 100, 1, 1000);
    var forceReprocess = arguments?["force_reprocess"]?.GetValue<bool>() ?? false;

    logger.LogInformation("Starting relationship crawl: batch_size={BatchSize}, force_reprocess={Force}", batchSize, forceReprocess);

    var totalEntities = 0;
    var totalRelationships = 0;
    var processedMemories = 0;

    // Track position for offset-based pagination (forceReprocess mode)
    var rowsFetched = 0;

    while (true)
    {
        // Get memories to process
        // force_reprocess=true: process all memories (re-extract entities/relationships)
        // force_reprocess=false: only process memories without existing entity links (rows leave result set after processing)
        List<Memory> memories;

        if (forceReprocess)
        {
            // Offset-based: track position by rows fetched to ensure all rows are visited
            memories = await store.GetAllMemoriesAsync(batchSize, rowsFetched);
            rowsFetched += memories.Count;
        }
        else
        {
            // Rows leave result set when processed (entities linked), so always query from offset 0
            memories = await store.GetMemoriesWithoutEntitiesAsync(batchSize);
        }

        if (memories.Count == 0) break;

        logger.LogInformation("Processing batch of {Count} memories (total processed: {Total})", memories.Count, processedMemories);

        foreach (var memory in memories)
        {
            // Extract entities and relationships
            var (entities, relationships) = await entityService.ExtractAllAsync(memory.Content);

            // Batch-create all entities for this memory
            var entitiesToCreate = entities.Select(e => new Entity
            {
                Name = e.Text,
                EntityType = e.Label,
                CanonicalName = e.Text.ToLowerInvariant(),
                FirstSeenMemoryId = memory.Id,
                CreatedAt = DateTime.UtcNow
            }).ToList();

            // Also collect entities from relationships that aren't in the entity list
            var entityNames = new HashSet<string>(entities.Select(e => e.Text));
            foreach (var rel in relationships)
            {
                if (entityNames.Add(rel.SourceEntity))
                {
                    entitiesToCreate.Add(new Entity
                    {
                        Name = rel.SourceEntity,
                        EntityType = "UNKNOWN",
                        CanonicalName = rel.SourceEntity.ToLowerInvariant(),
                        FirstSeenMemoryId = memory.Id,
                        CreatedAt = DateTime.UtcNow
                    });
                }
                if (entityNames.Add(rel.TargetEntity))
                {
                    entitiesToCreate.Add(new Entity
                    {
                        Name = rel.TargetEntity,
                        EntityType = "UNKNOWN",
                        CanonicalName = rel.TargetEntity.ToLowerInvariant(),
                        FirstSeenMemoryId = memory.Id,
                        CreatedAt = DateTime.UtcNow
                    });
                }
            }

            var entityIdMap = await store.CreateEntitiesBatchAsync(entitiesToCreate);
            totalEntities += entityIdMap.Count;

            // Batch-link entities to memory
            var entityRelevances = new Dictionary<Guid, float>();
            foreach (var entity in entities)
            {
                if (entityIdMap.TryGetValue(entity.Text, out var entityId))
                    entityRelevances[entityId] = entity.Confidence;
            }
            await store.LinkMemoryToEntitiesBatchAsync(memory.Id, entityRelevances);

            // Batch-create all relationships (explicit + co-occurrence)
            var allRelationships = new List<EntityRelationship>();

            foreach (var rel in relationships)
            {
                if (entityIdMap.TryGetValue(rel.SourceEntity, out var sourceId) &&
                    entityIdMap.TryGetValue(rel.TargetEntity, out var targetId))
                {
                    allRelationships.Add(new EntityRelationship
                    {
                        SourceEntityId = sourceId,
                        TargetEntityId = targetId,
                        RelationshipType = rel.RelationType,
                        Confidence = rel.Confidence,
                        FirstSeenMemoryId = memory.Id,
                        CreatedAt = DateTime.UtcNow
                    });
                }
            }

            // Infer co-occurrence relationships for entities in same memory
            var personEntities = entities.Where(e => e.Label == "PERSON").ToList();
            var orgEntities = entities.Where(e => e.Label == "ORG").ToList();

            foreach (var person in personEntities)
            {
                foreach (var org in orgEntities)
                {
                    if (entityIdMap.TryGetValue(person.Text, out var personId) &&
                        entityIdMap.TryGetValue(org.Text, out var orgId))
                    {
                        allRelationships.Add(new EntityRelationship
                        {
                            SourceEntityId = personId,
                            TargetEntityId = orgId,
                            RelationshipType = "MENTIONED_WITH",
                            Confidence = 0.5f,
                            FirstSeenMemoryId = memory.Id,
                            CreatedAt = DateTime.UtcNow
                        });
                    }
                }
            }

            await store.CreateRelationshipsBatchAsync(allRelationships);
            totalRelationships += allRelationships.Count;

            processedMemories++;
        }
    }

    var text = $"Relationship crawl completed!\n\n" +
               $"Memories processed: {processedMemories}\n" +
               $"Entities created: {totalEntities}\n" +
               $"Relationships created: {totalRelationships}";

    logger.LogInformation("Crawl completed: {Memories} memories, {Entities} entities, {Relationships} relationships",
        processedMemories, totalEntities, totalRelationships);

    return CreateTextResponse(text);
}

async Task<object> HandleGetGraphStatistics()
{
    // Single-connection combined query for all counts + server-side GROUP BY for type breakdown
    var (memoryCnt, entityCnt, relationshipCnt) = await store.GetGraphStatisticsAsync();
    var typeBreakdown = await store.GetRelationshipTypeBreakdownAsync();

    var text = $"Knowledge Graph Statistics\n\n" +
               $"Total Memories: {memoryCnt}\n" +
               $"Total Entities: {entityCnt}\n" +
               $"Total Relationships: {relationshipCnt}\n\n" +
               $"Relationship Types:\n" +
               string.Join("\n", typeBreakdown.Select(t => $"  - {t.Key}: {t.Value}"));

    return CreateTextResponse(text);
}

object HandleGetModelInfo()
{
    var isOpenAi = !string.IsNullOrEmpty(openAiApiKey);

    var text = $"## Current Embedding Model\n\n" +
               $"**Service:** {(isOpenAi ? "OpenAI" : "Ollama")}\n" +
               $"**Model:** {(isOpenAi ? openAiEmbedModel : ollamaModel)}\n" +
               (isOpenAi ? "" : $"**URL:** {ollamaUrl}\n") +
               $"**Embedding Dimension:** {embeddingService.EmbeddingDimension}\n\n" +
               (isOpenAi
                   ? $"## OpenAI Embedding Models\n\n" +
                     $"| Model | Dimensions | Notes |\n" +
                     $"|-------|------------|-------|\n" +
                     $"| text-embedding-3-small | 1536 | Default, fast, good quality |\n" +
                     $"| text-embedding-3-large | 3072 | Higher quality, slower |\n" +
                     $"| text-embedding-ada-002 | 1536 | Legacy model |\n\n"
                   : $"## Supported Ollama Models\n\n" +
                     $"| Model | Dimensions | Notes |\n" +
                     $"|-------|------------|-------|\n" +
                     $"| nomic-embed-text | 768 | Default, good quality |\n" +
                     $"| mxbai-embed-large | 1024 | Higher quality, slower |\n" +
                     $"| all-minilm | 384 | Fast, smaller vectors |\n\n") +
               $"## How to Switch Models\n\n" +
               (isOpenAi
                   ? $"Set environment variable: `OPENAI_EMBED_MODEL=<model-name>`\n" +
                     $"Then restart MCP server.\n\n" +
                     $"To switch to Ollama, unset OPENAI_API_KEY."
                   : $"1. Pull the new model: `ollama pull <model-name>`\n" +
                     $"2. Update environment variables:\n" +
                     $"   ```\n" +
                     $"   OLLAMA_MODEL=<model-name>\n" +
                     $"   OLLAMA_EMBEDDING_DIM=<dimension>\n" +
                     $"   ```\n" +
                     $"3. If dimension changed, migrate database\n" +
                     $"4. Restart MCP server and run `reembed_memories` with `force_all: true`");

    return CreateTextResponse(text);
}

async Task<object> HandleReembedMemories(JsonNode? arguments)
{
    var forceAll = arguments?["force_all"]?.GetValue<bool>() ?? false;
    var batchSize = Math.Clamp(arguments?["batch_size"]?.GetValue<int>() ?? 100, 1, 1000);

    logger.LogInformation("Starting re-embedding: force_all={ForceAll}, batch_size={BatchSize}", forceAll, batchSize);

    // Get total counts to report progress context
    var totalMemories = await store.GetMemoryCountAsync();
    long totalToProcess;

    if (forceAll)
    {
        totalToProcess = totalMemories;
    }
    else
    {
        // Count memories with null embeddings
        await using var countConn = await vectorDataSource.OpenConnectionAsync();
        totalToProcess = await countConn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM memories WHERE embedding IS NULL");
    }

    var processedCount = 0;
    var errorCount = 0;
    var stopwatch = Stopwatch.StartNew();

    // Reuse a single connection for all embedding UPDATEs
    await using var reembedConn = await vectorDataSource.OpenConnectionAsync();

    if (forceAll)
    {
        // For force_all mode: iterate through ALL memories using offset-based pagination
        // Track by rows fetched (not processed) to ensure no rows are skipped on errors
        var rowsFetched = 0;

        while (true)
        {
            var memories = await store.GetAllMemoriesAsync(batchSize, rowsFetched);
            if (memories.Count == 0) break;

            rowsFetched += memories.Count;

            foreach (var memory in memories)
            {
                try
                {
                    var embedding = await embeddingService.EmbedTextAsync(memory.Content);

                    await using var cmd = new NpgsqlCommand(
                        "UPDATE memories SET embedding = @Embedding WHERE id = @Id", reembedConn);
                    cmd.Parameters.AddWithValue("@Id", memory.Id);
                    cmd.Parameters.AddWithValue("@Embedding", new Vector(embedding));
                    await cmd.ExecuteNonQueryAsync();

                    processedCount++;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to re-embed memory {MemoryId}", memory.Id);
                    errorCount++;
                }
            }

            logger.LogInformation("Re-embedding progress: {Processed}/{Total} ({Errors} errors)",
                processedCount, totalToProcess, errorCount);
        }
    }
    else
    {
        // For null-embeddings mode: single batch since rows leave result set after processing
        // User can call repeatedly until all are processed
        var memories = await store.GetMemoriesWithNullEmbeddingsAsync(batchSize);

        foreach (var memory in memories)
        {
            try
            {
                var embedding = await embeddingService.EmbedTextAsync(memory.Content);

                await using var cmd = new NpgsqlCommand(
                    "UPDATE memories SET embedding = @Embedding WHERE id = @Id", reembedConn);
                cmd.Parameters.AddWithValue("@Id", memory.Id);
                cmd.Parameters.AddWithValue("@Embedding", new Vector(embedding));
                await cmd.ExecuteNonQueryAsync();

                processedCount++;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to re-embed memory {MemoryId}", memory.Id);
                errorCount++;
            }
        }
    }

    stopwatch.Stop();

    var rate = stopwatch.Elapsed.TotalSeconds > 0 ? processedCount / stopwatch.Elapsed.TotalSeconds : 0;

    // Recount remaining for accurate reporting
    long remaining;
    if (forceAll)
    {
        // In force_all mode, we processed everything (errors aside)
        remaining = errorCount;
    }
    else
    {
        await using var countConn = await vectorDataSource.OpenConnectionAsync();
        remaining = await countConn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM memories WHERE embedding IS NULL");
    }

    var text = $"Re-embedding completed!\n\n" +
               $"**Processed:** {processedCount} memories\n" +
               $"**Errors:** {errorCount}\n" +
               $"**Total memories:** {totalMemories}\n" +
               $"**Duration:** {stopwatch.Elapsed:hh\\:mm\\:ss}\n" +
               $"**Rate:** {rate:F1} memories/second\n" +
               $"**Embedding Dimension:** {embeddingService.EmbeddingDimension}";

    if (forceAll)
    {
        if (errorCount > 0)
        {
            text += $"\n\n**{errorCount} memories failed.** Check logs for details.";
        }
        else
        {
            text += "\n\n**All memories have been re-embedded successfully.**";
        }
    }
    else if (remaining > 0)
    {
        var estimatedBatches = (int)Math.Ceiling((double)remaining / batchSize);
        text += $"\n\n**{remaining} memories remaining.** Run `reembed_memories` {estimatedBatches} more time(s) to complete.";
    }
    else
    {
        text += "\n\n**All memories have embeddings.**";
    }

    return CreateTextResponse(text);
}

async Task<object> HandleInstantiateContext(JsonNode? arguments)
{
    var projectOrSubject = arguments?["project_or_subject"]?.GetValue<string>()?.Trim();
    var daysBack = Math.Clamp(arguments?["days_back"]?.GetValue<int>() ?? 3, 1, 30);
    var limit = Math.Clamp(arguments?["limit"]?.GetValue<int>() ?? 50, 1, 200);
    var includeEntities = arguments?["include_entities"]?.GetValue<bool>() ?? true;

    logger.LogInformation("Instantiating context: project={Project}, days_back={DaysBack}, limit={Limit}",
        projectOrSubject ?? "(all)", daysBack, limit);

    var context = await kgService.GetPreviousDayContextAsync(projectOrSubject, daysBack, limit, includeEntities);

    var text = new System.Text.StringBuilder();
    text.AppendLine("# Previous Session Context");
    text.AppendLine();
    text.AppendLine($"**Period:** {context.FromDate:yyyy-MM-dd} to {context.ToDate:yyyy-MM-dd}");

    if (!string.IsNullOrWhiteSpace(projectOrSubject))
    {
        text.AppendLine($"**Filter:** {projectOrSubject}");
    }

    text.AppendLine($"**Memories Found:** {context.MemoryCount} ({context.RecentMemoryCount} recent, {context.ContextMemoryCount} contextual)");
    text.AppendLine();
    text.AppendLine("## Summary");
    text.AppendLine(context.SessionSummary);
    text.AppendLine();

    // Load typed memory sections (Phase 2)
    try
    {
        var fromUtc = DateTime.UtcNow.AddDays(-daysBack);
        var toUtc = DateTime.UtcNow;

        var typedSections = new (string type, string heading, int maxItems)[]
        {
            ("error", "Recent Errors", 5),
            ("decision", "Active Decisions", 5),
            ("pattern", "Known Patterns", 5),
            ("session_summary", "Session Summaries", 3)
        };

        foreach (var (type, heading, maxItems) in typedSections)
        {
            try
            {
                var typed = await kgService.GetMemoriesByTypeAsync(type, maxItems);
                if (typed.Count > 0)
                {
                    text.AppendLine($"## {heading}");
                    text.AppendLine();
                    foreach (var m in typed)
                    {
                        var preview = m.Content.Length > 200 ? m.Content[..200] + "..." : m.Content;
                        text.AppendLine($"- [{m.CreatedAt:yyyy-MM-dd}] {preview}");
                    }
                    text.AppendLine();
                }
            }
            catch { /* Typed memory query failed - memory_type column may not exist yet */ }
        }
    }
    catch (Exception ex)
    {
        logger.LogDebug(ex, "Could not load typed memory sections (memory_type column may not exist)");
    }

    if (context.TopEntities.Count > 0)
    {
        text.AppendLine("## Key Entities");
        foreach (var entity in context.TopEntities)
        {
            text.AppendLine($"- **{entity.Name}** ({entity.Type})");
        }
        text.AppendLine();
    }

    if (context.TopRelationships.Count > 0)
    {
        text.AppendLine("## Key Relationships");
        foreach (var rel in context.TopRelationships.Take(5))
        {
            text.AppendLine($"- {rel.Source} --{rel.Type}--> {rel.Target}");
        }
        text.AppendLine();
    }

    if (context.Memories.Count > 0)
    {
        text.AppendLine("## Memories");
        text.AppendLine();

        var recentMemories = context.Memories.Where(m => m.CreatedAt >= context.FromDate && m.CreatedAt <= context.ToDate).ToList();
        var contextualMemories = context.Memories.Where(m => m.CreatedAt < context.FromDate).ToList();

        if (recentMemories.Count > 0)
        {
            text.AppendLine("### Recent (In Date Range)");
            text.AppendLine();

            foreach (var memory in recentMemories.Take(10))
            {
                text.AppendLine($"**{memory.CreatedAt:yyyy-MM-dd HH:mm}**");

                var contentPreview = memory.Content.Length > 300
                    ? memory.Content[..300] + "..."
                    : memory.Content;
                text.AppendLine(contentPreview);

                if (memory.Entities.Count > 0)
                {
                    text.AppendLine($"*Entities: {string.Join(", ", memory.Entities.Select(e => e.Name))}*");
                }

                text.AppendLine();
            }
        }

        if (contextualMemories.Count > 0)
        {
            text.AppendLine("### Contextual (Older Background)");
            text.AppendLine();

            foreach (var memory in contextualMemories.Take(5))
            {
                text.AppendLine($"**{memory.CreatedAt:yyyy-MM-dd}** *(older context)*");

                var contentPreview = memory.Content.Length > 200
                    ? memory.Content[..200] + "..."
                    : memory.Content;
                text.AppendLine(contentPreview);

                if (memory.Entities.Count > 0)
                {
                    text.AppendLine($"*Entities: {string.Join(", ", memory.Entities.Select(e => e.Name))}*");
                }

                text.AppendLine();
            }
        }

        if (context.Memories.Count > 10)
        {
            text.AppendLine($"*... and {context.Memories.Count - 10} more memories*");
        }
    }

    // Load active goals and append to context
    try
    {
        var goals = await kgService.GetActiveGoalsAsync();
        if (goals.Count > 0)
        {
            text.AppendLine("## Active Goals");
            text.AppendLine();
            foreach (var goal in goals)
            {
                var priorityLabel = goal.Confidence >= 0.8f ? "HIGH" : goal.Confidence >= 0.5f ? "MEDIUM" : "LOW";
                text.AppendLine($"- **{goal.AttributeKey}** [{priorityLabel}] — {goal.AttributeValue}");
            }
            text.AppendLine();
        }
    }
    catch (Exception ex)
    {
        logger.LogDebug(ex, "Could not load goals for context (user_personas table may not exist)");
    }

    logger.LogInformation("Context instantiated: {MemoryCount} memories, {EntityCount} entities",
        context.MemoryCount, context.TopEntities.Count);

    return CreateTextResponse(text.ToString());
}

object HandleGetToolsInCategory(JsonNode? arguments)
{
    var path = arguments?["path"]?.GetValue<string>()?.Trim()?.ToLowerInvariant() ?? "";

    if (string.IsNullOrEmpty(path))
    {
        // Root level: return category list (stable ordering)
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

    // Pass coreTools so session/admin categories can extract from the single source of truth
    var tools = ToolHierarchy.GetToolsForCategory(path, coreTools);
    return CreateTextResponse(
        $"## {categoryInfo.Title}\n{categoryInfo.Description}\n\n" +
        $"**{tools.Length} tools available.** Use `execute_tool` with path `{path}.<tool_name>` to execute.\n\n" +
        System.Text.Json.JsonSerializer.Serialize(tools, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
        }));
}

async Task<object> HandleExecuteTool(JsonNode? arguments)
{
    var toolPath = arguments?["tool_path"]?.GetValue<string>()?.Trim()?.ToLowerInvariant();
    if (string.IsNullOrEmpty(toolPath))
        throw new ArgumentException("tool_path is required (e.g. 'lifecycle.memory_update')");

    // Guard against recursion: execute_tool cannot dispatch to itself or get_tools_in_category
    if (toolPath is "meta.execute_tool" or "meta.get_tools_in_category")
        throw new ArgumentException("Cannot invoke meta-tools via execute_tool");

    if (!ToolHierarchy.ToolMap.TryGetValue(toolPath, out var actualToolName))
        throw new ArgumentException($"Unknown tool path: {toolPath}. Use get_tools_in_category to discover available tools.");

    var toolArguments = arguments?["arguments"];

    logger.LogInformation("→ execute_tool: {Path} → {Tool}", toolPath, actualToolName);

    // Dispatch directly to the tool handler (avoids double usage tracking via HandleToolsCall)
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
        TrackToolUsage(actualToolName, (int)sw.ElapsedMilliseconds, success, errorMessage);
    }
}

Task<object>? DispatchTool(string toolName, JsonNode? arguments) => toolName switch
{
    "memory_search" => HandleMemorySearch(arguments),
    "memory_ingest" => HandleMemoryIngest(arguments),
    "memory_about_user" => HandleMemoryAboutUser(arguments),
    "initialise_conversation_session" => HandleInitialiseSession(arguments),
    "end_conversation_session" => HandleEndSession(),
    "memory_multi_hop_search" => HandleMultiHopSearch(arguments),
    "get_integrations" => Task.FromResult(HandleGetIntegrations()),
    "import_from_core" => HandleImportFromCore(arguments),
    "set_user_persona" => HandleSetUserPersona(arguments),
    "crawl_relationships" => HandleCrawlRelationships(arguments),
    "get_graph_statistics" => HandleGetGraphStatistics(),
    "get_model_info" => Task.FromResult(HandleGetModelInfo()),
    "reembed_memories" => HandleReembedMemories(arguments),
    "instantiate_context" => HandleInstantiateContext(arguments),
    "memory_update" => lifecycleTools.HandleMemoryUpdate(arguments),
    "memory_delete" => lifecycleTools.HandleMemoryDelete(arguments),
    "memory_merge" => lifecycleTools.HandleMemoryMerge(arguments),
    "memory_split" => lifecycleTools.HandleMemorySplit(arguments),
    "memory_decay" => lifecycleTools.HandleMemoryDecay(arguments),
    "memory_reinforce" => lifecycleTools.HandleMemoryReinforce(arguments),
    "memory_expire" => lifecycleTools.HandleMemoryExpire(arguments),
    "memory_supersede" => lifecycleTools.HandleMemorySupersede(arguments),
    "memory_trace" => observabilityTools.HandleMemoryTrace(arguments),
    "memory_lineage" => observabilityTools.HandleMemoryLineage(arguments),
    "memory_explain" => observabilityTools.HandleMemoryExplain(arguments),
    "memory_conflicts" => observabilityTools.HandleMemoryConflicts(arguments),
    "detect_contradictions" => safetyTools.HandleDetectContradictions(arguments),
    "detect_hallucinations" => safetyTools.HandleDetectHallucinations(arguments),
    "verify_memory_integrity" => safetyTools.HandleVerifyIntegrity(arguments),
    "scan_loops" => safetyTools.HandleScanLoops(arguments),
    "export_workspace" => exportTools.HandleExportWorkspace(arguments),
    "export_memories" => exportTools.HandleExportMemories(arguments),
    "export_graph" => exportTools.HandleExportGraph(arguments),
    "export_user_profile" => exportTools.HandleExportUserProfile(arguments),
    "export_markdown" => exportTools.HandleExportMarkdown(arguments),
    "engineering_analyze" => reasoningTools.HandleEngineeringAnalyze(arguments),
    "engineering_visualize" => reasoningTools.HandleEngineeringVisualize(arguments),
    "engineering_reason" => reasoningTools.HandleEngineeringReason(arguments),
    // Goal tools
    "goal_set" => HandleGoalSet(arguments),
    "goal_list" => HandleGoalList(arguments),
    "goal_complete" => HandleGoalComplete(arguments),
    // Workspace tools
    "workspace_create" => workspaceTools.HandleWorkspaceCreate(arguments),
    "workspace_list" => workspaceTools.HandleWorkspaceList(arguments),
    "workspace_switch" => workspaceTools.HandleWorkspaceSwitch(arguments),
    "snapshot_create" => snapshotTools.HandleSnapshotCreate(arguments),
    "snapshot_list" => snapshotTools.HandleSnapshotList(arguments),
    "snapshot_load" => snapshotTools.HandleSnapshotLoad(arguments),
    // Auto-capture tools
    "drain_session_captures" => autoCaptureTools.HandleDrainSessionCaptures(arguments),
    "capture_status" => autoCaptureTools.HandleCaptureStatus(arguments),
    // Summarization tools
    "summarize_session" => summarizationTools?.HandleSummarizeSession(arguments) ?? Task.FromResult<object>(CreateTextResponse("No LLM configured for summarization.")),
    "summarize_context" => summarizationTools?.HandleSummarizeContext(arguments) ?? Task.FromResult<object>(CreateTextResponse("No LLM configured for summarization.")),
    _ => null
};

async Task<object> HandleGoalSet(JsonNode? arguments)
{
    var key = arguments?["key"]?.GetValue<string>()?.Trim();
    var description = arguments?["description"]?.GetValue<string>()?.Trim();
    if (string.IsNullOrEmpty(key))
        throw new ArgumentException("key is required");
    if (string.IsNullOrEmpty(description))
        throw new ArgumentException("description is required");

    var priority = Math.Clamp(arguments?["priority"]?.GetValue<float>() ?? 1.0f, 0.1f, 1f);
    var user = arguments?["user_id"]?.GetValue<string>()?.Trim() ?? "default_user";

    try
    {
        await kgService.SetGoalAsync(key, description, priority, user);
        return CreateTextResponse($"Goal set: **{key}** (priority: {priority:F1})\n{description}");
    }
    catch (PostgresException ex) when (ex.SqlState is "42P01" or "42703")
    {
        logger.LogWarning("goal_set skipped: {Message}", ex.MessageText);
        return CreateErrorResponse("User persona schema not available. Run workspace scoping migration to enable goals.");
    }
}

async Task<object> HandleGoalList(JsonNode? arguments)
{
    var user = arguments?["user_id"]?.GetValue<string>()?.Trim() ?? "default_user";

    try
    {
        var goals = await kgService.GetActiveGoalsAsync(user);

        if (goals.Count == 0)
            return CreateTextResponse("No active goals. Use `goal_set` to create one.");

        var text = $"## Active Goals ({goals.Count})\n\n";
        foreach (var goal in goals)
        {
            var priorityLabel = goal.Confidence >= 0.8f ? "HIGH" : goal.Confidence >= 0.5f ? "MEDIUM" : "LOW";
            text += $"- **{goal.AttributeKey}** [{priorityLabel}] — {goal.AttributeValue}\n";
            text += $"  *Updated: {goal.UpdatedAt:yyyy-MM-dd HH:mm}*\n";
        }
        return CreateTextResponse(text);
    }
    catch (PostgresException ex) when (ex.SqlState is "42P01" or "42703")
    {
        logger.LogWarning("goal_list skipped: {Message}", ex.MessageText);
        return CreateErrorResponse("User persona schema not available. Run workspace scoping migration to enable goals.");
    }
}

async Task<object> HandleGoalComplete(JsonNode? arguments)
{
    var key = arguments?["key"]?.GetValue<string>()?.Trim();
    if (string.IsNullOrEmpty(key))
        throw new ArgumentException("key is required");

    var user = arguments?["user_id"]?.GetValue<string>()?.Trim() ?? "default_user";

    try
    {
        await kgService.CompleteGoalAsync(key, user);
        return CreateTextResponse($"Goal completed: **{key}**");
    }
    catch (PostgresException ex) when (ex.SqlState is "42P01" or "42703")
    {
        logger.LogWarning("goal_complete skipped: {Message}", ex.MessageText);
        return CreateErrorResponse("User persona schema not available. Run workspace scoping migration to enable goals.");
    }
}

object CreateTextResponse(string text)
{
    return new
    {
        content = new[]
        {
            new
            {
                type = "text",
                text
            }
        }
    };
}

object CreateErrorResponse(string message)
{
    return new
    {
        content = new[]
        {
            new
            {
                type = "text",
                text = $"Error: {message}"
            }
        },
        isError = true
    };
}

#endregion
