using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Npgsql;
using SerialMemory.Core.Interfaces;

// Alias to avoid conflict with SerialMemory.Infrastructure.MemoryLayer namespace
using MemoryLayerType = SerialMemory.Core.Interfaces.MemoryLayer;

namespace SerialMemory.Infrastructure.Classification;

/// <summary>
/// Full L0→L4 classification pipeline using LLM calls.
/// Each layer processes the memory through structured prompts and validates output schemas.
/// </summary>
public sealed class ClassificationService(
    NpgsqlDataSource dataSource,
    ILlmService llmService,
    IEmbeddingService embeddingService,
    ILogger<ClassificationService> logger)
    : IClassificationService
{
    private readonly NpgsqlDataSource _dataSource = dataSource;
    private readonly IEmbeddingService _embeddingService = embeddingService;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <inheritdoc />
    public async Task<ClassificationResult> ClassifyAsync(
        string content,
        MemoryLayerType layer,
        string? previousLayerContent,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            var (systemPrompt, userPrompt) = GetPromptsForLayer(layer, content, previousLayerContent);
            var maxTokens = GetMaxTokensForLayer(layer);

            var response = await llmService.ChatAsync(
                userMessage: userPrompt,
                systemPrompt: systemPrompt,
                temperature: 0.3f,
                maxTokens: maxTokens,
                cancellationToken: cancellationToken);

            sw.Stop();

            // Extract and parse JSON from response
            var json = ExtractJson(response);
            if (string.IsNullOrEmpty(json))
            {
                logger.LogWarning("Failed to extract JSON from {Layer} response: {Response}",
                    layer, response.Length > 500 ? response[..500] : response);

                // Return a fallback result
                return CreateFallbackResult(layer, content, sw.ElapsedMilliseconds);
            }

            // Validate and parse the schema
            var result = ParseAndValidateResult(layer, json, sw.ElapsedMilliseconds);

            logger.LogDebug("Classified {Layer} in {ElapsedMs}ms, confidence: {Confidence}",
                layer, sw.ElapsedMilliseconds, result.Confidence);

            return result;
        }
        catch (Exception ex) when (IsTransientNetworkError(ex))
        {
            sw.Stop();
            logger.LogWarning(ex, "Transient network error during {Layer} classification, returning fallback", layer);
            return CreateFallbackResult(layer, content, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Classification failed for {Layer}", layer);
            throw;
        }
    }

    /// <summary>
    /// Gets the system and user prompts for a specific layer.
    /// </summary>
    private static (string SystemPrompt, string UserPrompt) GetPromptsForLayer(
        MemoryLayerType layer,
        string content,
        string? previousLayerContent)
    {
        return layer switch
        {
            MemoryLayerType.L0_RAW => GetL0Prompts(content),
            MemoryLayerType.L1_CONTEXT => GetL1Prompts(content, previousLayerContent),
            MemoryLayerType.L2_SUMMARY => GetL2Prompts(content, previousLayerContent),
            MemoryLayerType.L3_KNOWLEDGE => GetL3Prompts(content, previousLayerContent),
            MemoryLayerType.L4_HEURISTIC => GetL4Prompts(content, previousLayerContent),
            _ => throw new ArgumentOutOfRangeException(nameof(layer))
        };
    }

    #region L0_RAW Prompts

    private static (string SystemPrompt, string UserPrompt) GetL0Prompts(string content)
    {
        const string systemPrompt = """
            You are a memory ingestion system. Your job is to structure raw input into a clean JSON format.

            IMPORTANT: Respond with ONLY valid JSON. No markdown, no explanations, just the JSON object.

            Output format:
            {
              "raw_text": "<the exact user input, preserved verbatim>",
              "metadata": {
                "timestamp_utc": "<current ISO timestamp>",
                "source": "<detected source: web|api|mcp|chat|internal>",
                "actor": "<detected actor or 'user'>",
                "word_count": <number>,
                "language": "<detected language code>"
              }
            }
            """;

        var userPrompt = $"""
            Process this raw input and return structured JSON:

            ---
            {content}
            ---
            """;

        return (systemPrompt, userPrompt);
    }

    #endregion

    #region L1_CONTEXT Prompts

    private static (string SystemPrompt, string UserPrompt) GetL1Prompts(string content, string? previousLayer)
    {
        const string systemPrompt = """
            You are a context analysis engine. Analyze the input and extract contextual data.

            IMPORTANT: Respond with ONLY valid JSON. No markdown, no explanations, just the JSON object.

            Output format:
            {
              "speaker": "<who said/wrote it, or 'user' if unclear>",
              "listener": "<who it is directed to, or 'system' if unclear>",
              "intent": "<primary intent: inform|request|command|question|statement|expression>",
              "context_description": "<short natural language explanation of what's happening>",
              "sentiment": "<positive|negative|neutral>",
              "urgency": "<low|medium|high>",
              "topic_domain": "<primary domain: technical|personal|business|casual|other>",
              "tags": ["<relevant>", "<contextual>", "<tags>"]
            }
            """;

        var userPrompt = $"""
            Analyze this input and extract contextual data:

            ---
            {content}
            ---

            {(previousLayer != null ? $"Previous layer (L0_RAW):\n{previousLayer}" : "")}
            """;

        return (systemPrompt, userPrompt);
    }

    #endregion

    #region L2_SUMMARY Prompts

    private static (string SystemPrompt, string UserPrompt) GetL2Prompts(string content, string? previousLayer)
    {
        const string systemPrompt = """
            You are a summarization engine. Summarize the meaning of the memory while preserving intent and correctness.

            IMPORTANT: Respond with ONLY valid JSON. No markdown, no explanations, just the JSON object.

            Output format:
            {
              "summary": "<concise summary capturing the essential meaning, max 100 words>",
              "one_liner": "<single sentence summary, max 15 words>",
              "keywords": ["<important>", "<keywords>", "<max 10>"],
              "importance_score": <0.0 to 1.0>,
              "action_items": ["<any action items mentioned>"],
              "references": ["<any external references, URLs, or names mentioned>"]
            }
            """;

        var userPrompt = $"""
            Summarize this memory content:

            ---
            {content}
            ---

            {(previousLayer != null ? $"Context from L1:\n{previousLayer}" : "")}
            """;

        return (systemPrompt, userPrompt);
    }

    #endregion

    #region L3_KNOWLEDGE Prompts (FACTS)

    private static (string SystemPrompt, string UserPrompt) GetL3Prompts(string content, string? previousLayer)
    {
        const string systemPrompt = """
            You are a knowledge extraction engine. Extract atomic facts from the memory.

            IMPORTANT: Respond with ONLY valid JSON. No markdown, no explanations, just the JSON object.

            Fact types: assertion, preference, relationship, event, definition, procedure, rule
            Entity types: Person, Organization, Product, Location, Technology, Concept, Event, Date, Project, Service, File, Module, API, Database, Error, Feature, Bug, Task

            Output format:
            {
              "facts": [
                {
                  "type": "<fact_type>",
                  "subject": "<entity or concept>",
                  "predicate": "<relationship or action>",
                  "object": "<value, entity, or state>",
                  "confidence": <0.0 to 1.0>,
                  "evidence": "<quote from original text supporting this fact>"
                }
              ],
              "entities": [
                {
                  "name": "<entity name>",
                  "type": "<entity type>",
                  "aliases": ["<alternative names>"],
                  "description": "<brief description if inferrable>"
                }
              ],
              "relationships": [
                {
                  "source": "<source entity name>",
                  "target": "<target entity name>",
                  "relation_type": "<relationship type: works_at, created_by, depends_on, related_to, etc.>",
                  "confidence": <0.0 to 1.0>
                }
              ]
            }
            """;

        var userPrompt = $"""
            Extract facts, entities, and relationships from this memory:

            ---
            {content}
            ---

            {(previousLayer != null ? $"Summary from L2:\n{previousLayer}" : "")}
            """;

        return (systemPrompt, userPrompt);
    }

    #endregion

    #region L4_HEURISTIC Prompts (PATTERNS / RULES)

    private static (string SystemPrompt, string UserPrompt) GetL4Prompts(string content, string? previousLayer)
    {
        const string systemPrompt = """
            You are a pattern analysis engine. Analyze the memory and return inferred patterns, preferences, and heuristics.

            IMPORTANT: Respond with ONLY valid JSON. No markdown, no explanations, just the JSON object.

            Output format:
            {
              "preferences": ["<user preference statements>"],
              "habits": ["<observed behavioral patterns>"],
              "long_term_tendencies": ["<long-term preferences or tendencies>"],
              "classification": "<primary category: work|personal|learning|project|communication|planning|debugging|other>",
              "expertise_indicators": ["<domains where user shows expertise>"],
              "inferred_rules": [
                {
                  "rule": "<if-then rule inferred from the content>",
                  "confidence": <0.0 to 1.0>,
                  "scope": "<when this rule applies>"
                }
              ],
              "mental_model_updates": [
                {
                  "model": "<what mental model this updates>",
                  "update": "<how the model should be updated>",
                  "confidence": <0.0 to 1.0>
                }
              ]
            }
            """;

        var userPrompt = $"""
            Analyze this memory and extract patterns, preferences, and heuristics:

            ---
            {content}
            ---

            {(previousLayer != null ? $"Knowledge from L3:\n{previousLayer}" : "")}
            """;

        return (systemPrompt, userPrompt);
    }

    #endregion

    /// <summary>
    /// Parses and validates the classification result based on the layer schema.
    /// </summary>
    private ClassificationResult ParseAndValidateResult(MemoryLayerType layer, string json, long durationMs)
    {
        try
        {
            // Parse to verify it's valid JSON
            using var doc = JsonDocument.Parse(json);

            // Extract knowledge nodes for L3/L4
            List<KnowledgeNode>? knowledgeNodes = null;

            if (layer == MemoryLayerType.L3_KNOWLEDGE)
            {
                knowledgeNodes = ExtractL3KnowledgeNodes(doc);
            }
            else if (layer == MemoryLayerType.L4_HEURISTIC)
            {
                knowledgeNodes = ExtractL4KnowledgeNodes(doc);
            }

            // Calculate confidence based on content richness
            var confidence = CalculateConfidence(layer, doc);

            return new ClassificationResult
            {
                ContentJson = json,
                ModelName = $"{llmService.ProviderName}/{llmService.ModelName}",
                Confidence = confidence,
                KnowledgeNodes = knowledgeNodes
            };
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse {Layer} JSON: {Json}", layer, json);
            return CreateFallbackResult(layer, json, durationMs);
        }
    }

    /// <summary>
    /// Extracts knowledge nodes from L3 (facts) output.
    /// </summary>
    private List<KnowledgeNode> ExtractL3KnowledgeNodes(JsonDocument doc)
    {
        var nodes = new List<KnowledgeNode>();

        // Extract facts
        if (doc.RootElement.TryGetProperty("facts", out var facts))
        {
            foreach (var fact in facts.EnumerateArray())
            {
                nodes.Add(new KnowledgeNode
                {
                    NodeType = "fact",
                    Subject = fact.TryGetProperty("subject", out var s) ? SafeGetString(s) ?? "" : "",
                    Predicate = fact.TryGetProperty("predicate", out var p) ? SafeGetString(p) : null,
                    Object = fact.TryGetProperty("object", out var o) ? SafeGetString(o) : null,
                    Confidence = fact.TryGetProperty("confidence", out var c) ? SafeGetDecimal(c, 0.8m) : 0.8m,
                    Evidence = fact.TryGetProperty("evidence", out var e) ? SafeGetString(e) : null,
                    Metadata = JsonSerializer.Serialize(new { type = fact.TryGetProperty("type", out var t) ? SafeGetString(t) : "assertion" })
                });
            }
        }

        // Extract entities
        if (doc.RootElement.TryGetProperty("entities", out var entities))
        {
            foreach (var entity in entities.EnumerateArray())
            {
                nodes.Add(new KnowledgeNode
                {
                    NodeType = "entity",
                    Subject = entity.TryGetProperty("name", out var n) ? SafeGetString(n) ?? "" : "",
                    Predicate = "is_a",
                    Object = entity.TryGetProperty("type", out var t) ? SafeGetString(t) : "Unknown",
                    Confidence = 0.9m,
                    Evidence = entity.TryGetProperty("description", out var d) ? SafeGetString(d) : null
                });
            }
        }

        // Extract relationships
        if (doc.RootElement.TryGetProperty("relationships", out var relationships))
        {
            foreach (var rel in relationships.EnumerateArray())
            {
                nodes.Add(new KnowledgeNode
                {
                    NodeType = "relationship",
                    Subject = rel.TryGetProperty("source", out var src) ? SafeGetString(src) ?? "" : "",
                    Predicate = rel.TryGetProperty("relation_type", out var rt) ? SafeGetString(rt) : "related_to",
                    Object = rel.TryGetProperty("target", out var tgt) ? SafeGetString(tgt) : null,
                    Confidence = rel.TryGetProperty("confidence", out var c) ? SafeGetDecimal(c, 0.85m) : 0.85m
                });
            }
        }

        return nodes;
    }

    /// <summary>
    /// Extracts knowledge nodes from L4 (heuristics) output.
    /// </summary>
    private List<KnowledgeNode> ExtractL4KnowledgeNodes(JsonDocument doc)
    {
        var nodes = new List<KnowledgeNode>();

        // Extract preferences
        if (doc.RootElement.TryGetProperty("preferences", out var prefs))
        {
            foreach (var pref in prefs.EnumerateArray())
            {
                var prefText = pref.GetString();
                if (!string.IsNullOrEmpty(prefText))
                {
                    nodes.Add(new KnowledgeNode
                    {
                        NodeType = "preference",
                        Subject = "user",
                        Predicate = "prefers",
                        Object = prefText,
                        Confidence = 0.75m
                    });
                }
            }
        }

        // Extract habits
        if (doc.RootElement.TryGetProperty("habits", out var habits))
        {
            foreach (var habit in habits.EnumerateArray())
            {
                var habitText = habit.GetString();
                if (!string.IsNullOrEmpty(habitText))
                {
                    nodes.Add(new KnowledgeNode
                    {
                        NodeType = "habit",
                        Subject = "user",
                        Predicate = "typically",
                        Object = habitText,
                        Confidence = 0.7m
                    });
                }
            }
        }

        // Extract inferred rules
        if (doc.RootElement.TryGetProperty("inferred_rules", out var rules))
        {
            foreach (var rule in rules.EnumerateArray())
            {
                nodes.Add(new KnowledgeNode
                {
                    NodeType = "rule",
                    Subject = rule.TryGetProperty("scope", out var scope) ? scope.GetString() ?? "general" : "general",
                    Predicate = "implies",
                    Object = rule.TryGetProperty("rule", out var r) ? r.GetString() : null,
                    Confidence = rule.TryGetProperty("confidence", out var c) ? c.GetDecimal() : 0.6m
                });
            }
        }

        // Extract mental model updates
        if (doc.RootElement.TryGetProperty("mental_model_updates", out var models))
        {
            foreach (var model in models.EnumerateArray())
            {
                nodes.Add(new KnowledgeNode
                {
                    NodeType = "mental_model",
                    Subject = model.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "",
                    Predicate = "updated_by",
                    Object = model.TryGetProperty("update", out var u) ? u.GetString() : null,
                    Confidence = model.TryGetProperty("confidence", out var c) ? c.GetDecimal() : 0.65m
                });
            }
        }

        return nodes;
    }

    /// <summary>
    /// Calculates confidence score based on content richness.
    /// </summary>
    private static decimal CalculateConfidence(MemoryLayerType layer, JsonDocument doc)
    {
        var baseConfidence = 0.7m;

        try
        {
            var propCount = doc.RootElement.EnumerateObject().Count();

            // More properties = higher confidence
            baseConfidence += Math.Min(0.2m, propCount * 0.02m);

            // Layer-specific adjustments
            switch (layer)
            {
                case MemoryLayerType.L3_KNOWLEDGE:
                    if (doc.RootElement.TryGetProperty("facts", out var facts) && facts.GetArrayLength() > 0)
                        baseConfidence += 0.05m;
                    if (doc.RootElement.TryGetProperty("entities", out var entities) && entities.GetArrayLength() > 0)
                        baseConfidence += 0.05m;
                    break;

                case MemoryLayerType.L4_HEURISTIC:
                    if (doc.RootElement.TryGetProperty("preferences", out var prefs) && prefs.GetArrayLength() > 0)
                        baseConfidence += 0.03m;
                    if (doc.RootElement.TryGetProperty("inferred_rules", out var rules) && rules.GetArrayLength() > 0)
                        baseConfidence += 0.07m;
                    break;
            }
        }
        catch
        {
            // Ignore parsing errors in confidence calculation
        }

        return Math.Min(0.99m, baseConfidence);
    }

    /// <summary>
    /// Creates a fallback result when classification fails.
    /// </summary>
    private ClassificationResult CreateFallbackResult(MemoryLayerType layer, string content, long durationMs)
    {
        var fallbackJson = layer switch
        {
            MemoryLayerType.L0_RAW => JsonSerializer.Serialize(new
            {
                raw_text = content,
                metadata = new
                {
                    timestamp_utc = DateTime.UtcNow.ToString("O"),
                    source = "fallback",
                    actor = "system"
                }
            }),
            MemoryLayerType.L1_CONTEXT => JsonSerializer.Serialize(new
            {
                speaker = "unknown",
                listener = "system",
                intent = "unknown",
                context_description = "Classification fallback - could not parse LLM response",
                tags = new[] { "fallback" }
            }),
            MemoryLayerType.L2_SUMMARY => JsonSerializer.Serialize(new
            {
                summary = content.Length > 200 ? content[..200] + "..." : content,
                keywords = Array.Empty<string>(),
                one_liner = "Classification pending"
            }),
            MemoryLayerType.L3_KNOWLEDGE => JsonSerializer.Serialize(new
            {
                facts = Array.Empty<object>(),
                entities = Array.Empty<object>(),
                relationships = Array.Empty<object>()
            }),
            MemoryLayerType.L4_HEURISTIC => JsonSerializer.Serialize(new
            {
                preferences = Array.Empty<string>(),
                habits = Array.Empty<string>(),
                long_term_tendencies = Array.Empty<string>(),
                classification = "unknown"
            }),
            _ => "{}"
        };

        return new ClassificationResult
        {
            ContentJson = fallbackJson,
            ModelName = "fallback",
            Confidence = 0.1m,
            KnowledgeNodes = null
        };
    }

    /// <summary>
    /// Checks if an exception is a transient network error that should trigger fallback
    /// rather than failing the classification entirely.
    /// </summary>
    private static bool IsTransientNetworkError(Exception ex)
    {
        // AggregateException from SDK retry policies — flatten and check all inner exceptions
        if (ex is AggregateException agg)
            return agg.Flatten().InnerExceptions.Any(IsTransientNetworkError);

        // Walk the exception chain looking for network-related failures
        var current = ex;
        while (current != null)
        {
            if (current is System.Net.Sockets.SocketException)
                return true;
            if (current is System.Net.Http.HttpRequestException)
                return true;
            if (current is TaskCanceledException tce && tce.InnerException is TimeoutException)
                return true;
            // ClientResultException from System.ClientModel wraps HTTP transport failures
            if (current.GetType().Name == "ClientResultException"
                && current.InnerException is System.Net.Http.HttpRequestException)
                return true;

            current = current.InnerException;
        }

        return false;
    }

    /// <summary>
    /// Safely gets a string from a JsonElement, handling non-string types.
    /// </summary>
    private static string? SafeGetString(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => element.GetRawText().Trim('"')
        };
    }

    /// <summary>
    /// Safely gets a decimal from a JsonElement, handling non-numeric types.
    /// </summary>
    private static decimal SafeGetDecimal(JsonElement element, decimal fallback)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Number => element.GetDecimal(),
            JsonValueKind.String when decimal.TryParse(element.GetString(), out var d) => d,
            _ => fallback
        };
    }

    /// <summary>
    /// Returns the max token budget for the given layer.
    /// L3/L4 produce larger structured output (facts, entities, relationships).
    /// </summary>
    private static int GetMaxTokensForLayer(MemoryLayerType layer) => layer switch
    {
        MemoryLayerType.L0_RAW => 1000,
        MemoryLayerType.L1_CONTEXT => 1000,
        MemoryLayerType.L2_SUMMARY => 1500,
        MemoryLayerType.L3_KNOWLEDGE => 4000,
        MemoryLayerType.L4_HEURISTIC => 3000,
        _ => 2000
    };

    /// <summary>
    /// Extracts JSON from a response that may contain markdown or other text.
    /// </summary>
    private static string? ExtractJson(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        // Remove markdown code blocks if present
        text = text.Trim();
        if (text.StartsWith("```json"))
            text = text[7..];
        else if (text.StartsWith("```"))
            text = text[3..];

        if (text.EndsWith("```"))
            text = text[..^3];

        text = text.Trim();

        // Find the JSON object
        var start = text.IndexOf('{');
        if (start < 0)
            return null;

        var end = text.LastIndexOf('}');
        if (end <= start)
            return null;

        var candidate = text[start..(end + 1)];

        // Try parsing as-is first
        try
        {
            using var doc = JsonDocument.Parse(candidate);
            return candidate;
        }
        catch (JsonException)
        {
            // JSON may be truncated — attempt repair
        }

        // Attempt to repair truncated JSON from the full text after start
        var repaired = TryRepairTruncatedJson(text[start..]);
        return repaired;
    }

    /// <summary>
    /// Attempts to repair truncated JSON by closing unclosed brackets and braces.
    /// Handles strings, escaped characters, and nested structures.
    /// </summary>
    private static string? TryRepairTruncatedJson(string json)
    {
        var stack = new Stack<char>();
        var inString = false;
        var escaped = false;

        for (var i = 0; i < json.Length; i++)
        {
            var c = json[i];

            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (c == '\\' && inString)
            {
                escaped = true;
                continue;
            }

            if (c == '"')
            {
                inString = !inString;
                continue;
            }

            if (inString)
                continue;

            switch (c)
            {
                case '{':
                    stack.Push('}');
                    break;
                case '[':
                    stack.Push(']');
                    break;
                case '}':
                case ']':
                    if (stack.Count > 0 && stack.Peek() == c)
                        stack.Pop();
                    break;
            }
        }

        if (stack.Count == 0)
            return null; // Already balanced but still invalid — not a truncation issue

        // Trim trailing incomplete values (e.g., truncated string or partial number)
        var trimmed = json.TrimEnd();

        // If we ended mid-string, close the string
        if (inString)
            trimmed += "\"";

        // Remove trailing comma or colon that would make closing invalid
        var lastChar = trimmed.Length > 0 ? trimmed[^1] : '\0';
        if (lastChar == ',' || lastChar == ':')
            trimmed = trimmed[..^1];

        // Close all open brackets/braces in reverse order
        while (stack.Count > 0)
            trimmed += stack.Pop();

        // Validate the repaired JSON
        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            return trimmed;
        }
        catch (JsonException)
        {
            return null; // Repair didn't produce valid JSON
        }
    }
}
