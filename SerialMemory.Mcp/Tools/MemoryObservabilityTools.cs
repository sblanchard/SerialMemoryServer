using System.Text.Json.Nodes;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using SerialMemory.EventSourcing.Aggregates;
using SerialMemory.EventSourcing.Events;
using SerialMemory.EventSourcing.Store;

namespace SerialMemory.Mcp.Tools;

/// <summary>
/// MCP tool handlers for memory observability operations.
/// Provides tracing, lineage, explanation, and conflict detection.
/// </summary>
public sealed class MemoryObservabilityTools
{
    private readonly IEventStore _eventStore;
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger _logger;

    public MemoryObservabilityTools(
        IEventStore eventStore,
        string connectionString,
        ILogger logger)
    {
        _eventStore = eventStore;
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        _dataSource = builder.Build();
        _logger = logger;
    }

    private const string EventSourcingNotAvailable =
        "Event-sourcing schema not available. This tool requires the memory_events table. " +
        "Run eventsourcing_schema.sql migration to enable this feature.";

    /// <summary>
    /// memory_trace - Get complete event history for a memory.
    /// </summary>
    public async Task<object> HandleMemoryTrace(JsonNode? arguments)
    {
        var memoryIdStr = arguments?["memory_id"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrEmpty(memoryIdStr) || !Guid.TryParse(memoryIdStr, out var memoryId))
            throw new ArgumentException("Valid memory_id is required");

        var includePayloads = arguments?["include_payloads"]?.GetValue<bool>() ?? false;

        try
        {
            // Read all events for this stream
            var events = await _eventStore.ReadStreamAsync(memoryId);

            if (events.Count == 0)
                return CreateErrorResponse($"Memory {memoryId} not found");

            var traceLines = new List<string>
            {
                $"## Memory Trace: {memoryId}",
                $"Total Events: {events.Count}",
                ""
            };

            foreach (var evt in events.OrderBy(e => e.EventVersion))
            {
                traceLines.Add($"### Event {evt.EventVersion}: {evt.EventType}");
                traceLines.Add($"- Event ID: {evt.EventId}");
                traceLines.Add($"- Timestamp: {evt.CreatedAt:O}");
                traceLines.Add($"- Created By: {evt.CreatedBy ?? "N/A"}");
                traceLines.Add($"- Content Hash: {evt.ContentHash}");

                if (includePayloads)
                {
                    traceLines.Add($"- Payload:");
                    var payloadSummary = GetEventPayloadSummary(evt);
                    traceLines.Add($"  ```");
                    traceLines.Add($"  {payloadSummary}");
                    traceLines.Add($"  ```");
                }

                traceLines.Add("");
            }

            return CreateTextResponse(string.Join("\n", traceLines));
        }
        catch (PostgresException ex) when (ex.SqlState is "42P01" or "42703")
        {
            _logger.LogWarning("memory_trace skipped: {Message}", ex.MessageText);
            return CreateErrorResponse(EventSourcingNotAvailable);
        }
    }

