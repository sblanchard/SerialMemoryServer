using SerialMemory.Core.Models;

namespace SerialMemory.Core.Interfaces;

/// <summary>
/// Repository interface for knowledge graph operations
/// </summary>
public interface IKnowledgeGraphStore
{
    // Memory operations
    Task<Guid> CreateMemoryAsync(Memory memory, CancellationToken cancellationToken = default);
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

    // User persona operations
    Task SetUserPersonaAttributeAsync(UserPersona persona, CancellationToken cancellationToken = default);
    Task<Dictionary<string, Dictionary<string, object>>> GetUserPersonaAsync(string userId = "default_user", CancellationToken cancellationToken = default);

    // Conversation session operations
    Task<Guid> CreateConversationSessionAsync(ConversationSession session, CancellationToken cancellationToken = default);
    Task EndConversationSessionAsync(Guid sessionId, CancellationToken cancellationToken = default);
    Task<List<ConversationSession>> GetRecentSessionsAsync(int limit = 10, CancellationToken cancellationToken = default);
}
