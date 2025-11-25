using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SerialMemory.Core.Interfaces;
using SerialMemory.Core.Services;
using SerialMemory.Infrastructure;
using SerialMemory.ML;

// MCP STDIO Server for Serial Memory - CORE-like Temporal Knowledge Graph
// Implements Model Context Protocol over STDIN/STDOUT
// Backed by PostgreSQL + pgvector for semantic search

#region Configuration

var configuration = new ConfigurationBuilder()
    .AddEnvironmentVariables()
    .Build();

var postgresHost = configuration["POSTGRES_HOST"] ?? "localhost";
var postgresPort = configuration["POSTGRES_PORT"] ?? "5432";
var postgresUser = configuration["POSTGRES_USER"] ?? "postgres";
var postgresPassword = configuration["POSTGRES_PASSWORD"] ?? "postgres";
var postgresDb = configuration["POSTGRES_DB"] ?? "contextdb";

// Embedding service configuration
// Option 1: ONNX (pure C#, no Python required) - set ONNX_MODEL_PATH and VOCAB_PATH
// Option 2: HTTP (requires Python embedding service) - set EMBEDDING_SERVICE_URL
var onnxModelPath = configuration["ONNX_MODEL_PATH"];
var vocabPath = configuration["VOCAB_PATH"];
var embeddingServiceUrl = configuration["EMBEDDING_SERVICE_URL"] ?? "http://localhost:8765";

var connectionString = $"Host={postgresHost};Port={postgresPort};Database={postgresDb};Username={postgresUser};Password={postgresPassword}";

// Configure logging to stderr (MCP uses stdout for protocol)
using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
    builder.SetMinimumLevel(LogLevel.Information);
});
var logger = loggerFactory.CreateLogger("SerialMemory.Mcp");

#endregion

#region Service Initialization

logger.LogInformation("Initializing Serial Memory MCP Server (C# CORE-like)");
logger.LogInformation("Database: {Host}:{Port}/{Database}", postgresHost, postgresPort, postgresDb);

// Initialize services
IKnowledgeGraphStore store = new PostgresKnowledgeGraphStore(connectionString);

// Create embedding service (ONNX or HTTP)
IEmbeddingService embeddingService = EmbeddingServiceFactory.Create(
    onnxModelPath: onnxModelPath,
    vocabPath: vocabPath,
    httpServiceUrl: embeddingServiceUrl);

logger.LogInformation("Embedding service: {Type}", embeddingService.GetType().Name);

IEntityExtractionService entityService = new PatternEntityExtractionService();

var kgService = new KnowledgeGraphService(store, embeddingService, entityService);

// Session state
Guid? currentSessionId = null;

logger.LogInformation("Services initialized successfully");

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
    return new
    {
        tools = new object[]
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

    try
    {
        switch (toolName)
        {
            case "memory_search":
                return await HandleMemorySearch(arguments);

            case "memory_ingest":
                return await HandleMemoryIngest(arguments);

            case "memory_about_user":
                return await HandleMemoryAboutUser(arguments);

            case "initialise_conversation_session":
                return await HandleInitialiseSession(arguments);

            case "end_conversation_session":
                return await HandleEndSession();

            case "memory_multi_hop_search":
                return await HandleMultiHopSearch(arguments);

            case "get_integrations":
                return HandleGetIntegrations();

            case "import_from_core":
                return await HandleImportFromCore(arguments);

            case "set_user_persona":
                return await HandleSetUserPersona(arguments);

            default:
                throw new Exception($"Unknown tool: {toolName}");
        }
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Error executing tool {ToolName}", toolName);
        return CreateTextResponse($"Error: {ex.Message}");
    }
}

#endregion

#region Tool Implementations

async Task<object> HandleMemorySearch(JsonNode? arguments)
{
    var query = arguments?["query"]?.GetValue<string>() ?? throw new Exception("Missing query");
    var modeStr = arguments?["mode"]?.GetValue<string>() ?? "hybrid";
    var limit = arguments?["limit"]?.GetValue<int>() ?? 10;
    var threshold = arguments?["threshold"]?.GetValue<float>() ?? 0.7f;
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
    var content = arguments?["content"]?.GetValue<string>() ?? throw new Exception("Missing content");
    var source = arguments?["source"]?.GetValue<string>();
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
    var query = arguments?["query"]?.GetValue<string>() ?? throw new Exception("Missing query");
    var hops = arguments?["hops"]?.GetValue<int>() ?? 2;
    var maxResultsPerHop = arguments?["max_results_per_hop"]?.GetValue<int>() ?? 5;

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
    var attrType = arguments?["attribute_type"]?.GetValue<string>() ?? throw new Exception("Missing attribute_type");
    var attrKey = arguments?["attribute_key"]?.GetValue<string>() ?? throw new Exception("Missing attribute_key");
    var attrValue = arguments?["attribute_value"]?.GetValue<string>() ?? throw new Exception("Missing attribute_value");
    var confidence = arguments?["confidence"]?.GetValue<float>() ?? 1.0f;
    var userId = arguments?["user_id"]?.GetValue<string>() ?? "default_user";

    await kgService.SetUserPersonaAttributeAsync(attrType, attrKey, attrValue, confidence, userId);

    return CreateTextResponse($"User persona attribute set: {attrType}/{attrKey} = {attrValue} (confidence: {confidence:F2})");
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

#endregion
