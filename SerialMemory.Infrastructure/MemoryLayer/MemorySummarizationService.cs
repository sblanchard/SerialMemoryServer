using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using SerialMemory.Core.Interfaces;
using SerialMemory.ML;

namespace SerialMemory.Infrastructure.MemoryLayer;

/// <summary>
/// Service for summarizing related L1 memories into L2 summaries.
/// Uses OpenAI to generate concise, coherent summaries from memory clusters.
/// </summary>
public sealed class MemorySummarizationService(
    OpenAiClient llm,
    IEmbeddingService embeddings,
    IConfiguration configuration,
    ILogger<MemorySummarizationService> logger)
{
    private readonly string _connectionString = configuration.GetConnectionString("Postgres")
                                                ?? BuildConnectionString();
    private readonly int _minClusterSize = configuration.GetValue("MemoryLayerWorker:L1ToL2:MinClusterSize", 3);
    private readonly float _similarityThreshold = configuration.GetValue("MemoryLayerWorker:L1ToL2:SimilarityThreshold", 0.8f);

    private static string BuildConnectionString()
    {
        var host = Environment.GetEnvironmentVariable("POSTGRES_HOST") ?? "localhost";
        var port = Environment.GetEnvironmentVariable("POSTGRES_PORT") ?? "5434";
        var user = Environment.GetEnvironmentVariable("POSTGRES_USER") ?? "postgres";
        var password = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD") ?? "postgres";
        var database = Environment.GetEnvironmentVariable("POSTGRES_DB") ?? "contextdb";
        return $"Host={host};Port={port};Username={user};Password={password};Database={database}";
    }

    /// <summary>
    /// Finds clusters of related L1 memories that can be summarized.
    /// </summary>
    public async Task<IReadOnlyList<MemoryCluster>> FindSummarizableClustersAsync(
        Guid tenantId,
        int limit = 10,
        CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        // Find L1 memories that share entities (potential clusters)
        var candidates = await conn.QueryAsync<ClusterCandidateRow>("""
            WITH shared_entities AS (
                SELECT
                    me1.memory_id as memory_id_a,
                    me2.memory_id as memory_id_b,
                    COUNT(*) as shared_count
                FROM memory_entities me1
                JOIN memory_entities me2 ON me1.entity_id = me2.entity_id
                    AND me1.memory_id < me2.memory_id
                JOIN memories m1 ON me1.memory_id = m1.id
                JOIN memories m2 ON me2.memory_id = m2.id
                WHERE m1.layer = 'L1_CONTEXT'
                  AND m2.layer = 'L1_CONTEXT'
                  AND m1.tenant_id = @TenantId
                  AND m2.tenant_id = @TenantId
                GROUP BY me1.memory_id, me2.memory_id
                HAVING COUNT(*) >= 2
            ),
            cluster_seeds AS (
                SELECT DISTINCT memory_id_a as memory_id
                FROM shared_entities
                UNION
                SELECT DISTINCT memory_id_b
                FROM shared_entities
            )
            SELECT
                cs.memory_id,
                m.content,
                m.created_at,
                (SELECT array_agg(DISTINCT e.name)
                 FROM memory_entities me
                 JOIN entities e ON me.entity_id = e.id
                 WHERE me.memory_id = cs.memory_id) as entity_names
            FROM cluster_seeds cs
            JOIN memories m ON cs.memory_id = m.id
            WHERE NOT EXISTS (
                SELECT 1 FROM layer_transition_queue
                WHERE memory_id = cs.memory_id
                  AND to_layer = 'L2_SUMMARY'
                  AND status IN ('pending', 'processing', 'completed')
            )
            ORDER BY m.created_at DESC
            LIMIT @Limit
            """, new { TenantId = tenantId, Limit = limit * 5 });

        // Group candidates into clusters based on shared entities
        var clusters = GroupIntoClusters(candidates.ToList());

        return clusters.Take(limit).ToList();
    }

    /// <summary>
    /// Summarizes a cluster of related memories into a single L2 memory.
    /// </summary>
    public async Task<SummarizationResult> SummarizeClusterAsync(
        MemoryCluster cluster,
        CancellationToken ct = default)
    {
        if (cluster.Memories.Count < _minClusterSize)
        {
            return new SummarizationResult(
                Success: false,
                Error: $"Cluster has only {cluster.Memories.Count} memories, minimum is {_minClusterSize}",
                Summary: null,
                KeyPoints: null,
                Confidence: 0,
                SourceMemoryIds: cluster.Memories.Select(m => m.MemoryId).ToArray());
        }

        logger.LogInformation(
            "Summarizing cluster of {Count} memories with shared entities: {Entities}",
            cluster.Memories.Count, string.Join(", ", cluster.SharedEntities.Take(5)));

        // Prepare the content for summarization
        var memoriesContent = string.Join("\n\n---\n\n", cluster.Memories
            .OrderBy(m => m.CreatedAt)
            .Select((m, i) => $"Memory {i + 1} ({m.CreatedAt:yyyy-MM-dd}):\n{m.Content}"));

        const string systemPrompt = """
            You are a memory consolidation system. Your task is to synthesize multiple related memories
            into a single coherent summary that captures the essential information.

            Guidelines:
            - Combine overlapping information without repetition
            - Preserve key facts, decisions, and insights
            - Maintain temporal context where relevant
            - Be concise but complete
            - Identify patterns or themes across the memories
            - Note any contradictions or evolution of understanding

            Respond with a JSON object containing:
            {
              "summary": "A comprehensive summary paragraph (2-4 sentences)",
              "key_points": ["Point 1", "Point 2", ...],
              "entities_mentioned": ["Entity 1", "Entity 2", ...],
              "themes": ["Theme 1", "Theme 2", ...],
              "confidence": 0.0-1.0,
              "temporal_range": "Description of time span covered"
            }
            """;

        try
        {
            var result = await llm.ChatStructuredAsync<SummarizationResponse>(
                memoriesContent, systemPrompt, 0.3f, ct);

            if (result == null || string.IsNullOrEmpty(result.Summary))
            {
                return new SummarizationResult(
                    Success: false,
                    Error: "LLM returned empty or invalid response",
                    Summary: null,
                    KeyPoints: null,
                    Confidence: 0,
                    SourceMemoryIds: cluster.Memories.Select(m => m.MemoryId).ToArray());
            }

            return new SummarizationResult(
                Success: true,
                Error: null,
                Summary: result.Summary,
                KeyPoints: result.KeyPoints?.ToArray() ?? [],
                EntitiesMentioned: result.EntitiesMentioned?.ToArray() ?? [],
                Themes: result.Themes?.ToArray() ?? [],
                Confidence: result.Confidence,
                TemporalRange: result.TemporalRange,
                SourceMemoryIds: cluster.Memories.Select(m => m.MemoryId).ToArray());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to summarize memory cluster");
            return new SummarizationResult(
                Success: false,
                Error: ex.Message,
                Summary: null,
                KeyPoints: null,
                Confidence: 0,
                SourceMemoryIds: cluster.Memories.Select(m => m.MemoryId).ToArray());
        }
    }

    /// <summary>
    /// Creates an L2 memory from the summarization result.
    /// </summary>
    public async Task<Guid> CreateSummaryMemoryAsync(
        Guid tenantId,
        SummarizationResult result,
        CancellationToken ct = default)
    {
        if (!result.Success || string.IsNullOrEmpty(result.Summary))
        {
            throw new InvalidOperationException("Cannot create summary from failed summarization result");
        }

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        // Generate embedding for the summary
        var embedding = await embeddings.EmbedTextAsync(result.Summary, ct);

        var memoryId = Guid.CreateVersion7();
        var metadata = new
        {
            layer = "L2_SUMMARY",
            key_points = result.KeyPoints,
            entities_mentioned = result.EntitiesMentioned,
            themes = result.Themes,
            confidence = result.Confidence,
            temporal_range = result.TemporalRange,
            source_memory_count = result.SourceMemoryIds?.Length ?? 0,
            summarized_at = DateTime.UtcNow
        };

        await conn.ExecuteAsync("""
            INSERT INTO memories (id, tenant_id, content, embedding, layer, parent_memory_ids, metadata, source, created_at, updated_at)
            VALUES (@Id, @TenantId, @Content, @Embedding::vector, 'L2_SUMMARY', @ParentMemoryIds, @Metadata::jsonb, 'layer_promotion', NOW(), NOW())
            """,
            new
            {
                Id = memoryId,
                TenantId = tenantId,
                Content = result.Summary,
                Embedding = embedding,
                ParentMemoryIds = result.SourceMemoryIds,
                Metadata = JsonSerializer.Serialize(metadata)
            });

        logger.LogInformation(
            "Created L2 summary memory {MemoryId} from {SourceCount} source memories",
            memoryId, result.SourceMemoryIds?.Length ?? 0);

        return memoryId;
    }

    /// <summary>
    /// Processes a batch of L1→L2 summarizations.
    /// </summary>
    public async Task<BatchSummarizationResult> ProcessBatchAsync(
        Guid tenantId,
        int batchSize = 5,
        CancellationToken ct = default)
    {
        var clusters = await FindSummarizableClustersAsync(tenantId, batchSize, ct);

        var processed = 0;
        var succeeded = 0;
        var failed = 0;
        var createdMemoryIds = new List<Guid>();

        foreach (var cluster in clusters)
        {
            if (ct.IsCancellationRequested) break;

            processed++;
            var result = await SummarizeClusterAsync(cluster, ct);

            if (result.Success)
            {
                try
                {
                    var memoryId = await CreateSummaryMemoryAsync(tenantId, result, ct);
                    createdMemoryIds.Add(memoryId);
                    succeeded++;

                    // Update source memories to reference the new summary
                    await UpdateSourceMemoriesAsync(result.SourceMemoryIds!, memoryId, ct);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to create summary memory");
                    failed++;
                }
            }
            else
            {
                failed++;
                logger.LogWarning("Summarization failed: {Error}", result.Error);
            }
        }

        return new BatchSummarizationResult(
            ClustersFound: clusters.Count,
            Processed: processed,
            Succeeded: succeeded,
            Failed: failed,
            CreatedMemoryIds: createdMemoryIds.ToArray());
    }

    private async Task UpdateSourceMemoriesAsync(
        Guid[] sourceMemoryIds,
        Guid summaryMemoryId,
        CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        // Add a reference to the summary in the source memories' metadata
        await conn.ExecuteAsync("""
            UPDATE memories
            SET metadata = COALESCE(metadata, '{}'::jsonb) || jsonb_build_object('summarized_in', @SummaryId::text),
                updated_at = NOW()
            WHERE id = ANY(@SourceIds)
            """,
            new { SummaryId = summaryMemoryId, SourceIds = sourceMemoryIds });
    }

    private List<MemoryCluster> GroupIntoClusters(List<ClusterCandidateRow> candidates)
    {
        // Simple clustering by shared entities
        var clusters = new List<MemoryCluster>();
        var used = new HashSet<Guid>();

        foreach (var candidate in candidates)
        {
            if (used.Contains(candidate.memory_id)) continue;

            var clusterMemories = new List<ClusterMemory> { ToClusterMemory(candidate) };
            var clusterEntities = new HashSet<string>(candidate.entity_names ?? []);
            used.Add(candidate.memory_id);

            // Find related memories
            foreach (var other in candidates.Where(c => !used.Contains(c.memory_id)))
            {
                var otherEntities = other.entity_names ?? [];
                var sharedCount = clusterEntities.Intersect(otherEntities).Count();

                if (sharedCount >= 2)
                {
                    clusterMemories.Add(ToClusterMemory(other));
                    clusterEntities.UnionWith(otherEntities);
                    used.Add(other.memory_id);

                    if (clusterMemories.Count >= 10) break; // Cap cluster size
                }
            }

            if (clusterMemories.Count >= _minClusterSize)
            {
                clusters.Add(new MemoryCluster(
                    clusterMemories,
                    clusterEntities.ToArray()));
            }
        }

        return clusters;
    }

    private static ClusterMemory ToClusterMemory(ClusterCandidateRow row) =>
        new(row.memory_id, row.content, row.created_at, row.entity_names ?? []);

    #region DTOs

    private sealed class ClusterCandidateRow
    {
        public Guid memory_id { get; set; }
        public string content { get; set; } = "";
        public DateTime created_at { get; set; }
        public string[]? entity_names { get; set; }
    }

    private sealed class SummarizationResponse
    {
        public string? Summary { get; set; }
        public List<string>? KeyPoints { get; set; }
        public List<string>? EntitiesMentioned { get; set; }
        public List<string>? Themes { get; set; }
        public float Confidence { get; set; }
        public string? TemporalRange { get; set; }
    }

    #endregion
}

#region Public Types

/// <summary>
/// A cluster of related memories that can be summarized together.
/// </summary>
public sealed record MemoryCluster(
    IReadOnlyList<ClusterMemory> Memories,
    string[] SharedEntities);

/// <summary>
/// A memory within a cluster.
/// </summary>
public sealed record ClusterMemory(
    Guid MemoryId,
    string Content,
    DateTime CreatedAt,
    string[] EntityNames);

/// <summary>
/// Result of summarizing a memory cluster.
/// </summary>
public sealed record SummarizationResult(
    bool Success,
    string? Error,
    string? Summary,
    string[]? KeyPoints,
    float Confidence,
    Guid[]? SourceMemoryIds,
    string[]? EntitiesMentioned = null,
    string[]? Themes = null,
    string? TemporalRange = null);

/// <summary>
/// Result of processing a batch of summarizations.
/// </summary>
public sealed record BatchSummarizationResult(
    int ClustersFound,
    int Processed,
    int Succeeded,
    int Failed,
    Guid[] CreatedMemoryIds);

#endregion
