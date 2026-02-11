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
/// Extends KnowledgeGraphStoreDecorator — only overrides the methods that need instrumentation.
/// </summary>
public class InstrumentedKnowledgeGraphStore(
    IKnowledgeGraphStore inner,
    IGraphEventStore graphEventStore,
    ILiveEventEmitter liveEventEmitter,
    NpgsqlDataSource dataSource,
    ILogger<InstrumentedKnowledgeGraphStore> logger)
    : KnowledgeGraphStoreDecorator(inner)
{
    public override async Task<Guid> CreateEntityAsync(Entity entity, CancellationToken cancellationToken = default)
    {
        var entityId = await Inner.CreateEntityAsync(entity, cancellationToken);

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

    public override async Task<Guid> CreateRelationshipAsync(EntityRelationship relationship, CancellationToken cancellationToken = default)
    {
        var relationshipId = await Inner.CreateRelationshipAsync(relationship, cancellationToken);

        _ = Task.Run(async () =>
        {
            try
            {
                Entity? sourceEntity = null;
                Entity? targetEntity = null;

                try
                {
                    sourceEntity = await Inner.GetEntityByIdAsync(relationship.SourceEntityId, CancellationToken.None);
                    targetEntity = await Inner.GetEntityByIdAsync(relationship.TargetEntityId, CancellationToken.None);
                }
                catch (Exception ex) { logger.LogDebug(ex, "Could not resolve entity names for graph event"); }

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

    public override async Task LinkMemoryToEntityAsync(Guid memoryId, Guid entityId, float relevance = 1, CancellationToken cancellationToken = default)
    {
        await Inner.LinkMemoryToEntityAsync(memoryId, entityId, relevance, cancellationToken);

        _ = Task.Run(async () =>
        {
            try
            {
                var entity = await Inner.GetEntityByIdAsync(entityId, CancellationToken.None);

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

    public override async Task<Guid> CreateMemoryAsync(Memory memory, CancellationToken cancellationToken = default)
    {
        var memoryId = await Inner.CreateMemoryAsync(memory, cancellationToken);

        _ = Task.Run(async () =>
        {
            try
            {
                var tenantIdStr = memory.Metadata?.TryGetValue("tenant_id", out var tid) == true ? tid?.ToString() : null;
                var layer = memory.Metadata?.TryGetValue("layer", out var l) == true ? l?.ToString() : "L0_RAW";
                Guid? tenantId = Guid.TryParse(tenantIdStr, out var parsedTid) ? parsedTid : null;

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

                await WriteMemoryEventAsync(tenantId, memoryId, "created", new
                {
                    content_preview = memory.Content?.Length > 100 ? memory.Content[..100] : memory.Content,
                    source = memory.Source,
                    layer
                }, "system");

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
}
