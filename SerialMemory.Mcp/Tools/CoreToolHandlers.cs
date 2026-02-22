using System.Text.Json;
using System.Text.Json.Nodes;
using SerialMemory.Core.Services;
using static SerialMemory.Mcp.McpResponseHelpers;

namespace SerialMemory.Mcp.Tools;

/// <summary>
/// Handlers for core memory tools: search, ingest, multi-hop search.
/// </summary>
internal sealed class CoreToolHandlers(
    KnowledgeGraphService kgService,
    McpSessionState sessionState,
    AsyncLocal<Dictionary<string, object>?> toolMetadataContext)
{
    public async Task<object> HandleMemorySearch(JsonNode? arguments)
    {
        var query = arguments?["query"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrEmpty(query))
            throw new ArgumentException("Query is required and cannot be empty");
        if (query.Length > 10000)
            throw new ArgumentException("Query exceeds maximum length of 10000 characters");

        var modeStr = arguments?["mode"]?.GetValue<string>() ?? "hybrid";
        var limit = Math.Clamp(arguments?["limit"]?.GetValue<int>() ?? 10, 1, 100);
        var threshold = Math.Clamp(arguments?["threshold"]?.GetValue<float>() ?? 0.7f, 0f, 1f);
        var includeEntities = arguments?["include_entities"]?.GetValue<bool>() ?? true;
        var memoryType = arguments?["memory_type"]?.GetValue<string>()?.Trim()?.ToLowerInvariant();

        var mode = modeStr.ToLowerInvariant() switch
        {
            "semantic" => SearchMode.Semantic,
            "text" => SearchMode.Text,
            _ => SearchMode.Hybrid
        };

        var results = await kgService.SearchMemoriesAsync(query, mode, limit, threshold, includeEntities, memoryType);

        var text = $"Found {results.Count} memories:\n\n" +
            string.Join("\n\n", results.Select((r, i) =>
                $"**Memory {i + 1}** (ID: {r.Id})\n" +
                $"Created: {r.CreatedAt:O}\n" +
                $"Content: {r.Content}\n" +
                $"Entities: {string.Join(", ", r.Entities.Select(e => e.Name))}\n" +
                $"Similarity: {(r.Similarity > 0 ? r.Similarity : r.Rank):F3}"));

        return CreateTextResponse(text);
    }

    public async Task<object> HandleMemoryIngest(JsonNode? arguments)
    {
        var content = arguments?["content"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrEmpty(content))
            throw new ArgumentException("Content is required and cannot be empty");
        if (content.Length > 100000)
            throw new ArgumentException("Content exceeds maximum length of 100000 characters");

        var source = arguments?["source"]?.GetValue<string>()?.Trim();
        var extractEntities = arguments?["extract_entities"]?.GetValue<bool>() ?? true;
        var dedupMode = arguments?["dedup_mode"]?.GetValue<string>()?.Trim()?.ToLowerInvariant() ?? "warn";
        var dedupThreshold = Math.Clamp(arguments?["dedup_threshold"]?.GetValue<float>() ?? 0.85f, 0f, 1f);

        if (dedupMode is not ("warn" or "skip" or "append" or "off"))
            dedupMode = "warn";

        var memoryTypeParam = arguments?["memory_type"]?.GetValue<string>()?.Trim()?.ToLowerInvariant();
        string[] validMemoryTypes = ["error", "decision", "pattern", "learning", "knowledge", "session_summary", "auto_capture"];
        if (memoryTypeParam != null && !validMemoryTypes.Contains(memoryTypeParam))
            memoryTypeParam = "knowledge";

        Dictionary<string, object>? metadata = null;
        if (arguments?["metadata"] is JsonNode metadataNode)
        {
            metadata = JsonSerializer.Deserialize<Dictionary<string, object>>(metadataNode.ToJsonString());
        }

        // Structured observation fields (P1 - GAP 5)
        var title = arguments?["title"]?.GetValue<string>()?.Trim();
        var facts = ParseStringArray(arguments?["facts"]);
        var concepts = ParseStringArray(arguments?["concepts"]);
        var filesRead = ParseStringArray(arguments?["files_read"]);
        var filesModified = ParseStringArray(arguments?["files_modified"]);

        var result = await kgService.IngestMemoryAsync(
            content,
            source,
            sessionState.CurrentSessionId,
            metadata,
            extractEntities,
            dedupMode,
            dedupThreshold,
            memoryTypeParam,
            title,
            facts,
            concepts,
            filesRead,
            filesModified);

        // Set dedup metadata for usage tracking
        toolMetadataContext.Value = new Dictionary<string, object>
        {
            ["dedup_mode"] = dedupMode,
            ["dedup_detected"] = result.DuplicateDetected,
            ["dedup_threshold"] = dedupThreshold,
            ["similarity"] = result.DuplicateSimilarity
        };

        var text = result.DuplicateDetected && dedupMode == "skip"
            ? $"Duplicate detected — skipped ingestion.\n\n" +
              $"Existing Memory ID: {result.DuplicateOf}\n" +
              $"Similarity: {result.DuplicateSimilarity:F3}\n"
            : result.DuplicateDetected && dedupMode == "append"
                ? $"Duplicate detected — appended to existing memory.\n\n" +
                  $"Memory ID: {result.MemoryId}\n" +
                  $"Similarity: {result.DuplicateSimilarity:F3}\n"
                : $"Memory ingested successfully!\n\n" +
                  $"Memory ID: {result.MemoryId}\n" +
                  $"Entities extracted: {result.EntitiesCreated}\n" +
                  $"Relationships extracted: {result.RelationshipsCreated}\n\n" +
                  $"Entities: {string.Join(", ", result.Entities.Select(e => e.Name))}\n" +
                  $"Relationships: {string.Join(", ", result.Relationships.Select(r => $"{r.Source} --{r.Type}--> {r.Target}"))}";

        if (result.SimilarMemories is { Count: > 0 } && dedupMode == "warn")
        {
            text += $"\n\n**Dedup Warning:** {result.SimilarMemories.Count} similar memory(ies) found:\n";
            foreach (var dup in result.SimilarMemories)
            {
                text += $"  - {dup.MemoryId} (similarity: {dup.Similarity:F3}): {dup.ContentPreview}\n";
            }
        }

        return CreateTextResponse(text);
    }

    private static List<string>? ParseStringArray(JsonNode? node)
    {
        if (node is not JsonArray arr || arr.Count == 0) return null;
        return arr
            .Select(n => n?.GetValue<string>()?.Trim())
            .Where(s => !string.IsNullOrEmpty(s))
            .Cast<string>()
            .ToList();
    }

    public async Task<object> HandleMultiHopSearch(JsonNode? arguments)
    {
        var query = arguments?["query"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrEmpty(query))
            throw new ArgumentException("Query is required and cannot be empty");

        var hops = Math.Clamp(arguments?["hops"]?.GetValue<int>() ?? 2, 1, 5);
        var maxResultsPerHop = Math.Clamp(arguments?["max_results_per_hop"]?.GetValue<int>() ?? 5, 1, 20);

        var result = await kgService.MultiHopSearchAsync(query, hops, maxResultsPerHop);

        var text =
            $"Multi-hop search completed ({result.Hops} hops):\n\n" +
            $"Memories found: {result.Memories.Count}\n" +
            $"Entities discovered: {result.Entities.Count}\n" +
            $"Relationships: {result.Relationships.Count}\n\n" +
            string.Join("\n\n", result.Memories.Take(5).Select((m, i) =>
                $"**Memory {i + 1}:**\n{(m.Content.Length > 200 ? m.Content[..200] + "..." : m.Content)}"));

        return CreateTextResponse(text);
    }
}
