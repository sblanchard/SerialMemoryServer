using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using Pgvector;
using SerialMemory.Core.Interfaces;

namespace SerialMemory.Infrastructure.Rag;

/// <summary>
/// Personalized RAG service using L2 summaries as the primary retrieval surface.
/// Enriches results with L1 context, L3 facts, and L4 heuristics.
/// </summary>
public sealed class PersonalizedRagService : IPersonalizedRagService
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly IEmbeddingService _embeddingService;
    private readonly ILlmService _llmService;
    private readonly ILogger<PersonalizedRagService> _logger;

    private const string DefaultSystemPrompt = """
        You are SerialMemory, a personalized AI assistant with access to the user's memory system.
        You have been provided with relevant memories from the user's knowledge base.

        IMPORTANT RULES:
        1. Use ONLY the provided memories as evidence for your answers
        2. If the memories don't contain relevant information, clearly say "I don't have information about this in your memories"
        3. When referencing memories, explain which memory influenced your answer
        4. Prefer the user's known preferences and patterns (from L4 heuristics) when making suggestions
        5. Be concise but thorough in your responses
        6. If there are contradicting memories, acknowledge the contradiction

        Answer the user's query based on their memories.
        """;

    public PersonalizedRagService(
        NpgsqlDataSource dataSource,
        IEmbeddingService embeddingService,
        ILlmService llmService,
        ILogger<PersonalizedRagService> logger)
    {
        _dataSource = dataSource;
        _embeddingService = embeddingService;
        _llmService = llmService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<RagResult> AnswerAsync(
        Guid tenantId,
        string userId,
        string query,
        RagOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= new RagOptions();
        var sw = Stopwatch.StartNew();
        var queryId = Guid.CreateVersion7();

        _logger.LogDebug("RAG query started: {QueryId} for tenant {TenantId}", queryId, tenantId);

        try
        {
            // Step 1: Retrieve relevant memories
            var memories = await RetrieveMemoriesAsync(tenantId, query, options, ct);

            if (memories.Count == 0)
            {
                _logger.LogDebug("No relevant memories found for query");
                return new RagResult
                {
                    Answer = "I couldn't find any relevant information in your memories to answer this question.",
                    Memories = [],
                    ModelName = _llmService.ModelName,
                    LatencyMs = (int)sw.ElapsedMilliseconds,
                    QueryId = queryId
                };
            }

            // Step 2: Build the RAG prompt
            var prompt = BuildRagPrompt(query, memories, options);

            // Step 3: Generate the answer
            var systemPrompt = options.SystemPromptOverride ?? DefaultSystemPrompt;
            var answer = await _llmService.ChatAsync(
                userMessage: prompt,
                systemPrompt: systemPrompt,
                temperature: options.Temperature,
                maxTokens: options.MaxAnswerTokens,
                cancellationToken: ct);

            sw.Stop();

            // Step 4: Build reasoning trace if requested
            string? reasoningTrace = null;
            if (options.IncludeReasoningTrace)
            {
                reasoningTrace = BuildReasoningTrace(query, memories);
            }

            // Step 5: Store the query result for analytics
            await StoreQueryResultAsync(
                tenantId,
                userId,
                query,
                answer,
                memories.Select(m => m.MemoryId).ToArray(),
                reasoningTrace,
                (int)sw.ElapsedMilliseconds,
                ct);

            _logger.LogDebug("RAG query completed: {QueryId}, {MemoryCount} memories, {LatencyMs}ms",
                queryId, memories.Count, sw.ElapsedMilliseconds);

            return new RagResult
            {
                Answer = answer,
                Memories = memories,
                ReasoningTrace = reasoningTrace,
                ModelName = $"{_llmService.ProviderName}/{_llmService.ModelName}",
                LatencyMs = (int)sw.ElapsedMilliseconds,
                QueryId = queryId
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RAG query failed: {QueryId}", queryId);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RetrievedMemory>> SearchMemoriesAsync(
        Guid tenantId,
        string query,
        int limit = 10,
        CancellationToken ct = default)
    {
        var options = new RagOptions { MaxMemories = limit };
        return await RetrieveMemoriesAsync(tenantId, query, options, ct);
    }

    /// <summary>
    /// Retrieves memories using L2 vector search and enriches with L1/L3/L4.
    /// </summary>
    private async Task<IReadOnlyList<RetrievedMemory>> RetrieveMemoriesAsync(
        Guid tenantId,
        string query,
        RagOptions options,
        CancellationToken ct)
    {
        // Step 1: Generate query embedding
        var queryEmbedding = await _embeddingService.EmbedTextAsync(query, ct);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "SELECT set_config('app.tenant_id', @TenantId, false)",
            new { TenantId = tenantId.ToString() });

        // Step 2: Vector search on L2 embeddings
        var l2Results = await SearchL2EmbeddingsAsync(
            conn,
            tenantId,
            queryEmbedding,
            options.MaxMemories,
            options.SimilarityThreshold);

        if (l2Results.Count == 0)
            return [];

        // Step 3: Enrich each result with L1/L3/L4 layers
        var memories = new List<RetrievedMemory>();
        foreach (var l2 in l2Results)
        {
            var enriched = await EnrichMemoryAsync(
                conn,
                tenantId,
                l2,
                options);

            memories.Add(enriched);
        }

        return memories;
    }

    /// <summary>
    /// Performs vector similarity search on L2 embeddings.
    /// </summary>
    private async Task<List<L2SearchResult>> SearchL2EmbeddingsAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        float[] queryEmbedding,
        int limit,
        decimal threshold)
    {
        const string sql = """
            SELECT
                memory_id,
                summary,
                keywords,
                importance,
                (1 - (embedding <=> @QueryEmbedding))::DECIMAL AS similarity,
                created_at
            FROM memory_l2_embeddings
            WHERE tenant_id = @TenantId
              AND (1 - (embedding <=> @QueryEmbedding)) > @Threshold
            ORDER BY embedding <=> @QueryEmbedding
            LIMIT @Limit
            """;

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@TenantId", tenantId);
        cmd.Parameters.AddWithValue("@QueryEmbedding", new Vector(queryEmbedding));
        cmd.Parameters.AddWithValue("@Threshold", threshold);
        cmd.Parameters.AddWithValue("@Limit", limit);

        var results = new List<L2SearchResult>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new L2SearchResult
            {
                MemoryId = reader.GetGuid(0),
                Summary = reader.GetString(1),
                Keywords = reader.GetFieldValue<string[]>(2),
                Importance = reader.GetDecimal(3),
                Similarity = reader.GetDecimal(4),
                CreatedAt = reader.GetDateTime(5)
            });
        }

        return results;
    }

    /// <summary>
    /// Enriches a memory with L1/L3/L4 layer content.
    /// </summary>
    private async Task<RetrievedMemory> EnrichMemoryAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        L2SearchResult l2,
        RagOptions options)
    {
        string? l1Context = null;
        string? l2OneLiner = null;
        var l3Facts = new List<string>();
        var l4Heuristics = new List<string>();

        // Fetch all relevant layers
        var layers = await conn.QueryAsync<LayerContent>(
            """
            SELECT layer::TEXT as layer, content_json
            FROM memory_layers
            WHERE tenant_id = @TenantId
              AND memory_id = @MemoryId
              AND is_current = TRUE
              AND layer IN ('L1_CONTEXT', 'L2_SUMMARY', 'L3_KNOWLEDGE', 'L4_HEURISTIC')
            """,
            new { TenantId = tenantId, MemoryId = l2.MemoryId });

        foreach (var layer in layers)
        {
            try
            {
                using var doc = JsonDocument.Parse(layer.content_json ?? "{}");
                var root = doc.RootElement;

                switch (layer.layer)
                {
                    case "L1_CONTEXT" when options.IncludeL1:
                        l1Context = BuildL1Context(root);
                        break;

                    case "L2_SUMMARY":
                        if (root.TryGetProperty("one_liner", out var oneLiner))
                            l2OneLiner = oneLiner.GetString();
                        break;

                    case "L3_KNOWLEDGE" when options.IncludeL3:
                        l3Facts.AddRange(ExtractL3Facts(root));
                        break;

                    case "L4_HEURISTIC" when options.IncludeL4:
                        l4Heuristics.AddRange(ExtractL4Heuristics(root));
                        break;
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to parse layer {Layer} for memory {MemoryId}",
                    layer.layer, l2.MemoryId);
            }
        }

        return new RetrievedMemory
        {
            MemoryId = l2.MemoryId,
            L2Summary = l2.Summary,
            L2OneLiner = l2OneLiner,
            L1Context = l1Context,
            L3Facts = l3Facts,
            L4Heuristics = l4Heuristics,
            Keywords = l2.Keywords,
            Similarity = l2.Similarity,
            Importance = l2.Importance,
            CreatedAt = l2.CreatedAt
        };
    }

    /// <summary>
    /// Builds a human-readable L1 context string.
    /// </summary>
    private static string? BuildL1Context(JsonElement root)
    {
        var parts = new List<string>();

        if (root.TryGetProperty("speaker", out var speaker) && speaker.GetString() is { } s && s != "unknown")
            parts.Add($"Speaker: {s}");

        if (root.TryGetProperty("intent", out var intent) && intent.GetString() is { } i)
            parts.Add($"Intent: {i}");

        if (root.TryGetProperty("sentiment", out var sentiment) && sentiment.GetString() is { } sent)
            parts.Add($"Sentiment: {sent}");

        if (root.TryGetProperty("context_description", out var ctx) && ctx.GetString() is { } c)
            parts.Add(c);

        return parts.Count > 0 ? string.Join(" | ", parts) : null;
    }

    /// <summary>
    /// Extracts L3 facts as human-readable strings.
    /// </summary>
    private static List<string> ExtractL3Facts(JsonElement root)
    {
        var facts = new List<string>();

        if (root.TryGetProperty("facts", out var factsArray) && factsArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var fact in factsArray.EnumerateArray())
            {
                var subject = fact.TryGetProperty("subject", out var s) ? s.GetString() : null;
                var predicate = fact.TryGetProperty("predicate", out var p) ? p.GetString() : null;
                var obj = fact.TryGetProperty("object", out var o) ? o.GetString() : null;

                if (!string.IsNullOrWhiteSpace(subject) && !string.IsNullOrWhiteSpace(predicate))
                {
                    facts.Add($"{subject} {predicate} {obj}".Trim());
                }
            }
        }

        return facts;
    }

    /// <summary>
    /// Extracts L4 heuristics as human-readable strings.
    /// </summary>
    private static List<string> ExtractL4Heuristics(JsonElement root)
    {
        var heuristics = new List<string>();

        // Extract preferences
        if (root.TryGetProperty("preferences", out var prefs) && prefs.ValueKind == JsonValueKind.Array)
        {
            foreach (var pref in prefs.EnumerateArray())
            {
                if (pref.GetString() is { } p && !string.IsNullOrWhiteSpace(p))
                    heuristics.Add($"Preference: {p}");
            }
        }

        // Extract habits
        if (root.TryGetProperty("habits", out var habits) && habits.ValueKind == JsonValueKind.Array)
        {
            foreach (var habit in habits.EnumerateArray())
            {
                if (habit.GetString() is { } h && !string.IsNullOrWhiteSpace(h))
                    heuristics.Add($"Habit: {h}");
            }
        }

        // Extract long-term tendencies
        if (root.TryGetProperty("long_term_tendencies", out var tendencies) && tendencies.ValueKind == JsonValueKind.Array)
        {
            foreach (var tendency in tendencies.EnumerateArray())
            {
                if (tendency.GetString() is { } t && !string.IsNullOrWhiteSpace(t))
                    heuristics.Add($"Tendency: {t}");
            }
        }

        return heuristics;
    }

    /// <summary>
    /// Builds the RAG prompt with all retrieved memories.
    /// </summary>
    private static string BuildRagPrompt(string query, IReadOnlyList<RetrievedMemory> memories, RagOptions options)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"User Query: \"{query}\"");
        sb.AppendLine();
        sb.AppendLine("=== RELEVANT MEMORIES ===");
        sb.AppendLine();

        for (var i = 0; i < memories.Count; i++)
        {
            var memory = memories[i];
            sb.AppendLine($"[Memory #{i + 1}] (Relevance: {memory.Similarity:P0})");
            sb.AppendLine($"Summary: {memory.L2Summary}");

            if (!string.IsNullOrWhiteSpace(memory.L1Context) && options.IncludeL1)
                sb.AppendLine($"Context: {memory.L1Context}");

            if (memory.L3Facts.Count > 0 && options.IncludeL3)
            {
                sb.AppendLine("Facts:");
                foreach (var fact in memory.L3Facts.Take(5))
                    sb.AppendLine($"  - {fact}");
            }

            if (memory.L4Heuristics.Count > 0 && options.IncludeL4)
            {
                sb.AppendLine("User Patterns:");
                foreach (var heuristic in memory.L4Heuristics.Take(3))
                    sb.AppendLine($"  - {heuristic}");
            }

            if (memory.Keywords.Count > 0)
                sb.AppendLine($"Keywords: {string.Join(", ", memory.Keywords)}");

            sb.AppendLine();
        }

        sb.AppendLine("=== END MEMORIES ===");
        sb.AppendLine();
        sb.AppendLine("Based on the above memories, please answer the user's query.");
        sb.AppendLine("If the memories don't contain sufficient information, say so clearly.");

        return sb.ToString();
    }

    /// <summary>
    /// Builds a reasoning trace explaining how memories influenced the answer.
    /// </summary>
    private static string BuildReasoningTrace(string query, IReadOnlyList<RetrievedMemory> memories)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== Reasoning Trace ===");
        sb.AppendLine($"Query: {query}");
        sb.AppendLine($"Retrieved {memories.Count} memories");
        sb.AppendLine();

        foreach (var memory in memories)
        {
            sb.AppendLine($"Memory {memory.MemoryId}:");
            sb.AppendLine($"  Similarity: {memory.Similarity:P1}");
            sb.AppendLine($"  Importance: {memory.Importance:P1}");
            sb.AppendLine($"  Facts: {memory.L3Facts.Count}");
            sb.AppendLine($"  Heuristics: {memory.L4Heuristics.Count}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Stores the RAG query result for analytics and caching.
    /// </summary>
    private async Task StoreQueryResultAsync(
        Guid tenantId,
        string userId,
        string query,
        string answer,
        Guid[] memoryIds,
        string? reasoningTrace,
        int latencyMs,
        CancellationToken ct)
    {
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);
            await conn.ExecuteAsync("SELECT set_config('app.role', 'internal_admin', false)");

            await conn.ExecuteAsync(
                """
                INSERT INTO rag_query_results
                    (tenant_id, user_id, query, answer, memory_ids, reasoning_trace, latency_ms, model_name)
                VALUES
                    (@TenantId, @UserId, @Query, @Answer, @MemoryIds, @ReasoningTrace, @LatencyMs, @ModelName)
                """,
                new
                {
                    TenantId = tenantId,
                    UserId = userId,
                    Query = query,
                    Answer = answer,
                    MemoryIds = memoryIds,
                    ReasoningTrace = reasoningTrace,
                    LatencyMs = latencyMs,
                    ModelName = $"{_llmService.ProviderName}/{_llmService.ModelName}"
                });
        }
        catch (Exception ex)
        {
            // Don't fail the request if analytics storage fails
            _logger.LogWarning(ex, "Failed to store RAG query result");
        }
    }

    #region DTOs

    private sealed class L2SearchResult
    {
        public Guid MemoryId { get; set; }
        public string Summary { get; set; } = "";
        public string[] Keywords { get; set; } = [];
        public decimal Importance { get; set; }
        public decimal Similarity { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
    }

    private sealed class LayerContent
    {
        public string layer { get; set; } = "";
        public string? content_json { get; set; }
    }

    #endregion
}
