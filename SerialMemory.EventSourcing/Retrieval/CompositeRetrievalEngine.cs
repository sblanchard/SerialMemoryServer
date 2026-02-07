using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using Pgvector;
using SerialMemory.Core.Interfaces;

namespace SerialMemory.EventSourcing.Retrieval;

/// <summary>
/// Composite retrieval engine with multi-axis scoring.
/// Combines semantic, recency, confidence, affinity, and directive matching.
/// </summary>
public sealed class CompositeRetrievalEngine : IRetrievalEngine
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly IEmbeddingService _embeddingService;
    private readonly ILogger<CompositeRetrievalEngine> _logger;

    private const float RecencyHalfLifeDays = 90f;

    public CompositeRetrievalEngine(
        string connectionString,
        IEmbeddingService embeddingService,
        ILogger<CompositeRetrievalEngine> logger)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        builder.UseVector();
        _dataSource = builder.Build();
        _embeddingService = embeddingService;
        _logger = logger;
    }

    public async Task<IReadOnlyList<RetrievalResult>> SearchAsync(
        RetrievalQuery query,
        CancellationToken cancellationToken = default)
    {
        // Get query embedding if not provided
        var queryEmbedding = query.QueryEmbedding
            ?? await _embeddingService.EmbedTextAsync(query.QueryText, cancellationToken);

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        // Build dynamic query based on filters
        var layerFilter = query.LayerFilter?.Length > 0
            ? $"AND layer = ANY(@Layers::memory_layer[])"
            : "";

        var activeFilter = query.ActiveOnly ? "AND is_active = TRUE AND is_archived = FALSE" : "";

        // Main retrieval query with all scoring factors
        var sql = $@"
            WITH semantic_results AS (
                SELECT
                    memory_id,
                    content,
                    content_hash,
                    layer,
                    confidence_score,
                    half_life_days,
                    last_reinforced_at,
                    causal_parents,
                    tags,
                    user_affinity_score,
                    directive_relevance,
                    contradiction_ids,
                    created_at,
                    1 - (embedding <=> @QueryEmbedding) AS semantic_score
                FROM memory_projections
                WHERE embedding IS NOT NULL
                    {activeFilter}
                    {layerFilter}
                ORDER BY embedding <=> @QueryEmbedding
                LIMIT @MaxCandidates
            ),
            text_results AS (
                SELECT
                    memory_id,
                    content,
                    content_hash,
                    layer,
                    confidence_score,
                    half_life_days,
                    last_reinforced_at,
                    causal_parents,
                    tags,
                    user_affinity_score,
                    directive_relevance,
                    contradiction_ids,
                    created_at,
                    ts_rank(content_tsvector, plainto_tsquery('english', @QueryText)) * 0.5 AS semantic_score
                FROM memory_projections
                WHERE content_tsvector @@ plainto_tsquery('english', @QueryText)
                    {activeFilter}
                    {layerFilter}
                LIMIT @MaxCandidates
            ),
            combined AS (
                SELECT * FROM semantic_results
                UNION
                SELECT * FROM text_results
            ),
            scored AS (
                SELECT DISTINCT ON (memory_id)
                    memory_id,
                    content,
                    content_hash,
                    layer,
                    semantic_score,
                    -- Recency score: exponential decay
                    POWER(0.5, EXTRACT(EPOCH FROM (NOW() - created_at)) / 86400.0 / @RecencyHalfLife) AS recency_score,
                    -- Confidence with decay
                    GREATEST(0, confidence_score * POWER(0.5, EXTRACT(EPOCH FROM (NOW() - last_reinforced_at)) / 86400.0 / half_life_days)) AS confidence_score,
                    user_affinity_score,
                    directive_relevance,
                    COALESCE(array_length(contradiction_ids, 1), 0) AS contradiction_count,
                    causal_parents,
                    tags,
                    created_at
                FROM combined
                ORDER BY memory_id, semantic_score DESC
            )
            SELECT
                memory_id,
                content,
                content_hash,
                layer,
                semantic_score,
                recency_score,
                confidence_score,
                user_affinity_score,
                directive_relevance AS directive_match_score,
                contradiction_count,
                causal_parents,
                tags,
                created_at,
                -- Compute final score
                (semantic_score * @SemanticWeight) +
                (recency_score * @RecencyWeight) +
                (confidence_score * @ConfidenceWeight) +
                (user_affinity_score * @AffinityWeight) +
                (directive_relevance * @DirectiveWeight) -
                (contradiction_count * @ContradictionPenalty) AS final_score
            FROM scored
            ORDER BY final_score DESC
            LIMIT @MaxResults";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@QueryEmbedding", new Vector(queryEmbedding));
        cmd.Parameters.AddWithValue("@QueryText", query.QueryText);
        cmd.Parameters.AddWithValue("@MaxCandidates", query.MaxResults * 5);
        cmd.Parameters.AddWithValue("@MaxResults", query.MaxResults);
        cmd.Parameters.AddWithValue("@RecencyHalfLife", RecencyHalfLifeDays);
        cmd.Parameters.AddWithValue("@SemanticWeight", query.Weights.SemanticWeight);
        cmd.Parameters.AddWithValue("@RecencyWeight", query.Weights.RecencyWeight);
        cmd.Parameters.AddWithValue("@ConfidenceWeight", query.Weights.ConfidenceWeight);
        cmd.Parameters.AddWithValue("@AffinityWeight", query.Weights.UserAffinityWeight);
        cmd.Parameters.AddWithValue("@DirectiveWeight", query.Weights.DirectiveWeight);
        cmd.Parameters.AddWithValue("@ContradictionPenalty", query.Weights.ContradictionPenalty);

        if (query.LayerFilter?.Length > 0)
        {
            cmd.Parameters.AddWithValue("@Layers", query.LayerFilter.Select(l => l.ToString()).ToArray());
        }

        var results = new List<RetrievalResult>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var finalScore = reader.GetFloat(reader.GetOrdinal("final_score"));
            if (finalScore < query.MinScore) continue;

            results.Add(new RetrievalResult
            {
                MemoryId = reader.GetGuid(reader.GetOrdinal("memory_id")),
                Content = reader.GetString(reader.GetOrdinal("content")),
                Layer = Enum.Parse<MemoryLayer>(reader.GetString(reader.GetOrdinal("layer"))),
                Score = new RetrievalScore
                {
                    SemanticScore = reader.GetFloat(reader.GetOrdinal("semantic_score")),
                    RecencyScore = reader.GetFloat(reader.GetOrdinal("recency_score")),
                    ConfidenceScore = reader.GetFloat(reader.GetOrdinal("confidence_score")),
                    UserAffinityScore = reader.GetFloat(reader.GetOrdinal("user_affinity_score")),
                    DirectiveMatchScore = reader.GetFloat(reader.GetOrdinal("directive_match_score")),
                    ContradictionCount = reader.GetInt32(reader.GetOrdinal("contradiction_count"))
                },
                FinalScore = finalScore,
                CreatedAt = reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("created_at")),
                CausalParents = reader.GetFieldValue<Guid[]>(reader.GetOrdinal("causal_parents")),
                Tags = reader.GetFieldValue<string[]>(reader.GetOrdinal("tags"))
            });
        }

        _logger.LogDebug("Retrieved {Count} memories for query", results.Count);
        return results;
    }

    public async Task<RetrievalResult?> GetByIdAsync(
        Guid memoryId,
        bool verifyIntegrity = true,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        var row = await conn.QuerySingleOrDefaultAsync<dynamic>(new CommandDefinition(@"
            SELECT
                memory_id, content, content_hash, layer,
                confidence_score, half_life_days, last_reinforced_at,
                user_affinity_score, directive_relevance,
                causal_parents, tags, contradiction_ids, created_at
            FROM memory_projections
            WHERE memory_id = @MemoryId",
            new { MemoryId = memoryId },
            cancellationToken: cancellationToken));

        if (row == null) return null;

        // Verify integrity if requested
        if (verifyIntegrity)
        {
            var computedHash = ComputeHash((string)row.content);
            if (computedHash != (string)row.content_hash)
            {
                _logger.LogWarning("Content integrity check failed for memory {MemoryId}", memoryId);
                throw new InvalidOperationException($"Content integrity verification failed for memory {memoryId}");
            }
        }

        // Calculate current confidence with decay
        var lastReinforced = (DateTimeOffset)row.last_reinforced_at;
        var daysElapsed = (DateTimeOffset.UtcNow - lastReinforced).TotalDays;
        var decayedConfidence = (float)row.confidence_score * (float)Math.Pow(0.5, daysElapsed / (int)row.half_life_days);

        // Calculate recency
        var ageDays = (DateTimeOffset.UtcNow - (DateTimeOffset)row.created_at).TotalDays;
        var recencyScore = (float)Math.Pow(0.5, ageDays / RecencyHalfLifeDays);

        var score = new RetrievalScore
        {
            SemanticScore = 1.0f, // Direct lookup
            RecencyScore = recencyScore,
            ConfidenceScore = decayedConfidence,
            UserAffinityScore = (float)row.user_affinity_score,
            DirectiveMatchScore = (float)row.directive_relevance,
            ContradictionCount = ((Guid[])row.contradiction_ids)?.Length ?? 0
        };

        // Record access
        await conn.ExecuteAsync(new CommandDefinition(@"
            UPDATE memory_projections
            SET access_count = access_count + 1, last_accessed_at = NOW()
            WHERE memory_id = @MemoryId",
            new { MemoryId = memoryId },
            cancellationToken: cancellationToken));

        return new RetrievalResult
        {
            MemoryId = memoryId,
            Content = row.content,
            Layer = Enum.Parse<MemoryLayer>((string)row.layer),
            Score = score,
            FinalScore = score.ComputeFinalScore(RetrievalWeights.Default),
            CreatedAt = row.created_at,
            CausalParents = (Guid[]?)row.causal_parents ?? Array.Empty<Guid>(),
            Tags = (string[]?)row.tags ?? Array.Empty<string>()
        };
    }

    public async Task<IReadOnlyList<RetrievalResult>> GetRelatedMemoriesAsync(
        Guid memoryId,
        int maxDepth = 2,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        // Recursive CTE to traverse causal graph
        var sql = @"
            WITH RECURSIVE related AS (
                -- Start from the given memory
                SELECT memory_id, content, layer, causal_parents, 0 AS depth
                FROM memory_projections
                WHERE memory_id = @MemoryId

                UNION ALL

                -- Traverse to parents
                SELECT p.memory_id, p.content, p.layer, p.causal_parents, r.depth + 1
                FROM memory_projections p
                INNER JOIN related r ON p.memory_id = ANY(r.causal_parents)
                WHERE r.depth < @MaxDepth

                UNION ALL

                -- Traverse to children (memories that have this as parent)
                SELECT c.memory_id, c.content, c.layer, c.causal_parents, r.depth + 1
                FROM memory_projections c
                INNER JOIN related r ON r.memory_id = ANY(c.causal_parents)
                WHERE r.depth < @MaxDepth
            )
            SELECT DISTINCT ON (memory_id)
                r.memory_id, r.content, r.layer, r.depth,
                m.confidence_score, m.half_life_days, m.last_reinforced_at,
                m.user_affinity_score, m.directive_relevance,
                m.causal_parents, m.tags, m.contradiction_ids, m.created_at
            FROM related r
            JOIN memory_projections m ON r.memory_id = m.memory_id
            WHERE r.memory_id != @MemoryId
            ORDER BY memory_id, r.depth
            LIMIT 50";

        var rows = await conn.QueryAsync<dynamic>(new CommandDefinition(
            sql,
            new { MemoryId = memoryId, MaxDepth = maxDepth },
            cancellationToken: cancellationToken));

        return [.. rows.Select(row =>
        {
            var lastReinforced = (DateTimeOffset)row.last_reinforced_at;
            var daysElapsed = (DateTimeOffset.UtcNow - lastReinforced).TotalDays;
            var decayedConfidence = (float)row.confidence_score * (float)Math.Pow(0.5, daysElapsed / (int)row.half_life_days);
            var ageDays = (DateTimeOffset.UtcNow - (DateTimeOffset)row.created_at).TotalDays;

            var score = new RetrievalScore
            {
                SemanticScore = 1.0f - ((int)row.depth * 0.2f), // Decrease by depth
                RecencyScore = (float)Math.Pow(0.5, ageDays / RecencyHalfLifeDays),
                ConfidenceScore = decayedConfidence,
                UserAffinityScore = (float)row.user_affinity_score,
                DirectiveMatchScore = (float)row.directive_relevance,
                ContradictionCount = ((Guid[])row.contradiction_ids)?.Length ?? 0
            };

            return new RetrievalResult
            {
                MemoryId = row.memory_id,
                Content = row.content,
                Layer = Enum.Parse<MemoryLayer>((string)row.layer),
                Score = score,
                FinalScore = score.ComputeFinalScore(RetrievalWeights.Default),
                CreatedAt = row.created_at,
                CausalParents = (Guid[]?)row.causal_parents ?? Array.Empty<Guid>(),
                Tags = (string[]?)row.tags ?? Array.Empty<string>()
            };
        })];
    }

    public async Task<IReadOnlyList<(Guid MemoryA, Guid MemoryB, float Similarity)>> FindDuplicatesAsync(
        float similarityThreshold = 0.95f,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        var sql = @"
            SELECT
                a.memory_id AS memory_a_id,
                b.memory_id AS memory_b_id,
                1 - (a.embedding <=> b.embedding) AS similarity
            FROM memory_projections a
            CROSS JOIN LATERAL (
                SELECT memory_id, embedding
                FROM memory_projections
                WHERE memory_id > a.memory_id
                    AND is_active = TRUE
                    AND embedding IS NOT NULL
                ORDER BY embedding <=> a.embedding
                LIMIT 10
            ) b
            WHERE a.is_active = TRUE
                AND a.embedding IS NOT NULL
                AND 1 - (a.embedding <=> b.embedding) > @Threshold
            ORDER BY similarity DESC
            LIMIT 100";

        var results = await conn.QueryAsync<(Guid memory_a_id, Guid memory_b_id, float similarity)>(
            new CommandDefinition(sql, new { Threshold = similarityThreshold }, cancellationToken: cancellationToken));

        return results.Select(r => (r.memory_a_id, r.memory_b_id, r.similarity)).ToList();
    }

    public async Task RecordInteractionAsync(
        string userId,
        Guid memoryId,
        string interactionType,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        // Record interaction
        await conn.ExecuteAsync(new CommandDefinition(@"
            INSERT INTO user_memory_interactions (user_id, memory_id, interaction_type)
            VALUES (@UserId, @MemoryId, @InteractionType)",
            new { UserId = userId, MemoryId = memoryId, InteractionType = interactionType },
            cancellationToken: cancellationToken));

        // Update affinity score
        var affinityDelta = interactionType switch
        {
            "view" => 0.01f,
            "reference" => 0.05f,
            "reinforce" => 0.10f,
            "dismiss" => -0.10f,
            _ => 0f
        };

        if (affinityDelta != 0)
        {
            await conn.ExecuteAsync(new CommandDefinition(@"
                UPDATE memory_projections
                SET user_affinity_score = LEAST(1.0, GREATEST(0.0, user_affinity_score + @Delta))
                WHERE memory_id = @MemoryId",
                new { MemoryId = memoryId, Delta = affinityDelta },
                cancellationToken: cancellationToken));
        }
    }

    private static string ComputeHash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexStringLower(bytes);
    }
}
