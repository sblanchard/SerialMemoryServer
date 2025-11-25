using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

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
