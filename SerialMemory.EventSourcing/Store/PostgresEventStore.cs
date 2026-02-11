using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using SerialMemory.Core.Json;
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
        _jsonOptions = SerialMemoryJsonOptions.WithEnums;
    }

    public PostgresEventStore(NpgsqlDataSource dataSource, ILogger<PostgresEventStore> logger)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _logger = logger;
        _jsonOptions = SerialMemoryJsonOptions.WithEnums;
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

            // Build arrays for UNNEST-based batch insert (single roundtrip)
            var eventIds = new Guid[events.Count];
            var streamIds = new Guid[events.Count];
            var eventTypes = new string[events.Count];
            var eventVersions = new long[events.Count];
            var eventDatas = new string[events.Count];
            var metadatas = new string[events.Count];
            var createdAts = new DateTimeOffset[events.Count];
            var createdBys = new string?[events.Count];
            var contentHashes = new string[events.Count];

            for (int i = 0; i < events.Count; i++)
            {
                var @event = events[i];
                eventIds[i] = @event.EventId;
                streamIds[i] = @event.StreamId;
                eventTypes[i] = @event.EventType.ToString();
                eventVersions[i] = @event.EventVersion;
                eventDatas[i] = SerializeEvent(@event);
                metadatas[i] = JsonSerializer.Serialize(@event switch
                {
                    MemoryEventBase baseEvent => baseEvent.Metadata,
                    _ => new EventMetadata()
                }, _jsonOptions);
                createdAts[i] = @event.CreatedAt;
                createdBys[i] = @event.CreatedBy;
                contentHashes[i] = @event.ContentHash;
            }

            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO memory_events
                    (event_id, stream_id, event_type, event_version, event_data, metadata, created_at, created_by, content_hash)
                SELECT * FROM UNNEST(
                    @EventIds, @StreamIds, @EventTypes::memory_event_type[],
                    @EventVersions, @EventDatas::jsonb[], @Metadatas::jsonb[],
                    @CreatedAts, @CreatedBys, @ContentHashes)
                RETURNING global_sequence", conn);
            cmd.Transaction = transaction;

            cmd.Parameters.Add(new NpgsqlParameter<Guid[]>("EventIds", eventIds));
            cmd.Parameters.Add(new NpgsqlParameter<Guid[]>("StreamIds", streamIds));
            cmd.Parameters.Add(new NpgsqlParameter<string[]>("EventTypes", eventTypes));
            cmd.Parameters.Add(new NpgsqlParameter<long[]>("EventVersions", eventVersions));
            cmd.Parameters.Add(new NpgsqlParameter<string[]>("EventDatas", eventDatas));
            cmd.Parameters.Add(new NpgsqlParameter<string[]>("Metadatas", metadatas));
            cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset[]>("CreatedAts", createdAts));
            cmd.Parameters.Add(new NpgsqlParameter<string?[]>("CreatedBys", createdBys));
            cmd.Parameters.Add(new NpgsqlParameter<string[]>("ContentHashes", contentHashes));

            var sequences = new long[events.Count];
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            var idx = 0;
            while (await reader.ReadAsync(cancellationToken))
            {
                sequences[idx++] = reader.GetInt64(0);
            }

            await transaction.CommitAsync(cancellationToken);
            _logger.LogDebug("Appended {Count} events to stream {StreamId}", events.Count, streamId);

            // Notify subscribers that new events are available
            try
            {
                await conn.ExecuteAsync("NOTIFY memory_events_channel");
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "NOTIFY failed (non-critical)");
            }

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

        // Use LISTEN/NOTIFY for push-based event notification instead of polling
        await using var listenConn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await listenConn.ExecuteAsync("LISTEN memory_events_channel");

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
                // Wait for NOTIFY signal or timeout after 5 seconds (fallback poll)
                try
                {
                    await listenConn.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
                }
                catch (TimeoutException)
                {
                    // Timeout is expected - just re-check for events
                }
            }
        }
    }

    public async Task<Dictionary<Guid, IReadOnlyList<IMemoryEvent>>> ReadStreamsAsync(
        IEnumerable<Guid> streamIds,
        CancellationToken cancellationToken = default)
    {
        var ids = streamIds as Guid[] ?? streamIds.ToArray();
        var result = new Dictionary<Guid, IReadOnlyList<IMemoryEvent>>();

        if (ids.Length == 0)
            return result;

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        var stored = await conn.QueryAsync<StoredEventRow>(new CommandDefinition(
            @"SELECT event_id, stream_id, event_type, event_version, global_sequence,
                     event_data, metadata, created_at, created_by, content_hash
              FROM memory_events
              WHERE stream_id = ANY(@StreamIds)
              ORDER BY stream_id, event_version",
            new { StreamIds = ids },
            cancellationToken: cancellationToken
        ));

        foreach (var group in stored.GroupBy(r => r.stream_id))
        {
            result[group.Key] = [.. group.Select(DeserializeEvent)];
        }

        // Ensure all requested IDs have an entry (empty list for missing streams)
        foreach (var id in ids)
        {
            result.TryAdd(id, []);
        }

        return result;
    }

    public async Task AppendBatchEventsAsync(
        IReadOnlyList<(Guid StreamId, IReadOnlyList<IMemoryEvent> Events)> batches,
        CancellationToken cancellationToken = default)
    {
        if (batches.Count == 0) return;

        var totalEvents = 0;
        foreach (var batch in batches)
            totalEvents += batch.Events.Count;

        if (totalEvents == 0) return;

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await conn.BeginTransactionAsync(cancellationToken);

        try
        {
            // Verify and lock stream versions for all streams in the batch.
            // Collect current versions keyed by stream ID.
            var streamVersions = new Dictionary<Guid, long>();
            foreach (var (streamId, events) in batches)
            {
                if (streamVersions.ContainsKey(streamId)) continue;

                var currentVersion = await conn.ExecuteScalarAsync<long?>(new CommandDefinition(
                    "SELECT MAX(event_version) FROM memory_events WHERE stream_id = @StreamId FOR UPDATE",
                    new { StreamId = streamId },
                    transaction,
                    cancellationToken: cancellationToken
                )) ?? 0;

                streamVersions[streamId] = currentVersion;
            }

            // Build a single multi-row INSERT for all events across all streams.
            var sql = new System.Text.StringBuilder();
            sql.Append(@"
                INSERT INTO memory_events
                    (event_id, stream_id, event_type, event_version, event_data, metadata, created_at, created_by, content_hash)
                VALUES ");

            var parameters = new DynamicParameters();
            var paramIndex = 0;

            foreach (var (streamId, events) in batches)
            {
                var version = streamVersions[streamId];

                for (int i = 0; i < events.Count; i++)
                {
                    var @event = events[i];
                    version++;

                    var eventData = SerializeEvent(@event);
                    var metadata = JsonSerializer.Serialize(@event switch
                    {
                        MemoryEventBase baseEvent => baseEvent.Metadata,
                        _ => new EventMetadata()
                    }, _jsonOptions);

                    if (paramIndex > 0) sql.Append(", ");

                    sql.Append($"(@EventId_{paramIndex}, @StreamId_{paramIndex}, @EventType_{paramIndex}::memory_event_type, @EventVersion_{paramIndex}, @EventData_{paramIndex}::jsonb, @Metadata_{paramIndex}::jsonb, @CreatedAt_{paramIndex}, @CreatedBy_{paramIndex}, @ContentHash_{paramIndex})");

                    parameters.Add($"EventId_{paramIndex}", @event.EventId);
                    parameters.Add($"StreamId_{paramIndex}", streamId);
                    parameters.Add($"EventType_{paramIndex}", @event.EventType.ToString());
                    parameters.Add($"EventVersion_{paramIndex}", version);
                    parameters.Add($"EventData_{paramIndex}", eventData);
                    parameters.Add($"Metadata_{paramIndex}", metadata);
                    parameters.Add($"CreatedAt_{paramIndex}", @event.CreatedAt);
                    parameters.Add($"CreatedBy_{paramIndex}", @event.CreatedBy);
                    parameters.Add($"ContentHash_{paramIndex}", @event.ContentHash);

                    paramIndex++;
                }

                // Update the running version for this stream in case multiple batches
                // target the same stream.
                streamVersions[streamId] = version;
            }

            await conn.ExecuteAsync(new CommandDefinition(
                sql.ToString(),
                parameters,
                transaction,
                cancellationToken: cancellationToken));

            await transaction.CommitAsync(cancellationToken);
            _logger.LogDebug("Batch-appended {Count} events across {Streams} streams", totalEvents, streamVersions.Count);

            // Notify subscribers that new events are available
            try
            {
                await conn.ExecuteAsync("NOTIFY memory_events_channel");
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "NOTIFY failed (non-critical)");
            }
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync(cancellationToken);
            _logger.LogError(ex, "Failed to batch-append {Count} events", totalEvents);
            throw;
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
            MemoryArchivedEvent e => JsonSerializer.Serialize(e, _jsonOptions),
            MemoryRecalledEvent e => JsonSerializer.Serialize(e, _jsonOptions),
            MemoryIgnoredEvent e => JsonSerializer.Serialize(e, _jsonOptions),
            MemoryContradictedEvent e => JsonSerializer.Serialize(e, _jsonOptions),
            MemoryExpiredEvent e => JsonSerializer.Serialize(e, _jsonOptions),
            MemorySplitEvent e => JsonSerializer.Serialize(e, _jsonOptions),
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
            MemoryEventType.MemoryArchived => JsonSerializer.Deserialize<MemoryArchivedEvent>(row.event_data, _jsonOptions)!,
            MemoryEventType.MemoryRecalled => JsonSerializer.Deserialize<MemoryRecalledEvent>(row.event_data, _jsonOptions)!,
            MemoryEventType.MemoryIgnored => JsonSerializer.Deserialize<MemoryIgnoredEvent>(row.event_data, _jsonOptions)!,
            MemoryEventType.MemoryContradicted => JsonSerializer.Deserialize<MemoryContradictedEvent>(row.event_data, _jsonOptions)!,
            MemoryEventType.MemoryExpired => JsonSerializer.Deserialize<MemoryExpiredEvent>(row.event_data, _jsonOptions)!,
            MemoryEventType.MemorySplit => JsonSerializer.Deserialize<MemorySplitEvent>(row.event_data, _jsonOptions)!,
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
