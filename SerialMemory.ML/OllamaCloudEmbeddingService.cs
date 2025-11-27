using SerialMemory.Core.Interfaces;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace SerialMemory.ML;

/// <summary>
/// Embedding service that uses Ollama Cloud API.
/// Requires an API key from https://ollama.com/cloud
///
/// Recommended models:
/// - nomic-embed-text (768 dim) - Best quality/speed balance
/// - mxbai-embed-large (1024 dim) - Higher quality
/// - all-minilm (384 dim) - Fast, compatible with existing DB
/// </summary>
public class OllamaCloudEmbeddingService : IEmbeddingService, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _model;
    private readonly int _embeddingDimension;

    public OllamaCloudEmbeddingService(
        string apiKey,
        string model = "nomic-embed-text",
        int embeddingDimension = 768,
        string baseUrl = "https://api.ollama.com")
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("Ollama Cloud API key is required", nameof(apiKey));

        _model = model;
        _embeddingDimension = embeddingDimension;

        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = TimeSpan.FromSeconds(60)
        };
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    public int EmbeddingDimension => _embeddingDimension;

    public async Task<float[]> EmbedTextAsync(string text, CancellationToken cancellationToken = default)
    {
        var request = new OllamaCloudEmbedRequest
        {
            Model = _model,
            Input = text
        };

        var response = await _httpClient.PostAsJsonAsync("/api/embed", request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"Ollama Cloud API error ({response.StatusCode}): {errorContent}");
        }

        var result = await response.Content.ReadFromJsonAsync<OllamaCloudEmbedResponse>(cancellationToken);

        if (result?.Embeddings == null || result.Embeddings.Count == 0 || result.Embeddings[0].Length == 0)
        {
            throw new Exception($"Ollama Cloud returned empty embedding for model '{_model}'");
        }

        return result.Embeddings[0];
    }

    public async Task<List<float[]>> EmbedBatchAsync(List<string> texts, CancellationToken cancellationToken = default)
    {
        // Ollama Cloud supports batch embedding
        var request = new OllamaCloudBatchEmbedRequest
        {
            Model = _model,
            Input = texts
        };

        var response = await _httpClient.PostAsJsonAsync("/api/embed", request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"Ollama Cloud API error ({response.StatusCode}): {errorContent}");
        }

        var result = await response.Content.ReadFromJsonAsync<OllamaCloudEmbedResponse>(cancellationToken);

        if (result?.Embeddings == null)
        {
            throw new Exception($"Ollama Cloud returned null embeddings for model '{_model}'");
        }

        return result.Embeddings;
    }

    public void Dispose()
    {
        _httpClient?.Dispose();
    }

    private record OllamaCloudEmbedRequest
    {
        [JsonPropertyName("model")]
        public required string Model { get; init; }

        [JsonPropertyName("input")]
        public required string Input { get; init; }
    }

    private record OllamaCloudBatchEmbedRequest
    {
        [JsonPropertyName("model")]
        public required string Model { get; init; }

        [JsonPropertyName("input")]
        public required List<string> Input { get; init; }
    }

    private record OllamaCloudEmbedResponse
    {
        [JsonPropertyName("model")]
        public string? Model { get; init; }

        [JsonPropertyName("embeddings")]
        public List<float[]>? Embeddings { get; init; }
    }
}
