using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using SerialMemory.EventSourcing.Events;

namespace SerialMemory.EventSourcing.Store;

/// <summary>
/// PostgreSQL-backed event store with append-only semantics.
/// Events are never modified or deleted after insertion.
/// </summary>
public sealed class PostgresEventStore : IEventStore
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<PostgresEventStore> _logger;
    private readonly JsonSerializerOptions _jsonOptions;

    public PostgresEventStore(string connectionString, ILogger<PostgresEventStore> logger)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        _dataSource = builder.Build();
        _logger = logger;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
            Converters = { new JsonStringEnumConverter() }
        };
    }

    public async Task<long[]> AppendEventsAsync(
        Guid streamId,
        IReadOnlyList<IMemoryEvent> events,
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        if (events.Count == 0) return [];

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await conn.BeginTransactionAsync(cancellationToken);

        try
        {
            // Check current version with lock
            var currentVersion = await conn.ExecuteScalarAsync<long?>(new CommandDefinition(
                "SELECT MAX(event_version) FROM memory_events WHERE stream_id = @StreamId FOR UPDATE",
                new { StreamId = streamId },
                transaction,
                cancellationToken: cancellationToken
            )) ?? 0;

            if (currentVersion != expectedVersion)
            {
                throw new ConcurrencyException(streamId, expectedVersion, currentVersion);
            }

            var sequences = new long[events.Count];
            var insertSql = @"
                INSERT INTO memory_events
                    (event_id, stream_id, event_type, event_version, event_data, metadata, created_at, created_by, content_hash)
                VALUES
                    (@EventId, @StreamId, @EventType::memory_event_type, @EventVersion, @EventData::jsonb, @Metadata::jsonb, @CreatedAt, @CreatedBy, @ContentHash)
                RETURNING global_sequence";

            for (int i = 0; i < events.Count; i++)
            {
                var @event = events[i];
                var eventData = SerializeEvent(@event);
                var metadata = JsonSerializer.Serialize(@event switch
                {
                    MemoryEventBase baseEvent => baseEvent.Metadata,
                    _ => new EventMetadata()
                }, _jsonOptions);

                var sequence = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                    insertSql,
                    new
                    {
                        EventId = @event.EventId,
                        StreamId = @event.StreamId,
                        EventType = @event.EventType.ToString(),
                        EventVersion = @event.EventVersion,
                        EventData = eventData,
                        Metadata = metadata,
                        CreatedAt = @event.CreatedAt,
                        CreatedBy = @event.CreatedBy,
                        ContentHash = @event.ContentHash
                    },
                    transaction,
                    cancellationToken: cancellationToken
                ));

                sequences[i] = sequence;
            }

            await transaction.CommitAsync(cancellationToken);
            _logger.LogDebug("Appended {Count} events to stream {StreamId}", events.Count, streamId);

            return sequences;
        }
        catch (ConcurrencyException)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync(cancellationToken);
            _logger.LogError(ex, "Failed to append events to stream {StreamId}", streamId);
            throw;
        }
    }

    public async Task<IReadOnlyList<IMemoryEvent>> ReadStreamAsync(
        Guid streamId,
        CancellationToken cancellationToken = default)
    {
        return await ReadStreamAsync(streamId, 0, cancellationToken);
    }

    public async Task<IReadOnlyList<IMemoryEvent>> ReadStreamAsync(
        Guid streamId,
        long fromVersion,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        var stored = await conn.QueryAsync<StoredEventRow>(new CommandDefinition(
            @"SELECT event_id, stream_id, event_type, event_version, global_sequence,
                     event_data, metadata, created_at, created_by, content_hash
              FROM memory_events
              WHERE stream_id = @StreamId AND event_version >= @FromVersion
              ORDER BY event_version",
            new { StreamId = streamId, FromVersion = fromVersion },
            cancellationToken: cancellationToken
        ));

        return [.. stored.Select(DeserializeEvent)];
    }

    public async Task<IReadOnlyList<StoredEvent>> ReadAllAsync(
        long fromGlobalSequence,
        int maxCount = 1000,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        var stored = await conn.QueryAsync<StoredEventRow>(new CommandDefinition(
            @"SELECT event_id, stream_id, event_type, event_version, global_sequence,
                     event_data, metadata, created_at, created_by, content_hash
              FROM memory_events
              WHERE global_sequence > @FromSequence
              ORDER BY global_sequence
              LIMIT @MaxCount",
            new { FromSequence = fromGlobalSequence, MaxCount = maxCount },
            cancellationToken: cancellationToken
        ));

        return [.. stored.Select(row => new StoredEvent
        {
            EventId = row.event_id,
            StreamId = row.stream_id,
            EventType = Enum.TryParse<MemoryEventType>(row.event_type, ignoreCase: true, out var eventType)
                ? eventType
                : MemoryEventType.Unknown,
            EventVersion = row.event_version,
            GlobalSequence = row.global_sequence,
            EventData = row.event_data,
            Metadata = row.metadata,
            CreatedAt = row.created_at,
            CreatedBy = row.created_by,
            ContentHash = row.content_hash
        })];
    }

    public async Task<long> GetStreamVersionAsync(
        Guid streamId,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        return await conn.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT MAX(event_version) FROM memory_events WHERE stream_id = @StreamId",
            new { StreamId = streamId },
            cancellationToken: cancellationToken
        )) ?? 0;
    }

    public async Task<long> GetLatestGlobalSequenceAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        return await conn.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT MAX(global_sequence) FROM memory_events",
            cancellationToken: cancellationToken
        )) ?? 0;
    }

    public async IAsyncEnumerable<StoredEvent> SubscribeAsync(
        long fromGlobalSequence,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var lastSequence = fromGlobalSequence;

        while (!cancellationToken.IsCancellationRequested)
        {
            var events = await ReadAllAsync(lastSequence, 100, cancellationToken);

            foreach (var @event in events)
            {
                yield return @event;
                lastSequence = @event.GlobalSequence;
            }

            if (events.Count == 0)
            {
                await Task.Delay(100, cancellationToken);
            }
        }
    }

    private string SerializeEvent(IMemoryEvent @event)
    {
        return @event switch
        {
            MemoryCreatedEvent e => JsonSerializer.Serialize(e, _jsonOptions),
            MemoryUpdatedEvent e => JsonSerializer.Serialize(e, _jsonOptions),
            MemoryMergedEvent e => JsonSerializer.Serialize(e, _jsonOptions),
            MemoryInvalidatedEvent e => JsonSerializer.Serialize(e, _jsonOptions),
            MemoryDecayedEvent e => JsonSerializer.Serialize(e, _jsonOptions),
            MemoryReinforcedEvent e => JsonSerializer.Serialize(e, _jsonOptions),
            MemoryLayerTransitionedEvent e => JsonSerializer.Serialize(e, _jsonOptions),
            _ => throw new InvalidOperationException($"Unknown event type: {@event.GetType().Name}")
        };
    }

    private IMemoryEvent DeserializeEvent(StoredEventRow row)
    {
        if (!Enum.TryParse<MemoryEventType>(row.event_type, ignoreCase: true, out var eventType))
        {
            _logger.LogWarning("Unknown event type '{EventType}' for event {EventId}, treating as Unknown", row.event_type, row.event_id);
            eventType = MemoryEventType.Unknown;
        }

        return eventType switch
        {
            MemoryEventType.MemoryCreated => JsonSerializer.Deserialize<MemoryCreatedEvent>(row.event_data, _jsonOptions)!,
            MemoryEventType.MemoryUpdated => JsonSerializer.Deserialize<MemoryUpdatedEvent>(row.event_data, _jsonOptions)!,
            MemoryEventType.MemoryMerged => JsonSerializer.Deserialize<MemoryMergedEvent>(row.event_data, _jsonOptions)!,
            MemoryEventType.MemoryInvalidated => JsonSerializer.Deserialize<MemoryInvalidatedEvent>(row.event_data, _jsonOptions)!,
            MemoryEventType.MemoryDecayed => JsonSerializer.Deserialize<MemoryDecayedEvent>(row.event_data, _jsonOptions)!,
            MemoryEventType.MemoryReinforced => JsonSerializer.Deserialize<MemoryReinforcedEvent>(row.event_data, _jsonOptions)!,
            MemoryEventType.MemoryLayerTransitioned => JsonSerializer.Deserialize<MemoryLayerTransitionedEvent>(row.event_data, _jsonOptions)!,
            // For unknown/unhandled events, return a generic event that can be safely skipped
            _ => new UnknownEvent
            {
                EventId = row.event_id,
                StreamId = row.stream_id,
                EventType = eventType,
                EventVersion = row.event_version,
                CreatedAt = row.created_at,
                CreatedBy = row.created_by,
                ContentHash = row.content_hash,
                RawEventData = row.event_data
            }
        };
    }

    private sealed record StoredEventRow
    {
        public Guid event_id { get; init; }
        public Guid stream_id { get; init; }
        public string event_type { get; init; } = string.Empty;
        public long event_version { get; init; }
        public long global_sequence { get; init; }
        public string event_data { get; init; } = string.Empty;
        public string metadata { get; init; } = string.Empty;
        public DateTimeOffset created_at { get; init; }
        public string? created_by { get; init; }
        public string content_hash { get; init; } = string.Empty;
    }
}
