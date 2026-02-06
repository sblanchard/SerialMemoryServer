namespace SerialMemory.Mcp.Tools;

/// <summary>
/// Lazy-MCP tool hierarchy. Instead of listing all 37+ tools upfront (consuming ~7400 tokens),
/// expose only core tools + 2 meta-tools. Remaining tools are discoverable on demand.
/// Saves ~84% of tool listing overhead.
/// </summary>
public static class ToolHierarchy
{
    public static readonly Dictionary<string, CategoryInfo> Categories = new()
    {
        ["lifecycle"] = new("Memory Lifecycle", "Update, delete, merge, split, decay, reinforce, expire, supersede memories"),
        ["observability"] = new("Observability", "Trace event history, lineage, explain state, find conflicts"),
        ["safety"] = new("Safety & Integrity", "Detect contradictions, hallucinations, verify hashes, scan loops"),
        ["export"] = new("Export", "Export workspace, memories, graph, user profile, markdown vault"),
        ["reasoning"] = new("Engineering Reasoning", "Analyze graphs, visualize, multi-model reasoning"),
        ["session"] = new("Session Management", "Create/end sessions, instantiate context"),
        ["admin"] = new("Administration", "Persona, integrations, import, crawl, statistics, model info, reembed")
    };

    /// <summary>
    /// Maps category.tool_name -> actual MCP tool name for dispatch.
    /// </summary>
    public static readonly Dictionary<string, string> ToolMap = new()
    {
        // Lifecycle
        ["lifecycle.memory_update"] = "memory_update",
        ["lifecycle.memory_delete"] = "memory_delete",
        ["lifecycle.memory_merge"] = "memory_merge",
        ["lifecycle.memory_split"] = "memory_split",
        ["lifecycle.memory_decay"] = "memory_decay",
        ["lifecycle.memory_reinforce"] = "memory_reinforce",
        ["lifecycle.memory_expire"] = "memory_expire",
        ["lifecycle.memory_supersede"] = "memory_supersede",

        // Observability
        ["observability.memory_trace"] = "memory_trace",
        ["observability.memory_lineage"] = "memory_lineage",
        ["observability.memory_explain"] = "memory_explain",
        ["observability.memory_conflicts"] = "memory_conflicts",

        // Safety
        ["safety.detect_contradictions"] = "detect_contradictions",
        ["safety.detect_hallucinations"] = "detect_hallucinations",
        ["safety.verify_memory_integrity"] = "verify_memory_integrity",
        ["safety.scan_loops"] = "scan_loops",

        // Export
        ["export.export_workspace"] = "export_workspace",
        ["export.export_memories"] = "export_memories",
        ["export.export_graph"] = "export_graph",
        ["export.export_user_profile"] = "export_user_profile",
        ["export.export_markdown"] = "export_markdown",

        // Reasoning
        ["reasoning.engineering_analyze"] = "engineering_analyze",
        ["reasoning.engineering_visualize"] = "engineering_visualize",
        ["reasoning.engineering_reason"] = "engineering_reason",

        // Session
        ["session.initialise_conversation_session"] = "initialise_conversation_session",
        ["session.end_conversation_session"] = "end_conversation_session",
        ["session.instantiate_context"] = "instantiate_context",

        // Admin
        ["admin.set_user_persona"] = "set_user_persona",
        ["admin.get_integrations"] = "get_integrations",
        ["admin.import_from_core"] = "import_from_core",
        ["admin.crawl_relationships"] = "crawl_relationships",
        ["admin.get_graph_statistics"] = "get_graph_statistics",
        ["admin.get_model_info"] = "get_model_info",
        ["admin.reembed_memories"] = "reembed_memories"
    };

