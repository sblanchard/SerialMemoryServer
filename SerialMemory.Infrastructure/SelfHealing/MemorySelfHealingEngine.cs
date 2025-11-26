using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using Pgvector;
using SerialMemory.Core.Interfaces;

namespace SerialMemory.Infrastructure.SelfHealing;

public sealed class MemorySelfHealingEngine : IMemorySelfHealing
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly IEmbeddingService _embeddingService;
    private readonly ILogger<MemorySelfHealingEngine> _logger;
    private readonly string _workerId;

    public MemorySelfHealingEngine(
        NpgsqlDataSource dataSource,
        IEmbeddingService embeddingService,
        ILogger<MemorySelfHealingEngine> logger)
    {
        _dataSource = dataSource;
        _embeddingService = embeddingService;
        _logger = logger;
        _workerId = $"healer-{Environment.MachineName}-{Process.GetCurrentProcess().Id}";
    }

    public async Task<SelfHealingResult> RunCycleAsync(
        SelfHealingOptions options,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var operations = new List<SelfHealingOperation>();

        int contradictionsDetected = 0;
        int memoriesMerged = 0;
        int memoriesDecayed = 0;
        int memoriesReinforced = 0;

        try
        {
            // Phase 1: Detect and record contradictions
            if (options.DetectContradictions)
            {
                var detectionResult = await DetectAllContradictionsAsync(
                    options.MaxOperationsPerCycle,
                    cancellationToken);
                contradictionsDetected = detectionResult.Count;

                foreach (var contradiction in detectionResult)
                {
                    operations.Add(new SelfHealingOperation(
                        "detect_contradiction",
                        new[] { contradiction.MemoryIdA, contradiction.MemoryIdB },
                        null,
                        "detected",
                        0));
                }
            }

            // Phase 2: Merge semantically similar memories
            if (options.MergeDuplicates)
            {
                var mergeResult = await MergeAllSimilarMemoriesAsync(
                    options.SimilarityThreshold,
                    options.MaxOperationsPerCycle - operations.Count,
                    cancellationToken);
                memoriesMerged = mergeResult.Count;

                foreach (var merge in mergeResult)
                {
                    operations.Add(new SelfHealingOperation(
                        "merge_memories",
                        merge.SourceMemoryIds.ToArray(),
                        merge.TargetMemoryId,
                        "merged",
                        0));
                }
            }

            // Phase 3: Apply decay to old, unaccessed memories
            if (options.ApplyDecay)
            {
                memoriesDecayed = await ApplyDecayAsync(
                    options.DecayMaxAge,
                    options.DecayFactor,
                    cancellationToken);

                if (memoriesDecayed > 0)
                {
                    operations.Add(new SelfHealingOperation(
                        "apply_decay",
                        Array.Empty<Guid>(),
                        null,
                        $"decayed_{memoriesDecayed}",
                        0));
                }
            }

            // Phase 4: Reinforce stable, frequently accessed memories
            if (options.ReinforceStable)
            {
                var stableMemoryIds = await GetStableMemoryIdsAsync(
                    options.MaxOperationsPerCycle - operations.Count,
                    cancellationToken);

                memoriesReinforced = await ReinforceMemoriesAsync(
                    stableMemoryIds,
                    1.1f, // 10% boost
                    "stable_access_pattern",
                    cancellationToken);

                if (memoriesReinforced > 0)
                {
                    operations.Add(new SelfHealingOperation(
                        "reinforce_stable",
                        stableMemoryIds.ToArray(),
                        null,
                        $"reinforced_{memoriesReinforced}",
                        0));
                }
            }

            stopwatch.Stop();

            await LogSelfHealingCycleAsync(operations, stopwatch.ElapsedMilliseconds, cancellationToken);

            _logger.LogInformation(
                "Self-healing cycle completed in {Duration}ms: {Contradictions} contradictions, " +
                "{Merged} merged, {Decayed} decayed, {Reinforced} reinforced",
                stopwatch.ElapsedMilliseconds, contradictionsDetected, memoriesMerged,
                memoriesDecayed, memoriesReinforced);

            return new SelfHealingResult(
                contradictionsDetected,
                memoriesMerged,
                memoriesDecayed,
                memoriesReinforced,
                stopwatch.Elapsed,
                operations);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Self-healing cycle failed after {Duration}ms", stopwatch.ElapsedMilliseconds);
            throw;
        }
    }

    public async Task<IReadOnlyList<MemoryContradiction>> DetectContradictionsAsync(
        Guid memoryId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        // Get the memory's embedding and entities
        const string getMemorySql = @"
            SELECT m.content, m.embedding, array_agg(e.id) as entity_ids
            FROM memories m
            LEFT JOIN memory_entities me ON m.id = me.memory_id
            LEFT JOIN entities e ON me.entity_id = e.id
            WHERE m.id = @MemoryId
            GROUP BY m.id, m.content, m.embedding";

        await using var cmd = new NpgsqlCommand(getMemorySql, connection);
        cmd.Parameters.AddWithValue("MemoryId", memoryId);

        string? content = null;
        Vector? embedding = null;
        Guid[]? entityIds = null;

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            content = reader.GetString(0);
            embedding = reader.IsDBNull(1) ? null : reader.GetFieldValue<Vector>(1);
            entityIds = reader.IsDBNull(2) ? null : reader.GetFieldValue<Guid[]>(2);
        }
        await reader.CloseAsync();

        if (content == null || embedding == null)
        {
            return Array.Empty<MemoryContradiction>();
        }

        // Find semantically similar memories that share entities
        const string findSimilarSql = @"
            SELECT m.id, m.content, 1 - (m.embedding <=> @Embedding::vector) as similarity
            FROM memories m
            JOIN memory_entities me ON m.id = me.memory_id
            WHERE m.id != @MemoryId
              AND m.embedding IS NOT NULL
              AND 1 - (m.embedding <=> @Embedding::vector) > 0.7
              AND me.entity_id = ANY(@EntityIds)
            ORDER BY similarity DESC
            LIMIT 20";

        await using var similarCmd = new NpgsqlCommand(findSimilarSql, connection);
        similarCmd.Parameters.AddWithValue("MemoryId", memoryId);
        similarCmd.Parameters.AddWithValue("Embedding", embedding);
        similarCmd.Parameters.AddWithValue("EntityIds", entityIds ?? Array.Empty<Guid>());

        var contradictions = new List<MemoryContradiction>();

        await using var similarReader = await similarCmd.ExecuteReaderAsync(cancellationToken);
        while (await similarReader.ReadAsync(cancellationToken))
        {
            var otherId = similarReader.GetGuid(0);
            var otherContent = similarReader.GetString(1);
            var similarity = similarReader.GetFloat(2);

            // Detect semantic contradictions using heuristics
            var contradictionType = DetectContradictionType(content, otherContent);

            if (contradictionType != null)
            {
                var contradiction = new MemoryContradiction(
                    Guid.CreateVersion7(),
                    memoryId,
                    otherId,
                    contradictionType,
                    similarity * 0.8f, // Confidence based on similarity
                    DateTime.UtcNow,
                    null,
                    null,
                    null,
                    "semantic_similarity_with_negation",
                    new Dictionary<string, object>
                    {
                        ["similarity"] = similarity,
                        ["content_a_snippet"] = content.Length > 100 ? content[..100] : content,
                        ["content_b_snippet"] = otherContent.Length > 100 ? otherContent[..100] : otherContent
                    });

                contradictions.Add(contradiction);

                // Store the contradiction
                await StoreContradictionAsync(contradiction, cancellationToken);
            }
        }

        return contradictions;
    }

    private string? DetectContradictionType(string contentA, string contentB)
    {
        var lowerA = contentA.ToLowerInvariant();
        var lowerB = contentB.ToLowerInvariant();

        // Check for explicit negation patterns
        var negationPatterns = new[]
        {
            ("is ", "is not ", "negation"),
            ("was ", "was not ", "negation"),
            ("are ", "are not ", "negation"),
            ("will ", "will not ", "negation"),
            ("can ", "cannot ", "negation"),
            ("does ", "does not ", "negation"),
            ("true", "false", "boolean_contradiction"),
            ("yes", "no", "boolean_contradiction"),
            ("always", "never", "temporal_contradiction"),
            ("before", "after", "temporal_contradiction"),
            ("increase", "decrease", "direction_contradiction"),
            ("more", "less", "quantity_contradiction")
        };

        foreach (var (positive, negative, type) in negationPatterns)
        {
            // Check if one contains positive and other contains negative for same subject
            if ((lowerA.Contains(positive) && lowerB.Contains(negative)) ||
                (lowerA.Contains(negative) && lowerB.Contains(positive)))
            {
                return type;
            }
        }

        // Check for contradicting numbers in similar context
        var numbersA = ExtractNumbers(lowerA);
        var numbersB = ExtractNumbers(lowerB);

        if (numbersA.Any() && numbersB.Any())
        {
            // If talking about the same entity with different numbers
            var intersection = numbersA.Intersect(numbersB);
            var uniqueToA = numbersA.Except(numbersB);
            var uniqueToB = numbersB.Except(numbersA);

            if (!intersection.Any() && uniqueToA.Any() && uniqueToB.Any())
            {
                // Different numbers present - potential contradiction
                return "numeric_contradiction";
            }
        }

        return null;
    }

    private static List<float> ExtractNumbers(string text)
    {
        var numbers = new List<float>();
        var matches = System.Text.RegularExpressions.Regex.Matches(text, @"\d+\.?\d*");

        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            if (float.TryParse(match.Value, out var num))
            {
                numbers.Add(num);
            }
        }

        return numbers;
    }

    private async Task<List<MemoryContradiction>> DetectAllContradictionsAsync(
        int maxOperations,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        // Get recent memories not yet checked for contradictions
        const string sql = @"
            SELECT DISTINCT m.id
            FROM memories m
            WHERE m.embedding IS NOT NULL
              AND NOT EXISTS (
                  SELECT 1 FROM memory_contradictions mc
                  WHERE mc.memory_id_a = m.id OR mc.memory_id_b = m.id
              )
            ORDER BY m.created_at DESC
            LIMIT @Limit";

        var memoryIds = (await connection.QueryAsync<Guid>(sql, new { Limit = maxOperations })).ToList();

        var allContradictions = new List<MemoryContradiction>();

        foreach (var memoryId in memoryIds)
        {
            var contradictions = await DetectContradictionsAsync(memoryId, cancellationToken);
            allContradictions.AddRange(contradictions);

            if (allContradictions.Count >= maxOperations)
            {
                break;
            }
        }

        return allContradictions;
    }

    private async Task StoreContradictionAsync(
        MemoryContradiction contradiction,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        const string sql = @"
            INSERT INTO memory_contradictions (
                contradiction_id, memory_id_a, memory_id_b, contradiction_type,
                confidence, detected_at, detection_method, evidence
            ) VALUES (
                @ContradictionId, @MemoryIdA, @MemoryIdB, @ContradictionType,
                @Confidence, @DetectedAt, @DetectionMethod, @Evidence::jsonb
            )
            ON CONFLICT (memory_id_a, memory_id_b) DO NOTHING";

        await connection.ExecuteAsync(sql, new
        {
            contradiction.ContradictionId,
            contradiction.MemoryIdA,
            contradiction.MemoryIdB,
            contradiction.ContradictionType,
            contradiction.Confidence,
            contradiction.DetectedAt,
            contradiction.DetectionMethod,
            Evidence = JsonSerializer.Serialize(contradiction.Evidence)
        });
    }

    public async Task<MergeResult> MergeSimilarMemoriesAsync(
        IEnumerable<Guid> memoryIds,
        float similarityThreshold,
        CancellationToken cancellationToken = default)
    {
        var idList = memoryIds.ToList();
        if (idList.Count < 2)
        {
            throw new ArgumentException("At least 2 memories required for merge", nameof(memoryIds));
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        // Get all memories
        const string getMemoriesSql = @"
            SELECT id, content, embedding, created_at
            FROM memories
            WHERE id = ANY(@MemoryIds)
            ORDER BY created_at";

        await using var cmd = new NpgsqlCommand(getMemoriesSql, connection);
        cmd.Parameters.AddWithValue("MemoryIds", idList.ToArray());

        var memories = new List<(Guid Id, string Content, Vector Embedding, DateTime CreatedAt)>();

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            memories.Add((
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetFieldValue<Vector>(2),
                reader.GetDateTime(3)));
        }
        await reader.CloseAsync();

        // Verify similarity
        var embeddings = memories.Select(m => m.Embedding.ToArray()).ToList();
        var avgSimilarity = ComputeAverageSimilarity(embeddings);

        if (avgSimilarity < similarityThreshold)
        {
            throw new InvalidOperationException(
                $"Memories not similar enough for merge: {avgSimilarity:F3} < {similarityThreshold:F3}");
        }

        // Create merged content
        var mergedContent = string.Join("\n---\n", memories.Select(m => m.Content));
        var mergedEmbedding = await _embeddingService.EmbedTextAsync(mergedContent);
        var targetId = Guid.CreateVersion7();

        // Create merged memory
        const string insertMergedSql = @"
            INSERT INTO memories (id, content, embedding, source, metadata)
            VALUES (@Id, @Content, @Embedding, 'merged', @Metadata::jsonb)";

        await using var insertCmd = new NpgsqlCommand(insertMergedSql, connection);
        insertCmd.Parameters.AddWithValue("Id", targetId);
        insertCmd.Parameters.AddWithValue("Content", mergedContent);
        insertCmd.Parameters.AddWithValue("Embedding", new Vector(mergedEmbedding));
        insertCmd.Parameters.AddWithValue("Metadata", JsonSerializer.Serialize(new
        {
            merged_from = idList,
            merge_date = DateTime.UtcNow,
            original_count = idList.Count
        }));

        await insertCmd.ExecuteNonQueryAsync(cancellationToken);

        // Transfer entity links
        const string transferEntitiesSql = @"
            INSERT INTO memory_entities (memory_id, entity_id, relevance)
            SELECT @TargetId, entity_id, MAX(relevance)
            FROM memory_entities
            WHERE memory_id = ANY(@SourceIds)
            GROUP BY entity_id
            ON CONFLICT (memory_id, entity_id) DO UPDATE SET
                relevance = GREATEST(memory_entities.relevance, EXCLUDED.relevance)";

        await connection.ExecuteAsync(transferEntitiesSql, new
        {
            TargetId = targetId,
            SourceIds = idList.ToArray()
        });

        // Log the merge
        const string logMergeSql = @"
            INSERT INTO memory_merges (
                source_memory_ids, target_memory_id, merge_type,
                similarity_score, merged_by, metadata
            ) VALUES (
                @SourceIds, @TargetId, 'semantic_duplicate',
                @Similarity, @MergedBy, '{}'::jsonb
            )";

        await connection.ExecuteAsync(logMergeSql, new
        {
            SourceIds = idList.ToArray(),
            TargetId = targetId,
            Similarity = avgSimilarity,
            MergedBy = _workerId
        });

        // Soft-delete source memories (mark as merged)
        const string markMergedSql = @"
            UPDATE memories
            SET metadata = COALESCE(metadata, '{}'::jsonb) ||
                jsonb_build_object('merged_into', @TargetId::text, 'merged_at', NOW()::text)
            WHERE id = ANY(@SourceIds)";

        await connection.ExecuteAsync(markMergedSql, new
        {
            TargetId = targetId,
            SourceIds = idList.ToArray()
        });

        _logger.LogInformation(
            "Merged {Count} memories into {TargetId} with similarity {Similarity:F3}",
            idList.Count, targetId, avgSimilarity);

        return new MergeResult(targetId, idList, avgSimilarity, "semantic_duplicate");
    }

    private async Task<List<MergeResult>> MergeAllSimilarMemoriesAsync(
        float similarityThreshold,
        int maxOperations,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        // Find similar memory pairs not yet merged
        const string findDuplicatesSql = @"
            WITH memory_pairs AS (
                SELECT
                    m1.id as id_a,
                    m2.id as id_b,
                    1 - (m1.embedding <=> m2.embedding) as similarity
                FROM memories m1
                JOIN memories m2 ON m1.id < m2.id
                WHERE m1.embedding IS NOT NULL
                  AND m2.embedding IS NOT NULL
                  AND 1 - (m1.embedding <=> m2.embedding) >= @Threshold
                  AND NOT EXISTS (
                      SELECT 1 FROM memory_merges mm
                      WHERE m1.id = ANY(mm.source_memory_ids) OR m2.id = ANY(mm.source_memory_ids)
                  )
                  AND m1.metadata->>'merged_into' IS NULL
                  AND m2.metadata->>'merged_into' IS NULL
            )
            SELECT id_a, id_b, similarity
            FROM memory_pairs
            ORDER BY similarity DESC
            LIMIT @Limit";

        var pairs = (await connection.QueryAsync<(Guid id_a, Guid id_b, float similarity)>(
            findDuplicatesSql,
            new { Threshold = similarityThreshold, Limit = maxOperations })).ToList();

        var results = new List<MergeResult>();
        var processedIds = new HashSet<Guid>();

        foreach (var (idA, idB, similarity) in pairs)
        {
            if (processedIds.Contains(idA) || processedIds.Contains(idB))
            {
                continue;
            }

            try
            {
                var result = await MergeSimilarMemoriesAsync(
                    new[] { idA, idB },
                    similarityThreshold,
                    cancellationToken);

                results.Add(result);
                processedIds.Add(idA);
                processedIds.Add(idB);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to merge memories {IdA} and {IdB}", idA, idB);
            }

            if (results.Count >= maxOperations)
            {
                break;
            }
        }

        return results;
    }

    public async Task<int> ApplyDecayAsync(
        TimeSpan maxAge,
        float decayFactor,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        // Apply decay to memories not accessed recently
        const string sql = @"
            WITH decayed AS (
                UPDATE memory_projections mp
                SET confidence_score = GREATEST(0.1, confidence_score * @DecayFactor),
                    last_reinforced_at = NOW()
                WHERE last_accessed_at < NOW() - @MaxAge::interval
                  AND confidence_score > 0.1
                  AND is_active = TRUE
                RETURNING memory_id, confidence_score
            )
            INSERT INTO memory_decay_log (memory_id, previous_confidence, new_confidence, decay_factor, decay_reason)
            SELECT memory_id, confidence_score / @DecayFactor, confidence_score, @DecayFactor, 'time_decay'
            FROM decayed";

        var affected = await connection.ExecuteAsync(sql, new
        {
            MaxAge = maxAge.ToString(),
            DecayFactor = decayFactor
        });

        if (affected > 0)
        {
            _logger.LogDebug("Applied decay to {Count} memories older than {MaxAge}", affected, maxAge);
        }

        return affected;
    }

    public async Task<int> ReinforceMemoriesAsync(
        IEnumerable<Guid> memoryIds,
        float reinforcementFactor,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var idList = memoryIds.ToList();
        if (idList.Count == 0)
        {
            return 0;
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        const string sql = @"
            WITH reinforced AS (
                UPDATE memory_projections mp
                SET confidence_score = LEAST(1.0, confidence_score * @ReinforcementFactor),
                    last_reinforced_at = NOW()
                WHERE memory_id = ANY(@MemoryIds)
                  AND is_active = TRUE
                RETURNING memory_id, confidence_score
            )
            INSERT INTO memory_reinforcements (memory_id, previous_confidence, new_confidence, reinforcement_factor, reinforcement_reason)
            SELECT memory_id, confidence_score / @ReinforcementFactor, confidence_score, @ReinforcementFactor, @Reason
            FROM reinforced";

        var affected = await connection.ExecuteAsync(sql, new
        {
            MemoryIds = idList.ToArray(),
            ReinforcementFactor = reinforcementFactor,
            Reason = reason
        });

        if (affected > 0)
        {
            _logger.LogDebug("Reinforced {Count} memories with factor {Factor}: {Reason}",
                affected, reinforcementFactor, reason);
        }

        return affected;
    }

    private async Task<List<Guid>> GetStableMemoryIdsAsync(int limit, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        // Find memories with high access frequency and stable confidence
        const string sql = @"
            SELECT memory_id
            FROM memory_projections
            WHERE is_active = TRUE
              AND access_count >= 5
              AND confidence_score > 0.6
              AND last_accessed_at > NOW() - INTERVAL '7 days'
            ORDER BY access_count DESC, confidence_score DESC
            LIMIT @Limit";

        var ids = await connection.QueryAsync<Guid>(sql, new { Limit = limit });
        return ids.ToList();
    }

    private async Task LogSelfHealingCycleAsync(
        List<SelfHealingOperation> operations,
        long durationMs,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        foreach (var op in operations)
        {
            const string sql = @"
                INSERT INTO self_healing_logs (
                    operation_type, target_memory_ids, result_memory_id,
                    operation_result, details, duration_ms, worker_id
                ) VALUES (
                    @OperationType, @TargetMemoryIds, @ResultMemoryId,
                    @OperationResult, @Details::jsonb, @DurationMs, @WorkerId
                )";

            await connection.ExecuteAsync(sql, new
            {
                op.OperationType,
                TargetMemoryIds = op.TargetMemoryIds.ToArray(),
                ResultMemoryId = op.ResultMemoryId,
                OperationResult = op.Result,
                Details = JsonSerializer.Serialize(new { operations_count = operations.Count }),
                DurationMs = (int)durationMs,
                WorkerId = _workerId
            });
        }
    }

    private static float ComputeAverageSimilarity(List<float[]> embeddings)
    {
        if (embeddings.Count < 2) return 1f;

        var totalSimilarity = 0f;
        var count = 0;

        for (int i = 0; i < embeddings.Count; i++)
        {
            for (int j = i + 1; j < embeddings.Count; j++)
            {
                totalSimilarity += CosineSimilarity(embeddings[i], embeddings[j]);
                count++;
            }
        }

        return count > 0 ? totalSimilarity / count : 1f;
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        var dot = 0f;
        var normA = 0f;
        var normB = 0f;

        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        return dot / (MathF.Sqrt(normA) * MathF.Sqrt(normB));
    }
}
