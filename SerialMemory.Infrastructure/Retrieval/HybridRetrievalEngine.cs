using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using Pgvector;
using SerialMemory.Core.Interfaces;

namespace SerialMemory.Infrastructure.Retrieval;

public sealed class HybridRetrievalEngine(
    NpgsqlDataSource dataSource,
    IEmbeddingService embeddingService,
    ILogger<HybridRetrievalEngine> logger)
    : IHybridRetrievalEngine
{
    private readonly RetrievalScoreBreakdown _defaultWeights = new(
        SemanticWeight: 0.35f,
        SymbolicWeight: 0.15f,
        GraphWeight: 0.20f,
        TemporalWeight: 0.15f,
        ConfidenceWeight: 0.15f);

    public async Task<HybridRetrievalResult> RetrieveAsync(
        HybridRetrievalQuery query,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        // Generate query embedding if not provided
        var queryEmbedding = query.QueryEmbedding ?? await embeddingService.EmbedTextAsync(query.QueryText);

        // Run all retrieval strategies in parallel
        var semanticTask = GetSemanticMatchesAsync(queryEmbedding, query.Limit * 2, query.SemanticThreshold, cancellationToken);
        var rulesTask = query.UseSymbolicRules
            ? GetSymbolicMatchesAsync(query.QueryText, query.FilterCriteria, cancellationToken)
            : Task.FromResult(new SymbolicMatchResult(new List<MemoryScore>(), new List<string>()));
        var graphTask = query.UseGraphTraversal
            ? GetGraphMatchesAsync(queryEmbedding, query.Limit, cancellationToken)
            : Task.FromResult(new GraphMatchResult(new List<MemoryScore>(), new List<Guid>()));

        await Task.WhenAll(semanticTask, rulesTask, graphTask);

        var semanticResults = await semanticTask;
        var symbolicResults = await rulesTask;
        var graphResults = await graphTask;

        // Compute temporal scores
        var temporalAnchor = query.TemporalAnchor ?? DateTime.UtcNow;
        var allMemoryIds = semanticResults.Select(r => r.MemoryId)
            .Concat(symbolicResults.Memories.Select(r => r.MemoryId))
            .Concat(graphResults.Memories.Select(r => r.MemoryId))
            .Distinct()
            .ToList();

        var temporalScores = query.UseTemporalScoring
            ? await GetTemporalScoresAsync(allMemoryIds, temporalAnchor, cancellationToken)
            : new Dictionary<Guid, float>();

        var confidenceScores = await GetConfidenceScoresAsync(allMemoryIds, cancellationToken);

        // Merge and score all results
        var combinedScores = CombineScores(
            semanticResults,
            symbolicResults.Memories,
            graphResults.Memories,
            temporalScores,
            confidenceScores,
            _defaultWeights);

        // Get full memory content and build final results
        var topMemoryIds = combinedScores
            .OrderByDescending(kv => kv.Value.FinalScore)
            .Take(query.Limit)
            .Select(kv => kv.Key)
            .ToList();

        var memories = await GetMemoriesWithContentAsync(topMemoryIds, cancellationToken);

        var scoredMemories = topMemoryIds
            .Select(id =>
            {
                var score = combinedScores[id];
                var memory = memories.FirstOrDefault(m => m.Id == id);
                var matchedRules = symbolicResults.Memories
                    .Where(m => m.MemoryId == id)
                    .SelectMany(m => m.MatchedRules)
                    .Distinct()
                    .ToList();

                return new ScoredMemory(
                    id,
                    memory?.Content ?? string.Empty,
                    score.FinalScore,
                    score.SemanticScore,
                    score.SymbolicScore,
                    score.GraphScore,
                    score.TemporalScore,
                    score.ConfidenceScore,
                    memory?.CreatedAt ?? DateTime.UtcNow,
                    matchedRules);
            })
            .ToList();

        stopwatch.Stop();

        logger.LogDebug(
            "Hybrid retrieval completed in {Duration}ms: {Count} results",
            stopwatch.ElapsedMilliseconds,
            scoredMemories.Count);

        return new HybridRetrievalResult(
            scoredMemories,
            symbolicResults.AppliedRules,
            graphResults.TraversedEntities,
            _defaultWeights,
            stopwatch.Elapsed);
    }

    private async Task<List<MemoryScore>> GetSemanticMatchesAsync(
        float[] queryEmbedding,
        int limit,
        float threshold,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        const string sql = """

                                       SELECT id, 1 - (embedding <=> @QueryEmbedding::vector) as similarity
                                       FROM memories
                                       WHERE embedding IS NOT NULL
                                         AND 1 - (embedding <=> @QueryEmbedding::vector) >= @Threshold
                                       ORDER BY embedding <=> @QueryEmbedding::vector
                                       LIMIT @Limit
                           """;

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("QueryEmbedding", new Vector(queryEmbedding));
        cmd.Parameters.AddWithValue("Threshold", threshold);
        cmd.Parameters.AddWithValue("Limit", limit);

        var results = new List<MemoryScore>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new MemoryScore(
                reader.GetGuid(0),
                reader.GetFloat(1),
                0f, 0f, 0f, 0f,
                new List<string>()));
        }

        return results;
    }

    private async Task<SymbolicMatchResult> GetSymbolicMatchesAsync(
        string queryText,
        Dictionary<string, object>? filterCriteria,
        CancellationToken cancellationToken)
    {
        var rules = await GetActiveRulesAsync(cancellationToken);
        var appliedRules = new List<string>();
        var results = new Dictionary<Guid, (float Score, List<string> Rules)>();

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        foreach (var rule in rules.OrderByDescending(r => r.Priority))
        {
            if (!EvaluateRuleCondition(rule, queryText, filterCriteria))
            {
                continue;
            }

            appliedRules.Add(rule.RuleName);

            var matchedMemories = await ExecuteRuleQueryAsync(connection, rule, queryText, cancellationToken);

            foreach (var (memoryId, score) in matchedMemories)
            {
                if (!results.TryGetValue(memoryId, out var existing))
                {
                    existing = (0f, new List<string>());
                }

                results[memoryId] = (
                    Math.Max(existing.Score, score * rule.Weight),
                    existing.Rules.Append(rule.RuleName).ToList());
            }
        }

        var memories = results.Select(kv => new MemoryScore(
            kv.Key,
            0f,
            kv.Value.Score,
            0f, 0f, 0f,
            kv.Value.Rules)).ToList();

        return new SymbolicMatchResult(memories, appliedRules);
    }

    private bool EvaluateRuleCondition(SymbolicRule rule, string queryText, Dictionary<string, object>? filterCriteria)
    {
        try
        {
            // Parse condition expression (simple pattern matching)
            var condition = rule.ConditionExpression.ToLowerInvariant();
            var query = queryText.ToLowerInvariant();

            // Check for keyword matches
            if (condition.StartsWith("contains:"))
            {
                var keyword = condition["contains:".Length..].Trim();
                return query.Contains(keyword);
            }

            // Check for regex patterns
            if (condition.StartsWith("regex:"))
            {
                var pattern = condition["regex:".Length..].Trim();
                return Regex.IsMatch(query, pattern, RegexOptions.IgnoreCase);
            }

            // Check for entity type requirements
            if (condition.StartsWith("entity_type:"))
            {
                var entityType = condition["entity_type:".Length..].Trim();
                return filterCriteria?.TryGetValue("entity_type", out var value) == true
                    && value?.ToString()?.ToLowerInvariant() == entityType;
            }

            // Check for metadata conditions
            if (condition.StartsWith("metadata:"))
            {
                var metaCondition = condition["metadata:".Length..].Trim();
                var parts = metaCondition.Split('=', 2);
                if (parts.Length == 2 && filterCriteria != null)
                {
                    return filterCriteria.TryGetValue(parts[0].Trim(), out var value)
                        && value?.ToString()?.ToLowerInvariant() == parts[1].Trim();
                }
            }

            // Default: always apply
            if (condition == "*" || condition == "always")
            {
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to evaluate rule condition: {Condition}", rule.ConditionExpression);
            return false;
        }
    }

    private async Task<List<(Guid MemoryId, float Score)>> ExecuteRuleQueryAsync(
        NpgsqlConnection connection,
        SymbolicRule rule,
        string queryText,
        CancellationToken cancellationToken)
    {
        var results = new List<(Guid, float)>();

        // Different query strategies based on rule type
        var sql = rule.RuleType switch
        {
            "full_text_boost" => """

                                                 SELECT id, ts_rank(content_tsvector, plainto_tsquery('english', @Query)) as score
                                                 FROM memories
                                                 WHERE content_tsvector @@ plainto_tsquery('english', @Query)
                                                 ORDER BY score DESC
                                                 LIMIT 50
                                 """,

            "entity_match" => """

                                              SELECT DISTINCT m.id, 0.8 as score
                                              FROM memories m
                                              JOIN memory_entities me ON m.id = me.memory_id
                                              JOIN entities e ON me.entity_id = e.id
                                              WHERE LOWER(e.name) LIKE '%' || LOWER(@Query) || '%'
                                              LIMIT 50
                              """,

            "recency_boost" => """

                                               SELECT id, 1.0 - EXTRACT(EPOCH FROM (NOW() - created_at)) / 86400.0 / 30.0 as score
                                               FROM memories
                                               WHERE created_at > NOW() - INTERVAL '30 days'
                                               ORDER BY created_at DESC
                                               LIMIT 50
                               """,

            "source_filter" => """

                                               SELECT id, 1.0 as score
                                               FROM memories
                                               WHERE source = @Query
                                               LIMIT 50
                               """,

            _ => null
        };

        if (sql == null)
        {
            return results;
        }

        var rows = await connection.QueryAsync<(Guid id, float score)>(sql, new { Query = queryText });
        return rows.ToList();
    }

    private async Task<GraphMatchResult> GetGraphMatchesAsync(
        float[] queryEmbedding,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        // First, find entities related to the query
        const string findEntitiesSql = """

                                                   SELECT DISTINCT e.id, e.name
                                                   FROM entities e
                                                   JOIN memory_entities me ON e.id = me.entity_id
                                                   JOIN memories m ON me.memory_id = m.id
                                                   WHERE m.embedding IS NOT NULL
                                                   ORDER BY m.embedding <=> @QueryEmbedding::vector
                                                   LIMIT 10
                                       """;

        await using var cmd = new NpgsqlCommand(findEntitiesSql, connection);
        cmd.Parameters.AddWithValue("QueryEmbedding", new Vector(queryEmbedding));

        var seedEntities = new List<Guid>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            seedEntities.Add(reader.GetGuid(0));
        }
        await reader.CloseAsync();

        if (seedEntities.Count == 0)
        {
            return new GraphMatchResult(new List<MemoryScore>(), new List<Guid>());
        }

        // Traverse graph from seed entities (2 hops)
        var traversedEntities = new HashSet<Guid>(seedEntities);
        var frontier = new HashSet<Guid>(seedEntities);

        for (int hop = 0; hop < 2; hop++)
        {
            const string traverseSql = """

                                                       SELECT DISTINCT
                                                           CASE WHEN source_entity_id = ANY(@Frontier) THEN target_entity_id
                                                                ELSE source_entity_id END as entity_id
                                                       FROM entity_relationships
                                                       WHERE source_entity_id = ANY(@Frontier) OR target_entity_id = ANY(@Frontier)
                                       """;

            var newEntities = await connection.QueryAsync<Guid>(
                traverseSql,
                new { Frontier = frontier.ToArray() });

            frontier = newEntities.Where(e => !traversedEntities.Contains(e)).ToHashSet();
            foreach (var e in frontier)
            {
                traversedEntities.Add(e);
            }

            if (frontier.Count == 0) break;
        }

        // Get memories linked to traversed entities
        const string getMemoriesSql = """

                                                  SELECT DISTINCT m.id, COUNT(DISTINCT me.entity_id)::float / @TotalEntities as score
                                                  FROM memories m
                                                  JOIN memory_entities me ON m.id = me.memory_id
                                                  WHERE me.entity_id = ANY(@Entities)
                                                  GROUP BY m.id
                                                  ORDER BY score DESC
                                                  LIMIT @Limit
                                      """;

        var memoriesFromGraph = await connection.QueryAsync<(Guid id, float score)>(
            getMemoriesSql,
            new
            {
                Entities = traversedEntities.ToArray(),
                TotalEntities = (float)traversedEntities.Count,
                Limit = limit
            });

        var results = memoriesFromGraph
            .Select(m => new MemoryScore(m.id, 0f, 0f, m.score, 0f, 0f, new List<string>()))
            .ToList();

        return new GraphMatchResult(results, traversedEntities.ToList());
    }

    private async Task<Dictionary<Guid, float>> GetTemporalScoresAsync(
        List<Guid> memoryIds,
        DateTime anchor,
        CancellationToken cancellationToken)
    {
        if (memoryIds.Count == 0)
        {
            return new Dictionary<Guid, float>();
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        // Exponential decay: score = e^(-decay_rate * days_elapsed)
        // With half-life of 30 days: decay_rate = ln(2) / 30
        const string sql = """

                                       SELECT id,
                                              EXP(-0.0231 * EXTRACT(EPOCH FROM (@Anchor - created_at)) / 86400.0)::float as score
                                       FROM memories
                                       WHERE id = ANY(@MemoryIds)
                           """;

        var rows = await connection.QueryAsync<(Guid id, float score)>(
            sql,
            new { MemoryIds = memoryIds.ToArray(), Anchor = anchor });

        return rows.ToDictionary(r => r.id, r => Math.Max(0f, Math.Min(1f, r.score)));
    }

    private async Task<Dictionary<Guid, float>> GetConfidenceScoresAsync(
        List<Guid> memoryIds,
        CancellationToken cancellationToken)
    {
        if (memoryIds.Count == 0)
        {
            return new Dictionary<Guid, float>();
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        // Try memory_projections first (event-sourced), fallback to default confidence
        const string sql = """

                                       SELECT m.id,
                                              COALESCE(mp.confidence_score, 1.0)::float as confidence
                                       FROM memories m
                                       LEFT JOIN memory_projections mp ON m.id = mp.memory_id
                                       WHERE m.id = ANY(@MemoryIds)
                           """;

        var rows = await connection.QueryAsync<(Guid id, float confidence)>(
            sql,
            new { MemoryIds = memoryIds.ToArray() });

        return rows.ToDictionary(r => r.id, r => r.confidence);
    }

    private Dictionary<Guid, CombinedScore> CombineScores(
        List<MemoryScore> semanticResults,
        List<MemoryScore> symbolicResults,
        List<MemoryScore> graphResults,
        Dictionary<Guid, float> temporalScores,
        Dictionary<Guid, float> confidenceScores,
        RetrievalScoreBreakdown weights)
    {
        var combined = new Dictionary<Guid, CombinedScore>();

        void UpdateScore(Guid id, float semantic = 0, float symbolic = 0, float graph = 0, List<string>? rules = null)
        {
            if (!combined.TryGetValue(id, out var existing))
            {
                existing = new CombinedScore(0, 0, 0, 0, 0, 0, new List<string>());
            }

            var newRules = existing.MatchedRules.ToList();
            if (rules != null)
            {
                newRules.AddRange(rules);
            }

            combined[id] = existing with
            {
                SemanticScore = Math.Max(existing.SemanticScore, semantic),
                SymbolicScore = Math.Max(existing.SymbolicScore, symbolic),
                GraphScore = Math.Max(existing.GraphScore, graph),
                MatchedRules = newRules.Distinct().ToList()
            };
        }

        foreach (var r in semanticResults)
        {
            UpdateScore(r.MemoryId, semantic: r.SemanticScore);
        }

        foreach (var r in symbolicResults)
        {
            UpdateScore(r.MemoryId, symbolic: r.SymbolicScore, rules: r.MatchedRules);
        }

        foreach (var r in graphResults)
        {
            UpdateScore(r.MemoryId, graph: r.GraphScore);
        }

        // Apply temporal and confidence scores, compute final
        foreach (var id in combined.Keys.ToList())
        {
            var score = combined[id];
            var temporal = temporalScores.GetValueOrDefault(id, 0.5f);
            var confidence = confidenceScores.GetValueOrDefault(id, 1.0f);

            var final = score.SemanticScore * weights.SemanticWeight
                      + score.SymbolicScore * weights.SymbolicWeight
                      + score.GraphScore * weights.GraphWeight
                      + temporal * weights.TemporalWeight
                      + confidence * weights.ConfidenceWeight;

            combined[id] = score with
            {
                TemporalScore = temporal,
                ConfidenceScore = confidence,
                FinalScore = final
            };
        }

        return combined;
    }

    private async Task<List<MemoryRecord>> GetMemoriesWithContentAsync(
        List<Guid> memoryIds,
        CancellationToken cancellationToken)
    {
        if (memoryIds.Count == 0)
        {
            return new List<MemoryRecord>();
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        const string sql = """

                                       SELECT id, content, created_at
                                       FROM memories
                                       WHERE id = ANY(@MemoryIds)
                           """;

        var rows = await connection.QueryAsync<MemoryRecord>(sql, new { MemoryIds = memoryIds.ToArray() });
        return rows.ToList();
    }

    public async Task<IReadOnlyList<SymbolicRule>> GetActiveRulesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        const string sql = """

                                       SELECT rule_id, rule_name, rule_type, condition_expression,
                                              priority, weight, is_active, created_at, updated_at, metadata::text
                                       FROM symbolic_rules
                                       WHERE is_active = TRUE
                                       ORDER BY priority DESC
                           """;

        var rows = await connection.QueryAsync<RuleRow>(sql);

        return rows.Select(r => new SymbolicRule(
            r.rule_id,
            r.rule_name,
            r.rule_type,
            r.condition_expression,
            r.priority,
            r.weight,
            r.is_active,
            r.created_at,
            r.updated_at,
            string.IsNullOrEmpty(r.metadata)
                ? null
                : JsonSerializer.Deserialize<Dictionary<string, object>>(r.metadata))).ToList();
    }

    public async Task AddRuleAsync(SymbolicRule rule, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        const string sql = """

                                       INSERT INTO symbolic_rules (
                                           rule_id, rule_name, rule_type, condition_expression,
                                           priority, weight, is_active, metadata
                                       ) VALUES (
                                           @RuleId, @RuleName, @RuleType, @ConditionExpression,
                                           @Priority, @Weight, @IsActive, @Metadata::jsonb
                                       )
                                       ON CONFLICT (rule_name) DO UPDATE SET
                                           rule_type = EXCLUDED.rule_type,
                                           condition_expression = EXCLUDED.condition_expression,
                                           priority = EXCLUDED.priority,
                                           weight = EXCLUDED.weight,
                                           is_active = EXCLUDED.is_active,
                                           metadata = EXCLUDED.metadata,
                                           updated_at = NOW()
                           """;

        await connection.ExecuteAsync(sql, new
        {
            rule.RuleId,
            rule.RuleName,
            rule.RuleType,
            rule.ConditionExpression,
            rule.Priority,
            rule.Weight,
            rule.IsActive,
            Metadata = rule.Metadata != null ? JsonSerializer.Serialize(rule.Metadata) : "{}"
        });

        logger.LogInformation("Added/updated symbolic rule: {RuleName}", rule.RuleName);
    }

    public async Task<bool> RemoveRuleAsync(Guid ruleId, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        const string sql = @"DELETE FROM symbolic_rules WHERE rule_id = @RuleId";

        var deleted = await connection.ExecuteAsync(sql, new { RuleId = ruleId });
        return deleted > 0;
    }

    public async Task<bool> UpdateRuleAsync(SymbolicRule rule, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        const string sql = """

                                       UPDATE symbolic_rules SET
                                           rule_name = @RuleName,
                                           rule_type = @RuleType,
                                           condition_expression = @ConditionExpression,
                                           priority = @Priority,
                                           weight = @Weight,
                                           is_active = @IsActive,
                                           metadata = @Metadata::jsonb,
                                           updated_at = NOW()
                                       WHERE rule_id = @RuleId
                           """;

        var updated = await connection.ExecuteAsync(sql, new
        {
            rule.RuleId,
            rule.RuleName,
            rule.RuleType,
            rule.ConditionExpression,
            rule.Priority,
            rule.Weight,
            rule.IsActive,
            Metadata = rule.Metadata != null ? JsonSerializer.Serialize(rule.Metadata) : "{}"
        });

        return updated > 0;
    }

    private record MemoryScore(
        Guid MemoryId,
        float SemanticScore,
        float SymbolicScore,
        float GraphScore,
        float TemporalScore,
        float ConfidenceScore,
        List<string> MatchedRules);

    private record CombinedScore(
        float SemanticScore,
        float SymbolicScore,
        float GraphScore,
        float TemporalScore,
        float ConfidenceScore,
        float FinalScore,
        List<string> MatchedRules);

    private record SymbolicMatchResult(List<MemoryScore> Memories, List<string> AppliedRules);
    private record GraphMatchResult(List<MemoryScore> Memories, List<Guid> TraversedEntities);

    private record MemoryRecord(Guid Id, string Content, DateTime CreatedAt);

    private record RuleRow(
        Guid rule_id,
        string rule_name,
        string rule_type,
        string condition_expression,
        int priority,
        float weight,
        bool is_active,
        DateTime created_at,
        DateTime updated_at,
        string? metadata);
}
