using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Npgsql;
using SerialMemory.Core.Interfaces;
using SerialMemory.EventSourcing.Aggregates;
using SerialMemory.EventSourcing.Store;

namespace SerialMemory.Mcp.Tools;

/// <summary>
/// MCP tool handlers for memory lifecycle operations.
/// All operations follow append-only event sourcing - no hard deletes.
/// </summary>
public sealed class MemoryLifecycleTools(
    IEventStore eventStore,
    IEmbeddingService embeddingService,
    IEntityExtractionService entityExtractionService,
    IKnowledgeGraphStore knowledgeGraphStore,
    ILogger logger)
{
    private const string EventSourcingNotAvailable =
        "Event-sourcing schema not available. Lifecycle operations require the memory_events table. " +
        "Run eventsourcing_schema.sql migration to enable this feature.";

    /// <summary>
    /// memory_update - Update memory content with new embedding.
    /// </summary>
    public async Task<object> HandleMemoryUpdate(JsonNode? arguments)
    {
        var memoryIdStr = arguments?["memory_id"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrEmpty(memoryIdStr) || !Guid.TryParse(memoryIdStr, out var memoryId))
            throw new ArgumentException("Valid memory_id is required");

        var newContent = arguments?["new_content"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrEmpty(newContent))
            throw new ArgumentException("new_content is required");
        if (newContent.Length > 100000)
            throw new ArgumentException("Content exceeds maximum length of 100000 characters");

        var reason = arguments?["reason"]?.GetValue<string>()?.Trim();
        var actorId = arguments?["actor_id"]?.GetValue<string>()?.Trim();

        try
        {
            // Load aggregate
            var events = await eventStore.ReadStreamAsync(memoryId);
            if (events.Count == 0)
                return CreateErrorResponse($"Memory {memoryId} not found");

            var aggregate = MemoryAggregate.FromEvents(events);

            if (!aggregate.IsActive)
                return CreateErrorResponse($"Memory {memoryId} is inactive and cannot be updated");

            // Generate new embedding
            var newEmbedding = await embeddingService.EmbedTextAsync(newContent);

            // Apply update
            aggregate.Update(newContent, newEmbedding, reason, actorId);

            // Persist
            var sequences = await eventStore.AppendEventsAsync(
                aggregate.Id,
                aggregate.UncommittedEvents.ToList(),
                aggregate.Version - 1);

            logger.LogInformation("Updated memory {MemoryId} (version {Version})", memoryId, aggregate.Version);

            return CreateTextResponse(
                $"Memory updated successfully!\n\n" +
                $"Memory ID: {memoryId}\n" +
                $"New Version: {aggregate.Version}\n" +
                $"Previous Hash: {aggregate.ContentHash}\n" +
                $"Reason: {reason ?? "N/A"}\n" +
                $"Event Sequence: {string.Join(", ", sequences)}");
        }
        catch (PostgresException ex) when (ex.SqlState is "42P01" or "42703")
        {
            logger.LogWarning("memory_update skipped: {Message}", ex.MessageText);
            return CreateErrorResponse(EventSourcingNotAvailable);
        }
    }

    /// <summary>
    /// memory_delete - Soft delete (invalidate) a memory.
    /// </summary>
    public async Task<object> HandleMemoryDelete(JsonNode? arguments)
    {
        var memoryIdStr = arguments?["memory_id"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrEmpty(memoryIdStr) || !Guid.TryParse(memoryIdStr, out var memoryId))
            throw new ArgumentException("Valid memory_id is required");

        var reason = arguments?["reason"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrEmpty(reason))
            throw new ArgumentException("reason is required for soft delete");

        var supersededByIdStr = arguments?["superseded_by_id"]?.GetValue<string>()?.Trim();
        Guid? supersededById = null;
        if (!string.IsNullOrEmpty(supersededByIdStr) && Guid.TryParse(supersededByIdStr, out var sbId))
            supersededById = sbId;

        var actorId = arguments?["actor_id"]?.GetValue<string>()?.Trim();

        try
        {
            // Load aggregate
            var events = await eventStore.ReadStreamAsync(memoryId);
            if (events.Count == 0)
                return CreateErrorResponse($"Memory {memoryId} not found");

            var aggregate = MemoryAggregate.FromEvents(events);

            if (!aggregate.IsActive)
                return CreateTextResponse($"Memory {memoryId} is already inactive (soft deleted)");

            // Invalidate (soft delete)
            aggregate.Invalidate(reason, supersededById, null, actorId);

            // Persist
            var sequences = await eventStore.AppendEventsAsync(
                aggregate.Id,
                aggregate.UncommittedEvents.ToList(),
                aggregate.Version - 1);

            logger.LogInformation("Soft deleted memory {MemoryId}", memoryId);

            return CreateTextResponse(
                $"Memory soft deleted successfully!\n\n" +
                $"Memory ID: {memoryId}\n" +
                $"Reason: {reason}\n" +
                $"Superseded By: {supersededById?.ToString() ?? "N/A"}\n" +
                $"Event Sequence: {string.Join(", ", sequences)}\n\n" +
                $"NOTE: Memory is not hard deleted. It remains in the event store for audit purposes.");
        }
        catch (PostgresException ex) when (ex.SqlState is "42P01" or "42703")
        {
            logger.LogWarning("memory_delete skipped: {Message}", ex.MessageText);
            return CreateErrorResponse(EventSourcingNotAvailable);
        }
    }

    /// <summary>
    /// memory_merge - Merge multiple memories into one.
    /// </summary>
    public async Task<object> HandleMemoryMerge(JsonNode? arguments)
    {
        var sourceIdsNode = arguments?["source_memory_ids"];
        if (sourceIdsNode == null)
            throw new ArgumentException("source_memory_ids array is required");

        var sourceIds = sourceIdsNode.AsArray()
            .Select(n => Guid.TryParse(n?.GetValue<string>(), out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .ToArray();

        if (sourceIds.Length < 2)
            throw new ArgumentException("At least 2 source memory IDs required for merge");

        var mergedContent = arguments?["merged_content"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrEmpty(mergedContent))
            throw new ArgumentException("merged_content is required");

        var strategy = arguments?["strategy"]?.GetValue<string>()?.Trim() ?? "manual";
        var actorId = arguments?["actor_id"]?.GetValue<string>()?.Trim();

        try
        {
            // Validate all source memories exist and are active
            foreach (var sourceId in sourceIds)
            {
                var sourceEvents = await eventStore.ReadStreamAsync(sourceId);
                if (sourceEvents.Count == 0)
                    return CreateErrorResponse($"Source memory {sourceId} not found");

                var sourceAgg = MemoryAggregate.FromEvents(sourceEvents);
                if (!sourceAgg.IsActive)
                    return CreateErrorResponse($"Source memory {sourceId} is inactive");
            }

            // Generate embedding for merged content
            var embedding = await embeddingService.EmbedTextAsync(mergedContent);

            // Create new merged memory
            var aggregate = MemoryAggregate.Create(
                content: mergedContent,
                embedding: embedding,
                layer: MemoryLayer.L2_SUMMARY,
                causalParents: sourceIds,
                createdBy: actorId);

            // Record merge event
            aggregate.Merge(sourceIds, mergedContent, embedding, strategy, actorId);

            // Persist new memory
            var sequences = await eventStore.AppendEventsAsync(
                aggregate.Id,
                aggregate.UncommittedEvents.ToList(),
                0);

            // Invalidate source memories
            foreach (var sourceId in sourceIds)
            {
                var sourceEvents = await eventStore.ReadStreamAsync(sourceId);
                var sourceAgg = MemoryAggregate.FromEvents(sourceEvents);
                sourceAgg.Invalidate($"Merged into {aggregate.Id}", aggregate.Id, null, actorId);

                await eventStore.AppendEventsAsync(
                    sourceAgg.Id,
                    sourceAgg.UncommittedEvents.ToList(),
                    sourceAgg.Version - 1);
            }

            logger.LogInformation("Merged {Count} memories into {TargetId}", sourceIds.Length, aggregate.Id);

            return CreateTextResponse(
                $"Memories merged successfully!\n\n" +
                $"New Memory ID: {aggregate.Id}\n" +
                $"Source Memories: {string.Join(", ", sourceIds)}\n" +
                $"Strategy: {strategy}\n" +
                $"Layer: {aggregate.Layer}\n" +
                $"Event Sequences: {string.Join(", ", sequences)}\n\n" +
                $"Source memories have been soft deleted.");
        }
        catch (PostgresException ex) when (ex.SqlState is "42P01" or "42703")
        {
            logger.LogWarning("memory_merge skipped: {Message}", ex.MessageText);
            return CreateErrorResponse(EventSourcingNotAvailable);
        }
    }

    /// <summary>
    /// memory_split - Split a memory into multiple children.
    /// </summary>
    public async Task<object> HandleMemorySplit(JsonNode? arguments)
    {
        var memoryIdStr = arguments?["memory_id"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrEmpty(memoryIdStr) || !Guid.TryParse(memoryIdStr, out var memoryId))
            throw new ArgumentException("Valid memory_id is required");

        var childContentsNode = arguments?["child_contents"];
        if (childContentsNode == null)
            throw new ArgumentException("child_contents array is required");

        var childContents = childContentsNode.AsArray()
            .Select(n => n?.GetValue<string>()?.Trim())
            .Where(c => !string.IsNullOrEmpty(c))
            .ToArray();

        if (childContents.Length < 2)
            throw new ArgumentException("At least 2 child contents required for split");

        var strategy = arguments?["strategy"]?.GetValue<string>()?.Trim() ?? "manual";
        var reason = arguments?["reason"]?.GetValue<string>()?.Trim();
        var actorId = arguments?["actor_id"]?.GetValue<string>()?.Trim();

        try
        {
            // Load parent
            var events = await eventStore.ReadStreamAsync(memoryId);
            if (events.Count == 0)
                return CreateErrorResponse($"Memory {memoryId} not found");

            var parent = MemoryAggregate.FromEvents(events);

            if (!parent.IsActive)
                return CreateErrorResponse($"Memory {memoryId} is inactive and cannot be split");

            // Create child memories
            var childIds = new List<Guid>();
            foreach (var content in childContents)
            {
                var embedding = await embeddingService.EmbedTextAsync(content!);

                var child = MemoryAggregate.Create(
                    content: content!,
                    embedding: embedding,
                    layer: parent.Layer,
                    confidenceScore: parent.ConfidenceScore,
                    halfLifeDays: parent.HalfLifeDays,
                    causalParents: [memoryId],
                    source: parent.Source,
                    userId: parent.UserId,
                    createdBy: actorId);

                await eventStore.AppendEventsAsync(
                    child.Id,
                    child.UncommittedEvents.ToList(),
                    0);

                childIds.Add(child.Id);
            }

            // Mark parent as split
            parent.Split(childIds.ToArray(), strategy, reason, actorId);

            await eventStore.AppendEventsAsync(
                parent.Id,
                parent.UncommittedEvents.ToList(),
                parent.Version - 1);

            logger.LogInformation("Split memory {MemoryId} into {Count} children", memoryId, childIds.Count);

            return CreateTextResponse(
                $"Memory split successfully!\n\n" +
                $"Parent Memory ID: {memoryId}\n" +
                $"Child Memory IDs:\n" +
                string.Join("\n", childIds.Select((id, i) => $"  {i + 1}. {id}")) + "\n\n" +
                $"Strategy: {strategy}\n" +
                $"Reason: {reason ?? "N/A"}\n\n" +
                $"Parent memory has been marked as split (inactive).");
        }
        catch (PostgresException ex) when (ex.SqlState is "42P01" or "42703")
        {
            logger.LogWarning("memory_split skipped: {Message}", ex.MessageText);
            return CreateErrorResponse(EventSourcingNotAvailable);
        }
    }

    /// <summary>
    /// memory_decay - Apply time-based decay to a memory.
    /// </summary>
    public async Task<object> HandleMemoryDecay(JsonNode? arguments)
    {
        var memoryIdStr = arguments?["memory_id"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrEmpty(memoryIdStr) || !Guid.TryParse(memoryIdStr, out var memoryId))
            throw new ArgumentException("Valid memory_id is required");

        var actorId = arguments?["actor_id"]?.GetValue<string>()?.Trim();

        try
        {
            // Load aggregate
            var events = await eventStore.ReadStreamAsync(memoryId);
            if (events.Count == 0)
                return CreateErrorResponse($"Memory {memoryId} not found");

            var aggregate = MemoryAggregate.FromEvents(events);

            if (!aggregate.IsActive)
                return CreateTextResponse($"Memory {memoryId} is inactive, decay not applied");

            var previousConfidence = aggregate.ConfidenceScore;
            var currentConfidence = aggregate.CurrentConfidence;

            // Apply decay (only if significant change)
            aggregate.ApplyDecay(actorId);

            if (aggregate.UncommittedEvents.Count == 0)
            {
                return CreateTextResponse(
                    $"No decay applied.\n\n" +
                    $"Memory ID: {memoryId}\n" +
                    $"Current Confidence: {currentConfidence:F4}\n" +
                    $"Half-Life: {aggregate.HalfLifeDays} days\n" +
                    $"Days Since Reinforcement: {(DateTimeOffset.UtcNow - aggregate.LastReinforcedAt).TotalDays:F1}");
            }

            // Persist
            var sequences = await eventStore.AppendEventsAsync(
                aggregate.Id,
                aggregate.UncommittedEvents.ToList(),
                aggregate.Version - 1);

            logger.LogInformation("Applied decay to memory {MemoryId}: {Previous:F4} -> {New:F4}",
                memoryId, previousConfidence, currentConfidence);

            return CreateTextResponse(
                $"Decay applied successfully!\n\n" +
                $"Memory ID: {memoryId}\n" +
                $"Previous Confidence: {previousConfidence:F4}\n" +
                $"New Confidence: {currentConfidence:F4}\n" +
                $"Half-Life: {aggregate.HalfLifeDays} days\n" +
                $"Days Since Reinforcement: {(DateTimeOffset.UtcNow - aggregate.LastReinforcedAt).TotalDays:F1}\n" +
                $"Event Sequence: {string.Join(", ", sequences)}");
        }
        catch (PostgresException ex) when (ex.SqlState is "42P01" or "42703")
        {
            logger.LogWarning("memory_decay skipped: {Message}", ex.MessageText);
            return CreateErrorResponse(EventSourcingNotAvailable);
        }
    }

    /// <summary>
    /// memory_reinforce - Reset decay and boost confidence.
    /// </summary>
    public async Task<object> HandleMemoryReinforce(JsonNode? arguments)
    {
        var memoryIdStr = arguments?["memory_id"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrEmpty(memoryIdStr) || !Guid.TryParse(memoryIdStr, out var memoryId))
            throw new ArgumentException("Valid memory_id is required");

        var newConfidence = Math.Clamp(arguments?["confidence"]?.GetValue<float>() ?? 1.0f, 0f, 1f);
        var source = arguments?["source"]?.GetValue<string>()?.Trim() ?? "manual";
        var actorId = arguments?["actor_id"]?.GetValue<string>()?.Trim();

        var validatedByNode = arguments?["validated_by_ids"];
        var validatedByIds = validatedByNode?.AsArray()
            .Select(n => Guid.TryParse(n?.GetValue<string>(), out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .ToArray() ?? [];

        try
        {
            // Load aggregate
            var events = await eventStore.ReadStreamAsync(memoryId);
            if (events.Count == 0)
                return CreateErrorResponse($"Memory {memoryId} not found");

            var aggregate = MemoryAggregate.FromEvents(events);

            if (!aggregate.IsActive)
                return CreateErrorResponse($"Memory {memoryId} is inactive and cannot be reinforced");

            var previousConfidence = aggregate.ConfidenceScore;

            // Reinforce
            aggregate.Reinforce(newConfidence, source, validatedByIds, actorId);

            // Persist
            var sequences = await eventStore.AppendEventsAsync(
                aggregate.Id,
                aggregate.UncommittedEvents.ToList(),
                aggregate.Version - 1);

            logger.LogInformation("Reinforced memory {MemoryId}: {Previous:F4} -> {New:F4}",
                memoryId, previousConfidence, newConfidence);

            return CreateTextResponse(
                $"Memory reinforced successfully!\n\n" +
                $"Memory ID: {memoryId}\n" +
                $"Previous Confidence: {previousConfidence:F4}\n" +
                $"New Confidence: {newConfidence:F4}\n" +
                $"Source: {source}\n" +
                $"Validated By: {(validatedByIds.Length > 0 ? string.Join(", ", validatedByIds) : "N/A")}\n" +
                $"Decay Reset: Yes (last_reinforced_at updated)\n" +
                $"Event Sequence: {string.Join(", ", sequences)}");
        }
        catch (PostgresException ex) when (ex.SqlState is "42P01" or "42703")
        {
            logger.LogWarning("memory_reinforce skipped: {Message}", ex.MessageText);
            return CreateErrorResponse(EventSourcingNotAvailable);
        }
    }

    /// <summary>
    /// memory_expire - Expire a memory based on TTL policy.
    /// </summary>
    public async Task<object> HandleMemoryExpire(JsonNode? arguments)
    {
        var memoryIdStr = arguments?["memory_id"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrEmpty(memoryIdStr) || !Guid.TryParse(memoryIdStr, out var memoryId))
            throw new ArgumentException("Valid memory_id is required");

        var policy = arguments?["policy"]?.GetValue<string>()?.Trim() ?? "manual";
        var originalTtlDays = arguments?["ttl_days"]?.GetValue<int>() ?? 0;
        var actorId = arguments?["actor_id"]?.GetValue<string>()?.Trim();

        try
        {
            // Load aggregate
            var events = await eventStore.ReadStreamAsync(memoryId);
            if (events.Count == 0)
                return CreateErrorResponse($"Memory {memoryId} not found");

            var aggregate = MemoryAggregate.FromEvents(events);

            if (!aggregate.IsActive)
                return CreateTextResponse($"Memory {memoryId} is already inactive");

            if (aggregate.IsExpired)
                return CreateTextResponse($"Memory {memoryId} is already expired");

            var confidenceAtExpiration = aggregate.CurrentConfidence;
            var accessCount = aggregate.RecallCount;

            // Expire
            aggregate.Expire(policy, originalTtlDays, actorId);

            // Persist
            var sequences = await eventStore.AppendEventsAsync(
                aggregate.Id,
                aggregate.UncommittedEvents.ToList(),
                aggregate.Version - 1);

            logger.LogInformation("Expired memory {MemoryId} (policy: {Policy})", memoryId, policy);

            return CreateTextResponse(
                $"Memory expired successfully!\n\n" +
                $"Memory ID: {memoryId}\n" +
                $"Expiration Policy: {policy}\n" +
                $"Original TTL: {(originalTtlDays > 0 ? $"{originalTtlDays} days" : "N/A")}\n" +
                $"Confidence at Expiration: {confidenceAtExpiration:F4}\n" +
                $"Access Count at Expiration: {accessCount}\n" +
                $"Event Sequence: {string.Join(", ", sequences)}");
        }
        catch (PostgresException ex) when (ex.SqlState is "42P01" or "42703")
        {
            logger.LogWarning("memory_expire skipped: {Message}", ex.MessageText);
            return CreateErrorResponse(EventSourcingNotAvailable);
        }
    }

    /// <summary>
    /// memory_supersede - Replace a memory with new content.
    /// Creates new memory first, then invalidates old with superseded_by link.
    /// Note: Uses two separate event writes — not a database transaction.
    /// </summary>
    public async Task<object> HandleMemorySupersede(JsonNode? arguments)
    {
        var oldMemoryIdStr = arguments?["old_memory_id"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrEmpty(oldMemoryIdStr) || !Guid.TryParse(oldMemoryIdStr, out var oldMemoryId))
            throw new ArgumentException("Valid old_memory_id is required");

        var newContent = arguments?["new_content"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrEmpty(newContent))
            throw new ArgumentException("new_content is required");
        if (newContent.Length > 100000)
            throw new ArgumentException("Content exceeds maximum length of 100000 characters");

        var reason = arguments?["reason"]?.GetValue<string>()?.Trim() ?? "Superseded with updated content";
        var extractEntities = arguments?["extract_entities"]?.GetValue<bool>() ?? true;
        var actorId = arguments?["actor_id"]?.GetValue<string>()?.Trim();

        try
        {
        // Load old aggregate
        var oldEvents = await eventStore.ReadStreamAsync(oldMemoryId);
        if (oldEvents.Count == 0)
            return CreateErrorResponse($"Memory {oldMemoryId} not found");

        var oldAggregate = MemoryAggregate.FromEvents(oldEvents);

        if (!oldAggregate.IsActive)
            return CreateErrorResponse($"Memory {oldMemoryId} is already inactive and cannot be superseded");

        // Generate embedding for new content
        var newEmbedding = await embeddingService.EmbedTextAsync(newContent);

        // Create new memory inheriting properties from old
        var newAggregate = MemoryAggregate.Create(
            content: newContent,
            embedding: newEmbedding,
            layer: oldAggregate.Layer,
            confidenceScore: oldAggregate.ConfidenceScore,
            halfLifeDays: oldAggregate.HalfLifeDays,
            causalParents: [oldMemoryId],
            source: oldAggregate.Source,
            userId: oldAggregate.UserId,
            createdBy: actorId);

        // Persist new memory
        var newSequences = await eventStore.AppendEventsAsync(
            newAggregate.Id,
            newAggregate.UncommittedEvents.ToList(),
            0);

        // Extract entities from new content and link to new memory
        var entitiesExtracted = 0;
        var relationshipsExtracted = 0;
        if (extractEntities)
        {
            try
            {
                var (extractedEntities, extractedRelationships) =
                    await entityExtractionService.ExtractAllAsync(newContent);

                var entityIdMap = new Dictionary<string, Guid>();
                foreach (var extracted in extractedEntities)
                {
                    var entity = new SerialMemory.Core.Models.Entity
                    {
                        Name = extracted.Text,
                        EntityType = extracted.Label,
                        CanonicalName = extracted.Text.ToLowerInvariant(),
                        FirstSeenMemoryId = newAggregate.Id,
                        Metadata = new Dictionary<string, object>
                        {
                            ["confidence"] = extracted.Confidence,
                            ["start"] = extracted.Start,
                            ["end"] = extracted.End
                        }
                    };
                    var entityId = await knowledgeGraphStore.CreateEntityAsync(entity);
                    entityIdMap[extracted.Text] = entityId;
                    await knowledgeGraphStore.LinkMemoryToEntityAsync(newAggregate.Id, entityId, extracted.Confidence);
                    entitiesExtracted++;
                }

                foreach (var extracted in extractedRelationships)
                {
                    if (!entityIdMap.TryGetValue(extracted.SourceEntity, out var sourceId) ||
                        !entityIdMap.TryGetValue(extracted.TargetEntity, out var targetId))
                        continue;

                    var relationship = new SerialMemory.Core.Models.EntityRelationship
                    {
                        SourceEntityId = sourceId,
                        TargetEntityId = targetId,
                        RelationshipType = extracted.RelationType,
                        Confidence = extracted.Confidence,
                        FirstSeenMemoryId = newAggregate.Id
                    };
                    await knowledgeGraphStore.CreateRelationshipAsync(relationship);
                    relationshipsExtracted++;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Entity extraction failed for superseded memory {NewId}, continuing without entities", newAggregate.Id);
            }
        }

        // Invalidate old memory with superseded_by link
        oldAggregate.Invalidate(reason, supersededById: newAggregate.Id, null, actorId);

        var oldSequences = await eventStore.AppendEventsAsync(
            oldAggregate.Id,
            oldAggregate.UncommittedEvents.ToList(),
            oldAggregate.Version - 1);

        logger.LogInformation("Superseded memory {OldId} with {NewId}", oldMemoryId, newAggregate.Id);

        var entityInfo = extractEntities
            ? $"**Entities extracted:** {entitiesExtracted}\n**Relationships extracted:** {relationshipsExtracted}\n"
            : "";

        return CreateTextResponse(
            $"Memory superseded successfully!\n\n" +
            $"**Old Memory ID:** {oldMemoryId} (now inactive)\n" +
            $"**New Memory ID:** {newAggregate.Id}\n" +
            $"**Reason:** {reason}\n" +
            $"**Layer:** {newAggregate.Layer}\n" +
            $"**Confidence:** {newAggregate.ConfidenceScore:F3}\n" +
            $"**Causal Parent:** {oldMemoryId}\n" +
            entityInfo + "\n" +
            $"Old memory event sequence: {string.Join(", ", oldSequences)}\n" +
            $"New memory event sequence: {string.Join(", ", newSequences)}");
        }
        catch (PostgresException ex) when (ex.SqlState is "42P01" or "42703")
        {
            logger.LogWarning("memory_supersede skipped: {Message}", ex.MessageText);
            return CreateErrorResponse(EventSourcingNotAvailable);
        }
    }

    private static object CreateTextResponse(string text) =>
        new
        {
            content = new[]
            {
                new { type = "text", text }
            }
        };

    private static object CreateErrorResponse(string message) =>
        new
        {
            content = new[]
            {
                new { type = "text", text = $"Error: {message}" }
            },
            isError = true
        };
}