    /// <summary>
    /// memory_lineage - Trace causal ancestry of a memory.
    /// </summary>
    public async Task<object> HandleMemoryLineage(JsonNode? arguments)
    {
        var memoryIdStr = arguments?["memory_id"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrEmpty(memoryIdStr) || !Guid.TryParse(memoryIdStr, out var memoryId))
            throw new ArgumentException("Valid memory_id is required");

        var maxDepth = Math.Clamp(arguments?["max_depth"]?.GetValue<int>() ?? 5, 1, 10);
        var direction = arguments?["direction"]?.GetValue<string>()?.Trim()?.ToLowerInvariant() ?? "ancestors";

        try
        {
            var visited = new HashSet<Guid>();
            var lineage = new List<(Guid Id, int Depth, string Direction)>();

            if (direction == "ancestors" || direction == "both")
            {
                await TraceAncestors(memoryId, 0, maxDepth, visited, lineage);
            }

            if (direction == "descendants" || direction == "both")
            {
                visited.Clear();
                await TraceDescendants(memoryId, 0, maxDepth, visited, lineage);
            }

            var responseLines = new List<string>
            {
                $"## Memory Lineage: {memoryId}",
                $"Direction: {direction}",
                $"Max Depth: {maxDepth}",
                $"Nodes Found: {lineage.Count}",
                ""
            };

            var ancestors = lineage.Where(l => l.Direction == "ancestor").OrderByDescending(l => l.Depth).ToList();
            var descendants = lineage.Where(l => l.Direction == "descendant").OrderBy(l => l.Depth).ToList();

            if (ancestors.Count > 0)
            {
                responseLines.Add("### Ancestors (parents/grandparents):");
                foreach (var (id, depth, _) in ancestors)
                {
                    var indent = new string(' ', (maxDepth - depth) * 2);
                    var events = await _eventStore.ReadStreamAsync(id);
                    var summary = events.Count > 0 ? GetMemorySummary(events) : "Unknown";
                    responseLines.Add($"{indent}└─ [{depth}] {id}");
                    responseLines.Add($"{indent}   {summary}");
                }
                responseLines.Add("");
            }

            responseLines.Add($"### Current Memory:");
            responseLines.Add($"   ★ {memoryId}");
            var currentEvents = await _eventStore.ReadStreamAsync(memoryId);
            if (currentEvents.Count > 0)
            {
                responseLines.Add($"     {GetMemorySummary(currentEvents)}");
            }
            responseLines.Add("");

            if (descendants.Count > 0)
            {
                responseLines.Add("### Descendants (children/grandchildren):");
                foreach (var (id, depth, _) in descendants)
                {
                    var indent = new string(' ', depth * 2);
                    var events = await _eventStore.ReadStreamAsync(id);
                    var summary = events.Count > 0 ? GetMemorySummary(events) : "Unknown";
                    responseLines.Add($"{indent}└─ [{depth}] {id}");
                    responseLines.Add($"{indent}   {summary}");
                }
            }

            return CreateTextResponse(string.Join("\n", responseLines));
        }
        catch (PostgresException ex) when (ex.SqlState is "42P01" or "42703")
        {
            _logger.LogWarning("memory_lineage skipped: {Message}", ex.MessageText);
            return CreateErrorResponse(EventSourcingNotAvailable);
        }
    }

    /// <summary>
    /// memory_explain - Explain memory state and how it got there.
    /// </summary>
    public async Task<object> HandleMemoryExplain(JsonNode? arguments)
    {
        var memoryIdStr = arguments?["memory_id"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrEmpty(memoryIdStr) || !Guid.TryParse(memoryIdStr, out var memoryId))
            throw new ArgumentException("Valid memory_id is required");

        try
        {
        // Load aggregate
        var events = await _eventStore.ReadStreamAsync(memoryId);
        if (events.Count == 0)
            return CreateErrorResponse($"Memory {memoryId} not found");

        var aggregate = MemoryAggregate.FromEvents(events);

        var explanation = new List<string>
        {
            $"## Memory Explanation: {memoryId}",
            "",
            "### Current State:",
            $"- **Active**: {aggregate.IsActive}",
            $"- **Archived**: {aggregate.IsArchived}",
            $"- **Expired**: {aggregate.IsExpired}",
            $"- **Split**: {aggregate.IsSplit}",
            $"- **Layer**: {aggregate.Layer}",
            $"- **Version**: {aggregate.Version}",
            "",
            "### Confidence:",
            $"- **Base Confidence**: {aggregate.ConfidenceScore:F4}",
            $"- **Current (with decay)**: {aggregate.CurrentConfidence:F4}",
            $"- **Half-Life**: {aggregate.HalfLifeDays} days",
            $"- **Last Reinforced**: {aggregate.LastReinforcedAt:O}",
            $"- **Days Since Reinforcement**: {(DateTimeOffset.UtcNow - aggregate.LastReinforcedAt).TotalDays:F1}",
            "",
            "### Access Patterns:",
            $"- **Recall Count**: {aggregate.RecallCount}",
            $"- **Ignore Count**: {aggregate.IgnoreCount}",
            $"- **Last Recalled**: {aggregate.LastRecalledAt?.ToString("O") ?? "Never"}",
            "",
            "### Integrity:",
            $"- **Content Hash**: {aggregate.ContentHash}",
            $"- **Integrity Valid**: {aggregate.VerifyIntegrity()}",
            $"- **Causal Parents**: {(aggregate.CausalParents.Length > 0 ? string.Join(", ", aggregate.CausalParents) : "None")}",
            $"- **Validated By**: {(aggregate.ValidatedBy.Length > 0 ? string.Join(", ", aggregate.ValidatedBy) : "None")}",
            "",
            "### Relationships:",
            $"- **Contradictions**: {(aggregate.ContradictionIds.Length > 0 ? string.Join(", ", aggregate.ContradictionIds) : "None")}",
            $"- **Child Memories**: {(aggregate.ChildMemoryIds.Length > 0 ? string.Join(", ", aggregate.ChildMemoryIds) : "None")}",
            $"- **Merged Into**: {aggregate.MergedIntoId?.ToString() ?? "N/A"}",
            "",
            "### Content Preview:",
            $"```",
            aggregate.Content.Length > 500 ? aggregate.Content[..500] + "..." : aggregate.Content,
            $"```",
            "",
            "### Event History Summary:"
        };

        var eventCounts = events
            .GroupBy(e => e.EventType)
            .Select(g => $"- {g.Key}: {g.Count()}")
            .ToList();

        explanation.AddRange(eventCounts);

        explanation.Add("");
        explanation.Add($"### Why is this memory in its current state?");

        // Analyze state
        if (!aggregate.IsActive)
        {
            if (aggregate.IsExpired)
                explanation.Add("- Memory was **expired** due to TTL policy.");
            else if (aggregate.IsSplit)
                explanation.Add($"- Memory was **split** into {aggregate.ChildMemoryIds.Length} child memories.");
            else if (aggregate.MergedIntoId.HasValue)
                explanation.Add($"- Memory was **merged** into {aggregate.MergedIntoId}.");
            else
                explanation.Add("- Memory was **invalidated** (soft deleted).");
        }
        else
        {
            if (aggregate.CurrentConfidence < 0.5f)
                explanation.Add("- Memory confidence has **decayed significantly** - consider reinforcing or archiving.");
            if (aggregate.ContradictionIds.Length > 0)
                explanation.Add($"- Memory has **{aggregate.ContradictionIds.Length} contradictions** that should be resolved.");
            if (aggregate.RecallCount == 0)
                explanation.Add("- Memory has **never been recalled** - may be orphaned or irrelevant.");
            if (aggregate.RecallCount > 10)
                explanation.Add("- Memory is **frequently accessed** - considered valuable.");
        }

        return CreateTextResponse(string.Join("\n", explanation));
        }
        catch (PostgresException ex) when (ex.SqlState is "42P01" or "42703")
        {
            _logger.LogWarning("memory_explain skipped: {Message}", ex.MessageText);
            return CreateErrorResponse(EventSourcingNotAvailable);
        }
    }

