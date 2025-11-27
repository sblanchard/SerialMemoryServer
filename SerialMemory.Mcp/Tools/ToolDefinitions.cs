namespace SerialMemory.Mcp.Tools;

/// <summary>
/// MCP tool definitions for all new lifecycle, observability, safety, and export tools.
/// </summary>
public static class ToolDefinitions
{
    public static object[] GetLifecycleTools() =>
    [
        // memory_update
        new
        {
            name = "memory_update",
            description = "Update memory content with new embedding. Creates MemoryUpdated event. Does not mutate original - creates new version.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    memory_id = new { type = "string", description = "UUID of memory to update" },
                    new_content = new { type = "string", description = "New content to replace existing" },
                    reason = new { type = "string", description = "Reason for update (audit trail)" },
                    actor_id = new { type = "string", description = "ID of actor making the update" }
                },
                required = new[] { "memory_id", "new_content" }
            }
        },
        // memory_delete
        new
        {
            name = "memory_delete",
            description = "Soft delete (invalidate) a memory. No hard deletes - memory remains for audit. Creates MemoryInvalidated event.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    memory_id = new { type = "string", description = "UUID of memory to soft delete" },
                    reason = new { type = "string", description = "Reason for deletion (required for audit)" },
                    superseded_by_id = new { type = "string", description = "UUID of memory that supersedes this one" },
                    actor_id = new { type = "string", description = "ID of actor making the deletion" }
                },
                required = new[] { "memory_id", "reason" }
            }
        },
        // memory_merge
        new
        {
            name = "memory_merge",
            description = "Merge multiple memories into a single new memory. Source memories are soft deleted. Creates new memory with causal parents.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    source_memory_ids = new { type = "array", items = new { type = "string" }, description = "UUIDs of memories to merge (min 2)" },
                    merged_content = new { type = "string", description = "Combined content for new memory" },
                    strategy = new { type = "string", description = "Merge strategy (e.g., 'concatenate', 'summarize', 'manual')" },
                    actor_id = new { type = "string", description = "ID of actor performing merge" }
                },
                required = new[] { "source_memory_ids", "merged_content" }
            }
        },
        // memory_split
        new
        {
            name = "memory_split",
            description = "Split a memory into multiple child memories. Parent is marked as split (inactive). Children reference parent as causal parent.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    memory_id = new { type = "string", description = "UUID of memory to split" },
                    child_contents = new { type = "array", items = new { type = "string" }, description = "Content for each child memory (min 2)" },
                    strategy = new { type = "string", description = "Split strategy (e.g., 'semantic', 'temporal', 'manual')" },
                    reason = new { type = "string", description = "Reason for split" },
                    actor_id = new { type = "string", description = "ID of actor performing split" }
                },
                required = new[] { "memory_id", "child_contents" }
            }
        },
        // memory_decay
        new
        {
            name = "memory_decay",
            description = "Apply time-based confidence decay to a memory using exponential decay formula: confidence * 0.5^(days/half_life).",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    memory_id = new { type = "string", description = "UUID of memory to decay" },
                    actor_id = new { type = "string", description = "ID of actor/system applying decay" }
                },
                required = new[] { "memory_id" }
            }
        },
        // memory_reinforce
        new
        {
            name = "memory_reinforce",
            description = "Reinforce a memory - reset decay timer and optionally boost confidence. Use when memory is validated or frequently accessed.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    memory_id = new { type = "string", description = "UUID of memory to reinforce" },
                    confidence = new { type = "number", @default = 1.0, description = "New confidence score (0.0-1.0)" },
                    source = new { type = "string", @default = "manual", description = "Source of reinforcement (e.g., 'user_validation', 'frequent_access')" },
                    validated_by_ids = new { type = "array", items = new { type = "string" }, description = "UUIDs of validating memories" },
                    actor_id = new { type = "string", description = "ID of actor performing reinforcement" }
                },
                required = new[] { "memory_id" }
            }
        },
        // memory_expire
        new
        {
            name = "memory_expire",
            description = "Expire a memory based on TTL policy. Different from decay - this is a hard cutoff. Memory becomes inactive.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    memory_id = new { type = "string", description = "UUID of memory to expire" },
                    policy = new { type = "string", @default = "manual", description = "Expiration policy name" },
                    ttl_days = new { type = "integer", description = "Original TTL in days (for audit)" },
                    actor_id = new { type = "string", description = "ID of actor/system expiring memory" }
                },
                required = new[] { "memory_id" }
            }
        }
    ];

    public static object[] GetObservabilityTools() =>
    [
        // memory_trace
        new
        {
            name = "memory_trace",
            description = "Get complete event history for a memory. Shows all mutations in chronological order.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    memory_id = new { type = "string", description = "UUID of memory to trace" },
                    include_payloads = new { type = "boolean", @default = false, description = "Include full event payloads" }
                },
                required = new[] { "memory_id" }
            }
        },
        // memory_lineage
        new
        {
            name = "memory_lineage",
            description = "Trace causal ancestry and descendants of a memory through causal_parents relationships.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    memory_id = new { type = "string", description = "UUID of memory to trace lineage" },
                    max_depth = new { type = "integer", @default = 5, description = "Maximum depth to traverse (1-10)" },
                    direction = new { type = "string", @enum = new[] { "ancestors", "descendants", "both" }, @default = "ancestors", description = "Direction to trace" }
                },
                required = new[] { "memory_id" }
            }
        },
        // memory_explain
        new
        {
            name = "memory_explain",
            description = "Explain current state of a memory - why it's active/inactive, confidence calculations, relationships, and recommendations.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    memory_id = new { type = "string", description = "UUID of memory to explain" }
                },
                required = new[] { "memory_id" }
            }
        },
        // memory_conflicts
        new
        {
            name = "memory_conflicts",
            description = "Find all conflicts/contradictions involving a memory or list all unresolved conflicts.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    memory_id = new { type = "string", description = "UUID of memory to check (optional - if omitted, returns all unresolved)" },
                    limit = new { type = "integer", @default = 50, description = "Maximum conflicts to return" }
                }
            }
        }
    ];

    public static object[] GetSafetyTools() =>
    [
        // detect_contradictions
        new
        {
            name = "detect_contradictions",
            description = "Find memories that contradict each other using semantic similarity and content analysis.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    memory_id = new { type = "string", description = "UUID to check for contradictions (optional - if omitted, batch scan)" },
                    similarity_threshold = new { type = "number", @default = 0.85, description = "Minimum similarity to consider (0.5-0.99)" },
                    limit = new { type = "integer", @default = 20, description = "Maximum contradictions to return" },
                    auto_flag = new { type = "boolean", @default = false, description = "Automatically flag detected contradictions in database" }
                }
            }
        },
        // detect_hallucinations
        new
        {
            name = "detect_hallucinations",
            description = "Flag potential hallucinations based on confidence, validation status, access patterns, and isolation.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    memory_id = new { type = "string", description = "UUID to check (optional - if omitted, batch scan)" },
                    confidence_threshold = new { type = "number", @default = 0.3, description = "Flag memories below this confidence" },
                    limit = new { type = "integer", @default = 20, description = "Maximum results to return" },
                    auto_flag = new { type = "boolean", @default = false, description = "Automatically flag in database" }
                }
            }
        },
        // verify_memory_integrity
        new
        {
            name = "verify_memory_integrity",
            description = "Verify content hash integrity for memories. Detects content corruption.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    memory_id = new { type = "string", description = "UUID to verify (optional - if omitted, batch verify)" },
                    limit = new { type = "integer", @default = 100, description = "Maximum memories to check" },
                    fix_corrupted = new { type = "boolean", @default = false, description = "Automatically recompute hashes for corrupted entries" }
                }
            }
        },
        // scan_loops
        new
        {
            name = "scan_loops",
            description = "Detect cycles in causal parent relationships (loop detection). Cycles can cause infinite recursion.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    max_depth = new { type = "integer", @default = 10, description = "Maximum depth to search (1-20)" },
                    limit = new { type = "integer", @default = 50, description = "Maximum loops to return" }
                }
            }
        }
    ];

    public static object[] GetExportTools() =>
    [
        // export_workspace
        new
        {
            name = "export_workspace",
            description = "Export entire workspace - memories, entities, relationships, and optionally events. Supports encryption and compression.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    output_path = new { type = "string", description = "Output file path (default: workspace_export_YYYYMMDD.json)" },
                    include_events = new { type = "boolean", @default = false, description = "Include raw event store data" },
                    active_only = new { type = "boolean", @default = true, description = "Only export active memories" },
                    encrypt = new { type = "boolean", @default = false, description = "AES-256 encrypt the export" },
                    encryption_key = new { type = "string", description = "Encryption key (required if encrypt=true)" },
                    compress = new { type = "boolean", @default = false, description = "GZip compress the export" }
                }
            }
        },
        // export_memories
        new
        {
            name = "export_memories",
            description = "Export memories with filters. Supports JSON and CSV formats.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    output_path = new { type = "string", description = "Output file path" },
                    layer = new { type = "string", @enum = new[] { "L0_RAW", "L1_CONTEXT", "L2_SUMMARY", "L3_KNOWLEDGE", "L4_HEURISTIC" }, description = "Filter by layer" },
                    min_confidence = new { type = "number", description = "Minimum confidence filter (0.0-1.0)" },
                    from_date = new { type = "string", description = "Start date filter (ISO 8601)" },
                    to_date = new { type = "string", description = "End date filter (ISO 8601)" },
                    limit = new { type = "integer", @default = 10000, description = "Maximum memories to export" },
                    format = new { type = "string", @enum = new[] { "json", "csv" }, @default = "json", description = "Output format" }
                }
            }
        },
        // export_graph
        new
        {
            name = "export_graph",
            description = "Export knowledge graph (entities and relationships). Supports JSON, GraphML, and Cytoscape formats.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    output_path = new { type = "string", description = "Output file path" },
                    format = new { type = "string", @enum = new[] { "json", "graphml", "cytoscape" }, @default = "json", description = "Output format" },
                    include_isolated = new { type = "boolean", @default = false, description = "Include entities with no relationships" }
                }
            }
        },
        // export_user_profile
        new
        {
            name = "export_user_profile",
            description = "Export user persona attributes and memory statistics.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    user_id = new { type = "string", @default = "default_user", description = "User ID to export" },
                    output_path = new { type = "string", description = "Output file path" },
                    include_interactions = new { type = "boolean", @default = false, description = "Include interaction history" }
                }
            }
        }
    ];

    public static object[] GetReasoningTools() =>
    [
        // engineering_analyze
        new
        {
            name = "engineering_analyze",
            description = "Analyze the knowledge graph for engineering insights. Detects power integrity issues (voltage mismatch, overcurrent), signal integrity issues (clock/protocol mismatch), dependency corruption (cascading failures), and thermal risks.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    memory_id = new { type = "string", description = "Optional: analyze entities related to this memory" },
                    project = new { type = "string", description = "Optional: filter analysis to entities connected to this project name" }
                }
            }
        },
        // engineering_visualize
        new
        {
            name = "engineering_visualize",
            description = "Generate graph visualization data with nodes, links, and reasoning overlays. Returns JSON suitable for react-force-graph-3d rendering.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    memory_id = new { type = "string", description = "Optional: visualize entities related to this memory" },
                    project = new { type = "string", description = "Optional: filter to entities connected to this project name" },
                    mode = new { type = "string", @enum = new[] { "software", "hardware", "mixed" }, @default = "mixed", description = "Visualization mode filter" },
                    include_overlays = new { type = "boolean", @default = true, description = "Include reasoning-based risk/warning overlays" }
                }
            }
        },
        // engineering_reason
        new
        {
            name = "engineering_reason",
            description = "Run multi-model reasoning on the knowledge graph. Executes multiple reasoning models in parallel (Structural, Risk, Optimization, Contradiction) and merges results by confidence and agreement. Returns traced insights with source model attribution.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    memory_id = new { type = "string", description = "Optional: reason over entities related to this memory" },
                    project = new { type = "string", description = "Optional: filter reasoning to entities connected to this project name" },
                    max_duration_ms = new { type = "integer", @default = 30000, description = "Maximum reasoning time in milliseconds (default: 30000)" }
                }
            }
        }
    ];
}
