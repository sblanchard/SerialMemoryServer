using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using Pgvector;
using SerialMemory.Core.Interfaces;
using SerialMemory.EventSourcing.Aggregates;
using SerialMemory.EventSourcing.Store;

namespace SerialMemory.Mcp.Tools;

/// <summary>
/// MCP tool handlers for memory safety and integrity operations.
/// Detects contradictions, hallucinations, integrity failures, and causal loops.
/// </summary>
public sealed class MemorySafetyTools
{
    private readonly IEventStore _eventStore;
    private readonly IEmbeddingService _embeddingService;
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger _logger;

    public MemorySafetyTools(
        IEventStore eventStore,
        IEmbeddingService embeddingService,
        string connectionString,
        ILogger logger)
    {
        _eventStore = eventStore;
        _embeddingService = embeddingService;
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        builder.UseVector();
        _dataSource = builder.Build();
        _logger = logger;
    }

    /// <summary>
    /// detect_contradictions - Find memories that contradict each other.
    /// </summary>
    public async Task<object> HandleDetectContradictions(JsonNode? arguments)
    {
        var memoryIdStr = arguments?["memory_id"]?.GetValue<string>()?.Trim();
        Guid? targetMemoryId = null;
        if (!string.IsNullOrEmpty(memoryIdStr) && Guid.TryParse(memoryIdStr, out var mid))
            targetMemoryId = mid;

        var threshold = Math.Clamp(arguments?["similarity_threshold"]?.GetValue<float>() ?? 0.85f, 0.5f, 0.99f);
        var limit = Math.Clamp(arguments?["limit"]?.GetValue<int>() ?? 20, 1, 100);
        var autoFlag = arguments?["auto_flag"]?.GetValue<bool>() ?? false;

        await using var conn = await _dataSource.OpenConnectionAsync();

        var contradictions = new List<ContradictionResult>();

        if (targetMemoryId.HasValue)
        {
            // Find contradictions for specific memory
            var targetEvents = await _eventStore.ReadStreamAsync(targetMemoryId.Value);
            if (targetEvents.Count == 0)
                return CreateErrorResponse($"Memory {targetMemoryId} not found");

            var targetAgg = MemoryAggregate.FromEvents(targetEvents);

            // Get embedding and find similar memories
            var targetEmbedding = targetAgg.Embedding;

            var similar = await conn.QueryAsync<dynamic>(@"
                SELECT
                    memory_id,
                    content,
                    embedding <=> @Embedding::vector AS distance,
                    1 - (embedding <=> @Embedding::vector) AS similarity
                FROM memory_projections
                WHERE is_active = TRUE
                  AND memory_id != @TargetId
                  AND 1 - (embedding <=> @Embedding::vector) >= @Threshold
                ORDER BY distance
                LIMIT @Limit",
                new
                {
                    Embedding = new Vector(targetEmbedding),
                    TargetId = targetMemoryId.Value,
                    Threshold = threshold,
                    Limit = limit
                });

            foreach (var s in similar)
            {
                // Check for semantic contradiction (high similarity but potentially conflicting content)
                var score = await CalculateContradictionScore(targetAgg.Content, (string)s.content);

                if (score > 0.5f)
                {
                    contradictions.Add(new ContradictionResult
                    {
                        MemoryAId = targetMemoryId.Value,
                        MemoryBId = s.memory_id,
                        SimilarityScore = (float)s.similarity,
                        ContradictionScore = score,
                        DetectionMethod = "semantic_similarity"
                    });
                }
            }
        }
        else
        {
            // Batch contradiction detection for all active memories
            var memoryPairs = await conn.QueryAsync<dynamic>(@"
                WITH memory_pairs AS (
                    SELECT
                        a.memory_id AS memory_a_id,
                        b.memory_id AS memory_b_id,
                        a.content AS content_a,
                        b.content AS content_b,
                        1 - (a.embedding <=> b.embedding) AS similarity
                    FROM memory_projections a
                    CROSS JOIN memory_projections b
                    WHERE a.memory_id < b.memory_id
                      AND a.is_active = TRUE
                      AND b.is_active = TRUE
                      AND a.embedding IS NOT NULL
                      AND b.embedding IS NOT NULL
                )
                SELECT * FROM memory_pairs
                WHERE similarity >= @Threshold
                ORDER BY similarity DESC
                LIMIT @Limit",
                new { Threshold = threshold, Limit = limit });

            foreach (var pair in memoryPairs)
            {
                var score = await CalculateContradictionScore((string)pair.content_a, (string)pair.content_b);

                if (score > 0.5f)
                {
                    contradictions.Add(new ContradictionResult
                    {
                        MemoryAId = pair.memory_a_id,
                        MemoryBId = pair.memory_b_id,
                        SimilarityScore = (float)pair.similarity,
                        ContradictionScore = score,
                        DetectionMethod = "batch_similarity"
                    });
                }
            }
        }

        // Optionally flag contradictions
        if (autoFlag && contradictions.Count > 0)
        {
            foreach (var c in contradictions)
            {
                await conn.ExecuteAsync(@"
                    INSERT INTO contradiction_reports
                        (memory_a_id, memory_b_id, contradiction_score, detection_method, resolution_status)
                    VALUES
                        (@MemoryAId, @MemoryBId, @Score, @Method, 'detected')
                    ON CONFLICT (memory_a_id, memory_b_id) DO UPDATE
                    SET contradiction_score = GREATEST(contradiction_reports.contradiction_score, @Score)",
                    new
                    {
                        MemoryAId = c.MemoryAId,
                        MemoryBId = c.MemoryBId,
                        Score = c.ContradictionScore,
                        Method = c.DetectionMethod
                    });
            }
        }

        // Build response
        var responseLines = new List<string>
        {
            targetMemoryId.HasValue
                ? $"## Contradiction Detection for: {targetMemoryId}"
                : "## Batch Contradiction Detection",
            $"Similarity Threshold: {threshold:F2}",
            $"Potential Contradictions Found: {contradictions.Count}",
            autoFlag ? "Auto-flagging: Enabled" : "Auto-flagging: Disabled",
            ""
        };

        if (contradictions.Count == 0)
        {
            responseLines.Add("No contradictions detected.");
        }
        else
        {
            foreach (var c in contradictions.OrderByDescending(x => x.ContradictionScore))
            {
                responseLines.Add($"### Contradiction (score: {c.ContradictionScore:F3})");
                responseLines.Add($"- Memory A: {c.MemoryAId}");
                responseLines.Add($"- Memory B: {c.MemoryBId}");
                responseLines.Add($"- Similarity: {c.SimilarityScore:F3}");
                responseLines.Add($"- Detection: {c.DetectionMethod}");
                responseLines.Add("");
            }
        }

        _logger.LogInformation("Detected {Count} potential contradictions", contradictions.Count);

        return CreateTextResponse(string.Join("\n", responseLines));
    }

