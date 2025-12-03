using Dapper;
using Npgsql;

namespace SerialMemory.Api.Analysis;

/// <summary>
/// Hybrid Replay Engine - READ-ONLY forensic replay of events.
/// Reconstructs past state from events without modifying live data.
/// </summary>
public interface IReplayService
{
    Task<ReplayResult> ReplayAsync(ReplayRequest request, CancellationToken ct = default);
    Task<EventRange> GetEventRangeAsync(CancellationToken ct = default);
}

public sealed class ReplayService : IReplayService
{
    private readonly string _connectionString;
    private readonly ILogger<ReplayService> _logger;

    public ReplayService(string connectionString, ILogger<ReplayService> logger)
    {
        _connectionString = connectionString;
        _logger = logger;
    }

    public async Task<EventRange> GetEventRangeAsync(CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var memoryRange = await conn.QueryFirstOrDefaultAsync<(long? Min, long? Max, int? Count)>(@"
            SELECT MIN(global_sequence), MAX(global_sequence), COUNT(*)::int
            FROM memory_events");

        // Use ROW_NUMBER() since sequence_number may not exist in all schemas
        var graphRange = await conn.QueryFirstOrDefaultAsync<(long? Min, long? Max, int? Count)>(@"
            WITH numbered AS (
                SELECT ROW_NUMBER() OVER (ORDER BY COALESCE(occurred_at, NOW())) AS seq
                FROM graph_events
            )
            SELECT MIN(seq), MAX(seq), COUNT(*)::int FROM numbered");

        return new EventRange
        {
            MemoryEvents = new EventRangeInfo
            {
                MinSequence = memoryRange.Min ?? 0,
                MaxSequence = memoryRange.Max ?? 0,
                TotalCount = memoryRange.Count ?? 0
            },
            GraphEvents = new EventRangeInfo
            {
                MinSequence = graphRange.Min ?? 0,
                MaxSequence = graphRange.Max ?? 0,
                TotalCount = graphRange.Count ?? 0
            }
        };
    }

    public async Task<ReplayResult> ReplayAsync(ReplayRequest request, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        // Load events within the requested range
        var memoryEvents = await LoadMemoryEventsAsync(conn, request.FromSequence, request.ToSequence, ct);
        var graphEvents = await LoadGraphEventsAsync(conn, request.FromSequence, request.ToSequence, ct);

        // Build simulated replay state from events
        var replayState = BuildReplayState(memoryEvents, graphEvents);

        // Load current live state for comparison
        var liveState = await LoadLiveStateAsync(conn, ct);

        // Compute diff between replay and live states
        var diff = ComputeDiff(replayState, liveState);

        sw.Stop();

        return new ReplayResult
        {
            Summary = new ReplaySummary
            {
                FromSequence = request.FromSequence,
                ToSequence = request.ToSequence ?? memoryEvents.DefaultIfEmpty().Max(e => e?.SequenceNumber ?? 0),
                MemoryEventsProcessed = memoryEvents.Count,
                GraphEventsProcessed = graphEvents.Count,
                ProcessingTimeMs = sw.ElapsedMilliseconds
            },
            ReplayState = replayState,
            Diff = diff
        };
    }

    private async Task<List<MemoryEvent>> LoadMemoryEventsAsync(
        NpgsqlConnection conn, long fromSequence, long? toSequence, CancellationToken ct)
    {
        var sql = @"
            SELECT
                global_sequence AS ""SequenceNumber"",
                stream_id AS ""StreamId"",
                event_type::text AS ""EventType"",
                event_data AS ""Payload"",
                created_at AS ""OccurredAt"",
                created_by AS ""ActorId""
            FROM memory_events
            WHERE global_sequence >= @FromSequence
            " + (toSequence.HasValue ? "AND global_sequence <= @ToSequence" : "") + @"
            ORDER BY global_sequence ASC
            LIMIT 10000";

        var events = await conn.QueryAsync<MemoryEvent>(sql, new { FromSequence = fromSequence, ToSequence = toSequence });
        return events.ToList();
    }

    private async Task<List<GraphEvent>> LoadGraphEventsAsync(
        NpgsqlConnection conn, long fromSequence, long? toSequence, CancellationToken ct)
    {
        // Handle both power_user schema (sequence_number, entity_id) and graph_events_schema (event_id, node_id)
        var sql = @"
            SELECT
                ROW_NUMBER() OVER (ORDER BY occurred_at) AS ""SequenceNumber"",
                event_type::text AS ""EventType"",
                node_id AS ""EntityId"",
                edge_id AS ""RelationshipId"",
                COALESCE(new_state, metadata, '{}')::text AS ""Payload"",
                occurred_at AS ""OccurredAt""
            FROM graph_events
            WHERE occurred_at >= (
                SELECT COALESCE(MIN(occurred_at), '1970-01-01'::timestamptz)
                FROM graph_events
                WHERE ROW_NUMBER() OVER (ORDER BY occurred_at) >= @FromSequence
            )
            ORDER BY occurred_at ASC
            LIMIT 10000";

        // Simpler approach - just get events ordered by time
        sql = @"
            WITH numbered AS (
                SELECT
                    ROW_NUMBER() OVER (ORDER BY occurred_at) AS seq,
                    event_type::text AS event_type,
                    node_id,
                    edge_id,
                    COALESCE(new_state, metadata, '{}')::text AS payload,
                    occurred_at
                FROM graph_events
            )
            SELECT
                seq AS ""SequenceNumber"",
                event_type AS ""EventType"",
                node_id AS ""EntityId"",
                edge_id AS ""RelationshipId"",
                payload AS ""Payload"",
                occurred_at AS ""OccurredAt""
            FROM numbered
            WHERE seq >= @FromSequence
            " + (toSequence.HasValue ? "AND seq <= @ToSequence" : "") + @"
            ORDER BY seq ASC
            LIMIT 10000";

        var events = await conn.QueryAsync<GraphEvent>(sql, new { FromSequence = fromSequence, ToSequence = toSequence });
        return events.ToList();
    }

    private SimulatedState BuildReplayState(List<MemoryEvent> memoryEvents, List<GraphEvent> graphEvents)
    {
        var state = new SimulatedState();

        // Process memory events chronologically
        foreach (var evt in memoryEvents.OrderBy(e => e.SequenceNumber))
        {
            ApplyMemoryEvent(state, evt);
        }

        // Process graph events chronologically
        foreach (var evt in graphEvents.OrderBy(e => e.SequenceNumber))
        {
            ApplyGraphEvent(state, evt);
        }

        return state;
    }

    private void ApplyMemoryEvent(SimulatedState state, MemoryEvent evt)
    {
        switch (evt.EventType)
        {
            case "MemoryCreated":
                state.Memories[evt.StreamId] = new SimulatedMemory
                {
                    Id = evt.StreamId,
                    IsActive = true,
                    LastEventType = evt.EventType,
                    LastEventAt = evt.OccurredAt,
                    EventCount = 1
                };
                break;

            case "MemoryUpdated":
            case "MemoryReinforced":
            case "MemoryDecayed":
                if (state.Memories.TryGetValue(evt.StreamId, out var mem))
                {
                    mem.LastEventType = evt.EventType;
                    mem.LastEventAt = evt.OccurredAt;
                    mem.EventCount++;
                }
                break;

            case "MemoryInvalidated":
            case "MemoryArchived":
                if (state.Memories.TryGetValue(evt.StreamId, out var invalidMem))
                {
                    invalidMem.IsActive = false;
                    invalidMem.LastEventType = evt.EventType;
                    invalidMem.LastEventAt = evt.OccurredAt;
                    invalidMem.EventCount++;
                }
                break;

            case "MemoryMerged":
                // Merged memories become inactive, source memories link to target
                if (state.Memories.TryGetValue(evt.StreamId, out var mergedMem))
                {
                    mergedMem.IsActive = false;
                    mergedMem.LastEventType = evt.EventType;
                    mergedMem.LastEventAt = evt.OccurredAt;
                    mergedMem.EventCount++;
                }
                break;
        }
    }

    private void ApplyGraphEvent(SimulatedState state, GraphEvent evt)
    {
        switch (evt.EventType)
        {
            case "EntityCreated":
                if (evt.EntityId.HasValue)
                {
                    state.Entities[evt.EntityId.Value] = new SimulatedEntity
                    {
                        Id = evt.EntityId.Value,
                        IsActive = true,
                        CreatedAt = evt.OccurredAt
                    };
                }
                break;

            case "EntityDeleted":
                if (evt.EntityId.HasValue && state.Entities.TryGetValue(evt.EntityId.Value, out var entity))
                {
                    entity.IsActive = false;
                }
                break;

            case "RelationshipCreated":
                if (evt.RelationshipId.HasValue)
                {
                    state.Relationships[evt.RelationshipId.Value] = new SimulatedRelationship
                    {
                        Id = evt.RelationshipId.Value,
                        IsActive = true,
                        CreatedAt = evt.OccurredAt
                    };
                }
                break;

            case "RelationshipDeleted":
                if (evt.RelationshipId.HasValue && state.Relationships.TryGetValue(evt.RelationshipId.Value, out var rel))
                {
                    rel.IsActive = false;
                }
                break;
        }
    }

    private async Task<LiveState> LoadLiveStateAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        // Load current memory state from projections (eventsourcing)
        var memories = await conn.QueryAsync<LiveMemory>(@"
            SELECT
                memory_id AS ""Id"",
                is_active AS ""IsActive"",
                created_at AS ""CreatedAt"",
                updated_at AS ""UpdatedAt"",
                layer::text AS ""Layer"",
                confidence_score AS ""ConfidenceScore""
            FROM memory_projections
            LIMIT 10000");

        // Load current entity state
        var entities = await conn.QueryAsync<LiveEntity>(@"
            SELECT
                id AS ""Id"",
                name AS ""Name"",
                entity_type AS ""EntityType"",
                created_at AS ""CreatedAt""
            FROM entities
            LIMIT 10000");

        // Load current relationship state
        var relationships = await conn.QueryAsync<LiveRelationship>(@"
            SELECT
                id AS ""Id"",
                source_entity_id AS ""FromEntityId"",
                target_entity_id AS ""ToEntityId"",
                relationship_type AS ""RelationshipType"",
                created_at AS ""CreatedAt""
            FROM entity_relationships
            LIMIT 10000");

        return new LiveState
        {
            Memories = memories.ToDictionary(m => m.Id),
            Entities = entities.ToDictionary(e => e.Id),
            Relationships = relationships.ToDictionary(r => r.Id)
        };
    }

    private StateDiff ComputeDiff(SimulatedState replayState, LiveState liveState)
    {
        var diff = new StateDiff();

        // Memory diff
        var replayMemoryIds = replayState.Memories.Keys.ToHashSet();
        var liveMemoryIds = liveState.Memories.Keys.ToHashSet();

        diff.Memories.Added = liveMemoryIds.Except(replayMemoryIds)
            .Select(id => new DiffEntry { Id = id, ChangeType = "Added" }).ToList();

        diff.Memories.Removed = replayMemoryIds.Except(liveMemoryIds)
            .Select(id => new DiffEntry { Id = id, ChangeType = "Removed" }).ToList();

        foreach (var id in replayMemoryIds.Intersect(liveMemoryIds))
        {
            var replay = replayState.Memories[id];
            var live = liveState.Memories[id];
            if (replay.IsActive != live.IsActive)
            {
                diff.Memories.Changed.Add(new DiffEntry
                {
                    Id = id,
                    ChangeType = "Modified",
                    Details = $"IsActive: {replay.IsActive} -> {live.IsActive}"
                });
            }
        }

        // Entity diff
        var replayEntityIds = replayState.Entities.Keys.ToHashSet();
        var liveEntityIds = liveState.Entities.Keys.ToHashSet();

        diff.Entities.Added = liveEntityIds.Except(replayEntityIds)
            .Select(id => new DiffEntry { Id = id, ChangeType = "Added" }).ToList();

        diff.Entities.Removed = replayEntityIds.Except(liveEntityIds)
            .Select(id => new DiffEntry { Id = id, ChangeType = "Removed" }).ToList();

        // Relationship diff
        var replayRelIds = replayState.Relationships.Keys.ToHashSet();
        var liveRelIds = liveState.Relationships.Keys.ToHashSet();

        diff.Relationships.Added = liveRelIds.Except(replayRelIds)
            .Select(id => new DiffEntry { Id = id, ChangeType = "Added" }).ToList();

        diff.Relationships.Removed = replayRelIds.Except(liveRelIds)
            .Select(id => new DiffEntry { Id = id, ChangeType = "Removed" }).ToList();

        return diff;
    }
}

#region Models

public sealed class ReplayRequest
{
    public long FromSequence { get; set; }
    public long? ToSequence { get; set; }
}

public sealed class ReplayResult
{
    public ReplaySummary Summary { get; set; } = new();
    public SimulatedState ReplayState { get; set; } = new();
    public StateDiff Diff { get; set; } = new();
}

public sealed class ReplaySummary
{
    public long FromSequence { get; set; }
    public long ToSequence { get; set; }
    public int MemoryEventsProcessed { get; set; }
    public int GraphEventsProcessed { get; set; }
    public long ProcessingTimeMs { get; set; }
}

public sealed class EventRange
{
    public EventRangeInfo MemoryEvents { get; set; } = new();
    public EventRangeInfo GraphEvents { get; set; } = new();
}

public sealed class EventRangeInfo
{
    public long MinSequence { get; set; }
    public long MaxSequence { get; set; }
    public int TotalCount { get; set; }
}

public sealed class SimulatedState
{
    public Dictionary<Guid, SimulatedMemory> Memories { get; set; } = new();
    public Dictionary<Guid, SimulatedEntity> Entities { get; set; } = new();
    public Dictionary<Guid, SimulatedRelationship> Relationships { get; set; } = new();
}

public sealed class SimulatedMemory
{
    public Guid Id { get; set; }
    public bool IsActive { get; set; }
    public string LastEventType { get; set; } = "";
    public DateTimeOffset LastEventAt { get; set; }
    public int EventCount { get; set; }
}

public sealed class SimulatedEntity
{
    public Guid Id { get; set; }
    public bool IsActive { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class SimulatedRelationship
{
    public Guid Id { get; set; }
    public bool IsActive { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class LiveState
{
    public Dictionary<Guid, LiveMemory> Memories { get; set; } = new();
    public Dictionary<Guid, LiveEntity> Entities { get; set; } = new();
    public Dictionary<Guid, LiveRelationship> Relationships { get; set; } = new();
}

public sealed class LiveMemory
{
    public Guid Id { get; set; }
    public bool IsActive { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public string? Layer { get; set; }
    public double ConfidenceScore { get; set; }
}

public sealed class LiveEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string EntityType { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class LiveRelationship
{
    public Guid Id { get; set; }
    public Guid FromEntityId { get; set; }
    public Guid ToEntityId { get; set; }
    public string RelationshipType { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class StateDiff
{
    public DiffSection Memories { get; set; } = new();
    public DiffSection Entities { get; set; } = new();
    public DiffSection Relationships { get; set; } = new();
}

public sealed class DiffSection
{
    public List<DiffEntry> Added { get; set; } = new();
    public List<DiffEntry> Removed { get; set; } = new();
    public List<DiffEntry> Changed { get; set; } = new();
}

public sealed class DiffEntry
{
    public Guid Id { get; set; }
    public string ChangeType { get; set; } = "";
    public string? Details { get; set; }
}

// Event models for loading from DB
public sealed class MemoryEvent
{
    public long SequenceNumber { get; set; }
    public Guid StreamId { get; set; }
    public string EventType { get; set; } = "";
    public string? Payload { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string? ActorId { get; set; }
}

public sealed class GraphEvent
{
    public long SequenceNumber { get; set; }
    public string EventType { get; set; } = "";
    public Guid? EntityId { get; set; }
    public Guid? RelationshipId { get; set; }
    public string? Payload { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

#endregion
