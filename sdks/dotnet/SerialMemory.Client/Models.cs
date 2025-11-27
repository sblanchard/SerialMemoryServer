using System.Text.Json.Serialization;

namespace SerialMemory.Client;

#region Search Results

/// <summary>
/// Result of a memory search operation
/// </summary>
public record MemorySearchResult
{
    /// <summary>List of matching memories with scores</summary>
    public List<MemoryMatch> Memories { get; init; } = [];

    /// <summary>Total number of matches found</summary>
    public int TotalCount { get; init; }

    /// <summary>Search mode used</summary>
    public string? Mode { get; init; }
}

/// <summary>
/// A memory match from search results
/// </summary>
public record MemoryMatch
{
    /// <summary>Unique memory ID</summary>
    public Guid Id { get; init; }

    /// <summary>Memory content</summary>
    public string Content { get; init; } = "";

    /// <summary>Similarity score (0.0 - 1.0)</summary>
    public float Similarity { get; init; }

    /// <summary>Full-text search rank</summary>
    public float Rank { get; init; }

    /// <summary>When the memory was created</summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>Source of the memory</summary>
    public string? Source { get; init; }

    /// <summary>Linked entities (if includeEntities=true)</summary>
    public List<EntityInfo> Entities { get; init; } = [];

    /// <summary>Custom metadata</summary>
    public Dictionary<string, object>? Metadata { get; init; }
}

/// <summary>
/// Entity information from search results
/// </summary>
public record EntityInfo
{
    /// <summary>Entity ID</summary>
    public Guid Id { get; init; }

    /// <summary>Entity name</summary>
    public string Name { get; init; } = "";

    /// <summary>Entity type (PERSON, ORG, GPE, DATE, etc.)</summary>
    public string EntityType { get; init; } = "";
}

/// <summary>
/// Result of a multi-hop search operation
/// </summary>
public record MultiHopSearchResult
{
    /// <summary>Initial hop memories</summary>
    public List<MemoryMatch> InitialMemories { get; init; } = [];

    /// <summary>Related memories discovered through traversal</summary>
    public List<HopResult> Hops { get; init; } = [];

    /// <summary>All entities discovered</summary>
    public List<EntityInfo> Entities { get; init; } = [];

    /// <summary>Relationships between entities</summary>
    public List<RelationshipInfo> Relationships { get; init; } = [];
}

/// <summary>
/// Results from a single hop in multi-hop search
/// </summary>
public record HopResult
{
    /// <summary>Hop number (1-based)</summary>
    public int HopNumber { get; init; }

    /// <summary>Memories found at this hop</summary>
    public List<MemoryMatch> Memories { get; init; } = [];

    /// <summary>Entity that led to this hop</summary>
    public string? SourceEntity { get; init; }
}

/// <summary>
/// Relationship between two entities
/// </summary>
public record RelationshipInfo
{
    /// <summary>Source entity name</summary>
    public string From { get; init; } = "";

    /// <summary>Target entity name</summary>
    public string To { get; init; } = "";

    /// <summary>Relationship type</summary>
    public string RelationType { get; init; } = "";
}

#endregion

#region Ingest Results

/// <summary>
/// Result of a memory ingest operation
/// </summary>
public record MemoryIngestResult
{
    /// <summary>ID of the created memory</summary>
    public Guid MemoryId { get; init; }

    /// <summary>Entities extracted from the content</summary>
    public List<EntityInfo> ExtractedEntities { get; init; } = [];

    /// <summary>Relationships discovered</summary>
    public List<RelationshipInfo> ExtractedRelationships { get; init; } = [];

    /// <summary>Success message</summary>
    public string? Message { get; init; }
}

/// <summary>
/// Result of a memory update operation
/// </summary>
public record MemoryUpdateResult
{
    /// <summary>ID of the updated memory</summary>
    public Guid MemoryId { get; init; }

    /// <summary>New version number</summary>
    public int Version { get; init; }

    /// <summary>Success message</summary>
    public string? Message { get; init; }
}

/// <summary>
/// Result of a memory delete operation
/// </summary>
public record MemoryDeleteResult
{
    /// <summary>ID of the deleted memory</summary>
    public Guid MemoryId { get; init; }

    /// <summary>Whether deletion was successful</summary>
    public bool Success { get; init; }

    /// <summary>Success message</summary>
    public string? Message { get; init; }
}

#endregion

#region User Persona Results

/// <summary>
/// Result of getting user persona
/// </summary>
public record UserPersonaResult
{
    /// <summary>User ID</summary>
    public string UserId { get; init; } = "";

    /// <summary>User preferences</summary>
    public Dictionary<string, PersonaAttribute> Preferences { get; init; } = [];

    /// <summary>User skills</summary>
    public Dictionary<string, PersonaAttribute> Skills { get; init; } = [];

    /// <summary>User goals</summary>
    public Dictionary<string, PersonaAttribute> Goals { get; init; } = [];

    /// <summary>User background info</summary>
    public Dictionary<string, PersonaAttribute> Background { get; init; } = [];
}

/// <summary>
/// A persona attribute with value and confidence
/// </summary>
public record PersonaAttribute
{
    /// <summary>Attribute value</summary>
    public string Value { get; init; } = "";

    /// <summary>Confidence score (0.0 - 1.0)</summary>
    public float Confidence { get; init; }

    /// <summary>When the attribute was set</summary>
    public DateTime UpdatedAt { get; init; }
}

/// <summary>
/// Result of setting a persona attribute
/// </summary>
public record SetPersonaResult
{
    /// <summary>Whether the operation was successful</summary>
    public bool Success { get; init; }

    /// <summary>Success message</summary>
    public string? Message { get; init; }
}

#endregion

#region Session Results

/// <summary>
/// Result of session operations
/// </summary>
public record SessionResult
{
    /// <summary>Session ID</summary>
    public Guid SessionId { get; init; }

    /// <summary>Session name</summary>
    public string? SessionName { get; init; }

    /// <summary>Session status</summary>
    public string? Status { get; init; }

    /// <summary>Success message</summary>
    public string? Message { get; init; }
}

#endregion

#region Graph Statistics

/// <summary>
/// Knowledge graph statistics
/// </summary>
public record GraphStatisticsResult
{
    /// <summary>Total number of memories</summary>
    public int TotalMemories { get; init; }

    /// <summary>Total number of entities</summary>
    public int TotalEntities { get; init; }

    /// <summary>Total number of relationships</summary>
    public int TotalRelationships { get; init; }

    /// <summary>Entity counts by type</summary>
    public Dictionary<string, int> EntitiesByType { get; init; } = [];

    /// <summary>Relationship counts by type</summary>
    public Dictionary<string, int> RelationshipsByType { get; init; } = [];
}

#endregion
