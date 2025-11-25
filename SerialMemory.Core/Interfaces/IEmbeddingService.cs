namespace SerialMemory.Core.Interfaces;

/// <summary>
/// Service for generating semantic embeddings from text
/// </summary>
public interface IEmbeddingService
{
    /// <summary>
    /// Generate embedding for a single text
    /// </summary>
    Task<float[]> EmbedTextAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>
    /// Generate embeddings for multiple texts efficiently
    /// </summary>
    Task<List<float[]>> EmbedBatchAsync(List<string> texts, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get the dimension of embeddings produced by this service
    /// </summary>
    int EmbeddingDimension { get; }
}
