using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using SerialMemory.Core.Interfaces;
using SerialMemory.Core.Models;

namespace SerialMemory.Infrastructure;

/// <summary>
/// Security pipeline implementation for memory integrity validation
/// </summary>
public class SecurityPipeline(
    ISecurityEventStore eventStore,
    IKnowledgeGraphStore knowledgeGraphStore,
    IEmbeddingService? embeddingService,
    ILogger<SecurityPipeline> logger)
    : ISecurityPipeline
{
    /// <summary>
    /// Compute SHA-256 hash of content
    /// </summary>
    public static string ComputeContentHash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexStringLower(bytes);
    }

    public async Task<HashValidationResult> ValidateOnReadAsync(Memory memory, CancellationToken ct = default)
    {
        var actualHash = ComputeContentHash(memory.Content);

        // Check if memory has metadata with stored hash
        var expectedHash = memory.Metadata?.TryGetValue("content_hash", out var hash) == true
            ? hash?.ToString()
            : null;

        var result = new HashValidationResult
        {
            MemoryId = memory.Id,
            ActualHash = actualHash,
            ExpectedHash = expectedHash,
            Content = memory.Content
        };

        if (expectedHash == null)
        {
            // No stored hash - this is a pass but log as info
            result.IsValid = true;
            await eventStore.LogEventAsync(new IntegrityEvent
            {
                EventType = IntegrityEventType.MemoryReadValidated,
                Severity = IntegritySeverity.Info,
                MemoryId = memory.Id,
                Message = "Memory read validated (no stored hash)",
                ActualHash = actualHash,
                DetectedBy = "SecurityPipeline.ValidateOnRead"
            }, ct);
        }
        else if (expectedHash == actualHash)
        {
            result.IsValid = true;
            await eventStore.LogEventAsync(new IntegrityEvent
            {
                EventType = IntegrityEventType.HashValidationPass,
                Severity = IntegritySeverity.Info,
                MemoryId = memory.Id,
                Message = "Hash validation passed",
                ExpectedHash = expectedHash,
                ActualHash = actualHash,
                DetectedBy = "SecurityPipeline.ValidateOnRead"
            }, ct);
        }
        else
        {
            result.IsValid = false;
            logger.LogWarning("Hash mismatch detected for memory {MemoryId}: expected {Expected}, got {Actual}",
                memory.Id, expectedHash, actualHash);

            await eventStore.LogEventAsync(new IntegrityEvent
            {
                EventType = IntegrityEventType.HashMismatch,
                Severity = IntegritySeverity.Critical,
                MemoryId = memory.Id,
                Message = $"Content hash mismatch detected - possible tampering",
                ExpectedHash = expectedHash,
                ActualHash = actualHash,
                DetectedBy = "SecurityPipeline.ValidateOnRead",
                Details = new Dictionary<string, object>
                {
                    ["content_preview"] = memory.Content.Length > 100 ? memory.Content[..100] + "..." : memory.Content
                }
            }, ct);
        }

        return result;
    }

    public async Task<HashValidationResult> ValidateOnWriteAsync(Memory memory, CancellationToken ct = default)
    {
        var contentHash = ComputeContentHash(memory.Content);

        var result = new HashValidationResult
        {
            MemoryId = memory.Id,
            IsValid = true,
            ActualHash = contentHash,
            ExpectedHash = contentHash,
            Content = memory.Content
        };

        // Store hash in metadata
        memory.Metadata ??= new Dictionary<string, object>();
        memory.Metadata["content_hash"] = contentHash;

        await eventStore.LogEventAsync(new IntegrityEvent
        {
            EventType = IntegrityEventType.MemoryWriteValidated,
            Severity = IntegritySeverity.Info,
            MemoryId = memory.Id,
            Message = "Memory write validated with hash",
            ActualHash = contentHash,
            DetectedBy = "SecurityPipeline.ValidateOnWrite"
        }, ct);

        return result;
    }

    public async Task<List<ContradictionResult>> DetectContradictionsAsync(Memory memory, IEnumerable<Memory> candidates, CancellationToken ct = default)
    {
        var contradictions = new List<ContradictionResult>();

        if (embeddingService == null || memory.Embedding == null || memory.Embedding.Length == 0)
        {
            return contradictions;
        }

        foreach (var candidate in candidates)
        {
            if (candidate.Id == memory.Id || candidate.Embedding == null || candidate.Embedding.Length == 0)
                continue;

            // Calculate cosine similarity
            var similarity = CosineSimilarity(memory.Embedding, candidate.Embedding);

            // High similarity but different content suggests potential contradiction
            // This is a simplified heuristic - real implementation would use semantic analysis
            if (similarity > 0.85f)
            {
                // Check if content is actually different (not just similar phrasing)
                var contentSimilarity = CalculateJaccardSimilarity(memory.Content, candidate.Content);

                if (contentSimilarity < 0.5f) // High embedding similarity but low content overlap
                {
                    var contradiction = new ContradictionResult
                    {
                        MemoryAId = memory.Id,
                        MemoryBId = candidate.Id,
                        Score = similarity,
                        DetectionMethod = "semantic",
                        ReasonSummary = $"High semantic similarity ({similarity:P0}) but low content overlap ({contentSimilarity:P0})"
                    };

                    contradictions.Add(contradiction);

                    await eventStore.LogEventAsync(new IntegrityEvent
                    {
                        EventType = IntegrityEventType.ContradictionDetected,
                        Severity = IntegritySeverity.Warning,
                        MemoryId = memory.Id,
                        Message = $"Potential contradiction detected with memory {candidate.Id}",
                        ContradictingMemoryIds = [candidate.Id],
                        ContradictionScore = similarity,
                        DetectedBy = "SecurityPipeline.DetectContradictions",
                        Details = new Dictionary<string, object>
                        {
                            ["detection_method"] = "semantic",
                            ["content_similarity"] = contentSimilarity,
                            ["embedding_similarity"] = similarity
                        }
                    }, ct);
                }
            }
        }

        return contradictions;
    }

    public async Task<LoopDetectionResult> DetectLoopsAsync(Guid memoryId, Guid[] causalParents, CancellationToken ct = default)
    {
        var result = new LoopDetectionResult
        {
            StartingMemoryId = memoryId,
            HasLoop = false
        };

        if (causalParents == null || causalParents.Length == 0)
        {
            return result;
        }

        // Use DFS to detect cycles
        var visited = new HashSet<Guid> { memoryId };
        var path = new List<Guid> { memoryId };

        foreach (var parentId in causalParents)
        {
            if (await DetectLoopDfs(parentId, visited, path, 0, 10, ct))
            {
                result.HasLoop = true;
                result.LoopPath = new List<Guid>(path);
                result.Depth = path.Count;

                await eventStore.LogEventAsync(new IntegrityEvent
                {
                    EventType = IntegrityEventType.CausalLoopDetected,
                    Severity = IntegritySeverity.Error,
                    MemoryId = memoryId,
                    Message = $"Causal loop detected with depth {result.Depth}",
                    LoopPath = result.LoopPath,
                    LoopDepth = result.Depth,
                    DetectedBy = "SecurityPipeline.DetectLoops",
                    Details = new Dictionary<string, object>
                    {
                        ["starting_memory"] = memoryId,
                        ["loop_path_ids"] = result.LoopPath.Select(id => id.ToString()).ToList()
                    }
                }, ct);

                break;
            }

            // Reset for next parent
            visited.Clear();
            visited.Add(memoryId);
            path.Clear();
            path.Add(memoryId);
        }

        return result;
    }

    private async Task<bool> DetectLoopDfs(Guid currentId, HashSet<Guid> visited, List<Guid> path, int depth, int maxDepth, CancellationToken ct)
    {
        if (depth > maxDepth)
            return false;

        if (visited.Contains(currentId))
        {
            path.Add(currentId);
            return true; // Loop found
        }

        visited.Add(currentId);
        path.Add(currentId);

        // Get memory to check its causal parents
        var memory = await knowledgeGraphStore.GetMemoryByIdAsync(currentId, ct);
        if (memory?.Metadata?.TryGetValue("causal_parents", out var parentsObj) == true)
        {
            Guid[]? parents = null;
            if (parentsObj is Guid[] guidArray)
                parents = guidArray;
            else if (parentsObj is List<object> objList)
                parents = objList.OfType<Guid>().ToArray();
            else if (parentsObj is string json && json.StartsWith('['))
            {
                try
                {
                    parents = System.Text.Json.JsonSerializer.Deserialize<Guid[]>(json);
                }
                catch { /* ignore parse errors */ }
            }

            if (parents != null)
            {
                foreach (var parentId in parents)
                {
                    if (await DetectLoopDfs(parentId, visited, path, depth + 1, maxDepth, ct))
                        return true;
                }
            }
        }

        path.RemoveAt(path.Count - 1);
        return false;
    }

    public async Task<SecurityScan> RunFullIntegrityScanAsync(CancellationToken ct = default)
    {
        var scan = new SecurityScan
        {
            ScanType = "full",
            TriggeredBy = "manual"
        };

        await eventStore.StartScanAsync(scan, ct);
        var eventsGenerated = 0;
        var issuesFound = 0;
        var criticalIssues = 0;

        try
        {
            // Run hash validation
            var hashResult = await RunHashValidationScanAsync(500, ct);
            eventsGenerated += hashResult.EventsGenerated;
            issuesFound += hashResult.IssuesFound;
            criticalIssues += hashResult.CriticalIssues;

            // Run contradiction detection
            var contradictionResult = await RunContradictionScanAsync(100, 0.85f, ct);
            eventsGenerated += contradictionResult.EventsGenerated;
            issuesFound += contradictionResult.IssuesFound;
            criticalIssues += contradictionResult.CriticalIssues;

            // Run loop detection
            var loopResult = await RunLoopDetectionScanAsync(10, ct);
            eventsGenerated += loopResult.EventsGenerated;
            issuesFound += loopResult.IssuesFound;
            criticalIssues += loopResult.CriticalIssues;

            await eventStore.CompleteScanAsync(scan.ScanId, eventsGenerated, issuesFound, criticalIssues, ct);

            scan.Status = "completed";
            scan.EventsGenerated = eventsGenerated;
            scan.IssuesFound = issuesFound;
            scan.CriticalIssues = criticalIssues;
            scan.CompletedAt = DateTimeOffset.UtcNow;
        }
        catch (Exception ex)
        {
            await eventStore.FailScanAsync(scan.ScanId, ex.Message, ct);
            scan.Status = "failed";
            scan.ErrorMessage = ex.Message;
            logger.LogError(ex, "Full integrity scan failed");
        }

        return scan;
    }

    public async Task<SecurityScan> RunHashValidationScanAsync(int batchSize = 100, CancellationToken ct = default)
    {
        var scan = new SecurityScan
        {
            ScanType = "hash_validation",
            TriggeredBy = "manual"
        };

        await eventStore.StartScanAsync(scan, ct);
        var eventsGenerated = 0;
        var issuesFound = 0;
        var criticalIssues = 0;

        try
        {
            var memories = await knowledgeGraphStore.GetAllMemoriesAsync(batchSize, 0, ct);
            scan.MemoriesScanned = memories.Count;

            foreach (var memory in memories)
            {
                var result = await ValidateOnReadAsync(memory, ct);
                eventsGenerated++;

                if (!result.IsValid)
                {
                    issuesFound++;
                    criticalIssues++;
                }
            }

            await eventStore.CompleteScanAsync(scan.ScanId, eventsGenerated, issuesFound, criticalIssues, ct);
            scan.Status = "completed";
            scan.EventsGenerated = eventsGenerated;
            scan.IssuesFound = issuesFound;
            scan.CriticalIssues = criticalIssues;
            scan.CompletedAt = DateTimeOffset.UtcNow;
        }
        catch (Exception ex)
        {
            await eventStore.FailScanAsync(scan.ScanId, ex.Message, ct);
            scan.Status = "failed";
            scan.ErrorMessage = ex.Message;
            logger.LogError(ex, "Hash validation scan failed");
        }

        return scan;
    }

    public async Task<SecurityScan> RunContradictionScanAsync(int batchSize = 50, float threshold = 0.85f, CancellationToken ct = default)
    {
        var scan = new SecurityScan
        {
            ScanType = "contradiction",
            TriggeredBy = "manual"
        };

        await eventStore.StartScanAsync(scan, ct);
        var eventsGenerated = 0;
        var issuesFound = 0;

        try
        {
            var memories = await knowledgeGraphStore.GetRecentMemoriesAsync(batchSize, ct);
            scan.MemoriesScanned = memories.Count;

            // Compare each memory against others
            foreach (var memory in memories)
            {
                var candidates = memories.Where(m => m.Id != memory.Id);
                var contradictions = await DetectContradictionsAsync(memory, candidates, ct);

                eventsGenerated += contradictions.Count > 0 ? contradictions.Count : 0;
                issuesFound += contradictions.Count;
            }

            await eventStore.CompleteScanAsync(scan.ScanId, eventsGenerated, issuesFound, 0, ct);
            scan.Status = "completed";
            scan.EventsGenerated = eventsGenerated;
            scan.IssuesFound = issuesFound;
            scan.CompletedAt = DateTimeOffset.UtcNow;
        }
        catch (Exception ex)
        {
            await eventStore.FailScanAsync(scan.ScanId, ex.Message, ct);
            scan.Status = "failed";
            scan.ErrorMessage = ex.Message;
            logger.LogError(ex, "Contradiction scan failed");
        }

        return scan;
    }

    public async Task<SecurityScan> RunLoopDetectionScanAsync(int maxDepth = 10, CancellationToken ct = default)
    {
        var scan = new SecurityScan
        {
            ScanType = "loop",
            TriggeredBy = "manual"
        };

        await eventStore.StartScanAsync(scan, ct);
        var eventsGenerated = 0;
        var issuesFound = 0;

        try
        {
            var memories = await knowledgeGraphStore.GetRecentMemoriesAsync(100, ct);
            scan.MemoriesScanned = memories.Count;

            foreach (var memory in memories)
            {
                // Extract causal parents from metadata
                Guid[]? causalParents = null;
                if (memory.Metadata?.TryGetValue("causal_parents", out var parentsObj) == true)
                {
                    if (parentsObj is Guid[] guidArray)
                        causalParents = guidArray;
                    else if (parentsObj is List<object> objList)
                        causalParents = objList.OfType<Guid>().ToArray();
                }

                if (causalParents != null && causalParents.Length > 0)
                {
                    var result = await DetectLoopsAsync(memory.Id, causalParents, ct);
                    if (result.HasLoop)
                    {
                        eventsGenerated++;
                        issuesFound++;
                    }
                }
            }

            await eventStore.CompleteScanAsync(scan.ScanId, eventsGenerated, issuesFound, issuesFound, ct);
            scan.Status = "completed";
            scan.EventsGenerated = eventsGenerated;
            scan.IssuesFound = issuesFound;
            scan.CriticalIssues = issuesFound;
            scan.CompletedAt = DateTimeOffset.UtcNow;
        }
        catch (Exception ex)
        {
            await eventStore.FailScanAsync(scan.ScanId, ex.Message, ct);
            scan.Status = "failed";
            scan.ErrorMessage = ex.Message;
            logger.LogError(ex, "Loop detection scan failed");
        }

        return scan;
    }

    // Helper methods
    private static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0;

        float dotProduct = 0, magA = 0, magB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dotProduct += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }

        var magnitude = MathF.Sqrt(magA) * MathF.Sqrt(magB);
        return magnitude > 0 ? dotProduct / magnitude : 0;
    }

    private static float CalculateJaccardSimilarity(string a, string b)
    {
        var wordsA = new HashSet<string>(a.ToLower().Split([' ', '\n', '\r', '\t', '.', ',', '!', '?'], StringSplitOptions.RemoveEmptyEntries));
        var wordsB = new HashSet<string>(b.ToLower().Split([' ', '\n', '\r', '\t', '.', ',', '!', '?'], StringSplitOptions.RemoveEmptyEntries));

        var intersection = wordsA.Intersect(wordsB).Count();
        var union = wordsA.Union(wordsB).Count();

        return union > 0 ? (float)intersection / union : 0;
    }
}
