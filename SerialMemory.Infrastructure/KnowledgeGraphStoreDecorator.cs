using SerialMemory.Core.Interfaces;
using SerialMemory.Core.Models;

namespace SerialMemory.Infrastructure;

/// <summary>
/// Abstract base decorator for IKnowledgeGraphStore.
/// Delegates all methods to the inner store. Subclasses override only the methods they instrument.
/// </summary>
public abstract class KnowledgeGraphStoreDecorator(IKnowledgeGraphStore inner) : IKnowledgeGraphStore
{
    protected readonly IKnowledgeGraphStore Inner = inner;

    // Memory operations
    public virtual Task<Guid> CreateMemoryAsync(Memory memory, CancellationToken cancellationToken = default)
        => Inner.CreateMemoryAsync(memory, cancellationToken);
    public virtual Task UpdateMemoryAsync(Memory memory, CancellationToken cancellationToken = default)
        => Inner.UpdateMemoryAsync(memory, cancellationToken);
    public virtual Task<Memory?> GetMemoryByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => Inner.GetMemoryByIdAsync(id, cancellationToken);
    public virtual Task<List<Memory>> GetRecentMemoriesAsync(int limit = 10, CancellationToken cancellationToken = default)
        => Inner.GetRecentMemoriesAsync(limit, cancellationToken);
    public virtual Task<List<Memory>> SearchMemoriesByEmbeddingAsync(float[] queryEmbedding, int limit = 10, float threshold = 0.7f, string? memoryType = null, CancellationToken cancellationToken = default)
        => Inner.SearchMemoriesByEmbeddingAsync(queryEmbedding, limit, threshold, memoryType, cancellationToken);
    public virtual Task<List<Memory>> SearchMemoriesByTextAsync(string query, int limit = 10, string? memoryType = null, CancellationToken cancellationToken = default)
        => Inner.SearchMemoriesByTextAsync(query, limit, memoryType, cancellationToken);
    public virtual Task<List<Memory>> GetMemoriesByTypeAsync(string memoryType, int limit = 50, CancellationToken cancellationToken = default)
        => Inner.GetMemoriesByTypeAsync(memoryType, limit, cancellationToken);
    public virtual Task<List<Memory>> GetMemoriesBySessionAsync(Guid sessionId, int limit = 100, CancellationToken cancellationToken = default)
        => Inner.GetMemoriesBySessionAsync(sessionId, limit, cancellationToken);
    public virtual Task<List<Memory>> GetMemoriesWithoutEntitiesAsync(int limit = 100, CancellationToken cancellationToken = default)
        => Inner.GetMemoriesWithoutEntitiesAsync(limit, cancellationToken);
    public virtual Task<List<Memory>> GetMemoriesWithNullEmbeddingsAsync(int limit = 100, CancellationToken cancellationToken = default)
        => Inner.GetMemoriesWithNullEmbeddingsAsync(limit, cancellationToken);
    public virtual Task<List<Memory>> GetAllMemoriesAsync(int limit = 100, int offset = 0, CancellationToken cancellationToken = default)
        => Inner.GetAllMemoriesAsync(limit, offset, cancellationToken);
    public virtual Task<List<Memory>> GetMemoriesByDateRangeAsync(DateTime fromUtc, DateTime toUtc, int limit = 100, CancellationToken cancellationToken = default)
        => Inner.GetMemoriesByDateRangeAsync(fromUtc, toUtc, limit, cancellationToken);
    public virtual Task<List<Memory>> SearchMemoriesByEmbeddingInDateRangeAsync(float[] queryEmbedding, DateTime fromUtc, DateTime toUtc, float threshold = 0.3f, int limit = 50, CancellationToken cancellationToken = default)
        => Inner.SearchMemoriesByEmbeddingInDateRangeAsync(queryEmbedding, fromUtc, toUtc, threshold, limit, cancellationToken);
    public virtual Task<long> GetMemoryCountAsync(CancellationToken cancellationToken = default)
        => Inner.GetMemoryCountAsync(cancellationToken);