    /// <summary>
    /// detect_hallucinations - Flag potential hallucinations in memories.
    /// </summary>
    public async Task<object> HandleDetectHallucinations(JsonNode? arguments)
    {
        var memoryIdStr = arguments?["memory_id"]?.GetValue<string>()?.Trim();
        Guid? targetMemoryId = null;
        if (!string.IsNullOrEmpty(memoryIdStr) && Guid.TryParse(memoryIdStr, out var mid))
            targetMemoryId = mid;

        var confidenceThreshold = Math.Clamp(arguments?["confidence_threshold"]?.GetValue<float>() ?? 0.3f, 0f, 1f);
        var limit = Math.Clamp(arguments?["limit"]?.GetValue<int>() ?? 20, 1, 100);
        var autoFlag = arguments?["auto_flag"]?.GetValue<bool>() ?? false;

        await using var conn = await _dataSource.OpenConnectionAsync();

        var hallucinations = new List<HallucinationResult>();

        IEnumerable<dynamic> candidates;

        if (targetMemoryId.HasValue)
        {
            candidates = await conn.QueryAsync<dynamic>(@"
                SELECT memory_id, content, confidence_score, recall_count, ignore_count, created_at
                FROM memory_projections
                WHERE memory_id = @MemoryId AND is_active = TRUE",
                new { MemoryId = targetMemoryId.Value });
        }
        else
        {
            // Find memories with hallucination indicators:
            // - Low confidence
            // - High ignore rate
            // - No validations
            // - Isolated (no entity links)
            candidates = await conn.QueryAsync<dynamic>(@"
                SELECT
                    m.memory_id,
                    m.content,
                    m.confidence_score,
                    m.recall_count,
                    m.ignore_count,
                    m.created_at,
                    COALESCE(array_length(m.validated_by, 1), 0) AS validation_count,
                    (SELECT COUNT(*) FROM memory_entity_links WHERE memory_id = m.memory_id) AS entity_count
                FROM memory_projections m
                WHERE m.is_active = TRUE
                  AND (
                    m.confidence_score < @ConfidenceThreshold
                    OR (m.ignore_count > m.recall_count AND m.recall_count > 0)
                    OR (COALESCE(array_length(m.validated_by, 1), 0) = 0 AND m.recall_count = 0)
                  )
                ORDER BY m.confidence_score ASC
                LIMIT @Limit",
                new { ConfidenceThreshold = confidenceThreshold, Limit = limit });
        }

        foreach (var c in candidates)
        {
            var reasons = new List<string>();
            float hallucinationScore = 0;

            // Check confidence
            if ((decimal)c.confidence_score < (decimal)confidenceThreshold)
            {
                reasons.Add($"Low confidence: {c.confidence_score:F3}");
                hallucinationScore += 0.3f;
            }

            // Check ignore rate
            var recallCount = (int)(c.recall_count ?? 0);
            var ignoreCount = (int)(c.ignore_count ?? 0);
            if (ignoreCount > recallCount && recallCount > 0)
            {
                var ignoreRate = (float)ignoreCount / (recallCount + ignoreCount);
                reasons.Add($"High ignore rate: {ignoreRate:P0}");
                hallucinationScore += 0.3f * ignoreRate;
            }

            // Check validations
            var validationCount = 0;
            if (c is IDictionary<string, object> dict && dict.ContainsKey("validation_count"))
                validationCount = Convert.ToInt32(dict["validation_count"]);

            if (validationCount == 0 && recallCount == 0)
            {
                reasons.Add("Never validated or recalled");
                hallucinationScore += 0.2f;
            }

            // Check entity isolation
            var entityCount = 0;
            if (c is IDictionary<string, object> dict2 && dict2.ContainsKey("entity_count"))
                entityCount = Convert.ToInt32(dict2["entity_count"]);

            if (entityCount == 0)
            {
                reasons.Add("No entity links (isolated)");
                hallucinationScore += 0.2f;
            }

            if (reasons.Count > 0)
            {
                hallucinations.Add(new HallucinationResult
                {
                    MemoryId = c.memory_id,
                    HallucinationScore = Math.Min(hallucinationScore, 1.0f),
                    Reasons = reasons,
                    Content = ((string)c.content).Substring(0, Math.Min(100, ((string)c.content).Length))
                });
            }
        }

        // Auto-flag if requested
        if (autoFlag && hallucinations.Count > 0)
        {
            foreach (var h in hallucinations)
            {
                await conn.ExecuteAsync(@"
                    INSERT INTO hallucination_reports
                        (memory_id, detection_method, confidence_score, reason, resolution_status)
                    VALUES
                        (@MemoryId, 'heuristic', @Score, @Reason, 'detected')",
                    new
                    {
                        MemoryId = h.MemoryId,
                        Score = h.HallucinationScore,
                        Reason = string.Join("; ", h.Reasons)
                    });
            }
        }

        // Build response
        var responseLines = new List<string>
        {
            targetMemoryId.HasValue
                ? $"## Hallucination Detection for: {targetMemoryId}"
                : "## Batch Hallucination Detection",
            $"Confidence Threshold: {confidenceThreshold:F2}",
            $"Potential Hallucinations Found: {hallucinations.Count}",
            autoFlag ? "Auto-flagging: Enabled" : "Auto-flagging: Disabled",
            ""
        };

        if (hallucinations.Count == 0)
        {
            responseLines.Add("No potential hallucinations detected.");
        }
        else
        {
            foreach (var h in hallucinations.OrderByDescending(x => x.HallucinationScore))
            {
                responseLines.Add($"### Memory: {h.MemoryId}");
                responseLines.Add($"- Hallucination Score: {h.HallucinationScore:F3}");
                responseLines.Add($"- Reasons:");
                foreach (var r in h.Reasons)
                    responseLines.Add($"  - {r}");
                responseLines.Add($"- Content: {h.Content}...");
                responseLines.Add("");
            }
        }

        _logger.LogInformation("Detected {Count} potential hallucinations", hallucinations.Count);

        return CreateTextResponse(string.Join("\n", responseLines));
    }

