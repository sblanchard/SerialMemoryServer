using SerialMemory.Core.Interfaces;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace SerialMemory.ML;

/// <summary>
/// Embedding service that uses Ollama's local embedding API.
/// Pure C#, no Python required - just needs Ollama running locally.
///
/// Recommended models for embeddings:
/// - nomic-embed-text (768 dim) - Best quality/speed balance
/// - mxbai-embed-large (1024 dim) - Higher quality
/// - all-minilm (384 dim) - Fast, compatible with existing DB
///
/// Install a model: ollama pull nomic-embed-text
/// </summary>
public class OllamaEmbeddingService : IEmbeddingService, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _model;
    private readonly int _embeddingDimension;

    public OllamaEmbeddingService(
        string baseUrl = "http://localhost:11434",
        string model = "nomic-embed-text",
        int embeddingDimension = 768)
    {
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = TimeSpan.FromSeconds(60)
        };
        _model = model;
        _embeddingDimension = embeddingDimension;
    }

    public int EmbeddingDimension => _embeddingDimension;

    public async Task<float[]> EmbedTextAsync(string text, CancellationToken cancellationToken = default)
    {
        var request = new OllamaEmbedRequest
        {
            Model = _model,
            Prompt = text
        };

        var response = await _httpClient.PostAsJsonAsync("/api/embeddings", request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<OllamaEmbedResponse>(cancellationToken);

        if (result?.Embedding == null || result.Embedding.Length == 0)
        {
            throw new Exception($"Ollama returned empty embedding. Is model '{_model}' installed? Run: ollama pull {_model}");
        }

        return result.Embedding;
    }

    public async Task<List<float[]>> EmbedBatchAsync(List<string> texts, CancellationToken cancellationToken = default)
    {
        // Ollama doesn't have a native batch endpoint, so we process sequentially
        // Could parallelize with SemaphoreSlim for better performance
        var embeddings = new List<float[]>(texts.Count);

        foreach (var text in texts)
        {
            var embedding = await EmbedTextAsync(text, cancellationToken);
            embeddings.Add(embedding);
        }

        return embeddings;
    }

    public void Dispose()
    {
        _httpClient?.Dispose();
    }

    private record OllamaEmbedRequest
    {
        [JsonPropertyName("model")]
        public required string Model { get; init; }

        [JsonPropertyName("prompt")]
        public required string Prompt { get; init; }
    }

    private record OllamaEmbedResponse
    {
        [JsonPropertyName("embedding")]
        public float[]? Embedding { get; init; }
    }
}
