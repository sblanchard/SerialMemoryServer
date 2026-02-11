namespace SerialMemory.Mcp.Tools;

/// <summary>
/// MCP tool schema definitions for core tools (always listed in tools/list).
/// Separated from ToolDefinitions to keep files focused.
/// </summary>
public static class CoreToolDefinitions
{
    public static object[] GetCoreTools() =>
    [
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
    ];
}
