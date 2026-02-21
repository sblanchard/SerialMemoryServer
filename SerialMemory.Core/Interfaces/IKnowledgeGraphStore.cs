using SerialMemory.Core.Models;

namespace SerialMemory.Core.Interfaces;

/// <summary>
/// Memory CRUD and search operations.
/// </summary>
public interface IMemoryStore
{
    Task<Guid> CreateMemoryAsync(Memory memory, CancellationToken cancellationToken = default);
    Task UpdateMemoryAsync(Memory memory, CancellationToken cancellationToken = default);
    Task<Memory?> GetMemoryByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<List<Memory>> GetRecentMemoriesAsync(int limit = 10, CancellationToken cancellationToken = default);
    Task<List<Memory>> SearchMemoriesByEmbeddingAsync(float[] queryEmbedding, int limit = 10, float threshold = 0.7f, string? memoryType = null, CancellationToken cancellationToken = default);
    Task<List<Memory>> SearchMemoriesByTextAsync(string query, int limit = 10, string? memoryType = null, CancellationToken cancellationToken = default);
    Task<List<Memory>> GetMemoriesByTypeAsync(string memoryType, int limit = 50, CancellationToken cancellationToken = default);
    Task<List<Memory>> GetMemoriesBySessionAsync(Guid sessionId, int limit = 100, CancellationToken cancellationToken = default);
    Task<List<Memory>> GetMemoriesWithoutEntitiesAsync(int limit = 100, CancellationToken cancellationToken = default);
    Task<List<Memory>> GetMemoriesWithNullEmbeddingsAsync(int limit = 100, CancellationToken cancellationToken = default);
    Task<List<Memory>> GetAllMemoriesAsync(int limit = 100, int offset = 0, CancellationToken cancellationToken = default);
    Task<List<Memory>> GetMemoriesByDateRangeAsync(DateTime fromUtc, DateTime toUtc, int limit = 100, CancellationToken cancellationToken = default);
    Task<List<Memory>> SearchMemoriesByEmbeddingInDateRangeAsync(float[] queryEmbedding, DateTime fromUtc, DateTime toUtc, float threshold = 0.3f, int limit = 50, CancellationToken cancellationToken = default);
    Task<long> GetMemoryCountAsync(CancellationToken cancellationToken = default);

    // Progressive disclosure methods (P0 - GAP 2)
    Task<List<Memory>> GetMemoriesByIdsAsync(List<Guid> ids, CancellationToken cancellationToken = default);
    Task<List<Memory>> GetMemoriesAroundAnchorAsync(Guid anchorId, int before = 5, int after = 5, string? memoryType = null, CancellationToken cancellationToken = default);
    Task<List<Memory>> GetMemoriesAroundTimestampAsync(DateTime anchor, int before = 5, int after = 5, string? memoryType = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// Entity CRUD, search, and batch operations.
/// </summary>
public interface IEntityStore
{
    Task<Guid> CreateEntityAsync(Entity entity, CancellationToken cancellationToken = default);
    Task<Entity?> GetEntityByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<List<Entity>> GetEntitiesByIdsAsync(List<Guid> ids, CancellationToken cancellationToken = default);
    Task<List<Entity>> GetEntitiesForMemoryAsync(Guid memoryId, CancellationToken cancellationToken = default);
    Task<Dictionary<Guid, List<Entity>>> GetEntitiesForMemoriesAsync(List<Guid> memoryIds, CancellationToken cancellationToken = default);
    Task LinkMemoryToEntityAsync(Guid memoryId, Guid entityId, float relevance = 1.0f, CancellationToken cancellationToken = default);
    Task<List<Entity>> GetAllEntitiesAsync(int limit = 1000, CancellationToken cancellationToken = default);
    Task<Dictionary<string, Guid>> CreateEntitiesBatchAsync(List<Entity> entities, CancellationToken cancellationToken = default);
    Task LinkMemoryToEntitiesBatchAsync(Guid memoryId, Dictionary<Guid, float> entityRelevances, CancellationToken cancellationToken = default);
    Task<long> GetEntityCountAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Relationship CRUD and traversal operations.
/// </summary>
public interface IRelationshipStore
{
    Task<Guid> CreateRelationshipAsync(EntityRelationship relationship, CancellationToken cancellationToken = default);
    Task<List<EntityRelationship>> GetRelationshipsForEntityAsync(Guid entityId, CancellationToken cancellationToken = default);
    Task<Dictionary<Guid, List<EntityRelationship>>> GetRelationshipsForEntitiesAsync(List<Guid> entityIds, CancellationToken cancellationToken = default);
    Task<List<EntityRelationship>> GetAllRelationshipsAsync(int limit = 1000, CancellationToken cancellationToken = default);
    Task<List<Guid>> CreateRelationshipsBatchAsync(List<EntityRelationship> relationships, CancellationToken cancellationToken = default);
    Task<long> GetRelationshipCountAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// User persona and goal operations.
/// </summary>
public interface IUserProfileStore
{
    Task SetUserPersonaAttributeAsync(UserPersona persona, CancellationToken cancellationToken = default);
    Task<Dictionary<string, Dictionary<string, object>>> GetUserPersonaAsync(string userId = "default_user", CancellationToken cancellationToken = default);
    Task<List<UserPersona>> GetActiveGoalsAsync(string userId = "default_user", CancellationToken ct = default);
}

/// <summary>
/// Conversation session lifecycle operations.
/// </summary>
public interface ISessionStore
{
    Task<Guid> CreateConversationSessionAsync(ConversationSession session, CancellationToken cancellationToken = default);
    Task EndConversationSessionAsync(Guid sessionId, CancellationToken cancellationToken = default);
    Task<List<ConversationSession>> GetRecentSessionsAsync(int limit = 10, CancellationToken cancellationToken = default);
    Task UpdateSessionMetadataAsync(Guid sessionId, Dictionary<string, object> metadata, CancellationToken ct = default);
}

/// <summary>
/// Graph-wide statistics (approximate counts, breakdowns).
/// </summary>
public interface IStatisticsStore
{
    Task<(long Memories, long Entities, long Relationships)> GetGraphStatisticsAsync(CancellationToken cancellationToken = default);
    Task<Dictionary<string, int>> GetRelationshipTypeBreakdownAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Workspace and snapshot lifecycle operations.
/// </summary>
public interface IWorkspaceStore
{
    Task<Guid> CreateWorkspaceAsync(Workspace workspace, CancellationToken ct = default);
    Task<List<Workspace>> GetWorkspacesAsync(int limit = 50, CancellationToken ct = default);
    Task<Workspace?> GetWorkspaceBySlugAsync(string workspaceId, CancellationToken ct = default);
    Task<Guid> CreateSnapshotAsync(WorkspaceSnapshot snapshot, CancellationToken ct = default);
    Task<List<WorkspaceSnapshot>> GetSnapshotsAsync(string workspaceId, int limit = 20, CancellationToken ct = default);
    Task<WorkspaceSnapshot?> GetSnapshotByNameAsync(string workspaceId, string snapshotName, CancellationToken ct = default);
}

/// <summary>
/// Composite repository interface for knowledge graph operations.
/// Inherits from focused sub-interfaces for backward compatibility.
/// </summary>
public interface IKnowledgeGraphStore : IMemoryStore, IEntityStore, IRelationshipStore, IUserProfileStore, ISessionStore, IStatisticsStore, IWorkspaceStore
{
    Task<IAsyncDisposable> BeginUnitOfWorkAsync(CancellationToken cancellationToken = default);
}
