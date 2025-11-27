using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SerialMemory.EventSourcing.Events;
using SerialMemory.EventSourcing.Store;

namespace SerialMemory.EventSourcing.Streaming;

/// <summary>
/// Manages WebSocket connections for real-time event streaming.
/// Provides broadcast capabilities to connected clients with subscription filtering.
/// </summary>
public sealed class WebSocketEventHub(ILogger<WebSocketEventHub> logger) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, WebSocketConnection> _connections = new();

    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };
    private readonly SemaphoreSlim _broadcastLock = new(1, 1);

    /// <summary>
    /// Number of active WebSocket connections.
    /// </summary>
    public int ConnectionCount => _connections.Count;

    /// <summary>
    /// Register a new WebSocket connection.
    /// </summary>
    public Guid RegisterConnection(WebSocket webSocket, WebSocketSubscription? subscription = null)
    {
        var connectionId = Guid.CreateVersion7();
        var connection = new WebSocketConnection
        {
            ConnectionId = connectionId,
            WebSocket = webSocket,
            ConnectedAt = DateTimeOffset.UtcNow,
            Subscription = subscription ?? WebSocketSubscription.All
        };

        _connections.TryAdd(connectionId, connection);

        logger.LogInformation("WebSocket connection registered: {ConnectionId}", connectionId);

        return connectionId;
    }

    /// <summary>
    /// Unregister a WebSocket connection.
    /// </summary>
    public async Task UnregisterConnectionAsync(Guid connectionId)
    {
        if (_connections.TryRemove(connectionId, out var connection))
        {
            try
            {
                if (connection.WebSocket.State == WebSocketState.Open)
                {
                    await connection.WebSocket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Connection closed",
                        CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Error closing WebSocket {ConnectionId}", connectionId);
            }

            logger.LogInformation("WebSocket connection unregistered: {ConnectionId}", connectionId);
        }
    }

    /// <summary>
    /// Update subscription filter for a connection.
    /// </summary>
    public bool UpdateSubscription(Guid connectionId, WebSocketSubscription subscription)
    {
        if (_connections.TryGetValue(connectionId, out var connection))
        {
            connection.Subscription = subscription;
            logger.LogDebug("Updated subscription for {ConnectionId}: {EventTypes}",
                connectionId, string.Join(", ", subscription.EventTypes));
            return true;
        }
        return false;
    }

    /// <summary>
    /// Broadcast an event to all subscribed WebSocket clients.
    /// </summary>
    public async Task BroadcastEventAsync(StoredEvent @event, CancellationToken cancellationToken = default)
    {
        if (_connections.IsEmpty) return;

        var message = CreateEventMessage(@event);
        var buffer = Encoding.UTF8.GetBytes(message);
        var segment = new ArraySegment<byte>(buffer);

        await _broadcastLock.WaitAsync(cancellationToken);
        try
        {
            var tasks = new List<Task>();
            var deadConnections = new List<Guid>();

            foreach (var (connectionId, connection) in _connections)
            {
                // Check if connection is subscribed to this event type and stream
                if (!connection.Subscription.ShouldReceive(@event.EventType) ||
                    !connection.Subscription.ShouldReceiveStream(@event.StreamId))
                {
                    continue;
                }

                if (connection.WebSocket.State != WebSocketState.Open)
                {
                    deadConnections.Add(connectionId);
                    continue;
                }

                tasks.Add(SendToConnectionAsync(connection, segment, cancellationToken));
            }

            // Wait for all sends to complete
            if (tasks.Count > 0)
            {
                await Task.WhenAll(tasks);
            }

            // Clean up dead connections
            foreach (var deadId in deadConnections)
            {
                await UnregisterConnectionAsync(deadId);
            }
        }
        finally
        {
            _broadcastLock.Release();
        }
    }

    /// <summary>
    /// Send a message to a specific connection.
    /// </summary>
    public async Task SendToConnectionAsync(Guid connectionId, string message, CancellationToken cancellationToken = default)
    {
        if (_connections.TryGetValue(connectionId, out var connection))
        {
            if (connection.WebSocket.State == WebSocketState.Open)
            {
                var buffer = Encoding.UTF8.GetBytes(message);
                var segment = new ArraySegment<byte>(buffer);
                await connection.WebSocket.SendAsync(segment, WebSocketMessageType.Text, true, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Handle incoming WebSocket messages (for subscription updates, etc.)
    /// </summary>
    public async Task HandleMessagesAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        if (!_connections.TryGetValue(connectionId, out var connection))
        {
            return;
        }

        var buffer = new byte[4096];

        try
        {
            while (connection.WebSocket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var result = await connection.WebSocket.ReceiveAsync(
                    new ArraySegment<byte>(buffer),
                    cancellationToken);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await UnregisterConnectionAsync(connectionId);
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    await ProcessClientMessageAsync(connectionId, message, cancellationToken);
                }
            }
        }
        catch (WebSocketException ex)
        {
            logger.LogWarning(ex, "WebSocket error for connection {ConnectionId}", connectionId);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        finally
        {
            await UnregisterConnectionAsync(connectionId);
        }
    }

    private async Task ProcessClientMessageAsync(Guid connectionId, string message, CancellationToken cancellationToken)
    {
        try
        {
            var command = JsonSerializer.Deserialize<WebSocketCommand>(message, _jsonOptions);
            if (command == null) return;

            switch (command.Type?.ToLowerInvariant())
            {
                case "subscribe":
                    var eventTypes = command.EventTypes?
                        .Select(t => Enum.TryParse<MemoryEventType>(t, out var et) ? et : (MemoryEventType?)null)
                        .Where(t => t.HasValue)
                        .Select(t => t!.Value)
                        .ToArray() ?? [];

                    UpdateSubscription(connectionId, new WebSocketSubscription
                    {
                        EventTypes = eventTypes,
                        StreamIds = command.StreamIds?.Select(Guid.Parse).ToArray()
                    });

                    await SendToConnectionAsync(connectionId, JsonSerializer.Serialize(new
                    {
                        type = "subscribed",
                        eventTypes = eventTypes.Select(e => e.ToString()).ToArray()
                    }, _jsonOptions), cancellationToken);
                    break;

                case "ping":
                    await SendToConnectionAsync(connectionId, "{\"type\":\"pong\"}", cancellationToken);
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error processing WebSocket message from {ConnectionId}", connectionId);
        }
    }

    private async Task SendToConnectionAsync(WebSocketConnection connection, ArraySegment<byte> segment, CancellationToken cancellationToken)
    {
        try
        {
            await connection.WebSocket.SendAsync(segment, WebSocketMessageType.Text, true, cancellationToken);
            connection.LastMessageAt = DateTimeOffset.UtcNow;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error sending to WebSocket {ConnectionId}", connection.ConnectionId);
        }
    }

    private string CreateEventMessage(StoredEvent @event)
    {
        return JsonSerializer.Serialize(new WebSocketEventMessage
        {
            Type = "event",
            EventId = @event.EventId,
            StreamId = @event.StreamId,
            EventType = @event.EventType.ToString(),
            EventVersion = @event.EventVersion,
            GlobalSequence = @event.GlobalSequence,
            CreatedAt = @event.CreatedAt,
            ContentHash = @event.ContentHash
        }, _jsonOptions);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var connectionId in _connections.Keys)
        {
            await UnregisterConnectionAsync(connectionId);
        }

        _broadcastLock.Dispose();
    }
}

/// <summary>
/// Represents an active WebSocket connection.
/// </summary>
internal sealed class WebSocketConnection
{
    public required Guid ConnectionId { get; init; }
    public required WebSocket WebSocket { get; init; }
    public required DateTimeOffset ConnectedAt { get; init; }
    public WebSocketSubscription Subscription { get; set; } = WebSocketSubscription.All;
    public DateTimeOffset LastMessageAt { get; set; }
}

/// <summary>
/// Subscription filter for WebSocket events.
/// </summary>
public sealed class WebSocketSubscription
{
    /// <summary>
    /// Event types to receive. Empty = all events.
    /// </summary>
    public MemoryEventType[] EventTypes { get; init; } = [];

    /// <summary>
    /// Specific stream IDs to receive. Null = all streams.
    /// </summary>
    public Guid[]? StreamIds { get; init; }

    /// <summary>
    /// Default subscription that receives all events.
    /// </summary>
    public static WebSocketSubscription All => new();

    /// <summary>
    /// Check if this subscription should receive the given event type.
    /// </summary>
    public bool ShouldReceive(MemoryEventType eventType)
    {
        if (EventTypes.Length == 0) return true;
        return EventTypes.Contains(eventType);
    }

    /// <summary>
    /// Check if this subscription should receive events for the given stream.
    /// </summary>
    public bool ShouldReceiveStream(Guid streamId)
    {
        if (StreamIds == null || StreamIds.Length == 0) return true;
        return StreamIds.Contains(streamId);
    }
}

/// <summary>
/// WebSocket command from client.
/// </summary>
internal sealed class WebSocketCommand
{
    public string? Type { get; set; }
    public string[]? EventTypes { get; set; }
    public string[]? StreamIds { get; set; }
}

/// <summary>
/// WebSocket event message sent to clients.
/// </summary>
internal sealed class WebSocketEventMessage
{
    public string Type { get; set; } = "event";
    public Guid EventId { get; set; }
    public Guid StreamId { get; set; }
    public string EventType { get; set; } = "";
    public long EventVersion { get; set; }
    public long GlobalSequence { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? ContentHash { get; set; }
}
