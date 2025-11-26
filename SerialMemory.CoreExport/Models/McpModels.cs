using System.Text.Json.Serialization;

namespace SerialMemory.CoreExport.Models;

/// <summary>
/// Search request for CORE API
/// POST /api/v1/search
/// </summary>
public class CoreSearchRequest
{
    [JsonPropertyName("query")]
    public string Query { get; set; } = "*";

    [JsonPropertyName("limit")]
    public int? Limit { get; set; }

    [JsonPropertyName("startTime")]
    public string? StartTime { get; set; }

    [JsonPropertyName("endTime")]
    public string? EndTime { get; set; }

    [JsonPropertyName("labelIds")]
    public List<string>? LabelIds { get; set; }

    [JsonPropertyName("maxBfsDepth")]
    public int? MaxBfsDepth { get; set; }

    [JsonPropertyName("includeInvalidated")]
    public bool? IncludeInvalidated { get; set; }

    [JsonPropertyName("entityTypes")]
    public List<string>? EntityTypes { get; set; }

    [JsonPropertyName("scoreThreshold")]
    public float? ScoreThreshold { get; set; }

    [JsonPropertyName("minResults")]
    public int? MinResults { get; set; }

    [JsonPropertyName("adaptiveFiltering")]
    public bool? AdaptiveFiltering { get; set; }

    [JsonPropertyName("structured")]
    public bool? Structured { get; set; }

    [JsonPropertyName("sortBy")]
    public string? SortBy { get; set; } // "relevance" or "recency"
}

/// <summary>
/// Search response from CORE API
/// </summary>
public class CoreSearchResponse
{
    [JsonPropertyName("episodes")]
    public List<CoreEpisode> Episodes { get; set; } = new();

    [JsonPropertyName("nextCursor")]
    public string? NextCursor { get; set; }

    [JsonPropertyName("totalCount")]
    public int? TotalCount { get; set; }
}

/// <summary>
/// Episode (memory) from CORE API
/// </summary>
public class CoreEpisode
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTime? UpdatedAt { get; set; }

    [JsonPropertyName("metadata")]
    public Dictionary<string, object>? Metadata { get; set; }

    [JsonPropertyName("score")]
    public float? Score { get; set; }

    [JsonPropertyName("statements")]
    public List<CoreStatement>? Statements { get; set; }

    [JsonPropertyName("entities")]
    public List<CoreEntityRef>? Entities { get; set; }
}

/// <summary>
/// Statement (fact/relation) from CORE API
/// </summary>
public class CoreStatement
{
    [JsonPropertyName("subject")]
    public string? Subject { get; set; }

    [JsonPropertyName("predicate")]
    public string? Predicate { get; set; }

    [JsonPropertyName("object")]
    public string? Object { get; set; }

    [JsonPropertyName("score")]
    public float? Score { get; set; }
}

/// <summary>
/// Entity reference from CORE API
/// </summary>
public class CoreEntityRef
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }
}

/// <summary>
/// Space (workspace) from CORE API
/// </summary>
public class CoreSpace
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }
}

/// <summary>
/// Response from GET /v1/graph/clustered
/// </summary>
public class CoreGraphResponse
{
    [JsonPropertyName("entities")]
    public List<CoreGraphEntity>? Entities { get; set; }

    [JsonPropertyName("relations")]
    public List<CoreGraphRelation>? Relations { get; set; }

    [JsonPropertyName("clusters")]
    public List<CoreGraphCluster>? Clusters { get; set; }
}

/// <summary>
/// Entity from graph endpoint
/// </summary>
public class CoreGraphEntity
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("observations")]
    public List<string>? Observations { get; set; }

    [JsonPropertyName("episodeCount")]
    public int? EpisodeCount { get; set; }
}

/// <summary>
/// Relation from graph endpoint
/// </summary>
public class CoreGraphRelation
{
    [JsonPropertyName("from")]
    public string? From { get; set; }

    [JsonPropertyName("to")]
    public string? To { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("weight")]
    public float? Weight { get; set; }
}

/// <summary>
/// Cluster from graph endpoint
/// </summary>
public class CoreGraphCluster
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("entities")]
    public List<string>? Entities { get; set; }
}

/// <summary>
/// Response from memory_search tool
/// </summary>
public class McpMemorySearchResponse
{
    [JsonPropertyName("memories")]
    public List<McpMemory> Memories { get; set; } = new();

    [JsonPropertyName("next_cursor")]
    public string? NextCursor { get; set; }

    [JsonPropertyName("total_count")]
    public int? TotalCount { get; set; }
}

/// <summary>
/// Memory item from CORE MCP API
/// </summary>
public class McpMemory
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTime? UpdatedAt { get; set; }

    [JsonPropertyName("metadata")]
    public Dictionary<string, object>? Metadata { get; set; }

    [JsonPropertyName("embedding")]
    public float[]? Embedding { get; set; }

    [JsonPropertyName("score")]
    public float? Score { get; set; }

    [JsonPropertyName("entities")]
    public List<McpEntity>? Entities { get; set; }

    [JsonPropertyName("relations")]
    public List<McpRelation>? Relations { get; set; }
}

/// <summary>
/// Entity from CORE MCP API
/// </summary>
public class McpEntity
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("entityType")]
    public string EntityType { get; set; } = string.Empty;

    [JsonPropertyName("observations")]
    public List<string>? Observations { get; set; }
}

/// <summary>
/// Relation from CORE MCP API
/// </summary>
public class McpRelation
{
    [JsonPropertyName("from")]
    public string From { get; set; } = string.Empty;

    [JsonPropertyName("to")]
    public string To { get; set; } = string.Empty;

    [JsonPropertyName("relationType")]
    public string RelationType { get; set; } = string.Empty;
}

/// <summary>
/// Response from list_workspaces or similar tool
/// </summary>
public class McpWorkspaceListResponse
{
    [JsonPropertyName("workspaces")]
    public List<McpWorkspace> Workspaces { get; set; } = new();
}

/// <summary>
/// Workspace from CORE MCP API
/// </summary>
public class McpWorkspace
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("memory_count")]
    public int? MemoryCount { get; set; }
}

/// <summary>
/// Generic MCP response wrapper
/// </summary>
public class McpResponse<T>
{
    [JsonPropertyName("result")]
    public T? Result { get; set; }

    [JsonPropertyName("error")]
    public McpError? Error { get; set; }

    [JsonPropertyName("success")]
    public bool Success { get; set; }
}

/// <summary>
/// MCP error response
/// </summary>
public class McpError
{
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

/// <summary>
/// Response for full export - all memories without search
/// </summary>
public class McpExportResponse
{
    [JsonPropertyName("memories")]
    public List<McpMemory> Memories { get; set; } = new();

    [JsonPropertyName("entities")]
    public List<McpEntity> Entities { get; set; } = new();

    [JsonPropertyName("relations")]
    public List<McpRelation> Relations { get; set; } = new();

    [JsonPropertyName("next_cursor")]
    public string? NextCursor { get; set; }

    [JsonPropertyName("total_count")]
    public int? TotalCount { get; set; }
}
