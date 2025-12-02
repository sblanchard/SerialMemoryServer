using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SerialMemory.Core.Interfaces;

namespace SerialMemory.EventSourcing.Events;

/// <summary>
/// Base class for all memory events with common functionality.
/// </summary>
public abstract record MemoryEventBase : IMemoryEvent
{
    public Guid EventId { get; init; } = Guid.CreateVersion7();
    public required Guid StreamId { get; init; }
    public abstract MemoryEventType EventType { get; }
    public long EventVersion { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public string? CreatedBy { get; init; }
    public string ContentHash => ComputeContentHash();
    public EventMetadata Metadata { get; init; } = new();

    protected abstract string GetHashableContent();

    private string ComputeContentHash()
    {
        var content = GetHashableContent();
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexStringLower(bytes);
    }
}

/// <summary>
/// Event: A new memory was created.
/// </summary>
public sealed record MemoryCreatedEvent : MemoryEventBase
{
    public override MemoryEventType EventType => MemoryEventType.MemoryCreated;

    public required string Content { get; init; }
    public required MemoryLayer Layer { get; init; }
    public float[] Embedding { get; init; } = [];
    public float ConfidenceScore { get; init; } = 1.0f;
    public int HalfLifeDays { get; init; } = 30;
    public Guid[] CausalParents { get; init; } = [];
    public string? Source { get; init; }
    public Guid? SessionId { get; init; }
    public string UserId { get; init; } = "default_user";
    public string[] Tags { get; init; } = [];

    protected override string GetHashableContent() =>
        JsonSerializer.Serialize(new { Content, Layer, ConfidenceScore, CausalParents, Source });
}

/// <summary>
/// Event: Memory content was updated.
/// </summary>
public sealed record MemoryUpdatedEvent : MemoryEventBase
{
    public override MemoryEventType EventType => MemoryEventType.MemoryUpdated;

    public required string NewContent { get; init; }
    public string? PreviousContentHash { get; init; }
    public float[]? NewEmbedding { get; init; }
    public string? Reason { get; init; }

    protected override string GetHashableContent() =>
        JsonSerializer.Serialize(new { NewContent, PreviousContentHash, Reason });
}

/// <summary>
/// Event: Two or more memories were merged.
/// </summary>
public sealed record MemoryMergedEvent : MemoryEventBase
{
    public override MemoryEventType EventType => MemoryEventType.MemoryMerged;

    public required Guid[] SourceMemoryIds { get; init; }
    public required string MergedContent { get; init; }
    public float[]? MergedEmbedding { get; init; }
    public string? MergeStrategy { get; init; }

    protected override string GetHashableContent() =>
        JsonSerializer.Serialize(new { SourceMemoryIds, MergedContent, MergeStrategy });
}

/// <summary>
/// Event: Memory was invalidated (soft delete).
/// </summary>
public sealed record MemoryInvalidatedEvent : MemoryEventBase
{
    public override MemoryEventType EventType => MemoryEventType.MemoryInvalidated;

    public required string Reason { get; init; }
    public Guid? SupersededById { get; init; }
    public Guid[] ContradictedByIds { get; init; } = [];

    protected override string GetHashableContent() =>
        JsonSerializer.Serialize(new { Reason, SupersededById, ContradictedByIds });
}

/// <summary>
/// Event: Memory confidence decayed due to time.
/// </summary>
public sealed record MemoryDecayedEvent : MemoryEventBase
{
    public override MemoryEventType EventType => MemoryEventType.MemoryDecayed;

    public required float PreviousConfidence { get; init; }
    public required float NewConfidence { get; init; }
    public required int DaysSinceReinforcement { get; init; }

    protected override string GetHashableContent() =>
        JsonSerializer.Serialize(new { PreviousConfidence, NewConfidence, DaysSinceReinforcement });
}

/// <summary>
/// Event: Memory was reinforced, resetting decay.
/// </summary>
public sealed record MemoryReinforcedEvent : MemoryEventBase
{
    public override MemoryEventType EventType => MemoryEventType.MemoryReinforced;

    public required float PreviousConfidence { get; init; }
    public required float NewConfidence { get; init; }
    public required string ReinforcementSource { get; init; }
    public Guid[] ValidatedByIds { get; init; } = [];

    protected override string GetHashableContent() =>
        JsonSerializer.Serialize(new { PreviousConfidence, NewConfidence, ReinforcementSource, ValidatedByIds });
}

/// <summary>
/// Event: Memory transitioned to a different layer.
/// </summary>
public sealed record MemoryLayerTransitionedEvent : MemoryEventBase
{
    public override MemoryEventType EventType => MemoryEventType.MemoryLayerTransitioned;

    public required MemoryLayer PreviousLayer { get; init; }
    public required MemoryLayer NewLayer { get; init; }
    public string? TransitionReason { get; init; }
    public Guid? TriggeredByMemoryId { get; init; }

    protected override string GetHashableContent() =>
        JsonSerializer.Serialize(new { PreviousLayer, NewLayer, TransitionReason });
}

/// <summary>
/// Event: Memory was archived (cold storage).
/// </summary>
public sealed record MemoryArchivedEvent : MemoryEventBase
{
    public override MemoryEventType EventType => MemoryEventType.MemoryArchived;

