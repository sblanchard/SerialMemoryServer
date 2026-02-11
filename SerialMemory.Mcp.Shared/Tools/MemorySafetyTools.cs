using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using Pgvector;
using SerialMemory.EventSourcing.Aggregates;
using SerialMemory.EventSourcing.Store;

namespace SerialMemory.Mcp.Shared.Tools;

/// <summary>
/// Memory safety tools: contradictions, hallucinations, integrity, loops.
/// </summary>
public sealed class MemorySafetyTools
{
    private readonly IEventStore _eventStore;
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger _logger;

    public MemorySafetyTools(
        IEventStore eventStore,
        NpgsqlDataSource dataSource,
        ILogger logger)
    {
        _eventStore = eventStore;
        _logger = logger;
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    public async Task<object> HandleDetectContradictions(JsonNode? arguments)
    {
        var memoryIdStr = arguments?["memory_id"]?.GetValue<string>()?.Trim();
        Guid? targetId = null;
        if (!string.IsNullOrEmpty(memoryIdStr) && Guid.TryParse(memoryIdStr, out var mid))
            targetId = mid;

        var threshold = Math.Clamp(arguments?["similarity_threshold"]?.GetValue<float>() ?? 0.85f, 0.5f, 0.99f);
        var limit = Math.Clamp(arguments?["limit"]?.GetValue<int>() ?? 20, 1, 100);

        await using var conn = await _dataSource.OpenConnectionAsync();
        var contradictions = new List<(Guid A, Guid B, float Sim, float Score)>();

        if (targetId.HasValue)
        {
            var events = await _eventStore.ReadStreamAsync(targetId.Value);
            if (events.Count == 0)
                return CreateErrorResponse($"Memory {targetId} not found");

            var agg = MemoryAggregate.FromEvents(events);

            var similar = await conn.QueryAsync<dynamic>(@"
                SELECT memory_id, content, 1 - (embedding <=> @Embedding::vector) AS similarity
                FROM memory_projections
                WHERE is_active = TRUE AND memory_id != @TargetId
                  AND 1 - (embedding <=> @Embedding::vector) >= @Threshold
                ORDER BY embedding <=> @Embedding::vector LIMIT @Limit",
                new { Embedding = new Vector(agg.Embedding), TargetId = targetId.Value, Threshold = threshold, Limit = limit });

            foreach (var s in similar)
            {
                var score = CalculateContradictionScore(agg.Content, (string)s.content);
                if (score > 0.5f)
                    contradictions.Add((targetId.Value, s.memory_id, (float)s.similarity, score));
            }
        }
        else
        {
            var pairs = await conn.QueryAsync<dynamic>(@"
                WITH pairs AS (
                    SELECT a.memory_id AS a_id, b.memory_id AS b_id, a.content AS a_content, b.content AS b_content,
                           1 - (a.embedding <=> b.embedding) AS similarity
                    FROM memory_projections a CROSS JOIN memory_projections b
                    WHERE a.memory_id < b.memory_id AND a.is_active AND b.is_active
                      AND a.embedding IS NOT NULL AND b.embedding IS NOT NULL
                )
                SELECT * FROM pairs WHERE similarity >= @Threshold ORDER BY similarity DESC LIMIT @Limit",
                new { Threshold = threshold, Limit = limit });

            foreach (var p in pairs)
            {
                var score = CalculateContradictionScore((string)p.a_content, (string)p.b_content);
                if (score > 0.5f)
                    contradictions.Add((p.a_id, p.b_id, (float)p.similarity, score));
            }
        }

        var lines = new List<string>
        {
            targetId.HasValue ? $"## Contradictions for: {targetId}" : "## Batch Contradiction Scan",
            $"Threshold: {threshold:F2}, Found: {contradictions.Count}",
            ""
        };

        if (contradictions.Count == 0)
            lines.Add("No contradictions detected.");
        else
            foreach (var (a, b, sim, score) in contradictions.OrderByDescending(c => c.Score))
                lines.Add($"- {a} <-> {b} (sim: {sim:F3}, score: {score:F3})");

        _logger.LogInformation("Detected {Count} potential contradictions", contradictions.Count);
        return CreateTextResponse(string.Join("\n", lines));
    }

    public async Task<object> HandleDetectHallucinations(JsonNode? arguments)
    {
        var memoryIdStr = arguments?["memory_id"]?.GetValue<string>()?.Trim();
        Guid? targetId = null;
        if (!string.IsNullOrEmpty(memoryIdStr) && Guid.TryParse(memoryIdStr, out var mid))
            targetId = mid;

        var confidenceThreshold = Math.Clamp(arguments?["confidence_threshold"]?.GetValue<float>() ?? 0.3f, 0f, 1f);
        var limit = Math.Clamp(arguments?["limit"]?.GetValue<int>() ?? 20, 1, 100);

        await using var conn = await _dataSource.OpenConnectionAsync();

        var sql = targetId.HasValue
            ? "SELECT memory_id, content, confidence_score, recall_count, ignore_count FROM memory_projections WHERE memory_id = @MemoryId AND is_active = TRUE"
            : @"SELECT memory_id, content, confidence_score, recall_count, ignore_count
                FROM memory_projections WHERE is_active = TRUE
                AND (confidence_score < @Threshold OR (ignore_count > recall_count AND recall_count > 0))
                ORDER BY confidence_score LIMIT @Limit";

        var candidates = (await conn.QueryAsync<dynamic>(sql, new { MemoryId = targetId, Threshold = confidenceThreshold, Limit = limit })).ToList();

        var hallucinations = new List<(Guid Id, float Score, string Reason)>();
        foreach (var c in candidates)
        {
            var reasons = new List<string>();
            float score = 0;

            if ((decimal)c.confidence_score < (decimal)confidenceThreshold)
            {
                reasons.Add($"Low confidence: {c.confidence_score:F3}");
                score += 0.4f;
            }

            var recalls = (int)(c.recall_count ?? 0);
            var ignores = (int)(c.ignore_count ?? 0);
            if (ignores > recalls && recalls > 0)
            {
                reasons.Add($"High ignore rate: {(float)ignores / (recalls + ignores):P0}");
                score += 0.3f;
            }

            if (reasons.Count > 0)
                hallucinations.Add((c.memory_id, Math.Min(score, 1f), string.Join(", ", reasons)));
        }

        var lines = new List<string>
        {
            targetId.HasValue ? $"## Hallucination check: {targetId}" : "## Batch Hallucination Scan",
            $"Threshold: {confidenceThreshold:F2}, Found: {hallucinations.Count}",
            ""
        };

        if (hallucinations.Count == 0)
            lines.Add("No potential hallucinations detected.");
        else
            foreach (var (id, score, reason) in hallucinations.OrderByDescending(h => h.Score))
                lines.Add($"- {id} (score: {score:F3}): {reason}");

        return CreateTextResponse(string.Join("\n", lines));
    }

    public async Task<object> HandleVerifyIntegrity(JsonNode? arguments)
    {
        var memoryIdStr = arguments?["memory_id"]?.GetValue<string>()?.Trim();
        Guid? targetId = null;
        if (!string.IsNullOrEmpty(memoryIdStr) && Guid.TryParse(memoryIdStr, out var mid))
            targetId = mid;

        var limit = Math.Clamp(arguments?["limit"]?.GetValue<int>() ?? 100, 1, 1000);

        await using var conn = await _dataSource.OpenConnectionAsync();

        var sql = targetId.HasValue
            ? "SELECT memory_id, content, content_hash FROM memory_projections WHERE memory_id = @MemoryId"
            : "SELECT memory_id, content, content_hash FROM memory_projections WHERE is_active = TRUE ORDER BY created_at DESC LIMIT @Limit";

        var memories = (await conn.QueryAsync<dynamic>(sql, new { MemoryId = targetId, Limit = limit })).ToList();

        var failures = new List<Guid>();
        foreach (var m in memories)
        {
            var computed = ComputeHash((string)m.content);
            if ((string)m.content_hash != computed)
                failures.Add(m.memory_id);
        }

        var lines = new List<string>
        {
            targetId.HasValue ? $"## Integrity check: {targetId}" : "## Batch Integrity Check",
            $"Checked: {memories.Count}, Passed: {memories.Count - failures.Count}, Failed: {failures.Count}",
            ""
        };

        if (failures.Count == 0)
            lines.Add("All integrity checks passed.");
        else
        {
            lines.Add("### Failures:");
            foreach (var id in failures)
                lines.Add($"- {id}");
        }

        return CreateTextResponse(string.Join("\n", lines));
    }

    public async Task<object> HandleScanLoops(JsonNode? arguments)
    {
        var maxDepth = Math.Clamp(arguments?["max_depth"]?.GetValue<int>() ?? 10, 1, 20);
        var limit = Math.Clamp(arguments?["limit"]?.GetValue<int>() ?? 50, 1, 200);

        await using var conn = await _dataSource.OpenConnectionAsync();

        var memoriesWithParents = await conn.QueryAsync<dynamic>(
            "SELECT memory_id, causal_parents FROM memory_projections WHERE causal_parents IS NOT NULL AND array_length(causal_parents, 1) > 0 LIMIT @Limit",
            new { Limit = limit * 10 });

        var parentMap = memoriesWithParents.ToDictionary(
            m => (Guid)m.memory_id,
            m => ((Guid[])m.causal_parents) ?? Array.Empty<Guid>());

        var loops = new List<List<Guid>>();

        foreach (var memoryId in parentMap.Keys)
        {
            if (loops.Count >= limit) break;

            var visited = new HashSet<Guid>();
            var stack = new HashSet<Guid>();
            var path = new List<Guid>();

            if (DetectCycle(memoryId, parentMap, visited, stack, path, maxDepth))
                loops.Add(new List<Guid>(path));
        }

        var lines = new List<string>
        {
            "## Loop Detection",
            $"Scanned: {parentMap.Count}, Loops found: {loops.Count}",
            ""
        };

        if (loops.Count == 0)
            lines.Add("No causal loops detected.");
        else
            foreach (var loop in loops)
                lines.Add($"- Cycle: {string.Join(" -> ", loop.Take(5))}{(loop.Count > 5 ? "..." : "")}");

        return CreateTextResponse(string.Join("\n", lines));
    }

    private static bool DetectCycle(Guid current, Dictionary<Guid, Guid[]> parentMap, HashSet<Guid> visited, HashSet<Guid> stack, List<Guid> path, int maxDepth)
    {
        if (path.Count >= maxDepth) return false;
        if (stack.Contains(current)) { path.Add(current); return true; }
        if (visited.Contains(current)) return false;

        visited.Add(current);
        stack.Add(current);
        path.Add(current);

        if (parentMap.TryGetValue(current, out var parents))
            foreach (var parent in parents)
                if (DetectCycle(parent, parentMap, visited, stack, path, maxDepth))
                    return true;

        path.RemoveAt(path.Count - 1);
        stack.Remove(current);
        return false;
    }

    private static float CalculateContradictionScore(string a, string b)
    {
        var negations = new[] { "not", "never", "no", "false", "incorrect", "wrong", "isn't", "aren't", "don't", "doesn't" };
        var wordsA = a.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var wordsB = b.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var negA = wordsA.Count(w => negations.Contains(w));
        var negB = wordsB.Count(w => negations.Contains(w));

        if ((negA > 0) != (negB > 0)) return 0.6f;
        return 0.3f;
    }

    private static string ComputeHash(string content) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private static object CreateTextResponse(string text) =>
        new { content = new[] { new { type = "text", text } } };

    private static object CreateErrorResponse(string message) =>
        new { content = new[] { new { type = "text", text = $"Error: {message}" } }, isError = true };
}
