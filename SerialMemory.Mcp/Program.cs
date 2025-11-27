using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using Pgvector;
using SerialMemory.Core.Auth;
using SerialMemory.Core.Interfaces;
using SerialMemory.Core.Models;
using SerialMemory.Core.Services;
using SerialMemory.Infrastructure;
using TenantContext = SerialMemory.Core.Services.TenantContext;
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

// Embedding service configuration - Ollama (local only)
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

logger.LogInformation("Initializing Serial Memory MCP Server (C# CORE-like)");
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

// Initialize services with authenticated tenant context
var tenantContext = new FixedTenantContext(tenantId, "default", userId);
DebugFileLogger.Log("MCP", $"Created FixedTenantContext: TenantId={tenantContext.TenantId}, WorkspaceId={tenantContext.WorkspaceId}");
IKnowledgeGraphStore store = new PostgresKnowledgeGraphStore(connectionString, tenantContext);
DebugFileLogger.Log("MCP", $"Created PostgresKnowledgeGraphStore");


// Create embedding service - local Ollama
IEmbeddingService embeddingService = new OllamaEmbeddingService(ollamaUrl, ollamaModel, ollamaEmbeddingDim);
logger.LogInformation("Using local Ollama embedding service: {Model} at {Url} (dim={Dim})", ollamaModel, ollamaUrl, ollamaEmbeddingDim);

// Create entity extraction service (Ollama > HTTP > Pattern-based)
IEntityExtractionService entityService = EntityExtractionServiceFactory.Create(
    ollamaUrl: ollamaEntityUrl,
    ollamaModel: ollamaEntityModel,
    httpServiceUrl: extractionServiceUrl);

logger.LogInformation("Entity extraction service: {Type} (model: {Model})",
    entityService.GetType().Name, ollamaEntityModel);

var kgService = new KnowledgeGraphService(store, embeddingService, entityService);

// Initialize event store for lifecycle operations
IEventStore eventStore = new PostgresEventStore(connectionString, loggerFactory.CreateLogger<PostgresEventStore>());

// Initialize tool handlers
var lifecycleTools = new MemoryLifecycleTools(eventStore, embeddingService, logger);
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

// Initialize usage service (non-blocking metering) - use authenticated tenant context
var usageLogger = loggerFactory.CreateLogger<UsageService>();
using var usageService = new UsageService(connectionString, usageLogger, tenantId: tenantId, workspaceId: tenantContext.WorkspaceId);
logger.LogInformation("Usage metering service initialized for tenant {TenantId}", tenantId);

// Session state
Guid? currentSessionId = null;

logger.LogInformation("Services initialized successfully (v2.3 with lifecycle, observability, safety, export, reasoning, multi-model reasoning, usage metering)");

#endregion

#region MCP Protocol Handlers

var jsonOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
};

