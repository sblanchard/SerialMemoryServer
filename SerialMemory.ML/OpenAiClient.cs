using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Embeddings;
using SerialMemory.Core.Interfaces;

namespace SerialMemory.ML;

/// <summary>
/// OpenAI client for chat completions and embeddings.
/// Supports tenant-level API keys with fallback to system key.
///
/// Environment variables:
/// - OPENAI_API_KEY: System-level API key (fallback)
/// - OPENAI_ENDPOINT: Custom endpoint (optional, for Azure OpenAI)
/// - OPENAI_MODEL: Default chat model (default: gpt-5-nano-2025-08-07)
/// - OPENAI_EMBED_MODEL: Default embedding model (default: text-embedding-3-small)
/// </summary>
public sealed class OpenAiClient : IEmbeddingService, ILlmService, IDisposable
{
    private readonly OpenAIClient _client;
    private readonly string _chatModel;
    private readonly string _embedModel;
    private readonly ILogger<OpenAiClient>? _logger;
    private readonly int _embeddingDimension;

    public int EmbeddingDimension => _embeddingDimension;

    // ILlmService properties
    public string ProviderName => "OpenAI";
    public string ModelName => _chatModel;

    /// <summary>
    /// Creates an OpenAI client with the specified configuration.
    /// </summary>
    /// <param name="apiKey">API key (falls back to OPENAI_API_KEY env var)</param>
    /// <param name="endpoint">Custom endpoint (optional, for Azure OpenAI)</param>
    /// <param name="chatModel">Chat model (default: gpt-5-nano-2025-08-07)</param>
    /// <param name="embedModel">Embedding model (default: text-embedding-3-small)</param>
    /// <param name="embeddingDimension">Embedding dimension (default: 1536 for text-embedding-3-small)</param>
    /// <param name="logger">Optional logger</param>
    public OpenAiClient(
        string? apiKey = null,
        string? endpoint = null,
        string? chatModel = null,
        string? embedModel = null,
        int embeddingDimension = 1536,
        ILogger<OpenAiClient>? logger = null)
    {
        var key = apiKey
            ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY")
            ?? throw new InvalidOperationException(
                "OpenAI API key not provided. Set OPENAI_API_KEY environment variable or pass apiKey parameter.");

        var customEndpoint = endpoint ?? Environment.GetEnvironmentVariable("OPENAI_ENDPOINT");

        _chatModel = chatModel
            ?? Environment.GetEnvironmentVariable("OPENAI_MODEL")
            ?? "gpt-5-nano-2025-08-07";

        _embedModel = embedModel
            ?? Environment.GetEnvironmentVariable("OPENAI_EMBED_MODEL")
            ?? "text-embedding-3-small";

        _embeddingDimension = embeddingDimension;
        _logger = logger;

        var options = new OpenAIClientOptions();
        if (!string.IsNullOrEmpty(customEndpoint))
        {
            options.Endpoint = new Uri(customEndpoint);
        }

        _client = new OpenAIClient(new System.ClientModel.ApiKeyCredential(key), options);

        _logger?.LogInformation(
            "OpenAI client initialized with chat model {ChatModel}, embed model {EmbedModel}",
            _chatModel, _embedModel);
    }

    #region Embeddings

    /// <summary>
    /// Generate embedding for a single text.
    /// </summary>
    public async Task<float[]> EmbedTextAsync(string text, CancellationToken cancellationToken = default)
    {
        var embeddingClient = _client.GetEmbeddingClient(_embedModel);

        var response = await embeddingClient.GenerateEmbeddingAsync(
            text,
            new EmbeddingGenerationOptions { Dimensions = _embeddingDimension },
            cancellationToken);

        return response.Value.ToFloats().ToArray();
    }

    /// <summary>
    /// Generate embeddings for multiple texts efficiently.
    /// </summary>
    public async Task<List<float[]>> EmbedBatchAsync(List<string> texts, CancellationToken cancellationToken = default)
    {
        var embeddingClient = _client.GetEmbeddingClient(_embedModel);

        var response = await embeddingClient.GenerateEmbeddingsAsync(
            texts,
            new EmbeddingGenerationOptions { Dimensions = _embeddingDimension },
            cancellationToken);

        return response.Value.Select(e => e.ToFloats().ToArray()).ToList();
    }

    #endregion

    #region Chat Completions

    /// <summary>
    /// Simple chat completion.
    /// </summary>
    public async Task<string> ChatAsync(
        string userMessage,
        string? systemPrompt = null,
        float temperature = 0.7f,
        int? maxTokens = null,
        CancellationToken cancellationToken = default)
    {
        var messages = new List<ChatMessage>();

        if (!string.IsNullOrEmpty(systemPrompt))
        {
            messages.Add(ChatMessage.CreateSystemMessage(systemPrompt));
        }

        messages.Add(ChatMessage.CreateUserMessage(userMessage));

        return await ChatAsync(messages, temperature, maxTokens, cancellationToken);
    }

