using System.Text.Json;
using Microsoft.Extensions.Logging;
using SerialMemory.Core.Interfaces;

namespace SerialMemory.Infrastructure.MemoryLayer;

/// <summary>
/// Service for LLM-powered cognitive layer transitions.
/// Uses OpenAI to extract entities, summarize, and distill knowledge.
/// </summary>
public sealed class CognitiveLayerService(ILlmService llm, ILogger<CognitiveLayerService> logger)
{
    /// <summary>
    /// L0 → L1: Context Enrichment
    /// Extracts entities, context, and meaning from raw memory.
    /// </summary>
    public async Task<L1ContextResult> EnrichToL1Async(string rawContent, CancellationToken ct = default)
    {
        const string systemPrompt = """
            You are a cognitive memory processor. Analyze the input text and extract structured context.

            Your task is to:
            1. Identify all entities (people, organizations, technologies, concepts, dates, locations)
            2. Determine the main topic/subject
            3. Extract key facts and statements
            4. Identify any decisions, actions, or outcomes mentioned
            5. Note any relationships between entities

            Respond with JSON only:
            {
                "entities": [{"name": "...", "type": "PERSON|ORG|TECH|CONCEPT|DATE|LOCATION|PROJECT|SERVICE|API|OTHER", "context": "brief context"}],
                "topic": "main topic in 5-10 words",
                "keyFacts": ["fact 1", "fact 2"],
                "decisions": ["decision or action taken"],
                "relationships": [{"from": "entity1", "to": "entity2", "relation": "type"}],
                "contextSummary": "2-3 sentence contextual summary"
            }
            """;

        try
        {
            var response = await llm.ChatAsync(rawContent, systemPrompt, 0.2f, 1500, ct);
            var result = ParseJson<L1ContextResult>(response);

            if (result != null)
            {
                logger.LogDebug("L0→L1: Extracted {EntityCount} entities, topic: {Topic}",
                    result.Entities?.Count ?? 0, result.Topic);
            }

            return result ?? new L1ContextResult { ContextSummary = rawContent };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "L0→L1 enrichment failed, using raw content");
            return new L1ContextResult { ContextSummary = rawContent };
        }
    }

    /// <summary>
    /// L1 → L2: Summarization
    /// Creates a concise summary from contextualized memory.
    /// </summary>
    public async Task<L2SummaryResult> SummarizeToL2Async(
        string contextContent,
        List<string>? relatedMemories = null,
        CancellationToken ct = default)
    {
        var relatedContext = relatedMemories?.Count > 0
            ? $"\n\nRelated memories for context:\n{string.Join("\n---\n", relatedMemories.Take(5))}"
            : "";

        const string systemPrompt = """
            You are a memory summarization engine. Create a concise, information-dense summary.

            Your task is to:
            1. Distill the key information into a concise summary (2-4 sentences)
            2. Preserve all important facts, decisions, and outcomes
            3. Note any patterns or recurring themes if related memories are provided
            4. Assign importance score (0.0-1.0) based on actionability and significance
            5. Categorize the memory type

            Respond with JSON only:
            {
                "summary": "concise 2-4 sentence summary",
                "keyPoints": ["key point 1", "key point 2"],
                "category": "DECISION|LEARNING|FACT|PREFERENCE|PATTERN|TASK|EVENT|OTHER",
                "importance": 0.8,
                "tags": ["tag1", "tag2"],
                "patterns": ["any patterns noticed with related memories"]
            }
            """;

        try
        {
            var input = contextContent + relatedContext;
            var response = await llm.ChatAsync(input, systemPrompt, 0.2f, 1000, ct);
            var result = ParseJson<L2SummaryResult>(response);

            if (result != null)
            {
                logger.LogDebug("L1→L2: Summary created, importance: {Importance}, category: {Category}",
                    result.Importance, result.Category);
            }

            return result ?? new L2SummaryResult { Summary = contextContent };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "L1→L2 summarization failed");
            return new L2SummaryResult { Summary = contextContent };
        }
    }

    /// <summary>
    /// L2 → L3: Knowledge Extraction
    /// Extracts generalizable knowledge and facts from summaries.
    /// </summary>
    public async Task<L3KnowledgeResult> ExtractKnowledgeToL3Async(
        string summary,
        List<string>? relatedSummaries = null,
        CancellationToken ct = default)
    {
        var relatedContext = relatedSummaries?.Count > 0
            ? $"\n\nRelated summaries:\n{string.Join("\n---\n", relatedSummaries.Take(5))}"
            : "";

        const string systemPrompt = """
            You are a knowledge extraction engine. Extract generalizable knowledge from the summary.

            Your task is to:
            1. Identify facts that are generally true (not just this specific case)
            2. Extract preferences or patterns that could inform future behavior
            3. Identify any rules, constraints, or best practices
            4. Note any cause-effect relationships
            5. Rate confidence in each piece of knowledge (0.0-1.0)

            Respond with JSON only:
            {
                "facts": [{"statement": "...", "confidence": 0.9, "domain": "area of knowledge"}],
                "preferences": [{"preference": "...", "strength": 0.8}],
                "rules": [{"rule": "...", "condition": "when this applies", "confidence": 0.7}],
                "causeEffects": [{"cause": "...", "effect": "...", "confidence": 0.6}],
                "knowledgeSummary": "1-2 sentence distilled knowledge",
                "domains": ["domain1", "domain2"]
            }
            """;

        try
        {
            var input = summary + relatedContext;
            var response = await llm.ChatAsync(input, systemPrompt, 0.2f, 1200, ct);
            var result = ParseJson<L3KnowledgeResult>(response);

            if (result != null)
            {
                logger.LogDebug("L2→L3: Extracted {FactCount} facts, {RuleCount} rules",
                    result.Facts?.Count ?? 0, result.Rules?.Count ?? 0);
            }

            return result ?? new L3KnowledgeResult { KnowledgeSummary = summary };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "L2→L3 knowledge extraction failed");
            return new L3KnowledgeResult { KnowledgeSummary = summary };
        }
    }

    /// <summary>
    /// L3 → L4: Heuristic Learning (optional, advanced)
    /// Identifies patterns that can inform future behavior.
    /// </summary>
    public async Task<L4HeuristicResult> LearnHeuristicsToL4Async(
        string knowledge,
        List<string>? relatedKnowledge = null,
        CancellationToken ct = default)
    {
        var relatedContext = relatedKnowledge?.Count > 0
            ? $"\n\nRelated knowledge:\n{string.Join("\n---\n", relatedKnowledge.Take(10))}"
            : "";

        const string systemPrompt = """
            You are a heuristic learning engine. Identify patterns that can guide future behavior.

            Your task is to:
            1. Identify recurring patterns across the knowledge
            2. Form heuristics (rules of thumb) that could be applied in similar situations
            3. Note any anti-patterns or things to avoid
            4. Identify decision criteria that emerge from the knowledge
            5. Rate actionability of each heuristic (0.0-1.0)

            Respond with JSON only:
            {
                "heuristics": [{"rule": "...", "whenToApply": "...", "confidence": 0.8, "actionability": 0.9}],
                "antiPatterns": [{"pattern": "...", "whyAvoid": "...", "confidence": 0.7}],
                "decisionCriteria": [{"criteria": "...", "useWhen": "...", "weight": 0.6}],
                "metaLearning": "what has been learned about learning itself",
                "applicableDomains": ["domain1", "domain2"]
            }
            """;

        try
        {
            var input = knowledge + relatedContext;
            var response = await llm.ChatAsync(input, systemPrompt, 0.3f, 1200, ct);
            var result = ParseJson<L4HeuristicResult>(response);

            if (result != null)
            {
                logger.LogDebug("L3→L4: Learned {HeuristicCount} heuristics, {AntiPatternCount} anti-patterns",
                    result.Heuristics?.Count ?? 0, result.AntiPatterns?.Count ?? 0);
            }

            return result ?? new L4HeuristicResult { MetaLearning = knowledge };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "L3→L4 heuristic learning failed");
            return new L4HeuristicResult { MetaLearning = knowledge };
        }
    }

    private T? ParseJson<T>(string response) where T : class
    {
        try
        {
            // Extract JSON from response (handle markdown code blocks)
            var json = response.Trim();
            if (json.StartsWith("```"))
            {
                var startIndex = json.IndexOf('{');
                var endIndex = json.LastIndexOf('}');
                if (startIndex >= 0 && endIndex > startIndex)
                {
                    json = json.Substring(startIndex, endIndex - startIndex + 1);
                }
            }

            return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse JSON response");
            return null;
        }
    }
}