// Read JSON-RPC requests from STDIN, write responses to STDOUT
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
            name = "serial-memory-server",
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
    var coreTools = new object[]
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
                        include_entities = new { type = "boolean", @default = true, description = "Include linked entities" }
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
                        extract_entities = new { type = "boolean", @default = true, description = "Whether to extract entities and relationships" }
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
            }
    };

    // Combine core tools with lifecycle, observability, safety, export, and reasoning tools
    var allTools = coreTools
        .Concat(ToolDefinitions.GetLifecycleTools())
        .Concat(ToolDefinitions.GetObservabilityTools())
        .Concat(ToolDefinitions.GetSafetyTools())
        .Concat(ToolDefinitions.GetExportTools())
        .Concat(ToolDefinitions.GetReasoningTools())
        .ToArray();

    return new { tools = allTools };
}

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

    try
    {
        var result = toolName switch
        {
            "memory_search" => await HandleMemorySearch(arguments),
            "memory_ingest" => await HandleMemoryIngest(arguments),
            "memory_about_user" => await HandleMemoryAboutUser(arguments),
            "initialise_conversation_session" => await HandleInitialiseSession(arguments),
            "end_conversation_session" => await HandleEndSession(),
            "memory_multi_hop_search" => await HandleMultiHopSearch(arguments),
            "get_integrations" => HandleGetIntegrations(),
            "import_from_core" => await HandleImportFromCore(arguments),
            "set_user_persona" => await HandleSetUserPersona(arguments),
            "crawl_relationships" => await HandleCrawlRelationships(arguments),
            "get_graph_statistics" => await HandleGetGraphStatistics(),
            "get_model_info" => HandleGetModelInfo(),
            "reembed_memories" => await HandleReembedMemories(arguments),

            // Lifecycle tools
            "memory_update" => await lifecycleTools.HandleMemoryUpdate(arguments),
            "memory_delete" => await lifecycleTools.HandleMemoryDelete(arguments),
            "memory_merge" => await lifecycleTools.HandleMemoryMerge(arguments),
            "memory_split" => await lifecycleTools.HandleMemorySplit(arguments),
            "memory_decay" => await lifecycleTools.HandleMemoryDecay(arguments),
            "memory_reinforce" => await lifecycleTools.HandleMemoryReinforce(arguments),
            "memory_expire" => await lifecycleTools.HandleMemoryExpire(arguments),

            // Observability tools
            "memory_trace" => await observabilityTools.HandleMemoryTrace(arguments),
            "memory_lineage" => await observabilityTools.HandleMemoryLineage(arguments),
            "memory_explain" => await observabilityTools.HandleMemoryExplain(arguments),
            "memory_conflicts" => await observabilityTools.HandleMemoryConflicts(arguments),

            // Safety tools
            "detect_contradictions" => await safetyTools.HandleDetectContradictions(arguments),
            "detect_hallucinations" => await safetyTools.HandleDetectHallucinations(arguments),
            "verify_memory_integrity" => await safetyTools.HandleVerifyIntegrity(arguments),
            "scan_loops" => await safetyTools.HandleScanLoops(arguments),

            // Export tools
            "export_workspace" => await exportTools.HandleExportWorkspace(arguments),
            "export_memories" => await exportTools.HandleExportMemories(arguments),
            "export_graph" => await exportTools.HandleExportGraph(arguments),
            "export_user_profile" => await exportTools.HandleExportUserProfile(arguments),

            // Reasoning tools
            "engineering_analyze" => await reasoningTools.HandleEngineeringAnalyze(arguments),
            "engineering_visualize" => await reasoningTools.HandleEngineeringVisualize(arguments),
            "engineering_reason" => await reasoningTools.HandleEngineeringReason(arguments),

            _ => throw new Exception($"Unknown tool: {toolName}")
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
        // Track usage for metered tools (non-blocking)
        TrackToolUsage(toolName, (int)sw.ElapsedMilliseconds, success, errorMessage);
    }
}

void TrackToolUsage(string? toolName, int latencyMs, bool success, string? errorMessage)
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

        // Graph operations
        "crawl_relationships" => UsageEventType.CrawlRelationships,
        "get_graph_statistics" => UsageEventType.GetGraphStatistics,

        // Export operations
        "export_workspace" => UsageEventType.ExportWorkspace,
        "export_memories" => UsageEventType.ExportMemories,
        "export_graph" => UsageEventType.ExportGraph,
        "export_user_profile" => UsageEventType.ExportUserProfile,

        // Model operations
        "reembed_memories" => UsageEventType.ReembedMemories,
        "get_model_info" => UsageEventType.GetModelInfo,

        // User/session operations
        "memory_about_user" => UsageEventType.MemoryAboutUser,
        "set_user_persona" => UsageEventType.SetUserPersona,
        "initialise_conversation_session" => UsageEventType.InitialiseSession,
        "end_conversation_session" => UsageEventType.EndSession,

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
            errorMessage: errorMessage);
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

    var mode = modeStr.ToLowerInvariant() switch
    {
        "semantic" => SearchMode.Semantic,
        "text" => SearchMode.Text,
        _ => SearchMode.Hybrid
    };

    var results = await kgService.SearchMemoriesAsync(query, mode, limit, threshold, includeEntities);

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
        extractEntities);

    var text =
        $"Memory ingested successfully!\n\n" +
        $"Memory ID: {result.MemoryId}\n" +
        $"Entities extracted: {result.EntitiesCreated}\n" +
        $"Relationships extracted: {result.RelationshipsCreated}\n\n" +
        $"Entities: {string.Join(", ", result.Entities.Select(e => e.Name))}\n" +
        $"Relationships: {string.Join(", ", result.Relationships.Select(r => $"{r.Source} --{r.Type}--> {r.Target}"))}";

    return CreateTextResponse(text);
}