    /// <summary>
    /// Chat completion with message history.
    /// </summary>
    public async Task<string> ChatAsync(
        IEnumerable<ChatMessage> messages,
        float temperature = 0.7f,
        int? maxTokens = null,
        CancellationToken cancellationToken = default)
    {
        var chatClient = _client.GetChatClient(_chatModel);

        var options = new ChatCompletionOptions
        {
            Temperature = temperature
        };

        if (maxTokens.HasValue)
        {
            options.MaxOutputTokenCount = maxTokens.Value;
        }

        var response = await chatClient.CompleteChatAsync(messages, options, cancellationToken);

        return response.Value.Content[0].Text;
    }

    /// <summary>
    /// Streaming chat completion.
    /// </summary>
    public async IAsyncEnumerable<string> ChatStreamAsync(
        string userMessage,
        string? systemPrompt = null,
        float temperature = 0.7f,
        int? maxTokens = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var messages = new List<ChatMessage>();

        if (!string.IsNullOrEmpty(systemPrompt))
        {
            messages.Add(ChatMessage.CreateSystemMessage(systemPrompt));
        }

        messages.Add(ChatMessage.CreateUserMessage(userMessage));

        await foreach (var chunk in ChatStreamAsync(messages, temperature, maxTokens, cancellationToken))
        {
            yield return chunk;
        }
    }

    /// <summary>
    /// Streaming chat completion with message history.
    /// </summary>
    public async IAsyncEnumerable<string> ChatStreamAsync(
        IEnumerable<ChatMessage> messages,
        float temperature = 0.7f,
        int? maxTokens = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var chatClient = _client.GetChatClient(_chatModel);

        var options = new ChatCompletionOptions
        {
            Temperature = temperature
        };

        if (maxTokens.HasValue)
        {
            options.MaxOutputTokenCount = maxTokens.Value;
        }

        await foreach (var update in chatClient.CompleteChatStreamingAsync(messages, options, cancellationToken))
        {
            foreach (var part in update.ContentUpdate)
            {
                if (!string.IsNullOrEmpty(part.Text))
                {
                    yield return part.Text;
                }
            }
        }
    }

    /// <summary>
    /// Chat completion with structured JSON output.
    /// </summary>
    /// <typeparam name="T">Type to deserialize the response to</typeparam>
    public async Task<T?> ChatStructuredAsync<T>(
        string userMessage,
        string? systemPrompt = null,
        float temperature = 0.3f,
        CancellationToken cancellationToken = default) where T : class
    {
        var jsonSystemPrompt = (systemPrompt ?? "") +
            "\n\nIMPORTANT: You MUST respond with valid JSON only. No markdown, no explanations, just the JSON object.";

        var response = await ChatAsync(userMessage, jsonSystemPrompt, temperature, cancellationToken: cancellationToken);

        // Try to extract JSON from response
        var json = ExtractJson(response);

        if (string.IsNullOrEmpty(json))
        {
            _logger?.LogWarning("Failed to extract JSON from response: {Response}", response);
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch (JsonException ex)
        {
            _logger?.LogWarning(ex, "Failed to deserialize JSON response: {Json}", json);
            return null;
        }
    }

    private static string? ExtractJson(string text)
    {
        // Try to find JSON object or array
        var start = text.IndexOf('{');
        var arrayStart = text.IndexOf('[');

        if (start < 0 && arrayStart < 0)
            return null;

        if (arrayStart >= 0 && (start < 0 || arrayStart < start))
            start = arrayStart;

        var end = text.LastIndexOf(start == arrayStart ? ']' : '}');

        if (end <= start)
            return null;

        return text.Substring(start, end - start + 1);
    }

    #endregion

    #region Entity Extraction (using ChatStructured)

    /// <summary>
    /// Extract entities and relationships from text using OpenAI.
    /// </summary>
    public async Task<(List<ExtractedEntity> Entities, List<ExtractedRelationship> Relationships)> ExtractEntitiesAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        const string systemPrompt = """
            You are an entity extraction engine. Extract entities and relationships from the input text.

            Entity types: PERSON, ORG, TEAM, REPO, PROJECT, SERVICE, MODULE, API, DATABASE, TABLE,
            MESSAGE, CONFIG, ENV, VERSION, DEPLOYMENT, INCIDENT, BUG, TEST, TECH, PRODUCT, EVENT, DATE,
            COMPONENT, ASSEMBLY, MATERIAL, SIGNAL, POWER_RAIL, VOLTAGE, CURRENT, FREQUENCY, TEMPERATURE,
            PRESSURE, FORCE, TORQUE, DIMENSION, TOLERANCE, STANDARD, TOOL, LOCATION

            Relationship types: OWNS, MAINTAINS, WORKS_AT, WORKS_ON, IMPLEMENTS, CALLS, DEPENDS_ON,
            USES, INTEGRATES_WITH, DEPLOYED_TO, RUNS_ON, EMITS, CONSUMES, CONFIGURED_BY, TRIGGERS,
            VERSIONED_AS, CAUSED, FIXED_BY, TESTS, RELATED_TO, CONNECTS_TO, MOUNTED_ON, FEEDS,
            CONVERTS, MEASURES, CONTROLS, CARRIES, REQUIRES, LIMITED_BY, COMPLIES_WITH, FAILS_UNDER,
            CALIBRATED_BY, PART_OF

            Output JSON format:
            {"entities":[{"text":"...","label":"..."}],"relationships":[{"source":"...","target":"...","type":"..."}]}
            """;

        var result = await ChatStructuredAsync<ExtractionResult>(text, systemPrompt, 0.1f, cancellationToken);

        if (result == null)
            return ([], []);

        var entities = result.Entities?
            .Where(e => !string.IsNullOrWhiteSpace(e.Text) && !string.IsNullOrWhiteSpace(e.Label))
            .Select(e => new ExtractedEntity(
                Text: e.Text!.Trim(),
                Label: e.Label!.ToUpperInvariant().Trim(),
                Start: text.IndexOf(e.Text!, StringComparison.OrdinalIgnoreCase),
                End: text.IndexOf(e.Text!, StringComparison.OrdinalIgnoreCase) + e.Text!.Length,
                Confidence: 0.9f))
            .ToList() ?? [];

        var relationships = result.Relationships?
            .Where(r => !string.IsNullOrWhiteSpace(r.Source) && !string.IsNullOrWhiteSpace(r.Target))
            .Select(r => new ExtractedRelationship(
                SourceEntity: r.Source!.Trim(),
                TargetEntity: r.Target!.Trim(),
                RelationType: r.Type?.ToUpperInvariant().Trim() ?? "RELATED_TO",
                Confidence: 0.85f))
            .ToList() ?? [];

        return (entities, relationships);
    }

