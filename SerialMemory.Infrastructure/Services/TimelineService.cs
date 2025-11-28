using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using SerialMemory.Core.Interfaces;

namespace SerialMemory.Infrastructure.Services;

/// <summary>
/// PostgreSQL-backed timeline service using real event data.
/// </summary>
public sealed class TimelineService(string connectionString, ILogger<TimelineService> logger) : ITimelineService
{
    private readonly ILogger<TimelineService> _logger = logger;

    public async Task<MemoryTimeline> GetMemoryTimelineAsync(
        Guid memoryId,
        CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);

        // Get events from memory_events table
        var events = await conn.QueryAsync<EventRow>("""
            SELECT event_id, stream_id, event_type, event_version, global_sequence,
                   event_data, metadata, created_at, created_by, content_hash
            FROM memory_events
            WHERE stream_id = @MemoryId
            ORDER BY event_version ASC
            """,
            new { MemoryId = memoryId });

        var timelineEvents = new List<TimelineEvent>();
        float? prevConfidence = null;

        foreach (var row in events)
        {
            var evt = ParseEventRow(row, prevConfidence);
            timelineEvents.Add(evt);
            prevConfidence = evt.ConfidenceAfter ?? prevConfidence;
        }

        // Build current state by replaying events
        var currentState = ReplayEvents(memoryId, events.ToList());

