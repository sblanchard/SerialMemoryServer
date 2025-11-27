namespace SerialMemory.Core.Interfaces;

/// <summary>
/// Service for temporal replay of memory evolution, confidence drift, and causal changes.
/// </summary>
public interface ITimelineService
{
    /// <summary>Get timeline for a specific memory.</summary>
    Task<MemoryTimeline> GetMemoryTimelineAsync(
        Guid memoryId,
        CancellationToken ct = default);

    /// <summary>Get global timeline across all memories.</summary>
    Task<GlobalTimeline> GetGlobalTimelineAsync(
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        int limit = 100,
        CancellationToken ct = default);

    /// <summary>Get confidence drift over time for a memory.</summary>
    Task<ConfidenceDriftTimeline> GetConfidenceDriftAsync(
        Guid memoryId,
        CancellationToken ct = default);

    /// <summary>Get causal chain for a memory.</summary>
    Task<CausalChain> GetCausalChainAsync(
        Guid memoryId,
        int maxDepth = 5,
        CancellationToken ct = default);

    /// <summary>Replay memory state at a specific point in time.</summary>
    Task<TimelineMemorySnapshot?> ReplayAtAsync(
        Guid memoryId,
        DateTimeOffset pointInTime,
        CancellationToken ct = default);
}

/// <summary>Complete timeline for a single memory.</summary>
public sealed class MemoryTimeline
{
    public Guid MemoryId { get; init; }
    public List<TimelineEvent> Events { get; init; } = [];
    public TimelineMemorySnapshot? CurrentState { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public DateTimeOffset? LastModifiedAt { get; init; }
    public int TotalEvents { get; init; }
}

/// <summary>Global timeline across all memories.</summary>
public sealed class GlobalTimeline
{
    public List<TimelineEvent> Events { get; init; } = [];
    public DateTimeOffset From { get; init; }
    public DateTimeOffset To { get; init; }
    public int TotalEvents { get; init; }
    public Dictionary<string, int> EventTypeCounts { get; init; } = [];
}

/// <summary>Confidence drift over time.</summary>
public sealed class ConfidenceDriftTimeline
{
    public Guid MemoryId { get; init; }
    public List<ConfidencePoint> Points { get; init; } = [];
    public float InitialConfidence { get; init; }
    public float CurrentConfidence { get; init; }
    public float MinConfidence { get; init; }
    public float MaxConfidence { get; init; }
    public int DecayCount { get; init; }
    public int ReinforceCount { get; init; }
}

/// <summary>Single confidence measurement point.</summary>
public sealed class ConfidencePoint
{
    public DateTimeOffset Timestamp { get; init; }
    public float Confidence { get; init; }
    public string EventType { get; init; } = "";
    public string? Reason { get; init; }
}

/// <summary>Causal chain showing memory ancestry and descendants.</summary>
public sealed class CausalChain
{
    public Guid MemoryId { get; init; }
    public List<CausalNode> Ancestors { get; init; } = [];
    public List<CausalNode> Descendants { get; init; } = [];
    public int TotalAncestors { get; init; }
    public int TotalDescendants { get; init; }
}

/// <summary>Node in causal chain.</summary>
public sealed class CausalNode
{
    public Guid MemoryId { get; init; }
    public string? ContentPreview { get; init; }
    public string Relationship { get; init; } = "";
    public int Depth { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public float Confidence { get; init; }
    public bool IsActive { get; init; }
}

/// <summary>Single event in timeline.</summary>
public sealed class TimelineEvent
{
    public Guid EventId { get; init; }
    public Guid MemoryId { get; init; }
    public string EventType { get; init; } = "";
    public DateTimeOffset Timestamp { get; init; }
    public string Description { get; init; } = "";
    public Dictionary<string, object> Data { get; init; } = [];
    public float? ConfidenceBefore { get; init; }
    public float? ConfidenceAfter { get; init; }
    public string? ContentHash { get; init; }
}

/// <summary>Memory state snapshot at a point in time for timeline replay.</summary>
public sealed class TimelineMemorySnapshot
{
    public Guid MemoryId { get; init; }
    public string Content { get; init; } = "";
    public float Confidence { get; init; }
    public string Layer { get; init; } = "";
    public bool IsActive { get; init; }
    public DateTimeOffset AsOf { get; init; }
    public long Version { get; init; }
    public List<Guid> CausalParents { get; init; } = [];
    public string? ContentHash { get; init; }
}