    // Progressive disclosure methods
    public virtual Task<List<Memory>> GetMemoriesByIdsAsync(List<Guid> ids, CancellationToken cancellationToken = default)
        => Inner.GetMemoriesByIdsAsync(ids, cancellationToken);
    public virtual Task<List<Memory>> GetMemoriesAroundAnchorAsync(Guid anchorId, int before = 5, int after = 5, string? memoryType = null, CancellationToken cancellationToken = default)
        => Inner.GetMemoriesAroundAnchorAsync(anchorId, before, after, memoryType, cancellationToken);
    public virtual Task<List<Memory>> GetMemoriesAroundTimestampAsync(DateTime anchor, int before = 5, int after = 5, string? memoryType = null, CancellationToken cancellationToken = default)
        => Inner.GetMemoriesAroundTimestampAsync(anchor, before, after, memoryType, cancellationToken);

    // Entity operations
    public virtual Task<Guid> CreateEntityAsync(Entity entity, CancellationToken cancellationToken = default)
        => Inner.CreateEntityAsync(entity, cancellationToken);
    public virtual Task<Entity?> GetEntityByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => Inner.GetEntityByIdAsync(id, cancellationToken);
    public virtual Task<List<Entity>> GetEntitiesByIdsAsync(List<Guid> ids, CancellationToken cancellationToken = default)
        => Inner.GetEntitiesByIdsAsync(ids, cancellationToken);
    public virtual Task<List<Entity>> GetEntitiesForMemoryAsync(Guid memoryId, CancellationToken cancellationToken = default)
        => Inner.GetEntitiesForMemoryAsync(memoryId, cancellationToken);
    public virtual Task<Dictionary<Guid, List<Entity>>> GetEntitiesForMemoriesAsync(List<Guid> memoryIds, CancellationToken cancellationToken = default)
        => Inner.GetEntitiesForMemoriesAsync(memoryIds, cancellationToken);
    public virtual Task LinkMemoryToEntityAsync(Guid memoryId, Guid entityId, float relevance = 1.0f, CancellationToken cancellationToken = default)
        => Inner.LinkMemoryToEntityAsync(memoryId, entityId, relevance, cancellationToken);
    public virtual Task<List<Entity>> GetAllEntitiesAsync(int limit = 1000, CancellationToken cancellationToken = default)
        => Inner.GetAllEntitiesAsync(limit, cancellationToken);
    public virtual Task<Dictionary<string, Guid>> CreateEntitiesBatchAsync(List<Entity> entities, CancellationToken cancellationToken = default)
        => Inner.CreateEntitiesBatchAsync(entities, cancellationToken);
    public virtual Task LinkMemoryToEntitiesBatchAsync(Guid memoryId, Dictionary<Guid, float> entityRelevances, CancellationToken cancellationToken = default)
        => Inner.LinkMemoryToEntitiesBatchAsync(memoryId, entityRelevances, cancellationToken);
    public virtual Task<long> GetEntityCountAsync(CancellationToken cancellationToken = default)
        => Inner.GetEntityCountAsync(cancellationToken);

    // Relationship operations
    public virtual Task<Guid> CreateRelationshipAsync(EntityRelationship relationship, CancellationToken cancellationToken = default)
        => Inner.CreateRelationshipAsync(relationship, cancellationToken);
    public virtual Task<List<EntityRelationship>> GetRelationshipsForEntityAsync(Guid entityId, CancellationToken cancellationToken = default)
        => Inner.GetRelationshipsForEntityAsync(entityId, cancellationToken);
    public virtual Task<Dictionary<Guid, List<EntityRelationship>>> GetRelationshipsForEntitiesAsync(List<Guid> entityIds, CancellationToken cancellationToken = default)
        => Inner.GetRelationshipsForEntitiesAsync(entityIds, cancellationToken);
    public virtual Task<List<EntityRelationship>> GetAllRelationshipsAsync(int limit = 1000, CancellationToken cancellationToken = default)
        => Inner.GetAllRelationshipsAsync(limit, cancellationToken);
    public virtual Task<List<Guid>> CreateRelationshipsBatchAsync(List<EntityRelationship> relationships, CancellationToken cancellationToken = default)
        => Inner.CreateRelationshipsBatchAsync(relationships, cancellationToken);
    public virtual Task<long> GetRelationshipCountAsync(CancellationToken cancellationToken = default)
        => Inner.GetRelationshipCountAsync(cancellationToken);

