namespace SerialMemory.Mcp.Tools;

/// <summary>
/// Lazy-MCP tool hierarchy. Instead of listing all 37+ tools upfront (consuming ~7400 tokens),
/// expose only core tools + 2 meta-tools. Remaining tools are discoverable on demand.
/// Saves ~84% of tool listing overhead.
/// </summary>
public static class ToolHierarchy
{
    /// <summary>
    /// Ordered category list for consistent display.
    /// </summary>
    private static readonly (string Key, CategoryInfo Info)[] OrderedCategories =
    [
        ("lifecycle", new("Memory Lifecycle", "Update, delete, merge, split, decay, reinforce, expire, supersede memories")),
        ("observability", new("Observability", "Trace event history, lineage, explain state, find conflicts")),
        ("safety", new("Safety & Integrity", "Detect contradictions, hallucinations, verify hashes, scan loops")),
        ("export", new("Export", "Export workspace, memories, graph, user profile, markdown vault")),
        ("reasoning", new("Engineering Reasoning", "Analyze graphs, visualize, multi-model reasoning")),
        ("goals", new("Goals & Intent", "Set, list, and complete goals that persist across sessions")),
        ("session", new("Session Management", "Create/end sessions, instantiate context")),
        ("admin", new("Administration", "Persona, integrations, import, crawl, statistics, model info, reembed")),
        ("workspace", new("Workspace & Snapshots", "Create/switch workspaces, create/load state snapshots")),
        ("capture", new("Auto-Capture", "Drain session captures, check capture buffer status")),
        ("summarization", new("Summarization", "AI-powered session and context summarization"))
    ];

    public static readonly Dictionary<string, CategoryInfo> Categories =
        OrderedCategories.ToDictionary(x => x.Key, x => x.Info);

    /// <summary>
    /// Iterates categories in stable insertion order.
    /// </summary>
    public static IEnumerable<(string Key, CategoryInfo Info)> CategoriesOrdered => OrderedCategories;

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

        // Goals
        ["goals.goal_set"] = "goal_set",
        ["goals.goal_list"] = "goal_list",
        ["goals.goal_complete"] = "goal_complete",

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
        ["admin.reembed_memories"] = "reembed_memories",

        // Workspace & Snapshots
        ["workspace.workspace_create"] = "workspace_create",
        ["workspace.workspace_list"] = "workspace_list",
        ["workspace.workspace_switch"] = "workspace_switch",
        ["workspace.snapshot_create"] = "snapshot_create",
        ["workspace.snapshot_list"] = "snapshot_list",
        ["workspace.snapshot_load"] = "snapshot_load",

        // Auto-Capture
        ["capture.drain_session_captures"] = "drain_session_captures",
        ["capture.capture_status"] = "capture_status",

        // Summarization
        ["summarization.summarize_session"] = "summarize_session",
        ["summarization.summarize_context"] = "summarize_context"
    };

    /// <summary>
    /// Returns tool definitions for a given category.
    /// Session and admin tools are extracted from coreTools by name to avoid schema duplication.
    /// </summary>
    public static object[] GetToolsForCategory(string category, object[]? coreTools = null) =>
        category.ToLowerInvariant() switch
        {
            "lifecycle" => ToolDefinitions.GetLifecycleTools(),
            "observability" => ToolDefinitions.GetObservabilityTools(),
            "safety" => ToolDefinitions.GetSafetyTools(),
            "export" => ToolDefinitions.GetExportTools(),
            "reasoning" => ToolDefinitions.GetReasoningTools(),
            "goals" => ToolDefinitions.GetGoalTools(),
            "session" => FilterCoreToolsByName(coreTools,
                "initialise_conversation_session", "end_conversation_session", "instantiate_context"),
            "admin" => FilterCoreToolsByName(coreTools,
                "set_user_persona", "get_integrations", "import_from_core",
                "crawl_relationships", "get_graph_statistics", "get_model_info", "reembed_memories"),
            "workspace" => ToolDefinitions.GetWorkspaceTools(),
            "capture" => ToolDefinitions.GetCaptureTools(),
            "summarization" => ToolDefinitions.GetSummarizationTools(),
            _ => []
        };

    /// <summary>
    /// Filters core tools array by name to extract session/admin subsets.
    /// Falls back to empty array if coreTools is null.
    /// </summary>
    private static object[] FilterCoreToolsByName(object[]? coreTools, params string[] names)
    {
        if (coreTools == null) return [];
        var nameSet = new HashSet<string>(names);
        return coreTools.Where(t => nameSet.Contains(((dynamic)t).name)).ToArray();
    }

    /// <summary>
    /// The meta-tools that are always listed: get_tools_in_category + execute_tool
    /// </summary>
    public static object[] GetMetaTools() =>
    [
        new
        {
            name = "get_tools_in_category",
            description = "Browse available SerialMemory tools by category. Call with no path for root categories. Categories: lifecycle, observability, safety, export, reasoning, goals, session, admin, workspace, capture, summarization.",
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
