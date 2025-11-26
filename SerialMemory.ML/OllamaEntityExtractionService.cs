using SerialMemory.Core.Interfaces;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SerialMemory.ML;

/// <summary>
/// Entity extraction service using Ollama LLM.
/// Pure C#, no Python required - just needs Ollama running locally with a suitable model.
///
/// Recommended models:
/// - phi3 (3.8B) - Fast, good quality
/// - llama3.2 (3B) - Good balance
/// - mistral (7B) - Higher quality, slower
///
/// Install a model: ollama pull phi3
/// </summary>
public sealed class OllamaEntityExtractionService : IEntityExtractionService, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _model;
    private readonly JsonSerializerOptions _jsonOptions;

    private static readonly string ExtractionPrompt = """
        Extract entities and relationships from the following text.

        Entity types to extract:
        - PERSON: People's names
        - ORG: Organizations, companies, teams
        - GPE: Geographic locations (cities, countries)
        - PRODUCT: Products, software, tools
        - TECH: Technologies, frameworks, languages
        - DATE: Dates and time references
        - EVENT: Events, meetings, releases

        Return ONLY valid JSON in this exact format (no markdown, no explanation):
        {"entities":[{"text":"entity name","label":"TYPE"}],"relationships":[{"source":"entity1","target":"entity2","type":"RELATIONSHIP_TYPE"}]}

        Common relationship types: WORKS_AT, WORKS_ON, CREATED, USES, INTEGRATES_WITH, FOUNDED, KNOWS, LOCATED_IN

        Text to analyze:
        {TEXT}

        JSON output:
        """;

    public OllamaEntityExtractionService(
        string baseUrl = "http://localhost:11434",
        string model = "phi3")
    {
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = TimeSpan.FromSeconds(120)
        };
        _model = model;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }

    public async Task<List<ExtractedEntity>> ExtractEntitiesAsync(string text, CancellationToken cancellationToken = default)
    {
        var (entities, _) = await ExtractAllAsync(text, cancellationToken);
        return entities;
    }

    public async Task<List<ExtractedRelationship>> ExtractRelationshipsAsync(string text, CancellationToken cancellationToken = default)
    {
        var (_, relationships) = await ExtractAllAsync(text, cancellationToken);
        return relationships;
    }

    public async Task<(List<ExtractedEntity> Entities, List<ExtractedRelationship> Relationships)> ExtractAllAsync(
        string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return ([], []);
        }

        // Truncate very long texts to avoid timeout
        const int maxLength = 4000;
        var truncatedText = text.Length > maxLength ? text[..maxLength] : text;

        var prompt = ExtractionPrompt.Replace("{TEXT}", truncatedText);

        var request = new OllamaGenerateRequest
        {
            Model = _model,
            Prompt = prompt,
            Stream = false,
            Options = new OllamaOptions
            {
                Temperature = 0.1f,  // Low temperature for consistent structured output
                NumPredict = 2048
            }
        };

        try
        {
            var response = await _httpClient.PostAsJsonAsync("/api/generate", request, _jsonOptions, cancellationToken);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<OllamaGenerateResponse>(_jsonOptions, cancellationToken);

            if (string.IsNullOrWhiteSpace(result?.Response))
            {
                return ([], []);
            }

            return ParseExtractionResult(result.Response, text);
        }
        catch (HttpRequestException ex)
        {
            throw new Exception($"Ollama API error. Is Ollama running? Is model '{_model}' installed? Run: ollama pull {_model}", ex);
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            throw new Exception($"Ollama request timed out. Text may be too long or model '{_model}' may be slow.", ex);
        }
    }

    private (List<ExtractedEntity> Entities, List<ExtractedRelationship> Relationships) ParseExtractionResult(
        string response, string originalText)
    {
        var entities = new List<ExtractedEntity>();
        var relationships = new List<ExtractedRelationship>();

        try
        {
            // Extract JSON from response (handle markdown code blocks)
            var jsonMatch = Regex.Match(response, @"\{[\s\S]*\}", RegexOptions.Singleline);
            if (!jsonMatch.Success)
            {
                return (entities, relationships);
            }

            var json = jsonMatch.Value;
            var parsed = JsonSerializer.Deserialize<ExtractionResult>(json, _jsonOptions);

            if (parsed?.Entities != null)
            {
                foreach (var entity in parsed.Entities)
                {
                    if (string.IsNullOrWhiteSpace(entity.Text) || string.IsNullOrWhiteSpace(entity.Label))
                        continue;

                    // Find position in original text
                    var start = originalText.IndexOf(entity.Text, StringComparison.OrdinalIgnoreCase);
                    var end = start >= 0 ? start + entity.Text.Length : 0;

                    entities.Add(new ExtractedEntity(
                        Text: entity.Text.Trim(),
                        Label: NormalizeEntityLabel(entity.Label),
                        Start: Math.Max(0, start),
                        End: end,
                        Confidence: 0.85f  // LLM extraction confidence
                    ));
                }
            }

            if (parsed?.Relationships != null)
            {
                foreach (var rel in parsed.Relationships)
                {
                    if (string.IsNullOrWhiteSpace(rel.Source) || string.IsNullOrWhiteSpace(rel.Target))
                        continue;

                    relationships.Add(new ExtractedRelationship(
                        SourceEntity: rel.Source.Trim(),
                        TargetEntity: rel.Target.Trim(),
                        RelationType: NormalizeRelationType(rel.Type ?? "RELATED_TO"),
                        Confidence: 0.75f  // LLM relationship confidence
                    ));
                }
            }
        }
        catch (JsonException)
        {
            // Failed to parse JSON - return empty results
        }

        return (entities, relationships);
    }

    private static string NormalizeEntityLabel(string label)
    {
        var normalized = label.ToUpperInvariant().Trim();

        // Map common variations
        return normalized switch
        {
            "ORGANIZATION" => "ORG",
            "COMPANY" => "ORG",
            "LOCATION" => "GPE",
            "PLACE" => "GPE",
            "TECHNOLOGY" => "TECH",
            "SOFTWARE" => "PRODUCT",
            "TOOL" => "PRODUCT",
            "TIME" => "DATE",
            _ => normalized
        };
    }

    private static string NormalizeRelationType(string relationType)
    {
        // Normalize to UPPER_SNAKE_CASE
        var normalized = relationType.ToUpperInvariant().Trim();
        normalized = Regex.Replace(normalized, @"[\s-]+", "_");
        normalized = Regex.Replace(normalized, @"[^A-Z0-9_]", "");
        return string.IsNullOrEmpty(normalized) ? "RELATED_TO" : normalized;
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    // Request/Response DTOs
    private sealed record OllamaGenerateRequest
    {
        [JsonPropertyName("model")]
        public required string Model { get; init; }

        [JsonPropertyName("prompt")]
        public required string Prompt { get; init; }

        [JsonPropertyName("stream")]
        public bool Stream { get; init; }

        [JsonPropertyName("options")]
        public OllamaOptions? Options { get; init; }
    }

    private sealed record OllamaOptions
    {
        [JsonPropertyName("temperature")]
        public float Temperature { get; init; }

        [JsonPropertyName("num_predict")]
        public int NumPredict { get; init; }
    }

    private sealed record OllamaGenerateResponse
    {
        [JsonPropertyName("response")]
        public string? Response { get; init; }
    }

    private sealed record ExtractionResult
    {
        [JsonPropertyName("entities")]
        public List<EntityDto>? Entities { get; init; }

        [JsonPropertyName("relationships")]
        public List<RelationshipDto>? Relationships { get; init; }
    }

    private sealed record EntityDto
    {
        [JsonPropertyName("text")]
        public string? Text { get; init; }

        [JsonPropertyName("label")]
        public string? Label { get; init; }
    }

    private sealed record RelationshipDto
    {
        [JsonPropertyName("source")]
        public string? Source { get; init; }

        [JsonPropertyName("target")]
        public string? Target { get; init; }

        [JsonPropertyName("type")]
        public string? Type { get; init; }
    }
}
