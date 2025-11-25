using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using SerialMemory.Core.Interfaces;

namespace SerialMemory.Infrastructure.Reasoning;

/// <summary>
/// Dual-pass reasoning engine that uses ONNX for both draft generation and critique/repair.
/// Pass 1: Generate draft response based on retrieved memories
/// Pass 2: Critique and repair the draft using semantic analysis
/// </summary>
public sealed class DualPassReasoningEngine : IDualPassReasoning
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly IEmbeddingService _embeddingService;
    private readonly IHybridRetrievalEngine _retrievalEngine;
    private readonly ILogger<DualPassReasoningEngine> _logger;

    // Semantic similarity thresholds for reasoning
    private const float HighConfidenceThreshold = 0.85f;
    private const float MediumConfidenceThreshold = 0.70f;
    private const float LowConfidenceThreshold = 0.55f;

    public DualPassReasoningEngine(
        NpgsqlDataSource dataSource,
        IEmbeddingService embeddingService,
        IHybridRetrievalEngine retrievalEngine,
        ILogger<DualPassReasoningEngine> logger)
    {
        _dataSource = dataSource;
        _embeddingService = embeddingService;
        _retrievalEngine = retrievalEngine;
        _logger = logger;
    }

    public async Task<DualPassResult> ReasonAsync(
        DualPassInput input,
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        var reasoningId = Guid.CreateVersion7();
        var stopwatch = Stopwatch.StartNew();

        // Pass 1: Draft generation
        var (draftOutput, draftDuration) = await ExecutePass1Async(
            input,
            cancellationToken);

        // Pass 2: Critique and repair
        var (critique, finalOutput, improvements, pass2Duration) = await ExecutePass2Async(
            input,
            draftOutput,
            cancellationToken);

        stopwatch.Stop();

        // Compute confidence scores
        var confidenceBefore = ComputeConfidence(draftOutput);
        var confidenceAfter = ComputeConfidence(finalOutput);

        // Store reasoning result
        await StoreReasoningResultAsync(
            reasoningId,
            sessionId,
            input,
            draftOutput,
            critique,
            finalOutput,
            confidenceBefore,
            confidenceAfter,
            improvements,
            draftDuration,
            pass2Duration,
            cancellationToken);

        _logger.LogDebug(
            "Dual-pass reasoning completed in {Duration}ms: confidence {Before:F2} -> {After:F2}",
            stopwatch.ElapsedMilliseconds, confidenceBefore, confidenceAfter);

        return new DualPassResult(
            reasoningId,
            sessionId,
            draftOutput,
            critique,
            finalOutput,
            confidenceBefore,
            confidenceAfter,
            improvements,
            draftDuration,
            pass2Duration);
    }

    private async Task<(ReasoningOutput Output, int DurationMs)> ExecutePass1Async(
        DualPassInput input,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        // Generate query embedding
        var queryEmbedding = await _embeddingService.EmbedTextAsync(input.Query);

        // Analyze retrieved memories for relevance and extract key information
        var analyzedMemories = new List<AnalyzedMemory>();

        foreach (var memory in input.RetrievedMemories)
        {
            var analysis = await AnalyzeMemoryAsync(
                memory,
                queryEmbedding,
                cancellationToken);

            analyzedMemories.Add(analysis);
        }

        // Sort by combined relevance score
        var rankedMemories = analyzedMemories
            .OrderByDescending(m => m.CombinedScore)
            .ToList();

        // Extract key facts and relationships
        var extractedFacts = ExtractFacts(rankedMemories);
        var relationships = ExtractRelationships(rankedMemories);

        // Generate draft reasoning chain
        var reasoningChain = BuildReasoningChain(
            input.Query,
            extractedFacts,
            relationships,
            rankedMemories);

        // Compute draft answer
        var draftAnswer = SynthesizeDraftAnswer(
            input.Query,
            reasoningChain,
            rankedMemories);

        stopwatch.Stop();

        var output = new ReasoningOutput(
            draftAnswer.Answer,
            reasoningChain,
            extractedFacts,
            relationships,
            rankedMemories.Select(m => m.MemoryId).ToList(),
            draftAnswer.Confidence,
            draftAnswer.Caveats);

        return (output, (int)stopwatch.ElapsedMilliseconds);
    }

    private async Task<(CritiqueResult Critique, ReasoningOutput FinalOutput, List<string> Improvements, int DurationMs)>
        ExecutePass2Async(
            DualPassInput input,
            ReasoningOutput draftOutput,
            CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        // Critique the draft
        var critique = await CritiqueDraftAsync(
            input,
            draftOutput,
            cancellationToken);

        // Apply repairs based on critique
        var (repairedOutput, improvements) = await RepairDraftAsync(
            input,
            draftOutput,
            critique,
            cancellationToken);

        stopwatch.Stop();

        return (critique, repairedOutput, improvements, (int)stopwatch.ElapsedMilliseconds);
    }

    private async Task<AnalyzedMemory> AnalyzeMemoryAsync(
        ScoredMemory memory,
        float[] queryEmbedding,
        CancellationToken cancellationToken)
    {
        // Re-embed memory content for precise comparison
        var memoryEmbedding = await _embeddingService.EmbedTextAsync(memory.Content);

        // Compute semantic similarity
        var semanticSimilarity = CosineSimilarity(queryEmbedding, memoryEmbedding);

        // Extract key phrases
        var keyPhrases = ExtractKeyPhrases(memory.Content);

        // Determine information type
        var infoType = ClassifyInformationType(memory.Content);

        // Compute temporal relevance
        var temporalScore = ComputeTemporalRelevance(memory.CreatedAt);

        // Combined score
        var combinedScore = semanticSimilarity * 0.5f
                          + memory.ConfidenceScore * 0.2f
                          + temporalScore * 0.15f
                          + memory.FinalScore * 0.15f;

        return new AnalyzedMemory(
            memory.MemoryId,
            memory.Content,
            semanticSimilarity,
            temporalScore,
            memory.ConfidenceScore,
            combinedScore,
            keyPhrases,
            infoType,
            memory.MatchedRules);
    }

    private async Task<CritiqueResult> CritiqueDraftAsync(
        DualPassInput input,
        ReasoningOutput draft,
        CancellationToken cancellationToken)
    {
        var issues = new List<CritiqueIssue>();

        // Check for factual consistency
        var factualIssues = await CheckFactualConsistencyAsync(
            draft,
            input.RetrievedMemories,
            cancellationToken);
        issues.AddRange(factualIssues);

        // Check for logical coherence
        var logicalIssues = CheckLogicalCoherence(draft);
        issues.AddRange(logicalIssues);

        // Check for completeness
        var completenessIssues = CheckCompleteness(input.Query, draft);
        issues.AddRange(completenessIssues);

        // Check for contradictions
        var contradictionIssues = await CheckForContradictionsAsync(
            draft,
            cancellationToken);
        issues.AddRange(contradictionIssues);

        // Compute overall critique score
        var critiqueScore = issues.Count == 0
            ? 1.0f
            : Math.Max(0f, 1f - issues.Sum(i => i.Severity) / issues.Count);

        return new CritiqueResult(
            issues,
            critiqueScore,
            issues.Count == 0,
            GenerateRecommendations(issues));
    }

    private async Task<(ReasoningOutput Output, List<string> Improvements)> RepairDraftAsync(
        DualPassInput input,
        ReasoningOutput draft,
        CritiqueResult critique,
        CancellationToken cancellationToken)
    {
        if (critique.IsAcceptable)
        {
            return (draft, new List<string>());
        }

        var improvements = new List<string>();
        var repairedAnswer = draft.Answer;
        var repairedFacts = new List<string>(draft.Facts);
        var repairedCaveats = new List<string>(draft.Caveats);
        var repairedChain = new List<ReasoningStep>(draft.ReasoningChain);

        foreach (var issue in critique.Issues.OrderByDescending(i => i.Severity))
        {
            switch (issue.IssueType)
            {
                case "factual_inconsistency":
                    // Remove or correct the inconsistent fact
                    var factToRemove = issue.RelatedContent;
                    if (factToRemove != null && repairedFacts.Contains(factToRemove))
                    {
                        repairedFacts.Remove(factToRemove);
                        improvements.Add($"Removed inconsistent fact: {factToRemove[..Math.Min(50, factToRemove.Length)]}...");
                    }
                    break;

                case "incomplete_answer":
                    // Add missing information from retrieved memories
                    var additionalInfo = await FetchAdditionalInfoAsync(
                        issue.Suggestion,
                        input.RetrievedMemories,
                        cancellationToken);
                    if (additionalInfo != null)
                    {
                        repairedFacts.Add(additionalInfo);
                        repairedAnswer += $"\n\nAdditionally: {additionalInfo}";
                        improvements.Add($"Added missing information about {issue.Suggestion?[..Math.Min(30, issue.Suggestion.Length ?? 0)]}...");
                    }
                    break;

                case "logical_gap":
                    // Add reasoning step to fill the gap
                    var bridgeStep = new ReasoningStep(
                        repairedChain.Count,
                        "inference",
                        issue.Suggestion ?? "Bridging logical gap",
                        0.7f);
                    repairedChain.Add(bridgeStep);
                    improvements.Add($"Added reasoning step to address logical gap");
                    break;

                case "contradiction":
                    // Add caveat about the contradiction
                    var caveat = $"Note: There may be conflicting information regarding {issue.RelatedContent}";
                    repairedCaveats.Add(caveat);
                    improvements.Add($"Added caveat about potential contradiction");
                    break;

                case "low_confidence":
                    // Add uncertainty marker
                    repairedCaveats.Add("This response is based on limited supporting evidence.");
                    improvements.Add("Added uncertainty marker due to low confidence");
                    break;
            }
        }

        // Recalculate confidence after repairs
        var newConfidence = Math.Min(1f, draft.Confidence + improvements.Count * 0.05f);

        return (new ReasoningOutput(
            repairedAnswer,
            repairedChain,
            repairedFacts,
            draft.Relationships,
            draft.SourceMemoryIds,
            newConfidence,
            repairedCaveats), improvements);
    }

    private async Task<List<CritiqueIssue>> CheckFactualConsistencyAsync(
        ReasoningOutput draft,
        IReadOnlyList<ScoredMemory> memories,
        CancellationToken cancellationToken)
    {
        var issues = new List<CritiqueIssue>();

        // Embed each fact and check against source memories
        foreach (var fact in draft.Facts)
        {
            var factEmbedding = await _embeddingService.EmbedTextAsync(fact);
            var maxSimilarity = 0f;

            foreach (var memory in memories)
            {
                var memoryEmbedding = await _embeddingService.EmbedTextAsync(memory.Content);
                var similarity = CosineSimilarity(factEmbedding, memoryEmbedding);
                maxSimilarity = Math.Max(maxSimilarity, similarity);
            }

            if (maxSimilarity < LowConfidenceThreshold)
            {
                issues.Add(new CritiqueIssue(
                    "factual_inconsistency",
                    $"Fact not well supported by source memories (similarity: {maxSimilarity:F2})",
                    0.7f,
                    fact,
                    "Consider removing or qualifying this fact"));
            }
        }

        return issues;
    }

    private List<CritiqueIssue> CheckLogicalCoherence(ReasoningOutput draft)
    {
        var issues = new List<CritiqueIssue>();

        // Check if reasoning chain has gaps
        for (int i = 1; i < draft.ReasoningChain.Count; i++)
        {
            var prevStep = draft.ReasoningChain[i - 1];
            var currStep = draft.ReasoningChain[i];

            // Check if confidence drops significantly between steps
            if (prevStep.Confidence - currStep.Confidence > 0.3f)
            {
                issues.Add(new CritiqueIssue(
                    "logical_gap",
                    $"Significant confidence drop between step {i - 1} and {i}",
                    0.5f,
                    prevStep.Content,
                    "Add intermediate reasoning step"));
            }
        }

        // Check for circular reasoning
        var seenContent = new HashSet<string>();
        foreach (var step in draft.ReasoningChain)
        {
            var normalized = step.Content.ToLowerInvariant().Trim();
            if (seenContent.Contains(normalized))
            {
                issues.Add(new CritiqueIssue(
                    "circular_reasoning",
                    "Potential circular reasoning detected",
                    0.6f,
                    step.Content,
                    "Remove redundant reasoning step"));
            }
            seenContent.Add(normalized);
        }

        return issues;
    }

    private List<CritiqueIssue> CheckCompleteness(string query, ReasoningOutput draft)
    {
        var issues = new List<CritiqueIssue>();

        // Extract query intent keywords
        var queryKeywords = ExtractKeyPhrases(query);

        // Check if answer addresses query keywords
        var answerKeywords = ExtractKeyPhrases(draft.Answer);
        var coveredKeywords = queryKeywords
            .Count(qk => answerKeywords.Any(ak =>
                ak.Contains(qk, StringComparison.OrdinalIgnoreCase) ||
                qk.Contains(ak, StringComparison.OrdinalIgnoreCase)));

        var coverageRatio = queryKeywords.Count > 0
            ? (float)coveredKeywords / queryKeywords.Count
            : 1f;

        if (coverageRatio < 0.5f)
        {
            var missingKeywords = queryKeywords
                .Where(qk => !answerKeywords.Any(ak =>
                    ak.Contains(qk, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            issues.Add(new CritiqueIssue(
                "incomplete_answer",
                $"Answer may not fully address the query (coverage: {coverageRatio:P0})",
                0.6f,
                string.Join(", ", missingKeywords),
                $"Address these topics: {string.Join(", ", missingKeywords.Take(3))}"));
        }

        // Check if confidence is too low
        if (draft.Confidence < LowConfidenceThreshold)
        {
            issues.Add(new CritiqueIssue(
                "low_confidence",
                $"Overall confidence is low: {draft.Confidence:F2}",
                0.4f,
                null,
                "Gather more supporting evidence or add uncertainty markers"));
        }

        return issues;
    }

    private async Task<List<CritiqueIssue>> CheckForContradictionsAsync(
        ReasoningOutput draft,
        CancellationToken cancellationToken)
    {
        var issues = new List<CritiqueIssue>();

        // Check for internal contradictions in facts
        for (int i = 0; i < draft.Facts.Count; i++)
        {
            for (int j = i + 1; j < draft.Facts.Count; j++)
            {
                var emb1 = await _embeddingService.EmbedTextAsync(draft.Facts[i]);
                var emb2 = await _embeddingService.EmbedTextAsync(draft.Facts[j]);

                var similarity = CosineSimilarity(emb1, emb2);

                // High similarity but containing negation words might indicate contradiction
                if (similarity > 0.7f)
                {
                    var containsNegation1 = ContainsNegation(draft.Facts[i]);
                    var containsNegation2 = ContainsNegation(draft.Facts[j]);

                    if (containsNegation1 != containsNegation2)
                    {
                        issues.Add(new CritiqueIssue(
                            "contradiction",
                            "Potential contradiction between facts",
                            0.7f,
                            $"Fact 1: {draft.Facts[i][..Math.Min(50, draft.Facts[i].Length)]}... vs Fact 2: {draft.Facts[j][..Math.Min(50, draft.Facts[j].Length)]}...",
                            "Resolve contradiction or add clarifying caveat"));
                    }
                }
            }
        }

        return issues;
    }

    private static bool ContainsNegation(string text)
    {
        var negationWords = new[] { "not", "no", "never", "none", "neither", "nor", "nothing", "nobody", "nowhere", "isn't", "aren't", "wasn't", "weren't", "don't", "doesn't", "didn't", "won't", "wouldn't", "can't", "couldn't", "shouldn't" };
        var lowerText = text.ToLowerInvariant();
        return negationWords.Any(n => lowerText.Contains(n));
    }

    private async Task<string?> FetchAdditionalInfoAsync(
        string? topic,
        IReadOnlyList<ScoredMemory> memories,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(topic))
        {
            return null;
        }

        var topicEmbedding = await _embeddingService.EmbedTextAsync(topic);

        foreach (var memory in memories)
        {
            var memoryEmbedding = await _embeddingService.EmbedTextAsync(memory.Content);
            var similarity = CosineSimilarity(topicEmbedding, memoryEmbedding);

            if (similarity > MediumConfidenceThreshold)
            {
                // Extract relevant sentence from memory
                var sentences = memory.Content.Split(new[] { '.', '!', '?' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var sentence in sentences)
                {
                    var sentenceEmbedding = await _embeddingService.EmbedTextAsync(sentence.Trim());
                    if (CosineSimilarity(topicEmbedding, sentenceEmbedding) > MediumConfidenceThreshold)
                    {
                        return sentence.Trim();
                    }
                }
            }
        }

        return null;
    }

    private static List<string> ExtractKeyPhrases(string text)
    {
        // Simple keyword extraction
        var stopWords = new HashSet<string> { "the", "a", "an", "is", "are", "was", "were", "be", "been", "being", "have", "has", "had", "do", "does", "did", "will", "would", "could", "should", "may", "might", "must", "shall", "can", "need", "dare", "ought", "used", "to", "of", "in", "for", "on", "with", "at", "by", "from", "as", "into", "through", "during", "before", "after", "above", "below", "between", "under", "again", "further", "then", "once", "here", "there", "when", "where", "why", "how", "all", "each", "few", "more", "most", "other", "some", "such", "no", "nor", "not", "only", "own", "same", "so", "than", "too", "very", "just", "and", "but", "if", "or", "because", "until", "while", "this", "that", "these", "those", "it", "its" };

        return text.ToLowerInvariant()
            .Split(new[] { ' ', '\t', '\n', '\r', '.', ',', '!', '?', ';', ':', '"', '\'', '(', ')', '[', ']', '{', '}' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2 && !stopWords.Contains(w))
            .Distinct()
            .Take(10)
            .ToList();
    }

    private static string ClassifyInformationType(string content)
    {
        var lower = content.ToLowerInvariant();

        if (lower.Contains("because") || lower.Contains("therefore") || lower.Contains("since") || lower.Contains("due to"))
            return "causal";
        if (lower.Contains("when") || lower.Contains("while") || lower.Contains("during") || lower.Contains("after") || lower.Contains("before"))
            return "temporal";
        if (lower.Contains("where") || lower.Contains("location") || lower.Contains("place"))
            return "spatial";
        if (lower.Contains("how") || lower.Contains("method") || lower.Contains("process") || lower.Contains("step"))
            return "procedural";
        if (lower.Contains("is") || lower.Contains("are") || lower.Contains("was") || lower.Contains("were"))
            return "definitional";

        return "factual";
    }

    private static float ComputeTemporalRelevance(DateTime createdAt)
    {
        var age = DateTime.UtcNow - createdAt;
        var halfLifeDays = 30.0;
        return (float)Math.Exp(-age.TotalDays / halfLifeDays);
    }

    private static List<string> ExtractFacts(List<AnalyzedMemory> memories)
    {
        return memories
            .Where(m => m.CombinedScore > MediumConfidenceThreshold)
            .SelectMany(m => m.KeyPhrases.Take(3))
            .Distinct()
            .ToList();
    }

    private static List<string> ExtractRelationships(List<AnalyzedMemory> memories)
    {
        return memories
            .SelectMany(m => m.MatchedRules)
            .Distinct()
            .ToList();
    }

    private static List<ReasoningStep> BuildReasoningChain(
        string query,
        List<string> facts,
        List<string> relationships,
        List<AnalyzedMemory> memories)
    {
        var chain = new List<ReasoningStep>();

        // Step 1: Query understanding
        chain.Add(new ReasoningStep(0, "query_understanding", $"Understanding query: {query}", 0.9f));

        // Step 2: Evidence gathering
        if (memories.Count > 0)
        {
            var topMemory = memories.First();
            chain.Add(new ReasoningStep(1, "evidence", $"Found {memories.Count} relevant memories with top similarity {topMemory.SemanticSimilarity:F2}", topMemory.CombinedScore));
        }

        // Step 3: Fact synthesis
        if (facts.Count > 0)
        {
            chain.Add(new ReasoningStep(2, "synthesis", $"Synthesized {facts.Count} key facts", 0.8f));
        }

        // Step 4: Relationship analysis
        if (relationships.Count > 0)
        {
            chain.Add(new ReasoningStep(3, "relationship", $"Identified {relationships.Count} relationships", 0.75f));
        }

        // Step 5: Conclusion
        chain.Add(new ReasoningStep(chain.Count, "conclusion", "Formulating response based on evidence", 0.85f));

        return chain;
    }

    private static DraftAnswer SynthesizeDraftAnswer(
        string query,
        List<ReasoningStep> reasoningChain,
        List<AnalyzedMemory> memories)
    {
        // Build answer from top memories
        var relevantContent = memories
            .Where(m => m.CombinedScore > MediumConfidenceThreshold)
            .Take(3)
            .Select(m => m.Content)
            .ToList();

        var answer = relevantContent.Count > 0
            ? string.Join("\n\n", relevantContent)
            : "Based on the available information, I don't have enough relevant data to provide a detailed answer.";

        var confidence = memories.Count > 0
            ? memories.Take(3).Average(m => m.CombinedScore)
            : 0.3f;

        var caveats = new List<string>();
        if (confidence < MediumConfidenceThreshold)
        {
            caveats.Add("This response is based on limited available information.");
        }

        return new DraftAnswer(answer, (float)confidence, caveats);
    }

    private static List<string> GenerateRecommendations(List<CritiqueIssue> issues)
    {
        return issues
            .Where(i => !string.IsNullOrEmpty(i.Suggestion))
            .Select(i => i.Suggestion!)
            .Distinct()
            .ToList();
    }

    private static float ComputeConfidence(ReasoningOutput output)
    {
        return output.Confidence;
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

    private async Task StoreReasoningResultAsync(
        Guid reasoningId,
        Guid sessionId,
        DualPassInput input,
        ReasoningOutput draftOutput,
        CritiqueResult critique,
        ReasoningOutput finalOutput,
        float confidenceBefore,
        float confidenceAfter,
        List<string> improvements,
        int pass1Duration,
        int pass2Duration,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        var inputHash = ComputeHash(JsonSerializer.Serialize(input));
        var outputHash = ComputeHash(JsonSerializer.Serialize(finalOutput));

        const string sql = @"
            INSERT INTO dual_pass_reasoning (
                reasoning_id, session_id, pass_number, input_hash, output_hash,
                input_context, draft_output, critique, final_output,
                confidence_before, confidence_after, improvements_made, duration_ms
            ) VALUES
            (@ReasoningId, @SessionId, 1, @InputHash, @DraftHash, @InputContext::jsonb, @DraftOutput::jsonb, NULL, NULL, @ConfidenceBefore, NULL, NULL, @Pass1Duration),
            (@ReasoningId, @SessionId, 2, @DraftHash, @OutputHash, NULL, NULL, @Critique::jsonb, @FinalOutput::jsonb, NULL, @ConfidenceAfter, @Improvements::jsonb, @Pass2Duration)";

        await connection.ExecuteAsync(sql, new
        {
            ReasoningId = reasoningId,
            SessionId = sessionId,
            InputHash = inputHash,
            DraftHash = ComputeHash(JsonSerializer.Serialize(draftOutput)),
            OutputHash = outputHash,
            InputContext = JsonSerializer.Serialize(new { query = input.Query, memory_count = input.RetrievedMemories.Count }),
            DraftOutput = JsonSerializer.Serialize(draftOutput),
            Critique = JsonSerializer.Serialize(critique),
            FinalOutput = JsonSerializer.Serialize(finalOutput),
            ConfidenceBefore = confidenceBefore,
            ConfidenceAfter = confidenceAfter,
            Improvements = JsonSerializer.Serialize(improvements),
            Pass1Duration = pass1Duration,
            Pass2Duration = pass2Duration
        });
    }

    public async Task<DualPassResult?> GetReasoningResultAsync(
        Guid reasoningId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        const string sql = @"
            SELECT session_id, draft_output::text, critique::text, final_output::text,
                   confidence_before, confidence_after, improvements_made::text, duration_ms
            FROM dual_pass_reasoning
            WHERE reasoning_id = @ReasoningId
            ORDER BY pass_number";

        var rows = (await connection.QueryAsync<dynamic>(sql, new { ReasoningId = reasoningId })).ToList();

        if (rows.Count < 2)
        {
            return null;
        }

        var pass1 = rows[0];
        var pass2 = rows[1];

        return new DualPassResult(
            reasoningId,
            pass1.session_id,
            JsonSerializer.Deserialize<ReasoningOutput>(pass1.draft_output),
            JsonSerializer.Deserialize<CritiqueResult>(pass2.critique),
            JsonSerializer.Deserialize<ReasoningOutput>(pass2.final_output),
            pass1.confidence_before,
            pass2.confidence_after,
            JsonSerializer.Deserialize<List<string>>(pass2.improvements_made) ?? new List<string>(),
            pass1.duration_ms,
            pass2.duration_ms);
    }

    private static string ComputeHash(string input)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

// Supporting types
public record AnalyzedMemory(
    Guid MemoryId,
    string Content,
    float SemanticSimilarity,
    float TemporalScore,
    float ConfidenceScore,
    float CombinedScore,
    List<string> KeyPhrases,
    string InformationType,
    IReadOnlyList<string> MatchedRules);

public record ReasoningOutput(
    string Answer,
    List<ReasoningStep> ReasoningChain,
    List<string> Facts,
    List<string> Relationships,
    List<Guid> SourceMemoryIds,
    float Confidence,
    List<string> Caveats);

public record ReasoningStep(
    int StepNumber,
    string StepType,
    string Content,
    float Confidence);

public record CritiqueResult(
    List<CritiqueIssue> Issues,
    float CritiqueScore,
    bool IsAcceptable,
    List<string> Recommendations);

public record CritiqueIssue(
    string IssueType,
    string Description,
    float Severity,
    string? RelatedContent,
    string? Suggestion);

public record DraftAnswer(
    string Answer,
    float Confidence,
    List<string> Caveats);