        return new MemoryTimeline
        {
            MemoryId = memoryId,
            Events = timelineEvents,
            CurrentState = currentState,
            CreatedAt = timelineEvents.FirstOrDefault()?.Timestamp,
            LastModifiedAt = timelineEvents.LastOrDefault()?.Timestamp,
            TotalEvents = timelineEvents.Count
        };
    }

    public async Task<GlobalTimeline> GetGlobalTimelineAsync(
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        int limit = 100,
        CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);

        var fromDate = from ?? DateTimeOffset.UtcNow.AddDays(-7);
        var toDate = to ?? DateTimeOffset.UtcNow;

        var events = await conn.QueryAsync<EventRow>("""
            SELECT event_id, stream_id, event_type, event_version, global_sequence,
                   event_data, metadata, created_at, created_by, content_hash
            FROM memory_events
            WHERE created_at >= @From AND created_at <= @To
            ORDER BY global_sequence DESC
            LIMIT @Limit
            """,
            new { From = fromDate, To = toDate, Limit = limit });

        var timelineEvents = events.Select(e => ParseEventRow(e, null)).ToList();

        // Count by event type
        var typeCounts = timelineEvents
            .GroupBy(e => e.EventType)
            .ToDictionary(g => g.Key, g => g.Count());

        // Get total count
        var totalCount = await conn.ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM memory_events
            WHERE created_at >= @From AND created_at <= @To
            """,
            new { From = fromDate, To = toDate });

        return new GlobalTimeline
        {
            Events = timelineEvents,
            From = fromDate,
            To = toDate,
            TotalEvents = totalCount,
            EventTypeCounts = typeCounts
        };
    }

    public async Task<ConfidenceDriftTimeline> GetConfidenceDriftAsync(
        Guid memoryId,
        CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);

        var events = await conn.QueryAsync<EventRow>("""
            SELECT event_id, stream_id, event_type, event_version, global_sequence,
                   event_data, metadata, created_at, created_by, content_hash
            FROM memory_events
            WHERE stream_id = @MemoryId
            ORDER BY event_version ASC
            """,
            new { MemoryId = memoryId });

        var points = new List<ConfidencePoint>();
        float currentConfidence = 1.0f;
        float minConfidence = 1.0f;
        float maxConfidence = 0f;
        float initialConfidence = 1.0f;
        int decayCount = 0;
        int reinforceCount = 0;
        bool first = true;

        foreach (var row in events)
        {
            var data = ParseEventData(row.event_data);
            float? newConfidence = null;
            string? reason = null;

            switch (row.event_type)
            {
                case "MemoryCreated":
                    newConfidence = GetFloat(data, "confidenceScore") ?? 1.0f;
                    if (first)
                    {
                        initialConfidence = newConfidence.Value;
                        first = false;
                    }
                    reason = "Memory created";
                    break;

                case "MemoryDecayed":
                    newConfidence = GetFloat(data, "newConfidence");
                    reason = $"Decayed from {GetFloat(data, "previousConfidence"):P0}";
                    decayCount++;
                    break;

                case "MemoryReinforced":
                    newConfidence = GetFloat(data, "newConfidence");
                    reason = GetString(data, "reinforcementSource") ?? "Reinforced";
                    reinforceCount++;
                    break;

                case "MemoryUpdated":
                    reason = GetString(data, "reason") ?? "Content updated";
                    break;

                case "MemoryInvalidated":
                    newConfidence = 0f;
                    reason = GetString(data, "reason") ?? "Invalidated";
                    break;
            }

            if (newConfidence.HasValue)
            {
                currentConfidence = newConfidence.Value;
                minConfidence = Math.Min(minConfidence, currentConfidence);
                maxConfidence = Math.Max(maxConfidence, currentConfidence);

                points.Add(new ConfidencePoint
                {
                    Timestamp = row.created_at,
                    Confidence = currentConfidence,
                    EventType = row.event_type,
                    Reason = reason
                });
            }
        }

        return new ConfidenceDriftTimeline
        {
            MemoryId = memoryId,
            Points = points,
            InitialConfidence = initialConfidence,
            CurrentConfidence = currentConfidence,
            MinConfidence = minConfidence,
            MaxConfidence = maxConfidence,
            DecayCount = decayCount,
            ReinforceCount = reinforceCount
        };
    }

    public async Task<CausalChain> GetCausalChainAsync(
        Guid memoryId,
        int maxDepth = 5,
        CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);

        // Get the memory's causal parents from the created event
        var createEvent = await conn.QueryFirstOrDefaultAsync<EventRow>("""
            SELECT event_id, stream_id, event_type, event_version, global_sequence,
                   event_data, metadata, created_at, created_by, content_hash
            FROM memory_events
            WHERE stream_id = @MemoryId AND event_type = 'MemoryCreated'
            LIMIT 1
            """,
            new { MemoryId = memoryId });

        var ancestors = new List<CausalNode>();
        var descendants = new List<CausalNode>();

        if (createEvent != null)
        {
            var data = ParseEventData(createEvent.event_data);
            var causalParents = GetGuidArray(data, "causalParents");

            // Recursively get ancestors
            await GetAncestorsRecursive(conn, causalParents, ancestors, 1, maxDepth, ct);
        }

        // Find descendants (memories that have this memory as a causal parent)
        var descendantEvents = await conn.QueryAsync<EventRow>("""
            SELECT event_id, stream_id, event_type, event_version, global_sequence,
                   event_data, metadata, created_at, created_by, content_hash
            FROM memory_events
            WHERE event_type = 'MemoryCreated'
              AND event_data::text LIKE @Pattern
            """,
            new { Pattern = $"%{memoryId}%" });

        foreach (var evt in descendantEvents)
        {
            var data = ParseEventData(evt.event_data);
            var parents = GetGuidArray(data, "causalParents");

            if (parents.Contains(memoryId))
            {
                var content = GetString(data, "content") ?? "";
                var confidence = GetFloat(data, "confidenceScore") ?? 1.0f;

                descendants.Add(new CausalNode
                {
                    MemoryId = evt.stream_id,
                    ContentPreview = content.Length > 100 ? content[..100] + "..." : content,
                    Relationship = "caused_by",
                    Depth = 1,
                    CreatedAt = evt.created_at,
                    Confidence = confidence,
                    IsActive = true
                });
            }
        }

        return new CausalChain
        {
            MemoryId = memoryId,
            Ancestors = ancestors,
            Descendants = descendants,
            TotalAncestors = ancestors.Count,
            TotalDescendants = descendants.Count
        };
    }

    public async Task<TimelineMemorySnapshot?> ReplayAtAsync(
        Guid memoryId,
        DateTimeOffset pointInTime,
        CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);

        var events = await conn.QueryAsync<EventRow>("""
            SELECT event_id, stream_id, event_type, event_version, global_sequence,
                   event_data, metadata, created_at, created_by, content_hash
            FROM memory_events
            WHERE stream_id = @MemoryId AND created_at <= @PointInTime
            ORDER BY event_version ASC
            """,
            new { MemoryId = memoryId, PointInTime = pointInTime });

        var eventList = events.ToList();
        if (eventList.Count == 0) return null;

        return ReplayEvents(memoryId, eventList, pointInTime);
    }

    private async Task GetAncestorsRecursive(
        NpgsqlConnection conn,
        List<Guid> parentIds,
        List<CausalNode> ancestors,
        int depth,
        int maxDepth,
        CancellationToken ct)
    {
        if (depth > maxDepth || parentIds.Count == 0) return;

        foreach (var parentId in parentIds)
        {
            if (ancestors.Any(a => a.MemoryId == parentId)) continue;

            var createEvent = await conn.QueryFirstOrDefaultAsync<EventRow>("""
                SELECT event_id, stream_id, event_type, event_version, global_sequence,
                       event_data, metadata, created_at, created_by, content_hash
                FROM memory_events
                WHERE stream_id = @MemoryId AND event_type = 'MemoryCreated'
                LIMIT 1
                """,
                new { MemoryId = parentId });

            if (createEvent == null) continue;

            var data = ParseEventData(createEvent.event_data);
            var content = GetString(data, "content") ?? "";
            var confidence = GetFloat(data, "confidenceScore") ?? 1.0f;

            ancestors.Add(new CausalNode
            {
                MemoryId = parentId,
                ContentPreview = content.Length > 100 ? content[..100] + "..." : content,
                Relationship = "parent",
                Depth = depth,
                CreatedAt = createEvent.created_at,
                Confidence = confidence,
                IsActive = true
            });

            // Recurse into grandparents
            var grandparents = GetGuidArray(data, "causalParents");
            await GetAncestorsRecursive(conn, grandparents, ancestors, depth + 1, maxDepth, ct);
        }
    }

    private TimelineMemorySnapshot? ReplayEvents(Guid memoryId, List<EventRow> events, DateTimeOffset? asOf = null)
    {
        if (events.Count == 0) return null;

        string content = "";
        float confidence = 1.0f;
        string layer = "L0_RAW";
        bool isActive = true;
        long version = 0;
        var causalParents = new List<Guid>();
        string? contentHash = null;

        foreach (var row in events)
        {
            var data = ParseEventData(row.event_data);
            version = row.event_version;
            contentHash = row.content_hash;

            switch (row.event_type)
            {
                case "MemoryCreated":
                    content = GetString(data, "content") ?? "";
                    confidence = GetFloat(data, "confidenceScore") ?? 1.0f;
                    layer = GetString(data, "layer") ?? "L0_RAW";
                    causalParents = GetGuidArray(data, "causalParents");
                    break;

                case "MemoryUpdated":
                    content = GetString(data, "newContent") ?? content;
                    break;

                case "MemoryDecayed":
                    confidence = GetFloat(data, "newConfidence") ?? confidence;
                    break;

                case "MemoryReinforced":
                    confidence = GetFloat(data, "newConfidence") ?? confidence;
                    break;

                case "MemoryLayerTransitioned":
                    layer = GetString(data, "newLayer") ?? layer;
                    break;

                case "MemoryInvalidated":
                    isActive = false;
                    break;

                case "MemoryExpired":
                    isActive = false;
                    break;

                case "MemoryArchived":
                    isActive = false;
                    break;
            }
        }

        return new TimelineMemorySnapshot
        {
            MemoryId = memoryId,
            Content = content,
            Confidence = confidence,
            Layer = layer,
            IsActive = isActive,
            AsOf = asOf ?? events.Last().created_at,
            Version = version,
            CausalParents = causalParents,
            ContentHash = contentHash
        };
    }

    private TimelineEvent ParseEventRow(EventRow row, float? prevConfidence)
    {
        var data = ParseEventData(row.event_data);
        var description = GenerateDescription(row.event_type, data);
        float? confBefore = prevConfidence;
        float? confAfter = null;

        switch (row.event_type)
        {
            case "MemoryCreated":
                confAfter = GetFloat(data, "confidenceScore");
                break;
            case "MemoryDecayed":
                confBefore = GetFloat(data, "previousConfidence");
                confAfter = GetFloat(data, "newConfidence");
                break;
            case "MemoryReinforced":
                confBefore = GetFloat(data, "previousConfidence");
                confAfter = GetFloat(data, "newConfidence");
                break;
            case "MemoryInvalidated":
                confAfter = 0f;
                break;
        }

        return new TimelineEvent
        {
            EventId = row.event_id,
            MemoryId = row.stream_id,
            EventType = row.event_type,
            Timestamp = row.created_at,
            Description = description,
            Data = data,
            ConfidenceBefore = confBefore,
            ConfidenceAfter = confAfter,
            ContentHash = row.content_hash
        };
    }

    private string GenerateDescription(string eventType, Dictionary<string, object> data)
    {
        return eventType switch
        {
            "MemoryCreated" => $"Memory created with confidence {GetFloat(data, "confidenceScore"):P0}",
            "MemoryUpdated" => GetString(data, "reason") ?? "Memory content updated",
            "MemoryDecayed" => $"Confidence decayed from {GetFloat(data, "previousConfidence"):P0} to {GetFloat(data, "newConfidence"):P0}",
            "MemoryReinforced" => $"Reinforced by {GetString(data, "reinforcementSource") ?? "unknown"}",
            "MemoryInvalidated" => GetString(data, "reason") ?? "Memory invalidated",
            "MemoryMerged" => "Merged from multiple memories",
            "MemorySplit" => "Split into child memories",
            "MemoryLayerTransitioned" => $"Layer changed from {GetString(data, "previousLayer")} to {GetString(data, "newLayer")}",
            "MemoryArchived" => GetString(data, "reason") ?? "Memory archived",
            "MemoryExpired" => $"Expired by policy: {GetString(data, "expirationPolicy")}",
            "MemoryRecalled" => $"Recalled with similarity {GetFloat(data, "similarityScore"):P0}",
            "MemoryContradicted" => $"Contradiction detected: {GetString(data, "contradictionType")}",
            _ => eventType
        };
    }

    private Dictionary<string, object> ParseEventData(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private string? GetString(Dictionary<string, object> data, string key)
    {
        if (data.TryGetValue(key, out var value))
        {
            return value?.ToString();
        }
        return null;
    }

    private float? GetFloat(Dictionary<string, object> data, string key)
    {
        if (data.TryGetValue(key, out var value))
        {
            if (value is JsonElement je && je.TryGetSingle(out var f)) return f;
            if (float.TryParse(value?.ToString(), out var parsed)) return parsed;
        }
        return null;
    }

    private List<Guid> GetGuidArray(Dictionary<string, object> data, string key)
    {
        if (data.TryGetValue(key, out var value))
        {
            if (value is JsonElement je && je.ValueKind == JsonValueKind.Array)
            {
                var guids = new List<Guid>();
                foreach (var item in je.EnumerateArray())
                {
                    if (Guid.TryParse(item.GetString(), out var g))
                        guids.Add(g);
                }
                return guids;
            }
        }
        return [];
    }

    private sealed record EventRow
    {
        public Guid event_id { get; init; }
        public Guid stream_id { get; init; }
        public string event_type { get; init; } = "";
        public long event_version { get; init; }
        public long global_sequence { get; init; }
        public string event_data { get; init; } = "";
        public string metadata { get; init; } = "";
        public DateTimeOffset created_at { get; init; }
        public string? created_by { get; init; }
        public string content_hash { get; init; } = "";
    }
}
