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
/// Interface for subscribing to Redis Streams.
/// </summary>
public interface IEventStreamSubscriber
{
    /// <summary>Subscribe to events from Redis Stream</summary>
    IAsyncEnumerable<StoredEvent> SubscribeAsync(
        string consumerGroup,
        string consumerId,
        CancellationToken cancellationToken = default);

    /// <summary>Acknowledge processed event</summary>
    Task AcknowledgeAsync(string consumerGroup, string messageId, CancellationToken cancellationToken = default);
}
