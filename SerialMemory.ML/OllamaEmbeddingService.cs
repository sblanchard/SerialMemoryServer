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
public class OllamaEmbeddingService(
    string baseUrl = "http://localhost:11434",
    string model = "nomic-embed-text",
    int embeddingDimension = 768)
    : IEmbeddingService, IDisposable
{
    private readonly HttpClient _httpClient = new()
    {
        BaseAddress = new Uri(baseUrl),
        Timeout = TimeSpan.FromSeconds(60)
    };

    public int EmbeddingDimension => embeddingDimension;

    // Approximate max characters for the model's context window.
    // nomic-embed-text: 8192 tokens (~32000 chars)
    private const int MaxChars = 30000;

    public async Task<float[]> EmbedTextAsync(string text, CancellationToken cancellationToken = default)
    {
        var truncated = text.Length > MaxChars ? text[..MaxChars] : text;

        var request = new OllamaEmbedRequest
        {
            Model = model,
            Prompt = truncated
        };

        var response = await _httpClient.PostAsJsonAsync("/api/embeddings", request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<OllamaEmbedResponse>(cancellationToken);

        if (result?.Embedding == null || result.Embedding.Length == 0)
        {
            throw new Exception($"Ollama returned empty embedding. Is model '{model}' installed? Run: ollama pull {model}");
        }

        return result.Embedding;
    }

    public async Task<List<float[]>> EmbedBatchAsync(List<string> texts, CancellationToken cancellationToken = default)
    {
        if (texts.Count == 0)
            return [];

        if (texts.Count == 1)
            return [await EmbedTextAsync(texts[0], cancellationToken)];

        // Parallelize with bounded concurrency to avoid overwhelming Ollama
        var semaphore = new SemaphoreSlim(4);
        var tasks = texts.Select(async text =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                return await EmbedTextAsync(text, cancellationToken);
            }
            finally
            {
                semaphore.Release();
            }
        });

        var results = await Task.WhenAll(tasks);
        return results.ToList();
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
