using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SerialMemory.EventSourcing.Store;

namespace SerialMemory.EventSourcing.Streaming;

/// <summary>
/// Background service that subscribes to Redis Streams and broadcasts events to WebSocket clients.
/// Bridges the Redis event stream to real-time WebSocket notifications.
/// </summary>
public sealed class EventBroadcastService : BackgroundService
{
    private readonly IEventStreamSubscriber _subscriber;
    private readonly WebSocketEventHub _webSocketHub;
    private readonly ILogger<EventBroadcastService> _logger;
    private readonly string _consumerId;

    public EventBroadcastService(
        IEventStreamSubscriber subscriber,
        WebSocketEventHub webSocketHub,
        ILogger<EventBroadcastService> logger)
    {
        _subscriber = subscriber;
        _webSocketHub = webSocketHub;
        _logger = logger;
        _consumerId = $"broadcast-{Environment.MachineName}-{Guid.CreateVersion7():N}";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Event broadcast service starting with consumer ID: {ConsumerId}", _consumerId);

        try
        {
            await foreach (var @event in _subscriber.SubscribeAsync(
                "websocket-broadcast",
                _consumerId,
                stoppingToken))
            {
                try
                {
                    // Only broadcast if there are connected clients
                    if (_webSocketHub.ConnectionCount > 0)
                    {
                        await _webSocketHub.BroadcastEventAsync(@event, stoppingToken);

                        _logger.LogDebug(
                            "Broadcasted event {EventId} ({EventType}) to {ConnectionCount} WebSocket clients",
                            @event.EventId, @event.EventType, _webSocketHub.ConnectionCount);
                    }

                    // Acknowledge the message
                    await _subscriber.AcknowledgeAsync("websocket-broadcast", @event.EventId.ToString(), stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error broadcasting event {EventId}", @event.EventId);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Event broadcast service stopping");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Event broadcast service failed");
            throw;
        }
    }
}

/// <summary>
/// Unified event publisher that publishes to both Redis Streams and WebSocket clients.
/// Use this as the single entry point for event publishing.
/// </summary>
public sealed class UnifiedEventPublisher : IEventStreamPublisher
{
    private readonly RedisEventStreamPublisher _redisPublisher;
    private readonly WebSocketEventHub _webSocketHub;
    private readonly ILogger<UnifiedEventPublisher> _logger;

    public UnifiedEventPublisher(
        RedisEventStreamPublisher redisPublisher,
        WebSocketEventHub webSocketHub,
        ILogger<UnifiedEventPublisher> logger)
    {
        _redisPublisher = redisPublisher;
        _webSocketHub = webSocketHub;
        _logger = logger;
    }

    public async Task PublishToStreamAsync(StoredEvent @event, CancellationToken cancellationToken = default)
    {
        // Publish to Redis Stream for durability
        await _redisPublisher.PublishToStreamAsync(@event, cancellationToken);

        // Also broadcast directly to WebSocket clients for immediate updates
        await BroadcastToWebSocketsAsync(@event, cancellationToken);
    }

    public async Task BroadcastToWebSocketsAsync(StoredEvent @event, CancellationToken cancellationToken = default)
    {
        if (_webSocketHub.ConnectionCount > 0)
        {
            await _webSocketHub.BroadcastEventAsync(@event, cancellationToken);
        }
    }
}

/// <summary>
/// Event stream metrics for monitoring.
/// </summary>
public sealed class EventStreamMetrics
{
    private long _eventsPublished;
    private long _eventsBroadcasted;
    private long _broadcastFailures;

    public long EventsPublished => Interlocked.Read(ref _eventsPublished);
    public long EventsBroadcasted => Interlocked.Read(ref _eventsBroadcasted);
    public long BroadcastFailures => Interlocked.Read(ref _broadcastFailures);

    public void IncrementPublished() => Interlocked.Increment(ref _eventsPublished);
    public void IncrementBroadcasted() => Interlocked.Increment(ref _eventsBroadcasted);
    public void IncrementFailures() => Interlocked.Increment(ref _broadcastFailures);
}
