using SerialMemory.EventSourcing.Events;

namespace SerialMemory.EventSourcing.Store;

/// <summary>
/// Event store interface for append-only event persistence.
/// Events are immutable after storage.
/// </summary>
public interface IEventStore
{
    /// <summary>
    /// Append events to a stream with optimistic concurrency.
    /// </summary>
    /// <param name="streamId">Aggregate stream ID</param>
    /// <param name="events">Events to append</param>
    /// <param name="expectedVersion">Expected current version for concurrency check</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Global sequence numbers of appended events</returns>
    Task<long[]> AppendEventsAsync(
        Guid streamId,
        IReadOnlyList<IMemoryEvent> events,
        long expectedVersion,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Read all events for a stream.
    /// </summary>
    Task<IReadOnlyList<IMemoryEvent>> ReadStreamAsync(
        Guid streamId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Read events for a stream starting from a specific version.
    /// </summary>
    Task<IReadOnlyList<IMemoryEvent>> ReadStreamAsync(
        Guid streamId,
        long fromVersion,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Read all events from global position for projections.
    /// </summary>
    Task<IReadOnlyList<StoredEvent>> ReadAllAsync(
        long fromGlobalSequence,
        int maxCount = 1000,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get current version of a stream.
    /// </summary>
    Task<long> GetStreamVersionAsync(
        Guid streamId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get latest global sequence number.
    /// </summary>
    Task<long> GetLatestGlobalSequenceAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Subscribe to new events (for projections and streaming).
    /// </summary>
    IAsyncEnumerable<StoredEvent> SubscribeAsync(
        long fromGlobalSequence,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Read events for multiple streams in a single query.
    /// Returns a dictionary mapping each stream ID to its ordered list of events.
    /// </summary>
    Task<Dictionary<Guid, IReadOnlyList<IMemoryEvent>>> ReadStreamsAsync(
        IEnumerable<Guid> streamIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Append events across multiple streams in a single batch operation.
    /// Uses one connection, one transaction, and a single multi-row INSERT.
    /// Maintains stream-level version checking with FOR UPDATE per stream.
    /// </summary>
    /// <param name="batches">List of (StreamId, Events) pairs to append</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task AppendBatchEventsAsync(
        IReadOnlyList<(Guid StreamId, IReadOnlyList<IMemoryEvent> Events)> batches,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Stored event with all persistence metadata.
/// </summary>
public sealed record StoredEvent
{
    public required Guid EventId { get; init; }
    public required Guid StreamId { get; init; }
    public required MemoryEventType EventType { get; init; }
    public required long EventVersion { get; init; }
    public required long GlobalSequence { get; init; }
    public required string EventData { get; init; }
    public required string Metadata { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required string? CreatedBy { get; init; }
    public required string ContentHash { get; init; }
}

/// <summary>
/// Exception thrown on optimistic concurrency conflict.
/// </summary>
public sealed class ConcurrencyException : Exception
{
    public Guid StreamId { get; }
    public long ExpectedVersion { get; }
    public long ActualVersion { get; }

    public ConcurrencyException(Guid streamId, long expectedVersion, long actualVersion)
        : base($"Concurrency conflict on stream {streamId}: expected version {expectedVersion}, actual {actualVersion}")
    {
        StreamId = streamId;
        ExpectedVersion = expectedVersion;
        ActualVersion = actualVersion;
    }
}
