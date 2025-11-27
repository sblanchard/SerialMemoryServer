using Microsoft.Extensions.Logging;
using SerialMemory.Core.Interfaces;
using SerialMemory.Core.Models;

namespace SerialMemory.Infrastructure;

/// <summary>
/// Decorator that instruments the knowledge graph store with event emission.
/// Emits NodeCreated, NodeUpdated, EdgeCreated, EdgeDeleted events.
/// Never blocks core logic on logging failure.
/// </summary>
public class InstrumentedKnowledgeGraphStore : IKnowledgeGraphStore
{
    private readonly IKnowledgeGraphStore _inner;
    private readonly IGraphEventStore _graphEventStore;
    private readonly ILiveEventEmitter _liveEventEmitter;
    private readonly ILogger<InstrumentedKnowledgeGraphStore> _logger;

    public InstrumentedKnowledgeGraphStore(
        IKnowledgeGraphStore inner,
        IGraphEventStore graphEventStore,
        ILiveEventEmitter liveEventEmitter,
        ILogger<InstrumentedKnowledgeGraphStore> logger)
    {
        _inner = inner;
        _graphEventStore = graphEventStore;
        _liveEventEmitter = liveEventEmitter;
        _logger = logger;
    }

    #region Instrumented Entity Operations

    public async Task<Guid> CreateEntityAsync(Entity entity, CancellationToken cancellationToken = default)
    {
        var entityId = await _inner.CreateEntityAsync(entity, cancellationToken);

        // Fire and forget - never block on logging
        _ = Task.Run(async () =>
        {
            try
            {
                var graphEvent = new GraphEvent
                {
                    EventType = GraphEventType.NodeCreated,
                    NodeId = entityId,
                    NodeName = entity.Name,
                    NodeType = entity.EntityType,
                    MemoryId = entity.FirstSeenMemoryId,
                    TriggeredBy = "KnowledgeGraphStore.CreateEntity",
                    NewState = new Dictionary<string, object>
                    {
                        ["name"] = entity.Name,
                        ["type"] = entity.EntityType,
                        ["canonical_name"] = entity.CanonicalName ?? entity.Name.ToLowerInvariant()
                    }
                };

                await _graphEventStore.LogEventAsync(graphEvent, CancellationToken.None);

                await _liveEventEmitter.EmitGraphChangeAsync(new GraphChangeEvent
                {
                    EventId = graphEvent.EventId,
                    EventType = "node_created",
                    NodeId = entityId,
                    NodeName = entity.Name,
                    NodeType = entity.EntityType
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to log NodeCreated event for entity {EntityId}", entityId);
            }
        }, CancellationToken.None);

        return entityId;
    }

    public async Task<Guid> CreateRelationshipAsync(EntityRelationship relationship, CancellationToken cancellationToken = default)
    {
        var relationshipId = await _inner.CreateRelationshipAsync(relationship, cancellationToken);

        // Get entity names for the event
        Entity? sourceEntity = null;
        Entity? targetEntity = null;

        try
        {
            sourceEntity = await _inner.GetEntityByIdAsync(relationship.SourceEntityId, cancellationToken);
            targetEntity = await _inner.GetEntityByIdAsync(relationship.TargetEntityId, cancellationToken);
        }
        catch { /* Ignore - we'll proceed without names */ }

        // Fire and forget - never block on logging
        _ = Task.Run(async () =>
        {
            try
            {
                var graphEvent = new GraphEvent
                {
                    EventType = GraphEventType.EdgeCreated,
                    EdgeId = relationshipId,
                    EdgeType = relationship.RelationshipType,
                    SourceNodeId = relationship.SourceEntityId,
                    TargetNodeId = relationship.TargetEntityId,
                    SourceNodeName = sourceEntity?.Name,
                    TargetNodeName = targetEntity?.Name,
                    Confidence = relationship.Confidence,
                    MemoryId = relationship.FirstSeenMemoryId,
                    TriggeredBy = "KnowledgeGraphStore.CreateRelationship",
                    NewState = new Dictionary<string, object>
                    {
                        ["type"] = relationship.RelationshipType,
                        ["confidence"] = relationship.Confidence
                    }
                };

                await _graphEventStore.LogEventAsync(graphEvent, CancellationToken.None);

                await _liveEventEmitter.EmitGraphChangeAsync(new GraphChangeEvent
                {
                    EventId = graphEvent.EventId,
                    EventType = "edge_created",
                    EdgeId = relationshipId,
                    EdgeType = relationship.RelationshipType,
                    SourceNodeId = relationship.SourceEntityId.ToString(),
                    TargetNodeId = relationship.TargetEntityId.ToString()
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to log EdgeCreated event for relationship {RelationshipId}", relationshipId);
            }
        }, CancellationToken.None);

        return relationshipId;
    }

    public async Task LinkMemoryToEntityAsync(Guid memoryId, Guid entityId, float relevance = 1, CancellationToken cancellationToken = default)
    {
        await _inner.LinkMemoryToEntityAsync(memoryId, entityId, relevance, cancellationToken);

        // Fire and forget
        _ = Task.Run(async () =>
        {
            try
            {
                var entity = await _inner.GetEntityByIdAsync(entityId, CancellationToken.None);

                var graphEvent = new GraphEvent
                {
                    EventType = GraphEventType.NodeUpdated,
                    NodeId = entityId,
                    NodeName = entity?.Name,
                    NodeType = entity?.EntityType,
                    MemoryId = memoryId,
                    TriggeredBy = "KnowledgeGraphStore.LinkMemoryToEntity",
                    NewState = new Dictionary<string, object>
                    {
                        ["linked_memory_id"] = memoryId,
                        ["relevance"] = relevance
                    }
                };

                await _graphEventStore.LogEventAsync(graphEvent, CancellationToken.None);

                await _liveEventEmitter.EmitGraphChangeAsync(new GraphChangeEvent
                {
                    EventId = graphEvent.EventId,
                    EventType = "node_updated",
                    NodeId = entityId,
                    NodeName = entity?.Name,
                    NodeType = entity?.EntityType
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to log NodeUpdated event for entity {EntityId}", entityId);
            }
        }, CancellationToken.None);
    }

    #endregion

    #region Passthrough Operations (not instrumented)

    public Task<Guid> CreateMemoryAsync(Memory memory, CancellationToken cancellationToken = default)
        => _inner.CreateMemoryAsync(memory, cancellationToken);

    public Task<Memory?> GetMemoryByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => _inner.GetMemoryByIdAsync(id, cancellationToken);

    public Task<List<Memory>> GetRecentMemoriesAsync(int limit = 10, CancellationToken cancellationToken = default)
        => _inner.GetRecentMemoriesAsync(limit, cancellationToken);

    public Task<List<Memory>> SearchMemoriesByEmbeddingAsync(float[] queryEmbedding, int limit = 10, float threshold = 0.7f, CancellationToken cancellationToken = default)
        => _inner.SearchMemoriesByEmbeddingAsync(queryEmbedding, limit, threshold, cancellationToken);

    public Task<List<Memory>> SearchMemoriesByTextAsync(string query, int limit = 10, CancellationToken cancellationToken = default)
        => _inner.SearchMemoriesByTextAsync(query, limit, cancellationToken);

    public Task<Entity?> GetEntityByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => _inner.GetEntityByIdAsync(id, cancellationToken);

    public Task<List<Entity>> GetEntitiesForMemoryAsync(Guid memoryId, CancellationToken cancellationToken = default)
        => _inner.GetEntitiesForMemoryAsync(memoryId, cancellationToken);

    public Task<List<EntityRelationship>> GetRelationshipsForEntityAsync(Guid entityId, CancellationToken cancellationToken = default)
        => _inner.GetRelationshipsForEntityAsync(entityId, cancellationToken);

    public Task<List<EntityRelationship>> GetAllRelationshipsAsync(int limit = 1000, CancellationToken cancellationToken = default)
        => _inner.GetAllRelationshipsAsync(limit, cancellationToken);

    public Task<List<Entity>> GetAllEntitiesAsync(int limit = 1000, CancellationToken cancellationToken = default)
        => _inner.GetAllEntitiesAsync(limit, cancellationToken);

    public Task SetUserPersonaAttributeAsync(UserPersona persona, CancellationToken cancellationToken = default)
        => _inner.SetUserPersonaAttributeAsync(persona, cancellationToken);

    public Task<Dictionary<string, Dictionary<string, object>>> GetUserPersonaAsync(string userId = "default_user", CancellationToken cancellationToken = default)
        => _inner.GetUserPersonaAsync(userId, cancellationToken);

    public Task<Guid> CreateConversationSessionAsync(ConversationSession session, CancellationToken cancellationToken = default)
        => _inner.CreateConversationSessionAsync(session, cancellationToken);

    public Task EndConversationSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
        => _inner.EndConversationSessionAsync(sessionId, cancellationToken);

    public Task<List<ConversationSession>> GetRecentSessionsAsync(int limit = 10, CancellationToken cancellationToken = default)
        => _inner.GetRecentSessionsAsync(limit, cancellationToken);

    public Task<long> GetMemoryCountAsync(CancellationToken cancellationToken = default)
        => _inner.GetMemoryCountAsync(cancellationToken);

    public Task<long> GetEntityCountAsync(CancellationToken cancellationToken = default)
        => _inner.GetEntityCountAsync(cancellationToken);

    public Task<long> GetRelationshipCountAsync(CancellationToken cancellationToken = default)
        => _inner.GetRelationshipCountAsync(cancellationToken);

    public Task<List<Memory>> GetMemoriesWithoutEntitiesAsync(int limit = 100, CancellationToken cancellationToken = default)
        => _inner.GetMemoriesWithoutEntitiesAsync(limit, cancellationToken);

    public Task<List<Memory>> GetMemoriesWithNullEmbeddingsAsync(int limit = 100, CancellationToken cancellationToken = default)
        => _inner.GetMemoriesWithNullEmbeddingsAsync(limit, cancellationToken);

    public Task<List<Memory>> GetAllMemoriesAsync(int limit = 100, int offset = 0, CancellationToken cancellationToken = default)
        => _inner.GetAllMemoriesAsync(limit, offset, cancellationToken);

    #endregion
}
