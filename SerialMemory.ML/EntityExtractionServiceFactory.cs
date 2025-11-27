using SerialMemory.Core.Interfaces;

namespace SerialMemory.ML;

/// <summary>
/// Factory for creating entity extraction services.
/// Supports Ollama (default), HTTP service, or pattern-based extraction.
/// Now defaults to software-aware extraction for engineering contexts.
/// </summary>
public static class EntityExtractionServiceFactory
{
    /// <summary>
    /// Create an entity extraction service based on configuration.
    /// Priority: Ollama (if configured) > HTTP service > Software pattern-based
    /// </summary>
    /// <param name="ollamaUrl">Ollama API URL (e.g., http://localhost:11434)</param>
    /// <param name="ollamaModel">Ollama model for extraction (e.g., phi3, llama3.2)</param>
    /// <param name="httpServiceUrl">HTTP extraction service URL (legacy)</param>
    /// <param name="useSoftwareAware">Use software-aware pattern extraction (default: true)</param>
    /// <returns>An IEntityExtractionService implementation</returns>
    public static IEntityExtractionService Create(
        string? ollamaUrl = null,
        string? ollamaModel = null,
        string? httpServiceUrl = null,
        bool useSoftwareAware = true)
    {
        // Prefer Ollama if URL is provided
        if (!string.IsNullOrWhiteSpace(ollamaUrl))
        {
            var model = string.IsNullOrWhiteSpace(ollamaModel) ? "phi3" : ollamaModel;
            return new OllamaEntityExtractionService(ollamaUrl, model);
        }

        // Fall back to HTTP service if provided
        if (!string.IsNullOrWhiteSpace(httpServiceUrl))
        {
            return new HttpEntityExtractionService(httpServiceUrl);
        }

        // Default to software-aware pattern-based extraction
        // This recognizes REPO, SERVICE, API, DATABASE, etc.
        return useSoftwareAware
            ? new SoftwareEntityExtractionService()
            : new PatternEntityExtractionService();
    }

    /// <summary>
    /// Creates a software-aware entity extraction service.
    /// Recognizes software engineering entities: REPO, SERVICE, API, DATABASE, etc.
    /// </summary>
    public static IEntityExtractionService CreateSoftwareAware()
    {
        return new SoftwareEntityExtractionService();
    }

    /// <summary>
    /// Creates a generic pattern-based extraction service.
    /// Recognizes: PERSON, ORG, GPE, DATE, etc.
    /// </summary>
    public static IEntityExtractionService CreateGeneric()
    {
        return new PatternEntityExtractionService();
    }
}
