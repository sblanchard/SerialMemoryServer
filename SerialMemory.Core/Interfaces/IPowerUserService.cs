namespace SerialMemory.Core.Interfaces;

/// <summary>
/// Power-user service for direct memory manipulation.
/// NO GUARD RAILS. NO SAFE MODE.
/// </summary>
public interface IPowerUserService
{
    // ==========================================
    // RAW MEMORY EDITING
    // ==========================================

    /// <summary>Direct content replacement. No validation.</summary>
    Task<RawEditResult> EditMemoryContentAsync(
        Guid memoryId,
        string newContent,
        bool skipHashUpdate = false,
        CancellationToken ct = default);

    /// <summary>Direct metadata modification.</summary>
    Task<RawEditResult> EditMemoryMetadataAsync(
        Guid memoryId,
        MemoryMetadataUpdate update,
        CancellationToken ct = default);

    /// <summary>Force confidence value without decay calculation.</summary>
    Task<RawEditResult> ForceConfidenceAsync(
        Guid memoryId,
        float confidence,
        CancellationToken ct = default);

    /// <summary>Direct embedding replacement.</summary>
    Task<RawEditResult> ReplaceEmbeddingAsync(
        Guid memoryId,
        float[] embedding,
        CancellationToken ct = default);

    /// <summary>Hard delete memory. Bypasses soft-delete.</summary>
    Task<RawEditResult> HardDeleteMemoryAsync(
        Guid memoryId,
        bool cascade = false,
        CancellationToken ct = default);

    /// <summary>Batch raw edit multiple memories.</summary>
    Task<BatchEditResult> BatchEditAsync(
        List<RawMemoryEdit> edits,
        CancellationToken ct = default);

    // ==========================================
    // CONFLICT OVERLAYS
    // ==========================================

    /// <summary>Get all conflicts for visualization overlay.</summary>
    Task<ConflictOverlay> GetConflictOverlayAsync(
        int? limit = null,
        float? minSeverity = null,
        CancellationToken ct = default);

    /// <summary>Get conflicts for specific memory.</summary>
    Task<List<MemoryConflict>> GetMemoryConflictsAsync(
        Guid memoryId,
        CancellationToken ct = default);

    /// <summary>Force-resolve conflict by picking winner.</summary>
    Task<ConflictResolution> ForceResolveConflictAsync(
        Guid conflictId,
        Guid winnerId,
        ConflictResolutionAction action,
        CancellationToken ct = default);

    /// <summary>Manually flag contradiction between memories.</summary>
    Task<MemoryConflict> FlagContradictionAsync(
        Guid memoryA,
        Guid memoryB,
        float severity,
        string? reason = null,
        CancellationToken ct = default);

    // ==========================================
    // RAW TRACE VIEWERS
    // ==========================================

    /// <summary>Get raw event trace for memory.</summary>
    Task<RawEventTrace> GetRawTraceAsync(
        Guid memoryId,
        CancellationToken ct = default);

    /// <summary>Get raw events by type.</summary>
    Task<List<RawEvent>> GetRawEventsByTypeAsync(
        string eventType,
        int limit = 100,
        CancellationToken ct = default);

    /// <summary>Get all events in sequence order.</summary>
    Task<List<RawEvent>> GetEventStreamAsync(
        long fromSequence = 0,
        int limit = 1000,
        CancellationToken ct = default);

    /// <summary>Get raw SQL query results.</summary>
    Task<RawQueryResult> ExecuteRawQueryAsync(
        string sql,
        Dictionary<string, object>? parameters = null,
        CancellationToken ct = default);

    // ==========================================
    // LIVE GRAPH MUTATIONS
    // ==========================================

    /// <summary>Get live mutation feed snapshot.</summary>
    Task<MutationFeed> GetMutationFeedAsync(
        DateTimeOffset? since = null,
        int limit = 100,
        CancellationToken ct = default);

    /// <summary>Force graph edge creation without validation.</summary>
    Task<GraphMutation> ForceCreateEdgeAsync(
        Guid sourceId,
        Guid targetId,
        string edgeType,
        float confidence = 1.0f,
        CancellationToken ct = default);

    /// <summary>Force graph node deletion with cascade.</summary>
    Task<GraphMutation> ForceDeleteNodeAsync(
        Guid nodeId,
        bool cascadeEdges = true,
        CancellationToken ct = default);

    /// <summary>Replay mutations from sequence.</summary>
    Task<List<GraphMutation>> ReplayMutationsAsync(
        long fromSequence,
        long toSequence,
        CancellationToken ct = default);
}

// ==========================================
// RAW EDIT MODELS
// ==========================================

