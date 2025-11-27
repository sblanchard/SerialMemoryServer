using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SerialMemory.EventSourcing.Store;
using StackExchange.Redis;

namespace SerialMemory.EventSourcing.Streaming;

/// <summary>
/// Redis Streams implementation for event publishing and subscription.
/// Provides durable, ordered event delivery with consumer groups.
/// </summary>
public sealed class RedisEventStreamPublisher(
    string redisConnectionString,
    ILogger<RedisEventStreamPublisher> logger)
    : IEventStreamPublisher, IEventStreamSubscriber, IDisposable
{
    private readonly IConnectionMultiplexer _redis = ConnectionMultiplexer.Connect(redisConnectionString);

    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private const string StreamKey = "memory:events";

    public async Task PublishToStreamAsync(StoredEvent @event, CancellationToken cancellationToken = default)
    {
        var db = _redis.GetDatabase();

        var entries = new NameValueEntry[]
        {
            new("eventId", @event.EventId.ToString()),
            new("streamId", @event.StreamId.ToString()),
            new("eventType", @event.EventType.ToString()),
            new("eventVersion", @event.EventVersion.ToString()),
            new("globalSequence", @event.GlobalSequence.ToString()),
            new("eventData", @event.EventData),
            new("metadata", @event.Metadata),
            new("createdAt", @event.CreatedAt.ToString("O")),
            new("createdBy", @event.CreatedBy ?? ""),
            new("contentHash", @event.ContentHash)
        };

        var messageId = await db.StreamAddAsync(StreamKey, entries, maxLength: 100000);

        logger.LogDebug("Published event {EventId} to Redis stream as {MessageId}",
            @event.EventId, messageId);
    }

    public async Task BroadcastToWebSocketsAsync(StoredEvent @event, CancellationToken cancellationToken = default)
    {
        var pub = _redis.GetSubscriber();

        var message = JsonSerializer.Serialize(new
        {
            @event.EventId,
            @event.StreamId,
            @event.EventType,
            @event.EventVersion,
            @event.GlobalSequence,
            @event.CreatedAt
        }, _jsonOptions);

        await pub.PublishAsync(RedisChannel.Literal("memory:events:broadcast"), message);
    }

    public async IAsyncEnumerable<StreamMessage> SubscribeAsync(
        string consumerGroup,
        string consumerId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var db = _redis.GetDatabase();

        // Ensure consumer group exists
        try
        {
            await db.StreamCreateConsumerGroupAsync(StreamKey, consumerGroup, "$", createStream: true);
            logger.LogInformation("Created consumer group {Group}", consumerGroup);
        }
        catch (RedisServerException ex) when (ex.Message.Contains("BUSYGROUP"))
        {
            // Group already exists, ok
        }

        // First, drain any pending (unacknowledged) messages from previous runs
        var lastPendingId = "0-0";
        while (!cancellationToken.IsCancellationRequested)
        {
            var pending = await db.StreamReadGroupAsync(
                StreamKey, consumerGroup, consumerId,
                lastPendingId,
                count: 100);

            if (pending.Length == 0)
                break;

            foreach (var entry in pending)
            {
                var storedEvent = ParseStreamEntry(entry);
                if (storedEvent != null)
                {
                    yield return new StreamMessage(entry.Id.ToString(), storedEvent);
                }
                lastPendingId = entry.Id.ToString();
            }
        }

        logger.LogDebug("Finished draining pending messages for consumer {ConsumerId}", consumerId);

        // Now process new messages as they arrive
        while (!cancellationToken.IsCancellationRequested)
        {
            var entries = await db.StreamReadGroupAsync(
                StreamKey, consumerGroup, consumerId,
                ">", // Only new messages not yet delivered to any consumer
                count: 100);

            foreach (var entry in entries)
            {
                var storedEvent = ParseStreamEntry(entry);
                if (storedEvent != null)
                {
                    yield return new StreamMessage(entry.Id.ToString(), storedEvent);
                }
            }

            if (entries.Length == 0)
            {
                await Task.Delay(100, cancellationToken);
            }
        }
    }

    public async Task AcknowledgeAsync(string consumerGroup, string messageId, CancellationToken cancellationToken = default)
    {
        var db = _redis.GetDatabase();
        await db.StreamAcknowledgeAsync(StreamKey, consumerGroup, messageId);
    }

    /// <summary>
    /// Initialize consumer group for the stream.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var db = _redis.GetDatabase();

        try
        {
            await db.StreamCreateConsumerGroupAsync(StreamKey, "projections", "0", createStream: true);
            logger.LogInformation("Created projections consumer group");
        }
        catch (RedisServerException ex) when (ex.Message.Contains("BUSYGROUP"))
        {
            // Already exists
        }

        try
        {
            await db.StreamCreateConsumerGroupAsync(StreamKey, "maintenance", "0", createStream: true);
            logger.LogInformation("Created maintenance consumer group");
        }
        catch (RedisServerException ex) when (ex.Message.Contains("BUSYGROUP"))
        {
            // Already exists
        }
    }

    /// <summary>
    /// Get stream info including length and consumer groups.
    /// </summary>
    public async Task<StreamInfo> GetStreamInfoAsync(CancellationToken cancellationToken = default)
    {
        var db = _redis.GetDatabase();

        var info = await db.StreamInfoAsync(StreamKey);

        return new StreamInfo
        {
            Length = info.Length,
            FirstEntry = info.FirstEntry.Id.ToString(),
            LastEntry = info.LastEntry.Id.ToString()
        };
    }

    private StoredEvent? ParseStreamEntry(StreamEntry entry)
    {
        try
        {
            var dict = entry.Values.ToDictionary(x => x.Name.ToString(), x => x.Value.ToString());

            return new StoredEvent
            {
                EventId = Guid.Parse(dict["eventId"]),
                StreamId = Guid.Parse(dict["streamId"]),
                EventType = Enum.Parse<Events.MemoryEventType>(dict["eventType"]),
                EventVersion = long.Parse(dict["eventVersion"]),
                GlobalSequence = long.Parse(dict["globalSequence"]),
                EventData = dict["eventData"],
                Metadata = dict["metadata"],
                CreatedAt = DateTimeOffset.Parse(dict["createdAt"]),
                CreatedBy = string.IsNullOrEmpty(dict["createdBy"]) ? null : dict["createdBy"],
                ContentHash = dict["contentHash"]
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to parse stream entry {Id}", entry.Id);
            return null;
        }
    }

    public void Dispose()
    {
        _redis.Dispose();
    }
}

public sealed record StreamInfo
{
    public long Length { get; init; }
    public string? FirstEntry { get; init; }
    public string? LastEntry { get; init; }
}
