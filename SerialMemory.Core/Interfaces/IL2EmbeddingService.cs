namespace SerialMemory.Core.Interfaces;

/// <summary>
/// Service for indexing and managing L2 (summary) embeddings.
/// L2 summaries are the canonical retrieval surface for personalized RAG.
/// </summary>
public interface IL2EmbeddingService
{
    /// <summary>
    /// Indexes the L2 summary embedding for a specific memory.
    /// Should be called after L2 layer classification completes.
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="memoryId">Memory identifier</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>The L2 embedding ID</returns>
    Task<Guid> IndexL2Async(Guid tenantId, Guid memoryId, CancellationToken ct = default);

    /// <summary>
    /// Bulk reindexes all L2 embeddings for a tenant.
    /// Useful for model upgrades or data migration.
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Number of memories reindexed</returns>
    Task<int> BulkReindexTenantAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>
    /// Removes the L2 embedding for a memory.
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="memoryId">Memory identifier</param>
    /// <param name="ct">Cancellation token</param>
    Task RemoveL2EmbeddingAsync(Guid tenantId, Guid memoryId, CancellationToken ct = default);

    /// <summary>
    /// Checks if a memory has an L2 embedding indexed.
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="memoryId">Memory identifier</param>
    /// <param name="ct">Cancellation token</param>
    Task<bool> HasL2EmbeddingAsync(Guid tenantId, Guid memoryId, CancellationToken ct = default);
}
