namespace SerialMemory.EventSourcing.Events;

/// <summary>
/// Base interface for all memory events.
/// Events are immutable after creation.
/// </summary>
public interface IMemoryEvent
{
    /// <summary>Unique event identifier</summary>
    Guid EventId { get; }

    /// <summary>Memory aggregate stream ID</summary>
    Guid StreamId { get; }

    /// <summary>Event type discriminator</summary>
    MemoryEventType EventType { get; }

    /// <summary>Version within the stream for optimistic concurrency</summary>
    long EventVersion { get; }

    /// <summary>UTC timestamp when event was created</summary>
    DateTimeOffset CreatedAt { get; }

    /// <summary>Actor or source that created the event</summary>
    string? CreatedBy { get; }

    /// <summary>SHA-256 hash of the event data for integrity</summary>
    string ContentHash { get; }
}

/// <summary>
/// Metadata attached to events for correlation and causation tracking.
/// </summary>
public sealed record EventMetadata
{
    public Guid? CorrelationId { get; init; }
    public Guid? CausationId { get; init; }
    public string? SessionId { get; init; }
    public string? UserId { get; init; }
    public Dictionary<string, string> Tags { get; init; } = [];
}
