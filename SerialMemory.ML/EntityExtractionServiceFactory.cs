using SerialMemory.Core.Interfaces;

namespace SerialMemory.ML;

/// <summary>
/// Factory for creating entity extraction services.
/// Supports Ollama (default), HTTP service, or pattern-based extraction.
/// </summary>
public static class EntityExtractionServiceFactory
{
    /// <summary>
    /// Create an entity extraction service based on configuration.
    /// Priority: Ollama (if configured) > HTTP service > Pattern-based
    /// </summary>
    /// <param name="ollamaUrl">Ollama API URL (e.g., http://localhost:11434)</param>
    /// <param name="ollamaModel">Ollama model for extraction (e.g., phi3, llama3.2)</param>
    /// <param name="httpServiceUrl">HTTP extraction service URL (legacy)</param>
    /// <returns>An IEntityExtractionService implementation</returns>
    public static IEntityExtractionService Create(
        string? ollamaUrl = null,
        string? ollamaModel = null,
        string? httpServiceUrl = null)
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

        // Default to pattern-based extraction
        return new PatternEntityExtractionService();
    }
}
