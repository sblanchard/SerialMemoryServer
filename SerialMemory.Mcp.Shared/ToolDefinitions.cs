namespace SerialMemory.Mcp.Shared;

/// <summary>
/// Compact MCP tool definitions optimized for context size (~100-150 tokens per tool).
/// </summary>
public static class ToolDefinitions
{
    // =================================================================
    // CORE TOOLS (serialmemory-core) - Essential daily operations
    // =================================================================

    public static object[] GetCoreTools() =>
    [
        new
        {
            name = "memory_search",
            description = "Search memories by semantic similarity, keywords, or both. Returns ranked results with entities.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    query = new { type = "string", description = "Search query" },
                    mode = new { type = "string", @enum = new[] { "semantic", "text", "hybrid" }, @default = "hybrid" },
                    limit = new { type = "integer", @default = 10 },
                    threshold = new { type = "number", @default = (int)0.7 }
                },
                required = new[] { "query" }
            }
        },
        new
        {
            name = "memory_ingest",
            description = "Store new memory with auto-extracted entities and embeddings. Use for decisions, context, learnings.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    content = new { type = "string", description = "Memory content" },
                    source = new { type = "string" },
                    extract_entities = new { type = "boolean", @default = true }
                },
                required = new[] { "content" }
            }
        },
        new
        {
            name = "memory_about_user",
            description = "Get user persona: preferences, skills, goals, background.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    user_id = new { type = "string", @default = "default_user" }
                }
            }
        },
        new
        {
            name = "set_user_persona",
            description = "Set user attribute (preference, skill, goal, background).",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    attribute_type = new { type = "string", @enum = new[] { "preference", "skill", "goal", "background" } },
                    attribute_key = new { type = "string" },
                    attribute_value = new { type = "string" },
                    confidence = new { type = "number", @default = 1.0 }
                },
                required = new[] { "attribute_type", "attribute_key", "attribute_value" }
            }
        },
        new
        {
            name = "memory_update",
            description = "Update memory content. Creates new version, preserves history.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    memory_id = new { type = "string" },
                    new_content = new { type = "string" },
                    reason = new { type = "string" }
                },
                required = new[] { "memory_id", "new_content" }
            }
        },
        new
        {
            name = "memory_delete",
            description = "Soft-delete memory (audit trail preserved).",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    memory_id = new { type = "string" },
                    reason = new { type = "string" }
                },
                required = new[] { "memory_id", "reason" }
            }
        },
        new
        {
            name = "memory_merge",
            description = "Merge multiple memories into one. Sources are soft-deleted.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    source_memory_ids = new { type = "array", items = new { type = "string" } },
                    merged_content = new { type = "string" }
                },
                required = new[] { "source_memory_ids", "merged_content" }
            }
        },
        new
        {
            name = "memory_split",
            description = "Split memory into multiple children. Parent becomes inactive.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    memory_id = new { type = "string" },
                    child_contents = new { type = "array", items = new { type = "string" } }
                },
                required = new[] { "memory_id", "child_contents" }
            }
        },
        new
        {
            name = "memory_reinforce",
            description = "Reinforce memory - reset decay, boost confidence. Use when validated.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    memory_id = new { type = "string" },
                    confidence = new { type = "number", @default = 1.0 }
                },
                required = new[] { "memory_id" }
            }
        },
        new
        {
            name = "memory_expire",
            description = "Expire memory based on TTL policy. Memory becomes inactive.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    memory_id = new { type = "string" },
                    policy = new { type = "string", @default = "manual" }
                },
                required = new[] { "memory_id" }
            }
        },
        new
        {
            name = "initialise_conversation_session",
            description = "Start new conversation session for context tracking.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    session_name = new { type = "string" },
                    client_type = new { type = "string" }
                }
            }
        },
        new
        {
            name = "end_conversation_session",
            description = "End current conversation session.",
            inputSchema = new { type = "object", properties = new { } }
        }
    ];

    // =================================================================
    // ADMIN TOOLS (serialmemory-admin) - Maintenance, diagnostics, export
    // =================================================================

    public static object[] GetAdminTools() =>
    [
        // Graph traversal
        new
        {
            name = "memory_multi_hop_search",
            description = "Multi-hop graph traversal. Find connected memories through entity relationships.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    query = new { type = "string" },
                    hops = new { type = "integer", @default = 2 },
                    max_results_per_hop = new { type = "integer", @default = 5 }
                },
                required = new[] { "query" }
            }
        },
        new
        {
            name = "crawl_relationships",
            description = "Extract entities/relationships from existing memories to populate graph.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    batch_size = new { type = "integer", @default = 100 },
                    force_reprocess = new { type = "boolean", @default = false }
                }
            }
        },
        new
        {
            name = "get_graph_statistics",
            description = "Get graph stats: memory/entity/relationship counts by type.",
            inputSchema = new { type = "object", properties = new { } }
        },

        // Lifecycle maintenance
        new
        {
            name = "memory_decay",
            description = "Apply time-based confidence decay using half-life formula.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    memory_id = new { type = "string" }
                },
                required = new[] { "memory_id" }
            }
        },

        // Observability
        new
        {
            name = "memory_trace",
            description = "Get complete event history for a memory.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    memory_id = new { type = "string" },
                    include_payloads = new { type = "boolean", @default = false }
                },
                required = new[] { "memory_id" }
            }
        },
        new
        {
            name = "memory_lineage",
            description = "Trace causal ancestry/descendants through parent relationships.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    memory_id = new { type = "string" },
                    max_depth = new { type = "integer", @default = 5 },
                    direction = new { type = "string", @enum = new[] { "ancestors", "descendants", "both" }, @default = "ancestors" }
                },
                required = new[] { "memory_id" }
            }
        },
        new
        {
            name = "memory_explain",
            description = "Explain memory state: why active/inactive, confidence, recommendations.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    memory_id = new { type = "string" }
                },
                required = new[] { "memory_id" }
            }
        },
        new
        {
            name = "memory_conflicts",
            description = "Find contradictions involving a memory or list all unresolved.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    memory_id = new { type = "string" },
                    limit = new { type = "integer", @default = 50 }
                }
            }
        },

        // Safety
        new
        {
            name = "detect_contradictions",
            description = "Find contradicting memories using semantic similarity analysis.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    memory_id = new { type = "string" },
                    similarity_threshold = new { type = "number", @default = 0.85 },
                    limit = new { type = "integer", @default = 20 }
                }
            }
        },
        new
        {
            name = "detect_hallucinations",
            description = "Flag potential hallucinations based on confidence and validation.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    memory_id = new { type = "string" },
                    confidence_threshold = new { type = "number", @default = 0.3 },
                    limit = new { type = "integer", @default = 20 }
                }
            }
        },
        new
        {
            name = "verify_memory_integrity",
            description = "Verify content hash integrity. Detects corruption.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    memory_id = new { type = "string" },
                    limit = new { type = "integer", @default = 100 }
                }
            }
        },
        new
        {
            name = "scan_loops",
            description = "Detect cycles in causal relationships (prevents infinite recursion).",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    max_depth = new { type = "integer", @default = 10 },
                    limit = new { type = "integer", @default = 50 }
                }
            }
        },

        // Import/Export
        new
        {
            name = "import_from_core",
            description = "Import entities and relations from CORE MCP export format.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    data = new { type = "object", description = "CORE export with entities[] and relations[]" },
                    source = new { type = "string", @default = "core-import" }
                },
                required = new[] { "data" }
            }
        },
        new
        {
            name = "export_workspace",
            description = "Export entire workspace. Supports encryption and compression.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    output_path = new { type = "string" },
                    include_events = new { type = "boolean", @default = false },
                    encrypt = new { type = "boolean", @default = false },
                    compress = new { type = "boolean", @default = false }
                }
            }
        },
        new
        {
            name = "export_memories",
            description = "Export memories with filters. JSON or CSV format.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    output_path = new { type = "string" },
                    layer = new { type = "string" },
                    min_confidence = new { type = "number" },
                    format = new { type = "string", @enum = new[] { "json", "csv" }, @default = "json" }
                }
            }
        },
        new
        {
            name = "export_graph",
            description = "Export knowledge graph. JSON, GraphML, or Cytoscape format.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    output_path = new { type = "string" },
                    format = new { type = "string", @enum = new[] { "json", "graphml", "cytoscape" }, @default = "json" }
                }
            }
        },
        new
        {
            name = "export_user_profile",
            description = "Export user persona and memory statistics.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    user_id = new { type = "string", @default = "default_user" },
                    output_path = new { type = "string" }
                }
            }
        },

        // Model management
        new
        {
            name = "get_model_info",
            description = "Get embedding model info: name, dimensions, upgrade options.",
            inputSchema = new { type = "object", properties = new { } }
        },
        new
        {
            name = "reembed_memories",
            description = "Re-generate embeddings after model change.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    force_all = new { type = "boolean", @default = false },
                    batch_size = new { type = "integer", @default = 100 }
                }
            }
        },
        new
        {
            name = "get_integrations",
            description = "List available external integrations.",
            inputSchema = new { type = "object", properties = new { } }
        }
    ];
}
