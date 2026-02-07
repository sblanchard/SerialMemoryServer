using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using SerialMemory.Core.Interfaces;
using SerialMemory.Core.Models;

namespace SerialMemory.Infrastructure;

/// <summary>
/// Decorator that instruments the knowledge graph store with event emission.
/// Emits NodeCreated, NodeUpdated, EdgeCreated, EdgeDeleted events.
/// Also writes to memory_events table for dashboard Traces page.
/// Never blocks core logic on logging failure.
/// </summary>
public class InstrumentedKnowledgeGraphStore(
    IKnowledgeGraphStore inner,
    IGraphEventStore graphEventStore,
    ILiveEventEmitter liveEventEmitter,
    NpgsqlDataSource dataSource,
    ILogger<InstrumentedKnowledgeGraphStore> logger)
    : IKnowledgeGraphStore
{
    #region Instrumented Entity Operations

    public async Task<Guid> CreateEntityAsync(Entity entity, CancellationToken cancellationToken = default)
    {
        var entityId = await inner.CreateEntityAsync(entity, cancellationToken);

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

                await graphEventStore.LogEventAsync(graphEvent, CancellationToken.None);

                await liveEventEmitter.EmitGraphChangeAsync(new GraphChangeEvent
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
                logger.LogWarning(ex, "Failed to log NodeCreated event for entity {EntityId}", entityId);
            }
        }, CancellationToken.None);

        return entityId;
    }

    public async Task<Guid> CreateRelationshipAsync(EntityRelationship relationship, CancellationToken cancellationToken = default)
    {
        var relationshipId = await inner.CreateRelationshipAsync(relationship, cancellationToken);

        // Get entity names for the event
        Entity? sourceEntity = null;
        Entity? targetEntity = null;

        try
        {
            sourceEntity = await inner.GetEntityByIdAsync(relationship.SourceEntityId, cancellationToken);
            targetEntity = await inner.GetEntityByIdAsync(relationship.TargetEntityId, cancellationToken);
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

                await graphEventStore.LogEventAsync(graphEvent, CancellationToken.None);

                await liveEventEmitter.EmitGraphChangeAsync(new GraphChangeEvent
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
                logger.LogWarning(ex, "Failed to log EdgeCreated event for relationship {RelationshipId}", relationshipId);
            }
        }, CancellationToken.None);

        return relationshipId;
    }

    public async Task LinkMemoryToEntityAsync(Guid memoryId, Guid entityId, float relevance = 1, CancellationToken cancellationToken = default)
    {
        await inner.LinkMemoryToEntityAsync(memoryId, entityId, relevance, cancellationToken);

        // Fire and forget
        _ = Task.Run(async () =>
        {
            try
            {
                var entity = await inner.GetEntityByIdAsync(entityId, CancellationToken.None);

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

                await graphEventStore.LogEventAsync(graphEvent, CancellationToken.None);

                await liveEventEmitter.EmitGraphChangeAsync(new GraphChangeEvent
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
                logger.LogWarning(ex, "Failed to log NodeUpdated event for entity {EntityId}", entityId);
            }
        }, CancellationToken.None);
    }

    #endregion

    #region Instrumented Memory Operations

    public async Task<Guid> CreateMemoryAsync(Memory memory, CancellationToken cancellationToken = default)
    {
        var memoryId = await inner.CreateMemoryAsync(memory, cancellationToken);

        // Fire and forget - never block on logging
        _ = Task.Run(async () =>
        {
            try
            {
                // Get tenant ID from metadata if available
                var tenantIdStr = memory.Metadata?.TryGetValue("tenant_id", out var tid) == true ? tid?.ToString() : null;
                var layer = memory.Metadata?.TryGetValue("layer", out var l) == true ? l?.ToString() : "L0_RAW";
                Guid? tenantId = Guid.TryParse(tenantIdStr, out var parsedTid) ? parsedTid : null;

                // 1. Emit to graph event store (for graph visualization)
                var graphEvent = new GraphEvent
                {
                    EventType = GraphEventType.NodeCreated,
                    NodeId = memoryId,
                    NodeName = memory.Content?.Length > 50 ? memory.Content[..50] + "..." : memory.Content,
                    NodeType = "memory",
                    MemoryId = memoryId,
                    TriggeredBy = "KnowledgeGraphStore.CreateMemory",
                    NewState = new Dictionary<string, object>
                    {
                        ["source"] = memory.Source ?? "unknown",
                        ["layer"] = layer ?? "L0_RAW",
                        ["content_preview"] = memory.Content?.Length > 100 ? memory.Content[..100] : memory.Content ?? ""
                    }
                };

                await graphEventStore.LogEventAsync(graphEvent, CancellationToken.None);

                // 2. Write to memory_events table for dashboard Traces page
                await WriteMemoryEventAsync(tenantId, memoryId, "created", new
                {
                    content_preview = memory.Content?.Length > 100 ? memory.Content[..100] : memory.Content,
                    source = memory.Source,
                    layer
                }, "system");

                // 3. Emit memory event for Traces page via SignalR (real-time updates)
                await liveEventEmitter.EmitMemoryEventAsync(new MemoryEventBroadcast
                {
                    EventId = graphEvent.EventId,
                    TenantId = tenantIdStr ?? "",
                    MemoryId = memoryId,
                    EventType = "created",
                    Actor = "system",
                    Payload = new
                    {
                        content_preview = memory.Content?.Length > 100 ? memory.Content[..100] : memory.Content,
                        source = memory.Source,
                        layer
                    },
                    Timestamp = DateTimeOffset.UtcNow
                });

                // 4. Also emit as RecentEvent for Traces page (real-time)
                await liveEventEmitter.EmitRecentEventAsync(new RecentEventBroadcast
                {
                    Id = graphEvent.EventId,
                    TenantId = tenantIdStr ?? "",
                    EventType = "memory_created",
                    Category = "memory",
                    Actor = "system",
                    MemoryId = memoryId,
                    Payload = new
                    {
                        content_preview = memory.Content?.Length > 100 ? memory.Content[..100] : memory.Content,
                        source = memory.Source
                    },
                    CreatedAt = DateTimeOffset.UtcNow
                });
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to log MemoryCreated event for memory {MemoryId}", memoryId);
            }
        }, CancellationToken.None);

        return memoryId;
    }

    /// <summary>
    /// Writes to the memory_events table for dashboard Traces page.
    /// </summary>
    private async Task WriteMemoryEventAsync(Guid? tenantId, Guid memoryId, string eventType, object eventData, string actor)
    {
        try
        {
            await using var conn = await dataSource.OpenConnectionAsync();
            await conn.ExecuteAsync(
                """
                INSERT INTO memory_events (id, tenant_id, memory_id, event_type, event_data, created_at, actor_id)
                VALUES (@Id, @TenantId, @MemoryId, @EventType, @EventData::jsonb, NOW(), @ActorId)
                ON CONFLICT DO NOTHING
                """,
                new
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = tenantId,
                    MemoryId = memoryId,
                    EventType = eventType,
                    EventData = JsonSerializer.Serialize(eventData),
                    ActorId = actor
                });
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to write memory event to database - table may not exist yet");
        }
    }

    #endregion

    #region Passthrough Operations (not instrumented)

    public Task UpdateMemoryAsync(Memory memory, CancellationToken cancellationToken = default)
        => inner.UpdateMemoryAsync(memory, cancellationToken);

    public Task<Memory?> GetMemoryByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => inner.GetMemoryByIdAsync(id, cancellationToken);

    public Task<List<Memory>> GetRecentMemoriesAsync(int limit = 10, CancellationToken cancellationToken = default)
        => inner.GetRecentMemoriesAsync(limit, cancellationToken);

    public Task<List<Memory>> SearchMemoriesByEmbeddingAsync(float[] queryEmbedding, int limit = 10, float threshold = 0.7f, string? memoryType = null, CancellationToken cancellationToken = default)
        => inner.SearchMemoriesByEmbeddingAsync(queryEmbedding, limit, threshold, memoryType, cancellationToken);

    public Task<List<Memory>> SearchMemoriesByTextAsync(string query, int limit = 10, string? memoryType = null, CancellationToken cancellationToken = default)
        => inner.SearchMemoriesByTextAsync(query, limit, memoryType, cancellationToken);

    public Task<List<Memory>> GetMemoriesByTypeAsync(string memoryType, int limit = 50, CancellationToken cancellationToken = default)
        => inner.GetMemoriesByTypeAsync(memoryType, limit, cancellationToken);

    public Task<List<Memory>> GetMemoriesBySessionAsync(Guid sessionId, int limit = 100, CancellationToken cancellationToken = default)
        => inner.GetMemoriesBySessionAsync(sessionId, limit, cancellationToken);

    public Task<Entity?> GetEntityByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => inner.GetEntityByIdAsync(id, cancellationToken);

    public Task<List<Entity>> GetEntitiesForMemoryAsync(Guid memoryId, CancellationToken cancellationToken = default)
        => inner.GetEntitiesForMemoryAsync(memoryId, cancellationToken);

    public Task<List<EntityRelationship>> GetRelationshipsForEntityAsync(Guid entityId, CancellationToken cancellationToken = default)
        => inner.GetRelationshipsForEntityAsync(entityId, cancellationToken);

    public Task<List<EntityRelationship>> GetAllRelationshipsAsync(int limit = 1000, CancellationToken cancellationToken = default)
        => inner.GetAllRelationshipsAsync(limit, cancellationToken);

    public Task<List<Entity>> GetAllEntitiesAsync(int limit = 1000, CancellationToken cancellationToken = default)
        => inner.GetAllEntitiesAsync(limit, cancellationToken);

    public Task SetUserPersonaAttributeAsync(UserPersona persona, CancellationToken cancellationToken = default)
        => inner.SetUserPersonaAttributeAsync(persona, cancellationToken);

    public Task<Dictionary<string, Dictionary<string, object>>> GetUserPersonaAsync(string userId = "default_user", CancellationToken cancellationToken = default)
        => inner.GetUserPersonaAsync(userId, cancellationToken);

    public Task<List<UserPersona>> GetActiveGoalsAsync(string userId = "default_user", CancellationToken ct = default)
        => inner.GetActiveGoalsAsync(userId, ct);

    public Task<Guid> CreateConversationSessionAsync(ConversationSession session, CancellationToken cancellationToken = default)
        => inner.CreateConversationSessionAsync(session, cancellationToken);

    public Task EndConversationSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
        => inner.EndConversationSessionAsync(sessionId, cancellationToken);

    public Task<List<ConversationSession>> GetRecentSessionsAsync(int limit = 10, CancellationToken cancellationToken = default)
        => inner.GetRecentSessionsAsync(limit, cancellationToken);

    public Task<long> GetMemoryCountAsync(CancellationToken cancellationToken = default)
        => inner.GetMemoryCountAsync(cancellationToken);

    public Task<long> GetEntityCountAsync(CancellationToken cancellationToken = default)
        => inner.GetEntityCountAsync(cancellationToken);

    public Task<long> GetRelationshipCountAsync(CancellationToken cancellationToken = default)
        => inner.GetRelationshipCountAsync(cancellationToken);

    public Task<List<Memory>> GetMemoriesWithoutEntitiesAsync(int limit = 100, CancellationToken cancellationToken = default)
        => inner.GetMemoriesWithoutEntitiesAsync(limit, cancellationToken);

    public Task<List<Memory>> GetMemoriesWithNullEmbeddingsAsync(int limit = 100, CancellationToken cancellationToken = default)
        => inner.GetMemoriesWithNullEmbeddingsAsync(limit, cancellationToken);

    public Task<List<Memory>> GetAllMemoriesAsync(int limit = 100, int offset = 0, CancellationToken cancellationToken = default)
        => inner.GetAllMemoriesAsync(limit, offset, cancellationToken);

    public Task<List<Memory>> GetMemoriesByDateRangeAsync(DateTime fromUtc, DateTime toUtc, int limit = 100, CancellationToken cancellationToken = default)
        => inner.GetMemoriesByDateRangeAsync(fromUtc, toUtc, limit, cancellationToken);

    // Workspace operations
    public Task<Guid> CreateWorkspaceAsync(Workspace workspace, CancellationToken ct = default)
        => inner.CreateWorkspaceAsync(workspace, ct);

    public Task<List<Workspace>> GetWorkspacesAsync(int limit = 50, CancellationToken ct = default)
        => inner.GetWorkspacesAsync(limit, ct);

    public Task<Workspace?> GetWorkspaceBySlugAsync(string workspaceId, CancellationToken ct = default)
        => inner.GetWorkspaceBySlugAsync(workspaceId, ct);

    // Session metadata
    public Task UpdateSessionMetadataAsync(Guid sessionId, Dictionary<string, object> metadata, CancellationToken ct = default)
        => inner.UpdateSessionMetadataAsync(sessionId, metadata, ct);

    // Snapshot operations
    public Task<Guid> CreateSnapshotAsync(WorkspaceSnapshot snapshot, CancellationToken ct = default)
        => inner.CreateSnapshotAsync(snapshot, ct);

    public Task<List<WorkspaceSnapshot>> GetSnapshotsAsync(string workspaceId, int limit = 20, CancellationToken ct = default)
        => inner.GetSnapshotsAsync(workspaceId, limit, ct);

    public Task<WorkspaceSnapshot?> GetSnapshotByNameAsync(string workspaceId, string snapshotName, CancellationToken ct = default)
        => inner.GetSnapshotByNameAsync(workspaceId, snapshotName, ct);

    #endregion
}