    /// <summary>
    /// Returns tool definitions for a given category.
    /// </summary>
    public static object[] GetToolsForCategory(string category) => category.ToLowerInvariant() switch
    {
        "lifecycle" => ToolDefinitions.GetLifecycleTools(),
        "observability" => ToolDefinitions.GetObservabilityTools(),
        "safety" => ToolDefinitions.GetSafetyTools(),
        "export" => ToolDefinitions.GetExportTools(),
        "reasoning" => ToolDefinitions.GetReasoningTools(),
        "session" => GetSessionTools(),
        "admin" => GetAdminTools(),
        _ => []
    };

    /// <summary>
    /// Session management tools (defined inline since they're in Program.cs core tools).
    /// Returns just name + description for browsing, not full schemas.
    /// </summary>
    private static object[] GetSessionTools() =>
    [
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
        new
        {
            name = "instantiate_context",
            description = "Retrieve and summarize memories from the previous day(s) to continue where you left off.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    project_or_subject = new { type = "string", description = "Optional project name or subject to filter" },
                    days_back = new { type = "integer", @default = 3, description = "Number of days to look back" },
                    limit = new { type = "integer", @default = 50, description = "Maximum memories to retrieve" },
                    include_entities = new { type = "boolean", @default = true, description = "Include linked entities" }
                }
            }
        }
    ];

    /// <summary>
    /// Admin tools (defined inline since they're spread across Program.cs core tools).
    /// </summary>
    private static object[] GetAdminTools() =>
    [
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
                    attribute_key = new { type = "string", description = "Attribute name" },
                    attribute_value = new { type = "string", description = "Attribute value" },
                    confidence = new { type = "number", @default = 1.0, description = "Confidence score (0.0-1.0)" },
                    user_id = new { type = "string", @default = "default_user", description = "User identifier" }
                },
                required = new[] { "attribute_type", "attribute_key", "attribute_value" }
            }
        },
        new
        {
            name = "get_integrations",
            description = "List available integrations.",
            inputSchema = new { type = "object", properties = new { } }
        },
        new
        {
            name = "import_from_core",
            description = "Import entities, relations, and observations from CORE MCP export format.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    data = new { type = "object", description = "CORE export data with 'entities' and 'relations' arrays" },
                    source = new { type = "string", @default = "core-import", description = "Source identifier" }
                },
                required = new[] { "data" }
            }
        },
        new
        {
            name = "crawl_relationships",
            description = "Crawl existing memories to extract entities and relationships.",
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
        new
        {
            name = "get_graph_statistics",
            description = "Get statistics about the knowledge graph.",
            inputSchema = new { type = "object", properties = new { } }
        },
        new
        {
            name = "get_model_info",
            description = "Get information about the current embedding model.",
            inputSchema = new { type = "object", properties = new { } }
        },
        new
        {
            name = "reembed_memories",
            description = "Re-generate embeddings for memories after switching models.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    force_all = new { type = "boolean", @default = false, description = "Re-embed ALL memories" },
                    batch_size = new { type = "integer", @default = 100, description = "Number of memories to process" }
                }
            }
        }
    ];

    /// <summary>
    /// The meta-tools that are always listed: get_tools_in_category + execute_tool
    /// </summary>
    public static object[] GetMetaTools() =>
    [
        new
        {
            name = "get_tools_in_category",
            description = "Browse available SerialMemory tools by category. Call with no path for root categories. Categories: lifecycle, observability, safety, export, reasoning, session, admin.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    path = new { type = "string", description = "Category path (empty for root, e.g. 'lifecycle', 'safety')" }
                }
            }
        },
        new
        {
            name = "execute_tool",
            description = "Execute a SerialMemory tool by its category path. Use get_tools_in_category first to discover tools and their parameters.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    tool_path = new { type = "string", description = "Tool path (e.g. 'lifecycle.memory_update', 'safety.detect_contradictions')" },
                    arguments = new { type = "object", description = "Tool arguments as JSON object" }
                },
                required = new[] { "tool_path" }
            }
        }
    ];
}

public record CategoryInfo(string Title, string Description);
