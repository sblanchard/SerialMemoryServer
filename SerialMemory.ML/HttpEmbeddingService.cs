using SerialMemory.Core.Interfaces;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace SerialMemory.ML;

/// <summary>
/// Embedding service that calls a Python HTTP API
/// Best option: Fast, reliable, and easy to debug
///
/// Start the Python service first:
/// cd SerialMemory.Mcp.Python
/// python -c "from fastapi import FastAPI; from sentence_transformers import SentenceTransformer; app = FastAPI(); model = SentenceTransformer('sentence-transformers/all-MiniLM-L6-v2'); @app.post('/embed'); def embed(request: dict): return {'embedding': model.encode(request['text']).tolist()}; import uvicorn; uvicorn.run(app, port=8765)"
///
/// Or use the provided Python script: python tools/embedding_http_service.py
/// </summary>
public class HttpEmbeddingService(string serviceUrl = "http://localhost:8765", int embeddingDimension = 384)
    : IEmbeddingService, IDisposable
{
    private readonly HttpClient _httpClient = new()
    {
        BaseAddress = new Uri(serviceUrl),
        Timeout = TimeSpan.FromSeconds(30)
    };

    public int EmbeddingDimension => embeddingDimension;

    public async Task<float[]> EmbedTextAsync(string text, CancellationToken cancellationToken = default)
    {
        var request = new EmbedRequest { Text = text };
        var response = await _httpClient.PostAsJsonAsync("/embed", request, cancellationToken);

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<EmbedResponse>(cancellationToken);
        return result?.Embedding ?? throw new Exception("Failed to get embedding from service");
    }

    public async Task<List<float[]>> EmbedBatchAsync(List<string> texts, CancellationToken cancellationToken = default)
    {
        var request = new EmbedBatchRequest { Texts = texts };
        var response = await _httpClient.PostAsJsonAsync("/embed-batch", request, cancellationToken);

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<EmbedBatchResponse>(cancellationToken);
        return result?.Embeddings ?? throw new Exception("Failed to get embeddings from service");
    }

    public void Dispose()
    {
        _httpClient?.Dispose();
    }

    private record EmbedRequest
    {
        [JsonPropertyName("text")]
        public required string Text { get; init; }
    }

    private record EmbedResponse
    {
        [JsonPropertyName("embedding")]
        public required float[] Embedding { get; init; }
    }

    private record EmbedBatchRequest
    {
        [JsonPropertyName("texts")]
        public required List<string> Texts { get; init; }
    }

    private record EmbedBatchResponse
    {
        [JsonPropertyName("embeddings")]
        public required List<float[]> Embeddings { get; init; }
    }
}