    /// <summary>
    /// memory_conflicts - Find all conflicts/contradictions for a memory.
    /// </summary>
    public async Task<object> HandleMemoryConflicts(JsonNode? arguments)
    {
        var memoryIdStr = arguments?["memory_id"]?.GetValue<string>()?.Trim();
        Guid? memoryId = null;
        if (!string.IsNullOrEmpty(memoryIdStr) && Guid.TryParse(memoryIdStr, out var mid))
            memoryId = mid;

        var limit = Math.Clamp(arguments?["limit"]?.GetValue<int>() ?? 50, 1, 200);

        await using var conn = await _dataSource.OpenConnectionAsync();

        IEnumerable<dynamic> conflicts;

        if (memoryId.HasValue)
        {
            // Get conflicts for specific memory
            conflicts = await conn.QueryAsync<dynamic>(@"
                SELECT
                    report_id,
                    memory_a_id,
                    memory_b_id,
                    contradiction_score,
                    detection_method,
                    resolution_status,
                    notes,
                    created_at
                FROM contradiction_reports
                WHERE memory_a_id = @MemoryId OR memory_b_id = @MemoryId
                ORDER BY contradiction_score DESC
                LIMIT @Limit",
                new { MemoryId = memoryId.Value, Limit = limit });
        }
        else
        {
            // Get all unresolved conflicts
            conflicts = await conn.QueryAsync<dynamic>(@"
                SELECT
                    report_id,
                    memory_a_id,
                    memory_b_id,
                    contradiction_score,
                    detection_method,
                    resolution_status,
                    notes,
                    created_at
                FROM contradiction_reports
                WHERE resolution_status = 'detected'
                ORDER BY contradiction_score DESC
                LIMIT @Limit",
                new { Limit = limit });
        }

        var conflictList = conflicts.ToList();

        var responseLines = new List<string>
        {
            memoryId.HasValue
                ? $"## Conflicts for Memory: {memoryId}"
                : "## All Unresolved Conflicts",
            $"Found: {conflictList.Count} conflict(s)",
            ""
        };

        if (conflictList.Count == 0)
        {
            responseLines.Add("No conflicts found.");
        }
        else
        {
            foreach (var c in conflictList)
            {
                responseLines.Add($"### Conflict: {c.report_id}");
                responseLines.Add($"- Memory A: {c.memory_a_id}");
                responseLines.Add($"- Memory B: {c.memory_b_id}");
                responseLines.Add($"- Score: {c.contradiction_score:F3}");
                responseLines.Add($"- Detection Method: {c.detection_method}");
                responseLines.Add($"- Status: {c.resolution_status}");
                responseLines.Add($"- Created: {c.created_at:O}");
                if (c.notes != null)
                    responseLines.Add($"- Notes: {c.notes}");
                responseLines.Add("");
            }
        }

        return CreateTextResponse(string.Join("\n", responseLines));
    }

