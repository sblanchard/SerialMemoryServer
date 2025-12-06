using SerialMemory.Core.Interfaces;
using SerialMemory.ML;
using SerialMemory.Api.LLM;

namespace SerialMemory.Api.Configuration;

/// <summary>
/// Extension methods for LLM and embedding service configuration.
/// </summary>
public static class LlmConfiguration
{
    /// <summary>
    /// Adds LLM and embedding services (OpenAI primary, Ollama fallback).
    /// </summary>
    public static IServiceCollection AddLlmServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var openAiConfig = GetOpenAiConfig(configuration);
        var ollamaConfig = GetOllamaConfig(configuration);

        if (!string.IsNullOrEmpty(openAiConfig.ApiKey))
        {
            // OpenAI is available - use it as primary
            Console.WriteLine($"[INFO] Using OpenAI: chat={openAiConfig.ChatModel}, embed={openAiConfig.EmbedModel}");

            var openAiClient = new OpenAiClient(
                apiKey: openAiConfig.ApiKey,
                chatModel: openAiConfig.ChatModel,
                embedModel: openAiConfig.EmbedModel,
                embeddingDimension: openAiConfig.EmbeddingDimension);

            services.AddSingleton<IEmbeddingService>(openAiClient);
            services.AddSingleton<ILlmService>(openAiClient);
            services.AddSingleton(openAiClient);
        }
        else
        {
            // Fallback to Ollama
            Console.WriteLine($"[INFO] Using Ollama: embed={ollamaConfig.EmbedModel}, llm={ollamaConfig.LlmModel} at {ollamaConfig.BaseUrl}");

            services.AddSingleton<IEmbeddingService>(_ =>
                new OllamaEmbeddingService(ollamaConfig.BaseUrl, ollamaConfig.EmbedModel, ollamaConfig.EmbeddingDimension));
            services.AddSingleton<ILlmService>(_ =>
                new OllamaLlmService(ollamaConfig.BaseUrl, ollamaConfig.LlmModel));
        }

        return services;
    }

    /// <summary>
    /// Adds entity extraction service (HTTP/Ollama if configured, otherwise pattern-based).
    /// </summary>
    public static IServiceCollection AddEntityExtraction(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var extractionServiceUrl = configuration["ExtractionServiceUrl"]
            ?? Environment.GetEnvironmentVariable("EXTRACTION_SERVICE_URL");

        var entityExtractionService = EntityExtractionServiceFactory.Create(extractionServiceUrl);
        Console.WriteLine($"[INFO] Entity extraction: {entityExtractionService.GetType().Name}");

        services.AddSingleton<IEntityExtractionService>(_ => entityExtractionService);
        services.AddSingleton<SerialMemory.Infrastructure.MemoryLayer.CognitiveLayerService>();

        return services;
    }

    private static OpenAiConfig GetOpenAiConfig(IConfiguration configuration)
    {
        return new OpenAiConfig
        {
            ApiKey = configuration["OpenAI:ApiKey"]
                ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY"),
            ChatModel = configuration["OpenAI:Model"]
                ?? Environment.GetEnvironmentVariable("OPENAI_MODEL")
                ?? "gpt-4.1-mini",
            EmbedModel = configuration["OpenAI:EmbedModel"]
                ?? Environment.GetEnvironmentVariable("OPENAI_EMBED_MODEL")
                ?? "text-embedding-3-small",
            EmbeddingDimension = 1536
        };
    }

    private static OllamaConfig GetOllamaConfig(IConfiguration configuration)
    {
        var embeddingDim = int.TryParse(
            configuration["Ollama:EmbeddingDim"] ?? Environment.GetEnvironmentVariable("OLLAMA_EMBEDDING_DIM"),
            out var dim) ? dim : 768;

        return new OllamaConfig
        {
            BaseUrl = configuration["Ollama:Url"]
                ?? Environment.GetEnvironmentVariable("OLLAMA_BASE_URL")
                ?? Environment.GetEnvironmentVariable("OLLAMA_URL")
                ?? "http://localhost:11434",
            EmbedModel = configuration["Ollama:Model"]
                ?? Environment.GetEnvironmentVariable("OLLAMA_MODEL")
                ?? "nomic-embed-text",
            LlmModel = configuration["Ollama:LlmModel"]
                ?? Environment.GetEnvironmentVariable("OLLAMA_LLM_MODEL")
                ?? "qwen2.5:7b",
            EmbeddingDimension = embeddingDim
        };
    }

    private sealed record OpenAiConfig
    {
        public string? ApiKey { get; init; }
        public required string ChatModel { get; init; }
        public required string EmbedModel { get; init; }
        public int EmbeddingDimension { get; init; }
    }

    private sealed record OllamaConfig
    {
        public required string BaseUrl { get; init; }
        public required string EmbedModel { get; init; }
        public required string LlmModel { get; init; }
        public int EmbeddingDimension { get; init; }
    }
}
