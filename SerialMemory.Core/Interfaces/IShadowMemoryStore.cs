using SerialMemory.Core.Models;

namespace SerialMemory.Core.Interfaces;

/// <summary>
/// Store for shadow memory branches - isolated from main memory.
/// All operations are scoped to tenant for multi-tenant isolation.
/// </summary>
public interface IShadowMemoryStore
{
    #region Branch Operations

    /// <summary>Create a new shadow branch.</summary>
    Task<Guid> CreateBranchAsync(ShadowBranch branch, CancellationToken ct = default);

    /// <summary>Get branch by ID.</summary>
    Task<ShadowBranch?> GetBranchAsync(Guid branchId, CancellationToken ct = default);

    /// <summary>Get all active branches for tenant.</summary>
    Task<List<ShadowBranch>> GetActiveBranchesAsync(string tenantId, CancellationToken ct = default);

    /// <summary>Get child branches of a parent branch.</summary>
    Task<List<ShadowBranch>> GetChildBranchesAsync(Guid parentBranchId, CancellationToken ct = default);

    /// <summary>Update branch status.</summary>
    Task UpdateBranchStatusAsync(Guid branchId, ShadowBranchStatus status, CancellationToken ct = default);

    /// <summary>Update branch confidence.</summary>
    Task UpdateBranchConfidenceAsync(Guid branchId, float confidence, CancellationToken ct = default);

    /// <summary>Freeze branch - no more memories can be added.</summary>
    Task FreezeBranchAsync(Guid branchId, CancellationToken ct = default);

    /// <summary>Get branch statistics.</summary>
    Task<ShadowBranchStats> GetBranchStatsAsync(Guid branchId, CancellationToken ct = default);

    #endregion

    #region Shadow Memory Operations

    /// <summary>Create a shadow memory in a branch.</summary>
    Task<Guid> CreateShadowMemoryAsync(ShadowMemory memory, CancellationToken ct = default);

    /// <summary>Get shadow memory by ID.</summary>
    Task<ShadowMemory?> GetShadowMemoryAsync(Guid memoryId, CancellationToken ct = default);

    /// <summary>Get all memories in a branch.</summary>
    Task<List<ShadowMemory>> GetBranchMemoriesAsync(Guid branchId, CancellationToken ct = default);

    /// <summary>Search shadow memories by embedding within a branch.</summary>
    Task<List<ShadowMemory>> SearchShadowMemoriesAsync(
        Guid branchId,
        float[] queryEmbedding,
        int limit = 10,
        float threshold = 0.7f,
        CancellationToken ct = default);

    /// <summary>Update shadow memory confidence.</summary>
    Task UpdateShadowMemoryConfidenceAsync(Guid memoryId, float confidence, CancellationToken ct = default);

    #endregion

    #region Shadow Entity Operations

    /// <summary>Create a shadow entity in a branch.</summary>
    Task<Guid> CreateShadowEntityAsync(ShadowEntity entity, CancellationToken ct = default);

    /// <summary>Get all entities in a branch.</summary>
    Task<List<ShadowEntity>> GetBranchEntitiesAsync(Guid branchId, CancellationToken ct = default);

    #endregion

    #region Shadow Relationship Operations

    /// <summary>Create a shadow relationship in a branch.</summary>
    Task<Guid> CreateShadowRelationshipAsync(ShadowRelationship relationship, CancellationToken ct = default);

    /// <summary>Get all relationships in a branch.</summary>
    Task<List<ShadowRelationship>> GetBranchRelationshipsAsync(Guid branchId, CancellationToken ct = default);

    #endregion

    #region Promote/Discard Operations

    /// <summary>
    /// Promote all memories from a branch to main memory.
    /// Branch is marked as Promoted after successful promotion.
    /// </summary>
    Task<PromotionResult> PromoteBranchAsync(
        Guid branchId,
        IKnowledgeGraphStore mainStore,
        CancellationToken ct = default);

    /// <summary>
    /// Promote specific memories from a branch to main memory.
    /// </summary>
    Task<PromotionResult> PromoteMemoriesAsync(
        Guid branchId,
        List<Guid> memoryIds,
        IKnowledgeGraphStore mainStore,
        CancellationToken ct = default);

    /// <summary>
    /// Discard a branch and all its contents.
    /// </summary>
    Task<DiscardResult> DiscardBranchAsync(Guid branchId, string? reason = null, CancellationToken ct = default);

    /// <summary>
    /// Discard specific memories from a branch.
    /// </summary>
    Task DiscardMemoriesAsync(Guid branchId, List<Guid> memoryIds, CancellationToken ct = default);

    #endregion

    #region Maintenance Operations

    /// <summary>Get expired branches that should be auto-discarded.</summary>
    Task<List<ShadowBranch>> GetExpiredBranchesAsync(CancellationToken ct = default);

    /// <summary>Get branches below confidence threshold for auto-discard.</summary>
    Task<List<ShadowBranch>> GetLowConfidenceBranchesAsync(CancellationToken ct = default);

    /// <summary>Cleanup all discarded branches older than specified days.</summary>
    Task<int> CleanupDiscardedBranchesAsync(int olderThanDays = 7, CancellationToken ct = default);

    #endregion
}
