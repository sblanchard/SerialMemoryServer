using Microsoft.Extensions.Logging;
using SerialMemory.Core.Models;
using SerialMemory.Infrastructure;

namespace SerialMemory.Mcp;

/// <summary>
/// Maps MCP tool names to usage event types and fires usage tracking.
/// </summary>
internal sealed class McpToolUsageTracker(
    UsageService usageService,
    McpSessionState sessionState,
    ILogger logger)
{
    public void Track(string? toolName, int latencyMs, bool success, string? errorMessage, Dictionary<string, object>? metadata = null)
    {
        var eventType = MapToolToEventType(toolName);

        if (eventType.HasValue)
        {
            usageService.TrackUsage(
                eventType.Value,
                sessionId: sessionState.CurrentSessionId,
                latencyMs: latencyMs,
                success: success,
                errorMessage: errorMessage,
                metadata: metadata);
        }
        else if (!string.IsNullOrEmpty(toolName))
        {
            logger.LogWarning("Unknown tool not tracked for usage: {ToolName}", toolName);
        }
    }

    private static UsageEventType? MapToolToEventType(string? toolName) => toolName switch
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
}