    /// <summary>
    /// verify_memory_integrity - Verify content hash integrity for memories.
    /// </summary>
    public async Task<object> HandleVerifyIntegrity(JsonNode? arguments)
    {
        var memoryIdStr = arguments?["memory_id"]?.GetValue<string>()?.Trim();
        Guid? targetMemoryId = null;
        if (!string.IsNullOrEmpty(memoryIdStr) && Guid.TryParse(memoryIdStr, out var mid))
            targetMemoryId = mid;

        var limit = Math.Clamp(arguments?["limit"]?.GetValue<int>() ?? 100, 1, 1000);
        var fixCorrupted = arguments?["fix_corrupted"]?.GetValue<bool>() ?? false;

        await using var conn = await _dataSource.OpenConnectionAsync();

        var failures = new List<IntegrityFailure>();

        IEnumerable<dynamic> memories;

        if (targetMemoryId.HasValue)
        {
            memories = await conn.QueryAsync<dynamic>(@"
                SELECT memory_id, content, content_hash
                FROM memory_projections
                WHERE memory_id = @MemoryId",
                new { MemoryId = targetMemoryId.Value });
        }
        else
        {
            memories = await conn.QueryAsync<dynamic>(@"
                SELECT memory_id, content, content_hash
                FROM memory_projections
                WHERE is_active = TRUE
                ORDER BY created_at DESC
                LIMIT @Limit",
                new { Limit = limit });
        }

        var checkedCount = 0;
        var passedCount = 0;

        foreach (var m in memories)
        {
            checkedCount++;
            var content = (string)m.content;
            var storedHash = (string)m.content_hash;
            var computedHash = ComputeHash(content);

            if (storedHash != computedHash)
            {
                failures.Add(new IntegrityFailure
                {
                    MemoryId = m.memory_id,
                    ExpectedHash = storedHash,
                    ActualHash = computedHash
                });

                // Log to database
                await conn.ExecuteAsync(@"
                    INSERT INTO integrity_check_results
                        (memory_id, check_type, passed, expected_hash, actual_hash)
                    VALUES
                        (@MemoryId, 'content_hash', FALSE, @Expected, @Actual)",
                    new
                    {
                        MemoryId = m.memory_id,
                        Expected = storedHash,
                        Actual = computedHash
                    });

                // Optionally fix
                if (fixCorrupted)
                {
                    await conn.ExecuteAsync(@"
                        UPDATE memory_projections
                        SET content_hash = @NewHash
                        WHERE memory_id = @MemoryId",
                        new { MemoryId = m.memory_id, NewHash = computedHash });
                }
            }
            else
            {
                passedCount++;
            }
        }

        // Build response
        var responseLines = new List<string>
        {
            targetMemoryId.HasValue
                ? $"## Integrity Check for: {targetMemoryId}"
                : "## Batch Integrity Check",
            $"Memories Checked: {checkedCount}",
            $"Passed: {passedCount}",
            $"Failed: {failures.Count}",
            fixCorrupted && failures.Count > 0 ? "Auto-fix: Applied" : "Auto-fix: Disabled",
            ""
        };

        if (failures.Count == 0)
        {
            responseLines.Add("All integrity checks passed.");
        }
        else
        {
            responseLines.Add("### Integrity Failures:");
            foreach (var f in failures)
            {
                responseLines.Add($"- **{f.MemoryId}**");
                responseLines.Add($"  - Expected: {f.ExpectedHash.Substring(0, 16)}...");
                responseLines.Add($"  - Actual: {f.ActualHash.Substring(0, 16)}...");
            }
        }

        _logger.LogInformation("Integrity check: {Passed}/{Total} passed, {Failed} failures",
            passedCount, checkedCount, failures.Count);

        return CreateTextResponse(string.Join("\n", responseLines));
    }

