using SerialMemory.EventSourcing.Store;

namespace SerialMemory.EventSourcing.Streaming;

/// <summary>
/// Interface for publishing events to Redis Streams and WebSocket clients.
/// </summary>
public interface IEventStreamPublisher
{
    /// <summary>Publish event to Redis Stream</summary>
    Task PublishToStreamAsync(StoredEvent @event, CancellationToken cancellationToken = default);

    /// <summary>Broadcast event to WebSocket subscribers</summary>
    Task BroadcastToWebSocketsAsync(StoredEvent @event, CancellationToken cancellationToken = default);
}

/// <summary>
/// A stream message containing the event and its Redis stream message ID.
/// </summary>
/// <param name="MessageId">The Redis stream message ID (e.g., "1732547123456-0")</param>
/// <param name="Event">The stored event payload</param>
public readonly record struct StreamMessage(string MessageId, StoredEvent Event);

/// <summary>
/// Interface for subscribing to Redis Streams.
/// </summary>
public interface IEventStreamSubscriber
{
    /// <summary>Subscribe to events from Redis Stream, returning messages with their stream IDs</summary>
    IAsyncEnumerable<StreamMessage> SubscribeAsync(
        string consumerGroup,
        string consumerId,
        CancellationToken cancellationToken = default);

    /// <summary>Acknowledge processed event using the Redis stream message ID</summary>
    Task AcknowledgeAsync(string consumerGroup, string messageId, CancellationToken cancellationToken = default);
}
