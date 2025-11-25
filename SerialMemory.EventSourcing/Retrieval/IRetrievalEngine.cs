namespace SerialMemory.EventSourcing.Retrieval;

/// <summary>
/// Multi-axis retrieval engine interface.
/// Implements composite scoring beyond pure vector search.
/// </summary>
public interface IRetrievalEngine
{
    /// <summary>
    /// Search memories using multi-axis composite scoring.
    /// </summary>
    Task<IReadOnlyList<RetrievalResult>> SearchAsync(
        RetrievalQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get a single memory by ID with integrity verification.
    /// </summary>
    Task<RetrievalResult?> GetByIdAsync(
        Guid memoryId,
        bool verifyIntegrity = true,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Find memories related to a given memory via causal graph.
    /// </summary>
    Task<IReadOnlyList<RetrievalResult>> GetRelatedMemoriesAsync(
        Guid memoryId,
        int maxDepth = 2,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Find potential duplicate memories.
    /// </summary>
    Task<IReadOnlyList<(Guid MemoryA, Guid MemoryB, float Similarity)>> FindDuplicatesAsync(
        float similarityThreshold = 0.95f,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Update user affinity scores based on interaction.
    /// </summary>
    Task RecordInteractionAsync(
        string userId,
        Guid memoryId,
        string interactionType,
        CancellationToken cancellationToken = default);
}