    /// <summary>
    /// scan_loops - Detect cycles in causal parent relationships.
    /// </summary>
    public async Task<object> HandleScanLoops(JsonNode? arguments)
    {
        var maxDepth = Math.Clamp(arguments?["max_depth"]?.GetValue<int>() ?? 10, 1, 20);
        var limit = Math.Clamp(arguments?["limit"]?.GetValue<int>() ?? 50, 1, 200);

        await using var conn = await _dataSource.OpenConnectionAsync();

        var loops = new List<LoopResult>();

        // Get all memories with causal parents
        var memoriesWithParents = await conn.QueryAsync<dynamic>(@"
            SELECT memory_id, causal_parents
            FROM memory_projections
            WHERE causal_parents IS NOT NULL
              AND array_length(causal_parents, 1) > 0
            LIMIT @Limit",
            new { Limit = limit * 10 });

        var parentMap = new Dictionary<Guid, Guid[]>();
        foreach (var m in memoriesWithParents)
        {
            parentMap[(Guid)m.memory_id] = ((Guid[])m.causal_parents) ?? Array.Empty<Guid>();
        }

        // DFS for cycle detection
        var visited = new HashSet<Guid>();
        var recursionStack = new HashSet<Guid>();
        var cyclePaths = new List<List<Guid>>();

        foreach (var memoryId in parentMap.Keys)
        {
            if (loops.Count >= limit) break;

            var path = new List<Guid>();
            if (DetectCycle(memoryId, parentMap, visited, recursionStack, path, maxDepth))
            {
                // Found a cycle
                loops.Add(new LoopResult
                {
                    CycleMemoryIds = path.ToArray(),
                    CycleLength = path.Count
                });

                // Log to database
                await conn.ExecuteAsync(@"
                    INSERT INTO loop_detection_results
                        (cycle_memory_ids, cycle_length, detection_method, severity)
                    VALUES
                        (@CycleIds, @Length, 'dfs', 'warning')",
                    new
                    {
                        CycleIds = path.ToArray(),
                        Length = path.Count
                    });
            }

            visited.Clear();
            recursionStack.Clear();
        }

        // Build response
        var responseLines = new List<string>
        {
            "## Loop Detection (Causal Parent Cycles)",
            $"Max Depth: {maxDepth}",
            $"Memories Scanned: {parentMap.Count}",
            $"Loops Found: {loops.Count}",
            ""
        };

        if (loops.Count == 0)
        {
            responseLines.Add("No causal loops detected. Graph is acyclic.");
        }
        else
        {
            responseLines.Add("### Detected Cycles:");
            foreach (var loop in loops)
            {
                responseLines.Add($"- **Cycle length {loop.CycleLength}**:");
                responseLines.Add($"  {string.Join(" -> ", loop.CycleMemoryIds)} -> [cycle]");
            }

            responseLines.Add("");
            responseLines.Add("**Warning**: Causal loops can cause infinite recursion in lineage traversal.");
            responseLines.Add("Consider breaking cycles by invalidating one memory in each loop.");
        }

        _logger.LogInformation("Loop scan: {LoopCount} cycles detected", loops.Count);

        return CreateTextResponse(string.Join("\n", responseLines));
    }