public sealed class RawEditResult
{
    public Guid MemoryId { get; init; }
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string? PreviousHash { get; init; }
    public string? NewHash { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public Dictionary<string, object> Changes { get; init; } = [];
}

public sealed class MemoryMetadataUpdate
{
    public string? Layer { get; init; }
    public string? Source { get; init; }
    public bool? IsActive { get; init; }
    public List<Guid>? CausalParents { get; init; }
    public int? HalfLifeDays { get; init; }
    public DateTimeOffset? LastReinforcedAt { get; init; }
    public Dictionary<string, object>? CustomMetadata { get; init; }
}

public sealed class RawMemoryEdit
{
    public Guid MemoryId { get; init; }
    public string? NewContent { get; init; }
    public float? NewConfidence { get; init; }
    public MemoryMetadataUpdate? MetadataUpdate { get; init; }
    public bool HardDelete { get; init; }
}

public sealed class BatchEditResult
{
    public int TotalRequested { get; init; }
    public int Succeeded { get; init; }
    public int Failed { get; init; }
    public List<RawEditResult> Results { get; init; } = [];
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

// ==========================================
// CONFLICT OVERLAY MODELS
// ==========================================

public sealed class ConflictOverlay
{
    public List<MemoryConflict> Conflicts { get; init; } = [];
    public int TotalConflicts { get; init; }
    public int CriticalCount { get; init; }
    public int HighCount { get; init; }
    public int MediumCount { get; init; }
    public int LowCount { get; init; }
    public Dictionary<string, int> ConflictsByType { get; init; } = [];
    public List<ConflictCluster> Clusters { get; init; } = [];
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class MemoryConflict
{
    public Guid ConflictId { get; init; } = Guid.CreateVersion7();
    public Guid MemoryA { get; init; }
    public Guid MemoryB { get; init; }
    public string? ContentA { get; init; }
    public string? ContentB { get; init; }
    public float Severity { get; init; }
    public string ConflictType { get; init; } = "contradiction";
    public string? Reason { get; init; }
    public float SemanticSimilarity { get; init; }
    public DateTimeOffset DetectedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool IsResolved { get; init; }
    public Guid? ResolvedBy { get; init; }
    public string? Resolution { get; init; }
}

public sealed class ConflictCluster
{
    public Guid ClusterId { get; init; } = Guid.CreateVersion7();
    public List<Guid> MemoryIds { get; init; } = [];
    public int ConflictCount { get; init; }
    public float MaxSeverity { get; init; }
    public string? CommonTopic { get; init; }
}

public enum ConflictResolutionAction
{
    KeepWinner,
    InvalidateLoser,
    MergeBoth,
    DeleteBoth,
    MarkAsAlternatives
}

public sealed class ConflictResolution
{
    public Guid ConflictId { get; init; }
    public Guid WinnerId { get; init; }
    public ConflictResolutionAction Action { get; init; }
    public bool Success { get; init; }
    public string? Error { get; init; }
    public DateTimeOffset ResolvedAt { get; init; } = DateTimeOffset.UtcNow;
}

// ==========================================
// RAW TRACE MODELS
// ==========================================

public sealed class RawEventTrace
{
    public Guid MemoryId { get; init; }
    public List<RawEvent> Events { get; init; } = [];
    public int TotalEvents { get; init; }
    public DateTimeOffset? FirstEvent { get; init; }
    public DateTimeOffset? LastEvent { get; init; }
    public Dictionary<string, int> EventTypeCounts { get; init; } = [];
}

public sealed class RawEvent
{
    public Guid EventId { get; init; }
    public long SequenceNumber { get; init; }
    public string EventType { get; init; } = "";
    public Guid? MemoryId { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public string RawPayload { get; init; } = "{}";
    public Dictionary<string, object> ParsedPayload { get; init; } = [];
    public string? ActorId { get; init; }
    public string? CorrelationId { get; init; }
}

public sealed class RawQueryResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public int RowsAffected { get; init; }
    public List<Dictionary<string, object>> Rows { get; init; } = [];
    public List<string> Columns { get; init; } = [];
    public long ExecutionTimeMs { get; init; }
    public string Query { get; init; } = "";
}

// ==========================================
// LIVE MUTATION MODELS
// ==========================================

public sealed class MutationFeed
{
    public List<GraphMutation> Mutations { get; init; } = [];
    public long FromSequence { get; init; }
    public long ToSequence { get; init; }
    public int TotalMutations { get; init; }
    public Dictionary<string, int> MutationsByType { get; init; } = [];
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class GraphMutation
{
    public Guid MutationId { get; init; } = Guid.CreateVersion7();
    public long SequenceNumber { get; init; }
    public string MutationType { get; init; } = "";
    public Guid? NodeId { get; init; }
    public Guid? EdgeId { get; init; }
    public Guid? SourceNodeId { get; init; }
    public Guid? TargetNodeId { get; init; }
    public string? NodeName { get; init; }
    public string? EdgeType { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public Dictionary<string, object> PreviousState { get; init; } = [];
    public Dictionary<string, object> NewState { get; init; } = [];
    public string? TriggeredBy { get; init; }
}