async Task<object> HandleMemoryAboutUser(JsonNode? arguments)
{
    var userId = arguments?["user_id"]?.GetValue<string>() ?? "default_user";

    var persona = await kgService.GetUserPersonaAsync(userId);

    if (persona.Count == 0)
    {
        return CreateTextResponse($"No persona information found for user: {userId}");
    }

    var text = $"User Persona for {userId}:\n\n";
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
    if (arguments?["metadata"] is JsonNode metadataNode)
    {
        metadata = JsonSerializer.Deserialize<Dictionary<string, object>>(metadataNode.ToJsonString());
    }

    currentSessionId = await kgService.CreateConversationSessionAsync(sessionName, clientType, metadata);

    return CreateTextResponse($"Conversation session initialized: {currentSessionId}");
}

async Task<object> HandleEndSession()
{
    if (currentSessionId.HasValue)
    {
        await kgService.EndConversationSessionAsync(currentSessionId.Value);
        var oldSession = currentSessionId;
        currentSessionId = null;
        return CreateTextResponse($"Conversation session ended: {oldSession}");
    }

    return CreateTextResponse("No active conversation session to end.");
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
    var userId = arguments?["user_id"]?.GetValue<string>()?.Trim() ?? "default_user";

    await kgService.SetUserPersonaAttributeAsync(attrType, attrKey, attrValue, confidence, userId);

    return CreateTextResponse($"User persona attribute set: {attrType}/{attrKey} = {attrValue} (confidence: {confidence:F2})");
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

            // Create entities and link to memory
            var entityIdMap = new Dictionary<string, Guid>();
            foreach (var entity in entities)
            {
                var entityId = await store.CreateEntityAsync(new Entity
                {
                    Id = Guid.CreateVersion7(),
                    Name = entity.Text,
                    EntityType = entity.Label,
                    CanonicalName = entity.Text.ToLowerInvariant(),
                    FirstSeenMemoryId = memory.Id,
                    CreatedAt = DateTime.UtcNow
                });

                entityIdMap[entity.Text] = entityId;
                await store.LinkMemoryToEntityAsync(memory.Id, entityId, entity.Confidence);
                totalEntities++;
            }

            // Create relationships
            foreach (var rel in relationships)
            {
                Guid sourceId, targetId;

                if (!entityIdMap.TryGetValue(rel.SourceEntity, out sourceId))
                {
                    sourceId = await store.CreateEntityAsync(new Entity
                    {
                        Id = Guid.CreateVersion7(),
                        Name = rel.SourceEntity,
                        EntityType = "UNKNOWN",
                        CanonicalName = rel.SourceEntity.ToLowerInvariant(),
                        FirstSeenMemoryId = memory.Id,
                        CreatedAt = DateTime.UtcNow
                    });
                    entityIdMap[rel.SourceEntity] = sourceId;
                    totalEntities++;
                }

                if (!entityIdMap.TryGetValue(rel.TargetEntity, out targetId))
                {
                    targetId = await store.CreateEntityAsync(new Entity
                    {
                        Id = Guid.CreateVersion7(),
                        Name = rel.TargetEntity,
                        EntityType = "UNKNOWN",
                        CanonicalName = rel.TargetEntity.ToLowerInvariant(),
                        FirstSeenMemoryId = memory.Id,
                        CreatedAt = DateTime.UtcNow
                    });
                    entityIdMap[rel.TargetEntity] = targetId;
                    totalEntities++;
                }

                await store.CreateRelationshipAsync(new EntityRelationship
                {
                    Id = Guid.CreateVersion7(),
                    SourceEntityId = sourceId,
                    TargetEntityId = targetId,
                    RelationshipType = rel.RelationType,
                    Confidence = rel.Confidence,
                    FirstSeenMemoryId = memory.Id,
                    CreatedAt = DateTime.UtcNow
                });
                totalRelationships++;
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
                        await store.CreateRelationshipAsync(new EntityRelationship
                        {
                            Id = Guid.CreateVersion7(),
                            SourceEntityId = personId,
                            TargetEntityId = orgId,
                            RelationshipType = "MENTIONED_WITH",
                            Confidence = 0.5f,
                            FirstSeenMemoryId = memory.Id,
                            CreatedAt = DateTime.UtcNow
                        });
                        totalRelationships++;
                    }
                }
            }

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
    var memoryCnt = await store.GetMemoryCountAsync();
    var entityCnt = await store.GetEntityCountAsync();
    var relationshipCnt = await store.GetRelationshipCountAsync();

    // Get relationship type breakdown
    var relationships = await store.GetAllRelationshipsAsync(1000);
    var typeBreakdown = relationships
        .GroupBy(r => r.RelationshipType)
        .Select(g => new { Type = g.Key, Count = g.Count() })
        .OrderByDescending(x => x.Count)
        .ToList();

    var text = $"Knowledge Graph Statistics\n\n" +
               $"Total Memories: {memoryCnt}\n" +
               $"Total Entities: {entityCnt}\n" +
               $"Total Relationships: {relationshipCnt}\n\n" +
               $"Relationship Types:\n" +
               string.Join("\n", typeBreakdown.Select(t => $"  - {t.Type}: {t.Count}"));

    return CreateTextResponse(text);
}

