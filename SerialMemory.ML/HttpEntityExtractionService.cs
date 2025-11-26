using SerialMemory.Core.Interfaces;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace SerialMemory.ML;

/// <summary>
/// Entity extraction service that calls an HTTP API powered by Ollama/LLM
///
/// Start the Python service first:
/// cd SerialMemory.Mcp.Python
/// python tools/extraction_http_service.py --model phi3
///
/// Default port: 8766
/// </summary>
public class HttpEntityExtractionService(string serviceUrl = "http://localhost:8766")
    : IEntityExtractionService, IDisposable
{
    private readonly HttpClient _httpClient = new()
    {
        BaseAddress = new Uri(serviceUrl),
        Timeout = TimeSpan.FromSeconds(60) // LLM can be slow
    };

    // LLM can be slow

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
        if (string.IsNullOrWhiteSpace(text) || text.Length < 10)
            return (new List<ExtractedEntity>(), new List<ExtractedRelationship>());

        try
        {
            var request = new ExtractRequest { Text = text };
            var response = await _httpClient.PostAsJsonAsync("/extract", request, cancellationToken);

            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<ExtractResponse>(cancellationToken);

            if (result == null)
                return (new List<ExtractedEntity>(), new List<ExtractedRelationship>());

            // Convert API response to domain objects
            var entities = result.Entities.Select(e => new ExtractedEntity(
                Text: e.Text,
                Label: e.Label,
                Start: text.IndexOf(e.Text, StringComparison.OrdinalIgnoreCase),
                End: text.IndexOf(e.Text, StringComparison.OrdinalIgnoreCase) + e.Text.Length,
                Confidence: e.Confidence
            )).ToList();

            var relationships = result.Relationships.Select(r => new ExtractedRelationship(
                SourceEntity: r.Source,
                TargetEntity: r.Target,
                RelationType: r.Type,
                Confidence: r.Confidence
            )).ToList();

            return (entities, relationships);
        }
        catch (HttpRequestException)
        {
            // Service unavailable, return empty results
            return (new List<ExtractedEntity>(), new List<ExtractedRelationship>());
        }
        catch (TaskCanceledException)
        {
            // Timeout, return empty results
            return (new List<ExtractedEntity>(), new List<ExtractedRelationship>());
        }
    }

    public void Dispose()
    {
        _httpClient?.Dispose();
    }

    // Request/Response DTOs
    private record ExtractRequest
    {
        [JsonPropertyName("text")]
        public required string Text { get; init; }
    }

    private record ExtractResponse
    {
        [JsonPropertyName("entities")]
        public List<EntityDto> Entities { get; init; } = new();

        [JsonPropertyName("relationships")]
        public List<RelationshipDto> Relationships { get; init; } = new();
    }

    private record EntityDto
    {
        [JsonPropertyName("text")]
        public string Text { get; init; } = "";

        [JsonPropertyName("label")]
        public string Label { get; init; } = "";

        [JsonPropertyName("confidence")]
        public float Confidence { get; init; } = 0.9f;
    }

    private record RelationshipDto
    {
        [JsonPropertyName("source")]
        public string Source { get; init; } = "";

        [JsonPropertyName("target")]
        public string Target { get; init; } = "";

        [JsonPropertyName("type")]
        public string Type { get; init; } = "";

        [JsonPropertyName("confidence")]
        public float Confidence { get; init; } = 0.8f;
    }
}