    private sealed record ExtractionResult
    {
        public List<EntityDto>? Entities { get; init; }
        public List<RelationshipDto>? Relationships { get; init; }
    }

    private sealed record EntityDto
    {
        public string? Text { get; init; }
        public string? Label { get; init; }
    }

    private sealed record RelationshipDto
    {
        public string? Source { get; init; }
        public string? Target { get; init; }
        public string? Type { get; init; }
    }

    #endregion

    public void Dispose()
    {
        // OpenAIClient doesn't implement IDisposable, but we keep this for interface consistency
    }
}

/// <summary>
/// Factory for creating OpenAI clients with tenant-specific configuration.
/// </summary>
public sealed class OpenAiClientFactory
{
    private readonly string? _systemApiKey;
    private readonly string? _systemEndpoint;
    private readonly string _defaultChatModel;
    private readonly string _defaultEmbedModel;
    private readonly ILoggerFactory? _loggerFactory;

    public OpenAiClientFactory(
        string? systemApiKey = null,
        string? systemEndpoint = null,
        string? defaultChatModel = null,
        string? defaultEmbedModel = null,
        ILoggerFactory? loggerFactory = null)
    {
        _systemApiKey = systemApiKey ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        _systemEndpoint = systemEndpoint ?? Environment.GetEnvironmentVariable("OPENAI_ENDPOINT");
        _defaultChatModel = defaultChatModel
            ?? Environment.GetEnvironmentVariable("OPENAI_MODEL")
            ?? "gpt-5-nano-2025-08-07";
        _defaultEmbedModel = defaultEmbedModel
            ?? Environment.GetEnvironmentVariable("OPENAI_EMBED_MODEL")
            ?? "text-embedding-3-small";
        _loggerFactory = loggerFactory;
    }

    /// <summary>
    /// Create an OpenAI client for a tenant, using tenant key if available, otherwise system key.
    /// </summary>
    public OpenAiClient CreateClient(
        string? tenantApiKey = null,
        string? tenantEndpoint = null,
        string? chatModel = null,
        string? embedModel = null)
    {
        var apiKey = !string.IsNullOrEmpty(tenantApiKey) ? tenantApiKey : _systemApiKey;
        var endpoint = !string.IsNullOrEmpty(tenantEndpoint) ? tenantEndpoint : _systemEndpoint;

        return new OpenAiClient(
            apiKey: apiKey,
            endpoint: endpoint,
            chatModel: chatModel ?? _defaultChatModel,
            embedModel: embedModel ?? _defaultEmbedModel,
            logger: _loggerFactory?.CreateLogger<OpenAiClient>());
    }

    /// <summary>
    /// Check if OpenAI is available (system key is configured).
    /// </summary>
    public bool IsAvailable => !string.IsNullOrEmpty(_systemApiKey);
}
