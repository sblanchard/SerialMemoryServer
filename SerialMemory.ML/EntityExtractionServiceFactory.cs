using SerialMemory.Core.Interfaces;

namespace SerialMemory.ML;

/// <summary>
/// Factory for creating entity extraction services.
/// Supports pattern-based (default) or HTTP/Ollama-based extraction.
/// </summary>
public static class EntityExtractionServiceFactory
{
    /// <summary>
    /// Create an entity extraction service based on configuration.
    /// </summary>
    /// <param name="httpServiceUrl">If provided, uses HTTP extraction service (Ollama/LLM). Otherwise uses pattern-based extraction.</param>
    /// <returns>An IEntityExtractionService implementation</returns>
    public static IEntityExtractionService Create(string? httpServiceUrl = null)
    {
        if (!string.IsNullOrWhiteSpace(httpServiceUrl))
        {
            return new HttpEntityExtractionService(httpServiceUrl);
        }

        return new PatternEntityExtractionService();
    }
}
