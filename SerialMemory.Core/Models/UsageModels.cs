namespace SerialMemory.Core.Models;

/// <summary>
/// Usage event types for metering API calls.
/// Every MCP tool must have a corresponding event type.
/// </summary>
public enum UsageEventType
{
    // Core memory operations
    MemoryIngest,
    MemorySearch,
    MemoryMultiHopSearch,

    // Lifecycle operations
    MemoryUpdate,
    MemoryDelete,
    MemoryMerge,
    MemorySplit,
    MemoryDecay,
    MemoryReinforce,
    MemoryExpire,
    MemorySupersede,

    // Graph operations
    CrawlRelationships,
    GetGraphStatistics,

    // Export operations
    ExportWorkspace,
    ExportMemories,
    ExportGraph,
    ExportUserProfile,
    ExportMarkdown,

    // Model operations
    ReembedMemories,
    GetModelInfo,

    // User/session operations
    MemoryAboutUser,
    SetUserPersona,
    InitialiseSession,
    EndSession,
    InstantiateContext,

    // Integration operations
    GetIntegrations,
    ImportFromCore,

    // Observability operations
    MemoryTrace,
    MemoryLineage,
    MemoryExplain,
    MemoryConflicts,

    // Safety operations
    DetectContradictions,
    DetectHallucinations,
    VerifyMemoryIntegrity,
    ScanLoops,

    // Engineering reasoning operations
    EngineeringAnalyze,
    EngineeringVisualize,
    EngineeringReason,

    // Meta-tool operations (lazy-MCP)
    GetToolsInCategory,
    ExecuteTool,

    // LLM operations
    LlmChat,
    Embedding,
    EntityExtraction
}

/// <summary>
/// Credit costs for each operation type.
/// Costs are weighted by computational intensity and resource usage:
/// - Read-only ops: 0.1 - 0.25 credits
/// - Simple mutations: 0.2 - 0.5 credits
/// - Complex operations (embeddings, extraction): 1.0 - 2.0 credits
/// - Batch/export operations: 5.0 - 10.0 credits
/// - Reasoning/analysis: 3.0 - 5.0 credits
/// </summary>
public static class UsageCreditCosts
{
    private static readonly Dictionary<UsageEventType, decimal> Costs = new()
    {
        // Core memory operations (embedding + storage)
        [UsageEventType.MemoryIngest] = 1.0m,           // Embedding generation + DB write
        [UsageEventType.MemorySearch] = 0.25m,          // Vector similarity search
        [UsageEventType.MemoryMultiHopSearch] = 2.0m,   // Multi-hop graph traversal

        // Lifecycle operations
        [UsageEventType.MemoryUpdate] = 0.5m,           // Re-embedding + DB update
        [UsageEventType.MemoryDelete] = 0.1m,           // Soft delete
        [UsageEventType.MemoryMerge] = 1.5m,            // Combine + re-embed
        [UsageEventType.MemorySplit] = 1.5m,            // Split + multiple embeddings
        [UsageEventType.MemoryDecay] = 0.1m,            // Simple confidence update
        [UsageEventType.MemoryReinforce] = 0.1m,        // Simple confidence update
        [UsageEventType.MemoryExpire] = 0.1m,           // Mark as expired

        // Graph operations
        [UsageEventType.CrawlRelationships] = 1.0m,     // Entity extraction per memory
        [UsageEventType.GetGraphStatistics] = 0.1m,     // Aggregate query

        // Export operations (heavy I/O)
        [UsageEventType.ExportWorkspace] = 10.0m,       // Full workspace export
        [UsageEventType.ExportMemories] = 5.0m,         // Memory subset export
        [UsageEventType.ExportGraph] = 5.0m,            // Graph export
        [UsageEventType.ExportUserProfile] = 2.0m,      // User profile export

        // Model operations
        [UsageEventType.ReembedMemories] = 5.0m,        // Batch re-embedding
        [UsageEventType.GetModelInfo] = 0.0m,           // Free - just info

        // User/session operations
        [UsageEventType.MemoryAboutUser] = 0.1m,        // Simple query
        [UsageEventType.SetUserPersona] = 0.1m,         // Simple write
        [UsageEventType.InitialiseSession] = 0.0m,      // Free - session start
        [UsageEventType.EndSession] = 0.0m,             // Free - session end
        [UsageEventType.InstantiateContext] = 0.5m,     // Date range query + entity loading

        // Integration operations
        [UsageEventType.GetIntegrations] = 0.0m,        // Free - just info
        [UsageEventType.ImportFromCore] = 3.0m,         // Batch import with embeddings

        // Observability operations
        [UsageEventType.MemoryTrace] = 0.1m,            // Event log query
        [UsageEventType.MemoryLineage] = 0.25m,         // Graph traversal
        [UsageEventType.MemoryExplain] = 0.25m,         // Analysis query
        [UsageEventType.MemoryConflicts] = 0.25m,       // Conflict detection

        // Safety operations
        [UsageEventType.DetectContradictions] = 1.0m,   // Semantic comparison
        [UsageEventType.DetectHallucinations] = 0.5m,   // Confidence analysis
        [UsageEventType.VerifyMemoryIntegrity] = 0.25m, // Hash verification
        [UsageEventType.ScanLoops] = 0.5m,              // Graph cycle detection

        // Engineering reasoning operations (compute intensive)
        [UsageEventType.EngineeringAnalyze] = 3.0m,     // Graph analysis
        [UsageEventType.EngineeringVisualize] = 2.0m,   // Visualization data
        [UsageEventType.EngineeringReason] = 5.0m,      // Multi-model reasoning

        // LLM operations
        [UsageEventType.LlmChat] = 2.0m,                // Chat completion
        [UsageEventType.Embedding] = 0.5m,              // Single embedding
        [UsageEventType.EntityExtraction] = 1.5m        // Entity extraction
    };