    public required string Reason { get; init; }
    public float ConfidenceAtArchive { get; init; }
    public int AccessCountAtArchive { get; init; }
    public int DaysSinceLastAccess { get; init; }

    protected override string GetHashableContent() =>
        JsonSerializer.Serialize(new { Reason, ConfidenceAtArchive, DaysSinceLastAccess });
}

/// <summary>
/// Event: Memory was recalled/accessed during retrieval.
/// Tracks access patterns for decay and reinforcement decisions.
/// </summary>
public sealed record MemoryRecalledEvent : MemoryEventBase
{
    public override MemoryEventType EventType => MemoryEventType.MemoryRecalled;

    /// <summary>Query that triggered the recall</summary>
    public string? Query { get; init; }

    /// <summary>Similarity score when recalled (0.0-1.0)</summary>
    public float SimilarityScore { get; init; }

    /// <summary>Context in which memory was recalled</summary>
    public string? RecallContext { get; init; }

    /// <summary>Session ID during recall</summary>
    public Guid? SessionId { get; init; }

    protected override string GetHashableContent() =>
        JsonSerializer.Serialize(new { Query, SimilarityScore, RecallContext, SessionId });
}

/// <summary>
/// Event: Memory was present in results but explicitly ignored/skipped.
/// Useful for tracking negative signals about memory relevance.
/// </summary>
public sealed record MemoryIgnoredEvent : MemoryEventBase
{
    public override MemoryEventType EventType => MemoryEventType.MemoryIgnored;

    /// <summary>Query context where memory was ignored</summary>
    public string? Query { get; init; }

    /// <summary>Reason memory was ignored</summary>
    public required string Reason { get; init; }

    /// <summary>Session ID during ignore</summary>
    public Guid? SessionId { get; init; }

    protected override string GetHashableContent() =>
        JsonSerializer.Serialize(new { Query, Reason, SessionId });
}

/// <summary>
/// Event: Memory was marked as contradicting another memory.
/// Different from invalidation - both memories may remain active but flagged.
/// </summary>
public sealed record MemoryContradictedEvent : MemoryEventBase
{
    public override MemoryEventType EventType => MemoryEventType.MemoryContradicted;

    /// <summary>Memory IDs that contradict this memory</summary>
    public required Guid[] ContradictingMemoryIds { get; init; }

    /// <summary>Nature of the contradiction</summary>
    public required string ContradictionType { get; init; }

    /// <summary>How the contradiction was detected</summary>
    public string? DetectionMethod { get; init; }

    /// <summary>Confidence that this is a real contradiction (0.0-1.0)</summary>
    public float ContradictionConfidence { get; init; } = 1.0f;

    protected override string GetHashableContent() =>
        JsonSerializer.Serialize(new { ContradictingMemoryIds, ContradictionType, DetectionMethod, ContradictionConfidence });
}

/// <summary>
/// Event: Memory has expired due to time-based policies.
/// Distinct from decay (gradual) - expiration is a hard cutoff.
/// </summary>
public sealed record MemoryExpiredEvent : MemoryEventBase
{
    public override MemoryEventType EventType => MemoryEventType.MemoryExpired;

    /// <summary>Policy that triggered expiration</summary>
    public required string ExpirationPolicy { get; init; }

    /// <summary>Original TTL in days</summary>
    public int OriginalTtlDays { get; init; }

    /// <summary>Confidence at time of expiration</summary>
    public float ConfidenceAtExpiration { get; init; }

    /// <summary>Access count at time of expiration</summary>
    public int AccessCountAtExpiration { get; init; }

    protected override string GetHashableContent() =>
        JsonSerializer.Serialize(new { ExpirationPolicy, OriginalTtlDays, ConfidenceAtExpiration });
}

/// <summary>
/// Event: Memory was split into multiple child memories.
/// Inverse of merge - used for decomposing complex memories.
/// </summary>
public sealed record MemorySplitEvent : MemoryEventBase
{
    public override MemoryEventType EventType => MemoryEventType.MemorySplit;

    /// <summary>IDs of the resulting child memories</summary>
    public required Guid[] ChildMemoryIds { get; init; }

    /// <summary>Strategy used for splitting</summary>
    public string? SplitStrategy { get; init; }

    /// <summary>Reason for the split</summary>
    public string? Reason { get; init; }

    protected override string GetHashableContent() =>
        JsonSerializer.Serialize(new { ChildMemoryIds, SplitStrategy, Reason });
}

/// <summary>
/// Event: Unknown or legacy event type that cannot be deserialized.
/// Used for backwards compatibility when encountering event types
/// that were added in newer versions or removed in older versions.
/// </summary>
public sealed record UnknownEvent : IMemoryEvent
{
    public Guid EventId { get; init; }
    public Guid StreamId { get; init; }
    public MemoryEventType EventType { get; init; }
    public long EventVersion { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public string? CreatedBy { get; init; }
    public string ContentHash { get; init; } = "";

    /// <summary>Raw event data for debugging/logging</summary>
    public string? RawEventData { get; init; }
}
