using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using SerialMemory.Core.Interfaces;

namespace SerialMemory.ML;

/// <summary>
/// LLM service using Ollama for chat completions.
/// Pure C#, no Python required - just needs Ollama running locally.
/// </summary>
public sealed class OllamaLlmService(
    string baseUrl = "http://localhost:11434",
    string model = "qwen2.5:7b")
    : ILlmService, IDisposable
{
    private readonly HttpClient _httpClient = new()
    {
        BaseAddress = new Uri(baseUrl),
        Timeout = TimeSpan.FromSeconds(120)
    };

    public string ProviderName => "Ollama";
    public string ModelName => model;

    public async Task<string> ChatAsync(
        string userMessage,
        string? systemPrompt = null,
        float temperature = 0.7f,
        int? maxTokens = null,
        CancellationToken cancellationToken = default)
    {
        var messages = new List<OllamaChatMessage>();

        if (!string.IsNullOrEmpty(systemPrompt))
        {
            messages.Add(new OllamaChatMessage { Role = "system", Content = systemPrompt });
        }

        messages.Add(new OllamaChatMessage { Role = "user", Content = userMessage });

        var request = new OllamaChatRequest
        {
            Model = model,
            Messages = messages,
            Stream = false,
            Options = new OllamaOptions
            {
                Temperature = temperature,
                NumPredict = maxTokens ?? 2048
            }
        };

        var response = await _httpClient.PostAsJsonAsync("/api/chat", request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<OllamaChatResponse>(cancellationToken);

        return result?.Message?.Content ?? "";
    }

    public async IAsyncEnumerable<string> ChatStreamAsync(
        string userMessage,
        string? systemPrompt = null,
        float temperature = 0.7f,
        int? maxTokens = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var messages = new List<OllamaChatMessage>();

        if (!string.IsNullOrEmpty(systemPrompt))
        {
            messages.Add(new OllamaChatMessage { Role = "system", Content = systemPrompt });
        }

        messages.Add(new OllamaChatMessage { Role = "user", Content = userMessage });

        var request = new OllamaChatRequest
        {
            Model = model,
            Messages = messages,
            Stream = true,
            Options = new OllamaOptions
            {
                Temperature = temperature,
                NumPredict = maxTokens ?? 2048
            }
        };

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/api/chat")
        {
            Content = JsonContent.Create(request)
        };

        using var response = await _httpClient.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null) break; // End of stream
            if (line.Length == 0) continue;

            var chunk = JsonSerializer.Deserialize<OllamaChatResponse>(line);
            if (!string.IsNullOrEmpty(chunk?.Message?.Content))
            {
                yield return chunk.Message.Content;
            }
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private sealed record OllamaChatRequest
    {
        [JsonPropertyName("model")]
        public required string Model { get; init; }

        [JsonPropertyName("messages")]
        public required List<OllamaChatMessage> Messages { get; init; }

        [JsonPropertyName("stream")]
        public bool Stream { get; init; }

        [JsonPropertyName("options")]
        public OllamaOptions? Options { get; init; }
    }

    private sealed record OllamaChatMessage
    {
        [JsonPropertyName("role")]
        public required string Role { get; init; }

        [JsonPropertyName("content")]
        public required string Content { get; init; }
    }

    private sealed record OllamaOptions
    {
        [JsonPropertyName("temperature")]
        public float Temperature { get; init; }

        [JsonPropertyName("num_predict")]
        public int NumPredict { get; init; }
    }

    private sealed record OllamaChatResponse
    {
        [JsonPropertyName("message")]
        public OllamaChatMessage? Message { get; init; }

        [JsonPropertyName("done")]
        public bool Done { get; init; }
    }
}