#region Result DTOs

public sealed class L1ContextResult
{
    public List<EntityInfo>? Entities { get; set; }
    public string? Topic { get; set; }
    public List<string>? KeyFacts { get; set; }
    public List<string>? Decisions { get; set; }
    public List<RelationshipInfo>? Relationships { get; set; }
    public string? ContextSummary { get; set; }
}

public sealed class EntityInfo
{
    public string? Name { get; set; }
    public string? Type { get; set; }
    public string? Context { get; set; }
}

public sealed class RelationshipInfo
{
    public string? From { get; set; }
    public string? To { get; set; }
    public string? Relation { get; set; }
}

public sealed class L2SummaryResult
{
    public string? Summary { get; set; }
    public List<string>? KeyPoints { get; set; }
    public string? Category { get; set; }
    public float Importance { get; set; } = 0.5f;
    public List<string>? Tags { get; set; }
    public List<string>? Patterns { get; set; }
}

public sealed class L3KnowledgeResult
{
    public List<FactInfo>? Facts { get; set; }
    public List<PreferenceInfo>? Preferences { get; set; }
    public List<RuleInfo>? Rules { get; set; }
    public List<CauseEffectInfo>? CauseEffects { get; set; }
    public string? KnowledgeSummary { get; set; }
    public List<string>? Domains { get; set; }
}

public sealed class FactInfo
{
    public string? Statement { get; set; }
    public float Confidence { get; set; }
    public string? Domain { get; set; }
}

public sealed class PreferenceInfo
{
    public string? Preference { get; set; }
    public float Strength { get; set; }
}

public sealed class RuleInfo
{
    public string? Rule { get; set; }
    public string? Condition { get; set; }
    public float Confidence { get; set; }
}

public sealed class CauseEffectInfo
{
    public string? Cause { get; set; }
    public string? Effect { get; set; }
    public float Confidence { get; set; }
}

public sealed class L4HeuristicResult
{
    public List<HeuristicInfo>? Heuristics { get; set; }
    public List<AntiPatternInfo>? AntiPatterns { get; set; }
    public List<DecisionCriteriaInfo>? DecisionCriteria { get; set; }
    public string? MetaLearning { get; set; }
    public List<string>? ApplicableDomains { get; set; }
}

public sealed class HeuristicInfo
{
    public string? Rule { get; set; }
    public string? WhenToApply { get; set; }
    public float Confidence { get; set; }
    public float Actionability { get; set; }
}

public sealed class AntiPatternInfo
{
    public string? Pattern { get; set; }
    public string? WhyAvoid { get; set; }
    public float Confidence { get; set; }
}

public sealed class DecisionCriteriaInfo
{
    public string? Criteria { get; set; }
    public string? UseWhen { get; set; }
    public float Weight { get; set; }
}

#endregion
