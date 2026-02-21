using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SerialMemory.Core.Services;
using static SerialMemory.Mcp.McpResponseHelpers;

namespace SerialMemory.Mcp.Tools;

/// <summary>
/// Handlers for progressive disclosure tools (P0 - GAP 2):
/// memory_search_index, memory_timeline, memory_fetch.
///
/// Implements a 3-layer search workflow for ~10x token savings:
/// 1. memory_search_index → compact index (~50-80 tokens/result)
/// 2. memory_timeline → chronological context around anchor
/// 3. memory_fetch → full details for selected IDs (~500+ tokens/result)
/// </summary>
internal sealed class ProgressiveDisclosureTools(
    KnowledgeGraphService kgService,
    ILogger logger)
{
    /// <summary>
    /// Step 1: Search returning compact index results.
    /// Returns IDs, timestamps, types, titles, similarity scores (~50-80 tokens each).
    /// Use memory_fetch to get full content for selected results.
    /// </summary>
    public async Task<object> HandleMemorySearchIndex(JsonNode? arguments)
    {
        var query = arguments?["query"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrEmpty(query))
            throw new ArgumentException("Query is required and cannot be empty");
        if (query.Length > 10000)
            throw new ArgumentException("Query exceeds maximum length of 10000 characters");

        var modeStr = arguments?["mode"]?.GetValue<string>() ?? "hybrid";
        var limit = Math.Clamp(arguments?["limit"]?.GetValue<int>() ?? 20, 1, 100);
        var threshold = Math.Clamp(arguments?["threshold"]?.GetValue<float>() ?? 0.5f, 0f, 1f);
        var memoryType = arguments?["memory_type"]?.GetValue<string>()?.Trim()?.ToLowerInvariant();

        var mode = modeStr.ToLowerInvariant() switch
        {
            "semantic" => SearchMode.Semantic,
            "text" => SearchMode.Text,
            _ => SearchMode.Hybrid
        };

        var result = await kgService.SearchMemoriesCompactAsync(query, mode, limit, threshold, memoryType);

        if (result.TotalResults == 0)
        {
            return CreateTextResponse("No memories found matching the query.");
        }

        var text = $"## Search Index — {result.TotalResults} results\n" +
                   $"**Token savings:** {result.Meta.TokenSavingsPercent}% " +
                   $"(index: ~{result.Meta.IndexTokens} tokens | full: ~{result.Meta.FullFetchTokens} tokens)\n\n" +
                   $"| # | ID | Date | Type | Title | Score | Entities | Tokens |\n" +
                   $"|---|-----|------|------|-------|-------|----------|--------|\n" +
                   string.Join("\n", result.Results.Select((r, i) =>
                       $"| {i + 1} | `{r.Id.ToString()[..8]}` | {r.CreatedAt:yyyy-MM-dd HH:mm} | {r.MemoryType} | {Truncate(r.Title, 50)} | {r.Similarity:F2} | {r.EntityCount} | ~{r.EstimatedTokens} |")) +
                   "\n\nUse `memory_fetch` with selected IDs to get full content. " +
                   "Use `memory_timeline` with an anchor ID for chronological context.";

        return CreateTextResponse(text);
    }

    /// <summary>
    /// Step 2: Timeline navigation around an anchor point.
    /// Shows N memories before and after a specific memory or timestamp.
    /// </summary>
    public async Task<object> HandleMemoryTimeline(JsonNode? arguments)
    {
        var anchorIdStr = arguments?["anchor_id"]?.GetValue<string>()?.Trim();
        var anchorTimeStr = arguments?["anchor_time"]?.GetValue<string>()?.Trim();
        var before = Math.Clamp(arguments?["depth_before"]?.GetValue<int>() ?? 5, 0, 50);
        var after = Math.Clamp(arguments?["depth_after"]?.GetValue<int>() ?? 5, 0, 50);
        var memoryType = arguments?["memory_type"]?.GetValue<string>()?.Trim()?.ToLowerInvariant();

        Guid? anchorId = null;
        DateTime? anchorTime = null;

        if (!string.IsNullOrEmpty(anchorIdStr) && Guid.TryParse(anchorIdStr, out var parsedId))
        {
            anchorId = parsedId;
        }
        else if (!string.IsNullOrEmpty(anchorTimeStr) && DateTime.TryParse(anchorTimeStr, out var parsedTime))
        {
            anchorTime = parsedTime.ToUniversalTime();
        }

        var result = await kgService.GetTimelineAsync(anchorId, anchorTime, before, after, memoryType);

        if (result.TotalEntries == 0)
        {
            return CreateTextResponse("No memories found around the anchor point.");
        }

        var anchorLabel = anchorId.HasValue
            ? $"Memory `{anchorId.Value.ToString()[..8]}`"
            : anchorTime.HasValue
                ? $"{anchorTime.Value:yyyy-MM-dd HH:mm}"
                : "most recent";

        var text = $"## Timeline — {result.TotalEntries} entries around {anchorLabel}\n\n";

        foreach (var entry in result.Entries)
        {
            var isAnchor = anchorId.HasValue && entry.Id == anchorId.Value;
            var marker = isAnchor ? " **← anchor**" : "";
            text += $"- `{entry.Id.ToString()[..8]}` [{entry.CreatedAt:yyyy-MM-dd HH:mm}] ({entry.MemoryType}) {Truncate(entry.Title, 60)} (~{entry.EstimatedTokens} tokens){marker}\n";
        }

        text += "\nUse `memory_fetch` with IDs to get full content.";

        return CreateTextResponse(text);
    }

    /// <summary>
    /// Step 3: Batch fetch full memory details by IDs.
    /// Returns complete content, entities, structured fields.
    /// </summary>
    public async Task<object> HandleMemoryFetch(JsonNode? arguments)
    {
        var idsNode = arguments?["ids"];
        if (idsNode == null || idsNode is not JsonArray idsArray)
            throw new ArgumentException("ids is required (array of memory UUIDs)");

        var ids = new List<Guid>();
        foreach (var node in idsArray)
        {
            var idStr = node?.GetValue<string>()?.Trim();
            if (!string.IsNullOrEmpty(idStr) && Guid.TryParse(idStr, out var id))
                ids.Add(id);
        }

        if (ids.Count == 0)
            throw new ArgumentException("At least one valid memory ID is required");

        if (ids.Count > 50)
            throw new ArgumentException("Maximum 50 memories can be fetched at once");

        var includeEntities = arguments?["include_entities"]?.GetValue<bool>() ?? true;
        var results = await kgService.FetchMemoriesByIdsAsync(ids, includeEntities);

        if (results.Count == 0)
        {
            return CreateTextResponse("No memories found for the given IDs.");
        }

        var totalTokens = results.Sum(r => r.EstimatedTokens);

        var text = $"## Fetched {results.Count} memories (~{totalTokens} tokens)\n\n" +
            string.Join("\n---\n\n", results.Select((r, i) =>
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"### Memory {i + 1} (ID: {r.Id})");
                sb.AppendLine($"**Created:** {r.CreatedAt:O} | **Type:** {r.MemoryType ?? "knowledge"} | **Source:** {r.Source ?? "unknown"}");

                if (!string.IsNullOrEmpty(r.Title))
                    sb.AppendLine($"**Title:** {r.Title}");

                sb.AppendLine();
                sb.AppendLine(r.Content);

                if (r.Facts is { Count: > 0 })
                {
                    sb.AppendLine();
                    sb.AppendLine("**Facts:**");
                    foreach (var fact in r.Facts)
                        sb.AppendLine($"- {fact}");
                }

                if (r.Concepts is { Count: > 0 })
                    sb.AppendLine($"**Concepts:** {string.Join(", ", r.Concepts)}");

                if (r.FilesRead is { Count: > 0 })
                    sb.AppendLine($"**Files Read:** {string.Join(", ", r.FilesRead)}");

                if (r.FilesModified is { Count: > 0 })
                    sb.AppendLine($"**Files Modified:** {string.Join(", ", r.FilesModified)}");

                if (r.Entities.Count > 0)
                    sb.AppendLine($"**Entities:** {string.Join(", ", r.Entities.Select(e => $"{e.Name} ({e.Type})"))}");

                sb.AppendLine($"**Estimated Tokens:** ~{r.EstimatedTokens}");

                return sb.ToString();
            }));

        return CreateTextResponse(text);
    }

    private static string Truncate(string text, int maxLen)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return text.Length <= maxLen ? text : text[..(maxLen - 3)] + "...";
    }
}
