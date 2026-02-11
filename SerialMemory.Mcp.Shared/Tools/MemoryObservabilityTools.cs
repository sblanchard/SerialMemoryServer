using System.Text.Json.Nodes;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using SerialMemory.EventSourcing.Aggregates;
using SerialMemory.EventSourcing.Events;
using SerialMemory.EventSourcing.Store;

namespace SerialMemory.Mcp.Shared.Tools;

/// <summary>
/// Memory observability tools: trace, lineage, explain, conflicts.
/// </summary>
public sealed class MemoryObservabilityTools(
    IEventStore eventStore,
    NpgsqlDataSource dataSource,
    ILogger logger)
{
    private readonly NpgsqlDataSource _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public async Task<object> HandleMemoryTrace(JsonNode? arguments)
    {
        var memoryIdStr = arguments?["memory_id"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrEmpty(memoryIdStr) || !Guid.TryParse(memoryIdStr, out var memoryId))
            throw new ArgumentException("Valid memory_id required");

        var includePayloads = arguments?["include_payloads"]?.GetValue<bool>() ?? false;
        var events = await eventStore.ReadStreamAsync(memoryId);

        if (events.Count == 0)
            return CreateErrorResponse($"Memory {memoryId} not found");

        var lines = new List<string> { $"## Trace: {memoryId}", $"Events: {events.Count}", "" };

        foreach (var evt in events.OrderBy(e => e.EventVersion))
        {
            lines.Add($"### Event {evt.EventVersion}: {evt.EventType}");
            lines.Add($"- ID: {evt.EventId}");
            lines.Add($"- Time: {evt.CreatedAt:O}");
            lines.Add($"- Actor: {evt.CreatedBy ?? "N/A"}");

            if (includePayloads)
                lines.Add($"- Payload: {GetEventSummary(evt)}");
            lines.Add("");
        }

        return CreateTextResponse(string.Join("\n", lines));
    }

    public async Task<object> HandleMemoryLineage(JsonNode? arguments)
    {
        var memoryIdStr = arguments?["memory_id"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrEmpty(memoryIdStr) || !Guid.TryParse(memoryIdStr, out var memoryId))
            throw new ArgumentException("Valid memory_id required");

        var maxDepth = Math.Clamp(arguments?["max_depth"]?.GetValue<int>() ?? 5, 1, 10);
        var direction = arguments?["direction"]?.GetValue<string>()?.Trim()?.ToLowerInvariant() ?? "ancestors";

        var visited = new HashSet<Guid>();
        var lineage = new List<(Guid Id, int Depth, string Dir)>();

        if (direction is "ancestors" or "both")
            await TraceAncestors(memoryId, 0, maxDepth, visited, lineage);

        if (direction is "descendants" or "both")
        {
            visited.Clear();
            await TraceDescendants(memoryId, 0, maxDepth, visited, lineage);
        }

        var lines = new List<string>
        {
            $"## Lineage: {memoryId}",
            $"Direction: {direction}, Max Depth: {maxDepth}",
            $"Found: {lineage.Count} nodes",
            ""
        };

        var ancestors = lineage.Where(l => l.Dir == "ancestor").OrderByDescending(l => l.Depth);
        foreach (var (id, depth, _) in ancestors)
            lines.Add($"{"  ".PadLeft(depth * 2)}Ancestor [{depth}]: {id}");

        lines.Add($"* Current: {memoryId}");

        var descendants = lineage.Where(l => l.Dir == "descendant").OrderBy(l => l.Depth);
        foreach (var (id, depth, _) in descendants)
            lines.Add($"{"  ".PadLeft(depth * 2)}Descendant [{depth}]: {id}");

        return CreateTextResponse(string.Join("\n", lines));
    }

    public async Task<object> HandleMemoryExplain(JsonNode? arguments)
    {
        var memoryIdStr = arguments?["memory_id"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrEmpty(memoryIdStr) || !Guid.TryParse(memoryIdStr, out var memoryId))
            throw new ArgumentException("Valid memory_id required");

        var events = await eventStore.ReadStreamAsync(memoryId);
        if (events.Count == 0)
            return CreateErrorResponse($"Memory {memoryId} not found");

        var agg = MemoryAggregate.FromEvents(events);
        var daysSinceReinforcement = (DateTimeOffset.UtcNow - agg.LastReinforcedAt).TotalDays;

        var lines = new List<string>
        {
            $"## Memory: {memoryId}",
            "",
            "### State",
            $"- Active: {agg.IsActive}",
            $"- Layer: {agg.Layer}",
            $"- Version: {agg.Version}",
            "",
            "### Confidence",
            $"- Base: {agg.ConfidenceScore:F4}",
            $"- Current: {agg.CurrentConfidence:F4}",
            $"- Half-life: {agg.HalfLifeDays} days",
            $"- Days since reinforcement: {daysSinceReinforcement:F1}",
            "",
            "### Access",
            $"- Recalls: {agg.RecallCount}",
            $"- Ignores: {agg.IgnoreCount}",
            "",
            "### Integrity",
            $"- Hash valid: {agg.VerifyIntegrity()}",
            $"- Parents: {(agg.CausalParents.Length > 0 ? string.Join(", ", agg.CausalParents.Take(3)) : "None")}",
            $"- Contradictions: {agg.ContradictionIds.Length}",
            "",
            "### Content Preview",
            agg.Content.Length > 200 ? agg.Content[..200] + "..." : agg.Content
        };

        return CreateTextResponse(string.Join("\n", lines));
    }

    public async Task<object> HandleMemoryConflicts(JsonNode? arguments)
    {
        var memoryIdStr = arguments?["memory_id"]?.GetValue<string>()?.Trim();
        Guid? memoryId = null;
        if (!string.IsNullOrEmpty(memoryIdStr) && Guid.TryParse(memoryIdStr, out var mid))
            memoryId = mid;

        var limit = Math.Clamp(arguments?["limit"]?.GetValue<int>() ?? 50, 1, 200);

        await using var conn = await _dataSource.OpenConnectionAsync();

        var sql = memoryId.HasValue
            ? @"SELECT report_id, memory_a_id, memory_b_id, contradiction_score, detection_method, resolution_status, created_at
                FROM contradiction_reports WHERE memory_a_id = @MemoryId OR memory_b_id = @MemoryId
                ORDER BY contradiction_score DESC LIMIT @Limit"
            : @"SELECT report_id, memory_a_id, memory_b_id, contradiction_score, detection_method, resolution_status, created_at
                FROM contradiction_reports WHERE resolution_status = 'detected'
                ORDER BY contradiction_score DESC LIMIT @Limit";

        var conflicts = (await conn.QueryAsync<dynamic>(sql, new { MemoryId = memoryId, Limit = limit })).ToList();

        var lines = new List<string>
        {
            memoryId.HasValue ? $"## Conflicts for: {memoryId}" : "## Unresolved Conflicts",
            $"Found: {conflicts.Count}",
            ""
        };

        if (conflicts.Count == 0)
            lines.Add("No conflicts found.");
        else
            foreach (var c in conflicts)
                lines.Add($"- {c.memory_a_id} <-> {c.memory_b_id} (score: {c.contradiction_score:F3}, status: {c.resolution_status})");

        return CreateTextResponse(string.Join("\n", lines));
    }

    private async Task TraceAncestors(Guid memoryId, int depth, int maxDepth, HashSet<Guid> visited, List<(Guid, int, string)> lineage)
    {
        if (depth >= maxDepth || visited.Contains(memoryId)) return;
        visited.Add(memoryId);

        var events = await eventStore.ReadStreamAsync(memoryId);
        if (events.Count == 0) return;

        var agg = MemoryAggregate.FromEvents(events);
        foreach (var parentId in agg.CausalParents)
        {
            lineage.Add((parentId, depth + 1, "ancestor"));
            await TraceAncestors(parentId, depth + 1, maxDepth, visited, lineage);
        }
    }

    private async Task TraceDescendants(Guid memoryId, int depth, int maxDepth, HashSet<Guid> visited, List<(Guid, int, string)> lineage)
    {
        if (depth >= maxDepth || visited.Contains(memoryId)) return;
        visited.Add(memoryId);

        await using var conn = await _dataSource.OpenConnectionAsync();
        var children = await conn.QueryAsync<Guid>(
            "SELECT memory_id FROM memory_projections WHERE @ParentId = ANY(causal_parents) LIMIT 50",
            new { ParentId = memoryId });

        foreach (var childId in children)
        {
            lineage.Add((childId, depth + 1, "descendant"));
            await TraceDescendants(childId, depth + 1, maxDepth, visited, lineage);
        }
    }

    private static string GetEventSummary(IMemoryEvent evt) => evt switch
    {
        MemoryCreatedEvent e => $"Layer: {e.Layer}, Content: {e.Content[..Math.Min(50, e.Content.Length)]}...",
        MemoryUpdatedEvent e => $"Reason: {e.Reason}",
        MemoryReinforcedEvent e => $"Confidence: {e.PreviousConfidence:F2} -> {e.NewConfidence:F2}",
        MemoryInvalidatedEvent e => $"Reason: {e.Reason}",
        _ => evt.EventType.ToString()
    };

    private static object CreateTextResponse(string text) =>
        new { content = new[] { new { type = "text", text } } };

    private static object CreateErrorResponse(string message) =>
        new { content = new[] { new { type = "text", text = $"Error: {message}" } }, isError = true };
}
