using Microsoft.Extensions.Logging;
using SerialMemory.Core.Interfaces;

namespace SerialMemory.ML;

/// <summary>
/// Entity extraction service using OpenAI's GPT models.
/// Provides higher quality extraction than Ollama but at higher cost.
/// Use for production workloads requiring maximum accuracy.
/// </summary>
public sealed class OpenAiEntityExtractionService : IEntityExtractionService, IDisposable
{
    private readonly OpenAiClient _client;
    private readonly ILogger<OpenAiEntityExtractionService>? _logger;

    public OpenAiEntityExtractionService(
        string? apiKey = null,
        string? endpoint = null,
        string? model = null,
        ILogger<OpenAiEntityExtractionService>? logger = null)
    {
        _client = new OpenAiClient(
            apiKey: apiKey,
            endpoint: endpoint,
            chatModel: model ?? "gpt-4.1-mini",
            logger: null);
        _logger = logger;
    }

    public OpenAiEntityExtractionService(OpenAiClient client, ILogger<OpenAiEntityExtractionService>? logger = null)
    {
        _client = client;
        _logger = logger;
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

        try
        {
            return await _client.ExtractEntitiesAsync(text, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "OpenAI entity extraction failed");
            return ([], []);
        }
    }

    public void Dispose()
    {
        _client.Dispose();
    }
}