    // User profile operations
    public virtual Task SetUserPersonaAttributeAsync(UserPersona persona, CancellationToken cancellationToken = default)
        => Inner.SetUserPersonaAttributeAsync(persona, cancellationToken);
    public virtual Task<Dictionary<string, Dictionary<string, object>>> GetUserPersonaAsync(string userId = "default_user", CancellationToken cancellationToken = default)
        => Inner.GetUserPersonaAsync(userId, cancellationToken);
    public virtual Task<List<UserPersona>> GetActiveGoalsAsync(string userId = "default_user", CancellationToken ct = default)
        => Inner.GetActiveGoalsAsync(userId, ct);

    // Session operations
    public virtual Task<Guid> CreateConversationSessionAsync(ConversationSession session, CancellationToken cancellationToken = default)
        => Inner.CreateConversationSessionAsync(session, cancellationToken);
    public virtual Task EndConversationSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
        => Inner.EndConversationSessionAsync(sessionId, cancellationToken);
    public virtual Task<List<ConversationSession>> GetRecentSessionsAsync(int limit = 10, CancellationToken cancellationToken = default)
        => Inner.GetRecentSessionsAsync(limit, cancellationToken);
    public virtual Task UpdateSessionMetadataAsync(Guid sessionId, Dictionary<string, object> metadata, CancellationToken ct = default)
        => Inner.UpdateSessionMetadataAsync(sessionId, metadata, ct);

    // Statistics
    public virtual Task<(long Memories, long Entities, long Relationships)> GetGraphStatisticsAsync(CancellationToken cancellationToken = default)
        => Inner.GetGraphStatisticsAsync(cancellationToken);
    public virtual Task<Dictionary<string, int>> GetRelationshipTypeBreakdownAsync(CancellationToken cancellationToken = default)
        => Inner.GetRelationshipTypeBreakdownAsync(cancellationToken);

    // Workspace operations
    public virtual Task<Guid> CreateWorkspaceAsync(Workspace workspace, CancellationToken ct = default)
        => Inner.CreateWorkspaceAsync(workspace, ct);
    public virtual Task<List<Workspace>> GetWorkspacesAsync(int limit = 50, CancellationToken ct = default)
        => Inner.GetWorkspacesAsync(limit, ct);
    public virtual Task<Workspace?> GetWorkspaceBySlugAsync(string workspaceId, CancellationToken ct = default)
        => Inner.GetWorkspaceBySlugAsync(workspaceId, ct);

    // Snapshot operations
    public virtual Task<Guid> CreateSnapshotAsync(WorkspaceSnapshot snapshot, CancellationToken ct = default)
        => Inner.CreateSnapshotAsync(snapshot, ct);
    public virtual Task<List<WorkspaceSnapshot>> GetSnapshotsAsync(string workspaceId, int limit = 20, CancellationToken ct = default)
        => Inner.GetSnapshotsAsync(workspaceId, limit, ct);
    public virtual Task<WorkspaceSnapshot?> GetSnapshotByNameAsync(string workspaceId, string snapshotName, CancellationToken ct = default)
        => Inner.GetSnapshotByNameAsync(workspaceId, snapshotName, ct);

    // Unit of work
    public virtual Task<IAsyncDisposable> BeginUnitOfWorkAsync(CancellationToken cancellationToken = default)
        => Inner.BeginUnitOfWorkAsync(cancellationToken);
}
