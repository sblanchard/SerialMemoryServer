using SerialMemory.Core.Models;

namespace SerialMemory.Core.Interfaces;

/// <summary>
/// Repository interface for knowledge graph operations
/// </summary>
public interface IKnowledgeGraphStore
{
    // Memory operations
    Task<Guid> CreateMemoryAsync(Memory memory, CancellationToken cancellationToken = default);
    Task UpdateMemoryAsync(Memory memory, CancellationToken cancellationToken = default);
    Task<Memory?> GetMemoryByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<List<Memory>> GetRecentMemoriesAsync(int limit = 10, CancellationToken cancellationToken = default);
    Task<List<Memory>> SearchMemoriesByEmbeddingAsync(float[] queryEmbedding, int limit = 10, float threshold = 0.7f, CancellationToken cancellationToken = default);
    Task<List<Memory>> SearchMemoriesByTextAsync(string query, int limit = 10, CancellationToken cancellationToken = default);

    // Entity operations
    Task<Guid> CreateEntityAsync(Entity entity, CancellationToken cancellationToken = default);
    Task<Entity?> GetEntityByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<List<Entity>> GetEntitiesForMemoryAsync(Guid memoryId, CancellationToken cancellationToken = default);
    Task LinkMemoryToEntityAsync(Guid memoryId, Guid entityId, float relevance = 1.0f, CancellationToken cancellationToken = default);

    // Relationship operations
    Task<Guid> CreateRelationshipAsync(EntityRelationship relationship, CancellationToken cancellationToken = default);
    Task<List<EntityRelationship>> GetRelationshipsForEntityAsync(Guid entityId, CancellationToken cancellationToken = default);
    Task<List<EntityRelationship>> GetAllRelationshipsAsync(int limit = 1000, CancellationToken cancellationToken = default);

    // Entity listing
    Task<List<Entity>> GetAllEntitiesAsync(int limit = 1000, CancellationToken cancellationToken = default);

    // User persona operations
    Task SetUserPersonaAttributeAsync(UserPersona persona, CancellationToken cancellationToken = default);
    Task<Dictionary<string, Dictionary<string, object>>> GetUserPersonaAsync(string userId = "default_user", CancellationToken cancellationToken = default);

    // Conversation session operations
    Task<Guid> CreateConversationSessionAsync(ConversationSession session, CancellationToken cancellationToken = default);
    Task EndConversationSessionAsync(Guid sessionId, CancellationToken cancellationToken = default);
    Task<List<ConversationSession>> GetRecentSessionsAsync(int limit = 10, CancellationToken cancellationToken = default);

    // Statistics operations
    Task<long> GetMemoryCountAsync(CancellationToken cancellationToken = default);
    Task<long> GetEntityCountAsync(CancellationToken cancellationToken = default);
    Task<long> GetRelationshipCountAsync(CancellationToken cancellationToken = default);

    // Batch operations for crawling
    Task<List<Memory>> GetMemoriesWithoutEntitiesAsync(int limit = 100, CancellationToken cancellationToken = default);

    // Batch operations for re-embedding
    Task<List<Memory>> GetMemoriesWithNullEmbeddingsAsync(int limit = 100, CancellationToken cancellationToken = default);
    Task<List<Memory>> GetAllMemoriesAsync(int limit = 100, int offset = 0, CancellationToken cancellationToken = default);

    // Date range queries for context instantiation
    Task<List<Memory>> GetMemoriesByDateRangeAsync(DateTime fromUtc, DateTime toUtc, int limit = 100, CancellationToken cancellationToken = default);

    // Workspace operations
    Task<Guid> CreateWorkspaceAsync(Workspace workspace, CancellationToken ct = default);
    Task<List<Workspace>> GetWorkspacesAsync(int limit = 50, CancellationToken ct = default);
    Task<Workspace?> GetWorkspaceBySlugAsync(string workspaceId, CancellationToken ct = default);

    // Session metadata updates
    Task UpdateSessionMetadataAsync(Guid sessionId, Dictionary<string, object> metadata, CancellationToken ct = default);

    // Workspace snapshot operations
    Task<Guid> CreateSnapshotAsync(WorkspaceSnapshot snapshot, CancellationToken ct = default);
    Task<List<WorkspaceSnapshot>> GetSnapshotsAsync(string workspaceId, int limit = 20, CancellationToken ct = default);
    Task<WorkspaceSnapshot?> GetSnapshotByNameAsync(string workspaceId, string snapshotName, CancellationToken ct = default);
}