    private async Task TraceAncestors(Guid memoryId, int depth, int maxDepth, HashSet<Guid> visited, List<(Guid, int, string)> lineage)
    {
        if (depth >= maxDepth || visited.Contains(memoryId))
            return;

        visited.Add(memoryId);

        var events = await _eventStore.ReadStreamAsync(memoryId);
        if (events.Count == 0) return;

        var aggregate = MemoryAggregate.FromEvents(events);

        foreach (var parentId in aggregate.CausalParents)
        {
            lineage.Add((parentId, depth + 1, "ancestor"));
            await TraceAncestors(parentId, depth + 1, maxDepth, visited, lineage);
        }
    }

    private async Task TraceDescendants(Guid memoryId, int depth, int maxDepth, HashSet<Guid> visited, List<(Guid, int, string)> lineage)
    {
        if (depth >= maxDepth || visited.Contains(memoryId))
            return;

        visited.Add(memoryId);

        // Query for memories that have this memory as a causal parent
        await using var conn = await _dataSource.OpenConnectionAsync();

        var children = await conn.QueryAsync<Guid>(@"
            SELECT memory_id FROM memory_projections
            WHERE @ParentId = ANY(causal_parents)
            LIMIT 100",
            new { ParentId = memoryId });

        foreach (var childId in children)
        {
            lineage.Add((childId, depth + 1, "descendant"));
            await TraceDescendants(childId, depth + 1, maxDepth, visited, lineage);
        }
    }

    private static string GetEventPayloadSummary(IMemoryEvent @event)
    {
        return @event switch
        {
            MemoryCreatedEvent e => $"Content: {e.Content.Substring(0, Math.Min(100, e.Content.Length))}..., Layer: {e.Layer}",
            MemoryUpdatedEvent e => $"NewContent: {e.NewContent.Substring(0, Math.Min(100, e.NewContent.Length))}..., Reason: {e.Reason}",
            MemoryReinforcedEvent e => $"Confidence: {e.PreviousConfidence} -> {e.NewConfidence}, Source: {e.ReinforcementSource}",
            MemoryDecayedEvent e => $"Confidence: {e.PreviousConfidence} -> {e.NewConfidence}, Days: {e.DaysSinceReinforcement}",
            MemoryInvalidatedEvent e => $"Reason: {e.Reason}, SupersededBy: {e.SupersededById}",
            MemoryMergedEvent e => $"Sources: {string.Join(",", e.SourceMemoryIds)}, Strategy: {e.MergeStrategy}",
            MemorySplitEvent e => $"Children: {string.Join(",", e.ChildMemoryIds)}, Strategy: {e.SplitStrategy}",
            MemoryExpiredEvent e => $"Policy: {e.ExpirationPolicy}, TTL: {e.OriginalTtlDays} days",
            _ => @event.EventType.ToString()
        };
    }

    private static string GetMemorySummary(IReadOnlyList<IMemoryEvent> events)
    {
        var created = events.OfType<MemoryCreatedEvent>().FirstOrDefault();
        if (created == null) return "No creation event";

        var content = created.Content.Length > 60
            ? created.Content.Substring(0, 60) + "..."
            : created.Content;

        return $"[{created.Layer}] {content}";
    }

    private static object CreateTextResponse(string text) =>
        new
        {
            content = new[]
            {
                new { type = "text", text }
            }
        };

    private static object CreateErrorResponse(string message) =>
        new
        {
            content = new[]
            {
                new { type = "text", text = $"Error: {message}" }
            },
            isError = true
        };
}