    private bool DetectCycle(
        Guid current,
        Dictionary<Guid, Guid[]> parentMap,
        HashSet<Guid> visited,
        HashSet<Guid> recursionStack,
        List<Guid> path,
        int maxDepth)
    {
        if (path.Count >= maxDepth)
            return false;

        if (recursionStack.Contains(current))
        {
            path.Add(current);
            return true;
        }

        if (visited.Contains(current))
            return false;

        visited.Add(current);
        recursionStack.Add(current);
        path.Add(current);

        if (parentMap.TryGetValue(current, out var parents))
        {
            foreach (var parent in parents)
            {
                if (DetectCycle(parent, parentMap, visited, recursionStack, path, maxDepth))
                    return true;
            }
        }

        path.RemoveAt(path.Count - 1);
        recursionStack.Remove(current);
        return false;
    }

    private async Task<float> CalculateContradictionScore(string contentA, string contentB)
    {
        // Simple heuristic: check for negation patterns
        var negationWords = new[] { "not", "never", "no", "false", "incorrect", "wrong", "isn't", "aren't", "wasn't", "weren't", "don't", "doesn't", "didn't" };

        var wordsA = contentA.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var wordsB = contentB.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var negationCountA = wordsA.Count(w => negationWords.Contains(w));
        var negationCountB = wordsB.Count(w => negationWords.Contains(w));

        // If one has negations and the other doesn't, might be contradictory
        if ((negationCountA > 0) != (negationCountB > 0))
            return 0.6f;

        // Check for opposite sentiment indicators
        var positiveA = wordsA.Count(w => new[] { "yes", "true", "correct", "right", "good", "success" }.Contains(w));
        var positiveB = wordsB.Count(w => new[] { "yes", "true", "correct", "right", "good", "success" }.Contains(w));
        var negativeA = wordsA.Count(w => new[] { "no", "false", "incorrect", "wrong", "bad", "failure" }.Contains(w));
        var negativeB = wordsB.Count(w => new[] { "no", "false", "incorrect", "wrong", "bad", "failure" }.Contains(w));

        if ((positiveA > negativeA && negativeB > positiveB) ||
            (negativeA > positiveA && positiveB > negativeB))
            return 0.7f;

        return 0.3f; // Low base score for similar content
    }

    private static string ComputeHash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexStringLower(bytes);
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

    private sealed class ContradictionResult
    {
        public Guid MemoryAId { get; set; }
        public Guid MemoryBId { get; set; }
        public float SimilarityScore { get; set; }
        public float ContradictionScore { get; set; }
        public string DetectionMethod { get; set; } = "";
    }

    private sealed class HallucinationResult
    {
        public Guid MemoryId { get; set; }
        public float HallucinationScore { get; set; }
        public List<string> Reasons { get; set; } = [];
        public string Content { get; set; } = "";
    }

    private sealed class IntegrityFailure
    {
        public Guid MemoryId { get; set; }
        public string ExpectedHash { get; set; } = "";
        public string ActualHash { get; set; } = "";
    }

    private sealed class LoopResult
    {
        public Guid[] CycleMemoryIds { get; set; } = [];
        public int CycleLength { get; set; }
    }
}