object HandleGetModelInfo()
{
    var text = $"## Current Embedding Model\n\n" +
               $"**Service:** Ollama\n" +
               $"**Model:** {ollamaModel}\n" +
               $"**URL:** {ollamaUrl}\n" +
               $"**Embedding Dimension:** {embeddingService.EmbeddingDimension}\n\n" +
               $"## Supported Ollama Models\n\n" +
               $"| Model | Dimensions | Notes |\n" +
               $"|-------|------------|-------|\n" +
               $"| nomic-embed-text | 768 | Default, good quality |\n" +
               $"| mxbai-embed-large | 1024 | Higher quality, slower |\n" +
               $"| all-minilm | 384 | Fast, smaller vectors |\n\n" +
               $"## How to Switch Models\n\n" +
               $"1. Pull the new model: `ollama pull <model-name>`\n" +
               $"2. Update environment variables:\n" +
               $"   ```\n" +
               $"   OLLAMA_MODEL=<model-name>\n" +
               $"   OLLAMA_EMBEDDING_DIM=<dimension>\n" +
               $"   ```\n" +
               $"3. If dimension changed, migrate database:\n" +
               $"   ```sql\n" +
               $"   -- Set EMBEDDING_DIM in ops/migrate_embedding_dimension.sql\n" +
               $"   psql -f ops/migrate_embedding_dimension.sql\n" +
               $"   ```\n" +
               $"4. Restart MCP server and run `reembed_memories` with `force_all: true`";

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

                    // Use raw Npgsql instead of Dapper - Dapper doesn't support Pgvector.Vector type
                    await using var connection = await vectorDataSource.OpenConnectionAsync();
                    await using var cmd = new NpgsqlCommand(
                        "UPDATE memories SET embedding = @Embedding WHERE id = @Id", connection);
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

                // Use raw Npgsql instead of Dapper - Dapper doesn't support Pgvector.Vector type
                await using var connection = await vectorDataSource.OpenConnectionAsync();
                await using var cmd = new NpgsqlCommand(
                    "UPDATE memories SET embedding = @Embedding WHERE id = @Id", connection);
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