    public static decimal GetCost(UsageEventType eventType) =>
        Costs.TryGetValue(eventType, out var cost) ? cost : 1.0m;

    /// <summary>
    /// Get all credit costs for display/billing purposes.
    /// </summary>
    public static IReadOnlyDictionary<UsageEventType, decimal> GetAllCosts() => Costs;

    public static string ToSnakeCase(UsageEventType eventType) => eventType switch
    {
        UsageEventType.MemoryIngest => "memory_ingest",
        UsageEventType.MemorySearch => "memory_search",
        UsageEventType.MemoryMultiHopSearch => "memory_multi_hop_search",
        UsageEventType.MemoryUpdate => "memory_update",
        UsageEventType.MemoryDelete => "memory_delete",
        UsageEventType.MemoryMerge => "memory_merge",
        UsageEventType.MemorySplit => "memory_split",
        UsageEventType.MemoryDecay => "memory_decay",
        UsageEventType.MemoryReinforce => "memory_reinforce",
        UsageEventType.MemoryExpire => "memory_expire",
        UsageEventType.CrawlRelationships => "crawl_relationships",
        UsageEventType.GetGraphStatistics => "get_graph_statistics",
        UsageEventType.ExportWorkspace => "export_workspace",
        UsageEventType.ExportMemories => "export_memories",
        UsageEventType.ExportGraph => "export_graph",
        UsageEventType.ExportUserProfile => "export_user_profile",
        UsageEventType.ReembedMemories => "reembed_memories",
        UsageEventType.GetModelInfo => "get_model_info",
        UsageEventType.MemoryAboutUser => "memory_about_user",
        UsageEventType.SetUserPersona => "set_user_persona",
        UsageEventType.InitialiseSession => "initialise_session",
        UsageEventType.EndSession => "end_session",
        UsageEventType.InstantiateContext => "instantiate_context",
        UsageEventType.GetIntegrations => "get_integrations",
        UsageEventType.ImportFromCore => "import_from_core",
        UsageEventType.MemoryTrace => "memory_trace",
        UsageEventType.MemoryLineage => "memory_lineage",
        UsageEventType.MemoryExplain => "memory_explain",
        UsageEventType.MemoryConflicts => "memory_conflicts",
        UsageEventType.DetectContradictions => "detect_contradictions",
        UsageEventType.DetectHallucinations => "detect_hallucinations",
        UsageEventType.VerifyMemoryIntegrity => "verify_memory_integrity",
        UsageEventType.ScanLoops => "scan_loops",
        UsageEventType.EngineeringAnalyze => "engineering_analyze",
        UsageEventType.EngineeringVisualize => "engineering_visualize",
        UsageEventType.EngineeringReason => "engineering_reason",
        UsageEventType.LlmChat => "llm_chat",
        UsageEventType.Embedding => "embedding",
        UsageEventType.EntityExtraction => "entity_extraction",
        _ => eventType.ToString().ToLowerInvariant()
    };

