using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SerialMemory.Core.Interfaces;
using SerialMemory.EventSourcing.Aggregates;
using SerialMemory.EventSourcing.Events;
using SerialMemory.EventSourcing.Store;

namespace SerialMemory.Mcp.Shared.Tools;

/// <summary>
/// Memory lifecycle tool handlers (update, delete, merge, split, decay, reinforce, expire).
/// </summary>
public sealed class MemoryLifecycleTools(
    IEventStore eventStore,
    IEmbeddingService embeddingService,
    ILogger logger)
{
    public async Task<object> HandleMemoryUpdate(JsonNode? arguments)
    {
        var memoryIdStr = arguments?["memory_id"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrEmpty(memoryIdStr) || !Guid.TryParse(memoryIdStr, out var memoryId))
            throw new ArgumentException("Valid memory_id is required");

        var newContent = arguments?["new_content"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrEmpty(newContent))
            throw new ArgumentException("new_content is required");
        if (newContent.Length > 100000)
            throw new ArgumentException("Content exceeds maximum length");

        var reason = arguments?["reason"]?.GetValue<string>()?.Trim();
        var actorId = arguments?["actor_id"]?.GetValue<string>()?.Trim();

        var events = await eventStore.ReadStreamAsync(memoryId);
        if (events.Count == 0)
            return CreateErrorResponse($"Memory {memoryId} not found");

        var aggregate = MemoryAggregate.FromEvents(events);
        if (!aggregate.IsActive)
            return CreateErrorResponse($"Memory {memoryId} is inactive");

        var newEmbedding = await embeddingService.EmbedTextAsync(newContent);
        aggregate.Update(newContent, newEmbedding, reason, actorId);

        var sequences = await eventStore.AppendEventsAsync(
            aggregate.Id, aggregate.UncommittedEvents.ToList(), aggregate.Version - 1);

        logger.LogInformation("Updated memory {MemoryId}", memoryId);

        return CreateTextResponse(
            $"Memory updated.\n" +
            $"ID: {memoryId}\n" +
            $"Version: {aggregate.Version}\n" +
            $"Reason: {reason ?? "N/A"}");
    }

    public async Task<object> HandleMemoryDelete(JsonNode? arguments)
    {
        var memoryIdStr = arguments?["memory_id"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrEmpty(memoryIdStr) || !Guid.TryParse(memoryIdStr, out var memoryId))
            throw new ArgumentException("Valid memory_id is required");

        var reason = arguments?["reason"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrEmpty(reason))
            throw new ArgumentException("reason is required");

        var supersededByIdStr = arguments?["superseded_by_id"]?.GetValue<string>()?.Trim();
        Guid? supersededById = null;
        if (!string.IsNullOrEmpty(supersededByIdStr) && Guid.TryParse(supersededByIdStr, out var sbId))
            supersededById = sbId;

        var actorId = arguments?["actor_id"]?.GetValue<string>()?.Trim();

        var events = await eventStore.ReadStreamAsync(memoryId);
        if (events.Count == 0)
            return CreateErrorResponse($"Memory {memoryId} not found");

        var aggregate = MemoryAggregate.FromEvents(events);
        if (!aggregate.IsActive)
            return CreateTextResponse($"Memory {memoryId} already inactive");

        aggregate.Invalidate(reason, supersededById, null, actorId);

        await eventStore.AppendEventsAsync(
            aggregate.Id, aggregate.UncommittedEvents.ToList(), aggregate.Version - 1);

        logger.LogInformation("Soft deleted memory {MemoryId}", memoryId);

        return CreateTextResponse(
            $"Memory soft deleted.\n" +
            $"ID: {memoryId}\n" +
            $"Reason: {reason}\n" +
            $"Audit trail preserved.");
    }

    public async Task<object> HandleMemoryMerge(JsonNode? arguments)
    {
        var sourceIdsNode = arguments?["source_memory_ids"];
        if (sourceIdsNode == null)
            throw new ArgumentException("source_memory_ids required");

        var sourceIds = sourceIdsNode.AsArray()
            .Select(n => Guid.TryParse(n?.GetValue<string>(), out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .ToArray();

        if (sourceIds.Length < 2)
            throw new ArgumentException("At least 2 source IDs required");

        var mergedContent = arguments?["merged_content"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrEmpty(mergedContent))
            throw new ArgumentException("merged_content required");

        var strategy = arguments?["strategy"]?.GetValue<string>()?.Trim() ?? "manual";
        var actorId = arguments?["actor_id"]?.GetValue<string>()?.Trim();

        // Validate sources
        foreach (var sourceId in sourceIds)
        {
            var sourceEvents = await eventStore.ReadStreamAsync(sourceId);
            if (sourceEvents.Count == 0)
                return CreateErrorResponse($"Source {sourceId} not found");

            var sourceAgg = MemoryAggregate.FromEvents(sourceEvents);
            if (!sourceAgg.IsActive)
                return CreateErrorResponse($"Source {sourceId} inactive");
        }

        var embedding = await embeddingService.EmbedTextAsync(mergedContent);

        var aggregate = MemoryAggregate.Create(
            content: mergedContent,
            embedding: embedding,
            layer: MemoryLayer.L2_SUMMARY,
            causalParents: sourceIds,
            createdBy: actorId);

        aggregate.Merge(sourceIds, mergedContent, embedding, strategy, actorId);

        await eventStore.AppendEventsAsync(aggregate.Id, aggregate.UncommittedEvents.ToList(), 0);

        // Invalidate sources
        foreach (var sourceId in sourceIds)
        {
            var sourceEvents = await eventStore.ReadStreamAsync(sourceId);
            var sourceAgg = MemoryAggregate.FromEvents(sourceEvents);
            sourceAgg.Invalidate($"Merged into {aggregate.Id}", aggregate.Id, null, actorId);
            await eventStore.AppendEventsAsync(sourceAgg.Id, sourceAgg.UncommittedEvents.ToList(), sourceAgg.Version - 1);
        }

        logger.LogInformation("Merged {Count} memories into {TargetId}", sourceIds.Length, aggregate.Id);

        return CreateTextResponse(
            $"Merged {sourceIds.Length} memories.\n" +
            $"New ID: {aggregate.Id}\n" +
            $"Sources soft deleted.");
    }

    public async Task<object> HandleMemorySplit(JsonNode? arguments)
    {
        var memoryIdStr = arguments?["memory_id"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrEmpty(memoryIdStr) || !Guid.TryParse(memoryIdStr, out var memoryId))
            throw new ArgumentException("Valid memory_id required");

        var childContentsNode = arguments?["child_contents"];
        if (childContentsNode == null)
            throw new ArgumentException("child_contents required");

        var childContents = childContentsNode.AsArray()
            .Select(n => n?.GetValue<string>()?.Trim())
            .Where(c => !string.IsNullOrEmpty(c))
            .ToArray();

        if (childContents.Length < 2)
            throw new ArgumentException("At least 2 children required");

        var strategy = arguments?["strategy"]?.GetValue<string>()?.Trim() ?? "manual";
        var reason = arguments?["reason"]?.GetValue<string>()?.Trim();
        var actorId = arguments?["actor_id"]?.GetValue<string>()?.Trim();

        var events = await eventStore.ReadStreamAsync(memoryId);
        if (events.Count == 0)
            return CreateErrorResponse($"Memory {memoryId} not found");

        var parent = MemoryAggregate.FromEvents(events);
        if (!parent.IsActive)
            return CreateErrorResponse($"Memory {memoryId} inactive");

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

            await eventStore.AppendEventsAsync(child.Id, child.UncommittedEvents.ToList(), 0);
            childIds.Add(child.Id);
        }

        parent.Split(childIds.ToArray(), strategy, reason, actorId);
        await eventStore.AppendEventsAsync(parent.Id, parent.UncommittedEvents.ToList(), parent.Version - 1);

        logger.LogInformation("Split memory {MemoryId} into {Count} children", memoryId, childIds.Count);

        return CreateTextResponse(
            $"Split into {childIds.Count} memories.\n" +
            $"Children: {string.Join(", ", childIds.Take(3))}{(childIds.Count > 3 ? "..." : "")}\n" +
            $"Parent now inactive.");
    }

    public async Task<object> HandleMemoryDecay(JsonNode? arguments)
    {
        var memoryIdStr = arguments?["memory_id"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrEmpty(memoryIdStr) || !Guid.TryParse(memoryIdStr, out var memoryId))
            throw new ArgumentException("Valid memory_id required");

        var actorId = arguments?["actor_id"]?.GetValue<string>()?.Trim();

        var events = await eventStore.ReadStreamAsync(memoryId);
        if (events.Count == 0)
            return CreateErrorResponse($"Memory {memoryId} not found");

        var aggregate = MemoryAggregate.FromEvents(events);
        if (!aggregate.IsActive)
            return CreateTextResponse($"Memory {memoryId} inactive, no decay");

        var previousConfidence = aggregate.ConfidenceScore;
        aggregate.ApplyDecay(actorId);

        if (aggregate.UncommittedEvents.Count == 0)
        {
            return CreateTextResponse(
                $"No decay needed.\n" +
                $"Confidence: {aggregate.CurrentConfidence:F4}");
        }

        await eventStore.AppendEventsAsync(aggregate.Id, aggregate.UncommittedEvents.ToList(), aggregate.Version - 1);

        logger.LogInformation("Decayed memory {MemoryId}: {Prev:F4} -> {New:F4}",
            memoryId, previousConfidence, aggregate.CurrentConfidence);

        return CreateTextResponse(
            $"Decay applied.\n" +
            $"Previous: {previousConfidence:F4}\n" +
            $"Current: {aggregate.CurrentConfidence:F4}");
    }

    public async Task<object> HandleMemoryReinforce(JsonNode? arguments)
    {
        var memoryIdStr = arguments?["memory_id"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrEmpty(memoryIdStr) || !Guid.TryParse(memoryIdStr, out var memoryId))
            throw new ArgumentException("Valid memory_id required");

        var newConfidence = Math.Clamp(arguments?["confidence"]?.GetValue<float>() ?? 1.0f, 0f, 1f);
        var source = arguments?["source"]?.GetValue<string>()?.Trim() ?? "manual";
        var actorId = arguments?["actor_id"]?.GetValue<string>()?.Trim();

        var validatedByNode = arguments?["validated_by_ids"];
        var validatedByIds = validatedByNode?.AsArray()
            .Select(n => Guid.TryParse(n?.GetValue<string>(), out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .ToArray() ?? [];

        var events = await eventStore.ReadStreamAsync(memoryId);
        if (events.Count == 0)
            return CreateErrorResponse($"Memory {memoryId} not found");

        var aggregate = MemoryAggregate.FromEvents(events);
        if (!aggregate.IsActive)
            return CreateErrorResponse($"Memory {memoryId} inactive");

        var previousConfidence = aggregate.ConfidenceScore;
        aggregate.Reinforce(newConfidence, source, validatedByIds, actorId);

        await eventStore.AppendEventsAsync(aggregate.Id, aggregate.UncommittedEvents.ToList(), aggregate.Version - 1);

        logger.LogInformation("Reinforced memory {MemoryId}: {Prev:F4} -> {New:F4}",
            memoryId, previousConfidence, newConfidence);

        return CreateTextResponse(
            $"Memory reinforced.\n" +
            $"Previous: {previousConfidence:F4}\n" +
            $"Current: {newConfidence:F4}\n" +
            $"Decay reset.");
    }

    public async Task<object> HandleMemoryExpire(JsonNode? arguments)
    {
        var memoryIdStr = arguments?["memory_id"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrEmpty(memoryIdStr) || !Guid.TryParse(memoryIdStr, out var memoryId))
            throw new ArgumentException("Valid memory_id required");

        var policy = arguments?["policy"]?.GetValue<string>()?.Trim() ?? "manual";
        var originalTtlDays = arguments?["ttl_days"]?.GetValue<int>() ?? 0;
        var actorId = arguments?["actor_id"]?.GetValue<string>()?.Trim();

        var events = await eventStore.ReadStreamAsync(memoryId);
        if (events.Count == 0)
            return CreateErrorResponse($"Memory {memoryId} not found");

        var aggregate = MemoryAggregate.FromEvents(events);
        if (!aggregate.IsActive)
            return CreateTextResponse($"Memory {memoryId} already inactive");
        if (aggregate.IsExpired)
            return CreateTextResponse($"Memory {memoryId} already expired");

        aggregate.Expire(policy, originalTtlDays, actorId);

        await eventStore.AppendEventsAsync(aggregate.Id, aggregate.UncommittedEvents.ToList(), aggregate.Version - 1);

        logger.LogInformation("Expired memory {MemoryId}", memoryId);

        return CreateTextResponse(
            $"Memory expired.\n" +
            $"ID: {memoryId}\n" +
            $"Policy: {policy}");
    }

    private static object CreateTextResponse(string text) =>
        new { content = new[] { new { type = "text", text } } };

    private static object CreateErrorResponse(string message) =>
        new { content = new[] { new { type = "text", text = $"Error: {message}" } }, isError = true };
}