    public static UsageEventType? FromSnakeCase(string snakeCase) => snakeCase switch
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
        "get_graph_statistics" => UsageEventType.GetGraphStatistics,
        "export_workspace" => UsageEventType.ExportWorkspace,
        "export_memories" => UsageEventType.ExportMemories,
        "export_graph" => UsageEventType.ExportGraph,
        "export_user_profile" => UsageEventType.ExportUserProfile,
        "reembed_memories" => UsageEventType.ReembedMemories,
        "get_model_info" => UsageEventType.GetModelInfo,
        "memory_about_user" => UsageEventType.MemoryAboutUser,
        "set_user_persona" => UsageEventType.SetUserPersona,
        "initialise_session" => UsageEventType.InitialiseSession,
        "end_session" => UsageEventType.EndSession,
        "instantiate_context" => UsageEventType.InstantiateContext,
        "get_integrations" => UsageEventType.GetIntegrations,
        "import_from_core" => UsageEventType.ImportFromCore,
        "memory_trace" => UsageEventType.MemoryTrace,
        "memory_lineage" => UsageEventType.MemoryLineage,
        "memory_explain" => UsageEventType.MemoryExplain,
        "memory_conflicts" => UsageEventType.MemoryConflicts,
        "detect_contradictions" => UsageEventType.DetectContradictions,
        "detect_hallucinations" => UsageEventType.DetectHallucinations,
        "verify_memory_integrity" => UsageEventType.VerifyMemoryIntegrity,
        "scan_loops" => UsageEventType.ScanLoops,
        "engineering_analyze" => UsageEventType.EngineeringAnalyze,
        "engineering_visualize" => UsageEventType.EngineeringVisualize,
        "engineering_reason" => UsageEventType.EngineeringReason,
        "llm_chat" => UsageEventType.LlmChat,
        "embedding" => UsageEventType.Embedding,
        "entity_extraction" => UsageEventType.EntityExtraction,
        _ => null
    };
}

/// <summary>
/// Individual usage event record.
/// </summary>
public sealed class UsageEvent
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = "self";
    public string WorkspaceId { get; set; } = "default";
    public Guid? BillingCycleId { get; set; }
    public UsageEventType EventType { get; set; }
    public decimal CreditsConsumed { get; set; }
    public DateTimeOffset EventTimestamp { get; set; } = DateTimeOffset.UtcNow;
    public Guid? MemoryId { get; set; }
    public string? UserId { get; set; }
    public Guid? SessionId { get; set; }
    public int? LatencyMs { get; set; }
    public bool Success { get; set; } = true;
    public string? ErrorMessage { get; set; }
    public Dictionary<string, object>? Metadata { get; set; }
}

/// <summary>
/// Daily rollup of usage statistics.
/// </summary>
public sealed class UsageDailyRollup
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = "self";
    public string WorkspaceId { get; set; } = "default";
    public DateOnly RollupDate { get; set; }
    public UsageEventType EventType { get; set; }
    public int EventCount { get; set; }
    public decimal TotalCredits { get; set; }
    public int SuccessCount { get; set; }
    public int FailureCount { get; set; }
    public decimal? AvgLatencyMs { get; set; }
    public int? P95LatencyMs { get; set; }
    public int? P99LatencyMs { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// Billing cycle for tracking credit usage periods.
/// </summary>
public sealed class BillingCycle
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = "self";
    public string WorkspaceId { get; set; } = "default";
    public Guid? PlanId { get; set; }
    public DateTimeOffset CycleStart { get; set; }
    public DateTimeOffset CycleEnd { get; set; }
    public decimal CreditsAllocated { get; set; }
    public decimal CreditsUsed { get; set; }
    public decimal CreditsRemaining => CreditsAllocated - CreditsUsed;
    public bool IsCurrent { get; set; } = true;
    public DateTimeOffset? ClosedAt { get; set; }
    public Dictionary<string, object>? Metadata { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// Tenant plan defining credit limits and quotas.
/// </summary>
public sealed class TenantPlan
{
    public Guid Id { get; set; }
    public string PlanName { get; set; } = "self";
    public string DisplayName { get; set; } = "Self-Hosted";
    public decimal CreditsPerCycle { get; set; } = 999999999m;
    public int CycleDays { get; set; } = 30;
    public int? MaxMemories { get; set; }
    public int? MaxEntities { get; set; }
    public bool IsActive { get; set; } = true;
    public Dictionary<string, object>? Metadata { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
