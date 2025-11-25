using Microsoft.Extensions.Logging;
using SerialMemory.Core.Interfaces;
using SerialMemory.EventSourcing.Aggregates;
using SerialMemory.EventSourcing.Store;
using SerialMemory.EventSourcing.Streaming;

namespace SerialMemory.EventSourcing.CQRS;

internal static class EventPublishingExtensions
{
    /// <summary>
    /// Publishes uncommitted events to the Redis stream for downstream projections/subscribers.
    /// </summary>
    public static async Task PublishUncommittedEventsAsync(
        this IEventStreamPublisher publisher,
        MemoryAggregate aggregate,
        IReadOnlyList<long> sequences,
        CancellationToken cancellationToken)
    {
        var uncommitted = aggregate.UncommittedEvents.ToList();
        for (var i = 0; i < uncommitted.Count; i++)
        {
            var @event = uncommitted[i];
            await publisher.PublishToStreamAsync(new StoredEvent
            {
                EventId = @event.EventId,
                StreamId = @event.StreamId,
                EventType = @event.EventType,
                EventVersion = @event.EventVersion,
                GlobalSequence = sequences[i],
                EventData = System.Text.Json.JsonSerializer.Serialize(@event, @event.GetType()),
                Metadata = "{}",
                CreatedAt = @event.CreatedAt,
                CreatedBy = @event.CreatedBy,
                ContentHash = @event.ContentHash
            }, cancellationToken);
        }
    }
}

/// <summary>
/// Handler for CreateMemoryCommand.
/// </summary>
public sealed class CreateMemoryCommandHandler : ICommandHandler<CreateMemoryCommand>
{
    private readonly IEventStore _eventStore;
    private readonly IEmbeddingService _embeddingService;
    private readonly IEventStreamPublisher _streamPublisher;
    private readonly ILogger<CreateMemoryCommandHandler> _logger;

    public CreateMemoryCommandHandler(
        IEventStore eventStore,
        IEmbeddingService embeddingService,
        IEventStreamPublisher streamPublisher,
        ILogger<CreateMemoryCommandHandler> logger)
    {
        _eventStore = eventStore;
        _embeddingService = embeddingService;
        _streamPublisher = streamPublisher;
        _logger = logger;
    }

    public async Task<CommandResult> HandleAsync(CreateMemoryCommand command, CancellationToken cancellationToken = default)
    {
        try
        {
            // Generate embedding
            var embedding = await _embeddingService.EmbedTextAsync(command.Content, cancellationToken);

            // Create aggregate
            var aggregate = MemoryAggregate.Create(
                content: command.Content,
                embedding: embedding,
                layer: command.Layer,
                confidenceScore: command.ConfidenceScore,
                halfLifeDays: command.HalfLifeDays,
                causalParents: command.CausalParents,
                source: command.Source,
                sessionId: command.SessionId,
                userId: command.UserId,
                tags: command.Tags,
                createdBy: command.ActorId);

            // Persist events
            var sequences = await _eventStore.AppendEventsAsync(
                aggregate.Id,
                aggregate.UncommittedEvents.ToList(),
                0,
                cancellationToken);

            // Publish to stream for projections
            await _streamPublisher.PublishUncommittedEventsAsync(aggregate, sequences, cancellationToken);

            aggregate.ClearUncommittedEvents();

            _logger.LogInformation("Created memory {MemoryId}", aggregate.Id);
            return CommandResult.Ok(aggregate.Id, aggregate.Version, sequences);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create memory");
            return CommandResult.Fail(ex.Message);
        }
    }
}

/// <summary>
/// Handler for UpdateMemoryCommand.
/// </summary>
public sealed class UpdateMemoryCommandHandler : ICommandHandler<UpdateMemoryCommand>
{
    private readonly IEventStore _eventStore;
    private readonly IEmbeddingService _embeddingService;
    private readonly IEventStreamPublisher _streamPublisher;
    private readonly ILogger<UpdateMemoryCommandHandler> _logger;

    public UpdateMemoryCommandHandler(
        IEventStore eventStore,
        IEmbeddingService embeddingService,
        IEventStreamPublisher streamPublisher,
        ILogger<UpdateMemoryCommandHandler> logger)
    {
        _eventStore = eventStore;
        _embeddingService = embeddingService;
        _streamPublisher = streamPublisher;
        _logger = logger;
    }

    public async Task<CommandResult> HandleAsync(UpdateMemoryCommand command, CancellationToken cancellationToken = default)
    {
        try
        {
            // Load aggregate from events
            var events = await _eventStore.ReadStreamAsync(command.MemoryId, cancellationToken);
            if (events.Count == 0)
                return CommandResult.Fail($"Memory {command.MemoryId} not found");

            var aggregate = MemoryAggregate.FromEvents(events);

            // Generate new embedding
            var newEmbedding = await _embeddingService.EmbedTextAsync(command.NewContent, cancellationToken);

            // Apply command
            aggregate.Update(command.NewContent, newEmbedding, command.Reason, command.ActorId);

            // Persist events
            var sequences = await _eventStore.AppendEventsAsync(
                aggregate.Id,
                aggregate.UncommittedEvents.ToList(),
                aggregate.Version - 1,
                cancellationToken);

            // Publish to stream for projections
            await _streamPublisher.PublishUncommittedEventsAsync(aggregate, sequences, cancellationToken);

            aggregate.ClearUncommittedEvents();

            _logger.LogInformation("Updated memory {MemoryId}", aggregate.Id);
            return CommandResult.Ok(aggregate.Id, aggregate.Version, sequences);
        }
        catch (ConcurrencyException ex)
        {
            _logger.LogWarning("Concurrency conflict updating memory {MemoryId}", command.MemoryId);
            return CommandResult.Fail(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update memory {MemoryId}", command.MemoryId);
            return CommandResult.Fail(ex.Message);
        }
    }
}

/// <summary>
/// Handler for ReinforceMemoryCommand.
/// </summary>
public sealed class ReinforceMemoryCommandHandler : ICommandHandler<ReinforceMemoryCommand>
{
    private readonly IEventStore _eventStore;
    private readonly IEventStreamPublisher _streamPublisher;
    private readonly ILogger<ReinforceMemoryCommandHandler> _logger;

    public ReinforceMemoryCommandHandler(
        IEventStore eventStore,
        IEventStreamPublisher streamPublisher,
        ILogger<ReinforceMemoryCommandHandler> logger)
    {
        _eventStore = eventStore;
        _streamPublisher = streamPublisher;
        _logger = logger;
    }

    public async Task<CommandResult> HandleAsync(ReinforceMemoryCommand command, CancellationToken cancellationToken = default)
    {
        try
        {
            var events = await _eventStore.ReadStreamAsync(command.MemoryId, cancellationToken);
            if (events.Count == 0)
                return CommandResult.Fail($"Memory {command.MemoryId} not found");

            var aggregate = MemoryAggregate.FromEvents(events);
            aggregate.Reinforce(command.NewConfidence, command.Source, command.ValidatedByIds, command.ActorId);

            var sequences = await _eventStore.AppendEventsAsync(
                aggregate.Id,
                aggregate.UncommittedEvents.ToList(),
                aggregate.Version - 1,
                cancellationToken);

            // Publish to stream for projections
            await _streamPublisher.PublishUncommittedEventsAsync(aggregate, sequences, cancellationToken);

            aggregate.ClearUncommittedEvents();

            _logger.LogInformation("Reinforced memory {MemoryId}", aggregate.Id);
            return CommandResult.Ok(aggregate.Id, aggregate.Version, sequences);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reinforce memory {MemoryId}", command.MemoryId);
            return CommandResult.Fail(ex.Message);
        }
    }
}

/// <summary>
/// Handler for InvalidateMemoryCommand.
/// </summary>
public sealed class InvalidateMemoryCommandHandler : ICommandHandler<InvalidateMemoryCommand>
{
    private readonly IEventStore _eventStore;
    private readonly IEventStreamPublisher _streamPublisher;
    private readonly ILogger<InvalidateMemoryCommandHandler> _logger;

    public InvalidateMemoryCommandHandler(
        IEventStore eventStore,
        IEventStreamPublisher streamPublisher,
        ILogger<InvalidateMemoryCommandHandler> logger)
    {
        _eventStore = eventStore;
        _streamPublisher = streamPublisher;
        _logger = logger;
    }

    public async Task<CommandResult> HandleAsync(InvalidateMemoryCommand command, CancellationToken cancellationToken = default)
    {
        try
        {
            var events = await _eventStore.ReadStreamAsync(command.MemoryId, cancellationToken);
            if (events.Count == 0)
                return CommandResult.Fail($"Memory {command.MemoryId} not found");

            var aggregate = MemoryAggregate.FromEvents(events);
            aggregate.Invalidate(command.Reason, command.SupersededById, command.ContradictedByIds, command.ActorId);

            var sequences = await _eventStore.AppendEventsAsync(
                aggregate.Id,
                aggregate.UncommittedEvents.ToList(),
                aggregate.Version - 1,
                cancellationToken);

            // Publish to stream for projections
            await _streamPublisher.PublishUncommittedEventsAsync(aggregate, sequences, cancellationToken);

            aggregate.ClearUncommittedEvents();

            _logger.LogInformation("Invalidated memory {MemoryId}", aggregate.Id);
            return CommandResult.Ok(aggregate.Id, aggregate.Version, sequences);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to invalidate memory {MemoryId}", command.MemoryId);
            return CommandResult.Fail(ex.Message);
        }
    }
}

/// <summary>
/// Handler for TransitionLayerCommand.
/// </summary>
public sealed class TransitionLayerCommandHandler : ICommandHandler<TransitionLayerCommand>
{
    private readonly IEventStore _eventStore;
    private readonly IEventStreamPublisher _streamPublisher;
    private readonly ILogger<TransitionLayerCommandHandler> _logger;

    public TransitionLayerCommandHandler(
        IEventStore eventStore,
        IEventStreamPublisher streamPublisher,
        ILogger<TransitionLayerCommandHandler> logger)
    {
        _eventStore = eventStore;
        _streamPublisher = streamPublisher;
        _logger = logger;
    }

    public async Task<CommandResult> HandleAsync(TransitionLayerCommand command, CancellationToken cancellationToken = default)
    {
        try
        {
            var events = await _eventStore.ReadStreamAsync(command.MemoryId, cancellationToken);
            if (events.Count == 0)
                return CommandResult.Fail($"Memory {command.MemoryId} not found");

            var aggregate = MemoryAggregate.FromEvents(events);
            aggregate.TransitionLayer(command.NewLayer, command.Reason, command.TriggeredByMemoryId, command.ActorId);

            var sequences = await _eventStore.AppendEventsAsync(
                aggregate.Id,
                aggregate.UncommittedEvents.ToList(),
                aggregate.Version - 1,
                cancellationToken);

            // Publish to stream for projections
            await _streamPublisher.PublishUncommittedEventsAsync(aggregate, sequences, cancellationToken);

            aggregate.ClearUncommittedEvents();

            _logger.LogInformation("Transitioned memory {MemoryId} to layer {Layer}", aggregate.Id, command.NewLayer);
            return CommandResult.Ok(aggregate.Id, aggregate.Version, sequences);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to transition memory {MemoryId}", command.MemoryId);
            return CommandResult.Fail(ex.Message);
        }
    }
}

/// <summary>
/// Handler for MergeMemoriesCommand.
/// </summary>
public sealed class MergeMemoriesCommandHandler : ICommandHandler<MergeMemoriesCommand>
{
    private readonly IEventStore _eventStore;
    private readonly IEmbeddingService _embeddingService;
    private readonly IEventStreamPublisher _streamPublisher;
    private readonly ILogger<MergeMemoriesCommandHandler> _logger;

    public MergeMemoriesCommandHandler(
        IEventStore eventStore,
        IEmbeddingService embeddingService,
        IEventStreamPublisher streamPublisher,
        ILogger<MergeMemoriesCommandHandler> logger)
    {
        _eventStore = eventStore;
        _embeddingService = embeddingService;
        _streamPublisher = streamPublisher;
        _logger = logger;
    }

    public async Task<CommandResult> HandleAsync(MergeMemoriesCommand command, CancellationToken cancellationToken = default)
    {
        try
        {
            // Generate embedding for merged content
            var embedding = await _embeddingService.EmbedTextAsync(command.MergedContent, cancellationToken);

            // Create new aggregate for merged memory
            var aggregate = MemoryAggregate.Create(
                content: command.MergedContent,
                embedding: embedding,
                layer: Events.MemoryLayer.L2_SUMMARY,
                causalParents: command.SourceMemoryIds,
                createdBy: command.ActorId);

            // Also record merge event
            aggregate.Merge(command.SourceMemoryIds, command.MergedContent, embedding, command.MergeStrategy, command.ActorId);

            var sequences = await _eventStore.AppendEventsAsync(
                aggregate.Id,
                aggregate.UncommittedEvents.ToList(),
                0,
                cancellationToken);

            // Publish to stream for projections
            await _streamPublisher.PublishUncommittedEventsAsync(aggregate, sequences, cancellationToken);

            aggregate.ClearUncommittedEvents();

            _logger.LogInformation("Merged memories {SourceIds} into {TargetId}",
                command.SourceMemoryIds, aggregate.Id);
            return CommandResult.Ok(aggregate.Id, aggregate.Version, sequences);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to merge memories");
            return CommandResult.Fail(ex.Message);
        }
    }
}

/// <summary>
/// Handler for ApplyDecayCommand.
/// </summary>
public sealed class ApplyDecayCommandHandler : ICommandHandler<ApplyDecayCommand>
{
    private readonly IEventStore _eventStore;
    private readonly IEventStreamPublisher _streamPublisher;
    private readonly ILogger<ApplyDecayCommandHandler> _logger;

    public ApplyDecayCommandHandler(
        IEventStore eventStore,
        IEventStreamPublisher streamPublisher,
        ILogger<ApplyDecayCommandHandler> logger)
    {
        _eventStore = eventStore;
        _streamPublisher = streamPublisher;
        _logger = logger;
    }

    public async Task<CommandResult> HandleAsync(ApplyDecayCommand command, CancellationToken cancellationToken = default)
    {
        try
        {
            var events = await _eventStore.ReadStreamAsync(command.MemoryId, cancellationToken);
            if (events.Count == 0)
                return CommandResult.Fail($"Memory {command.MemoryId} not found");

            var aggregate = MemoryAggregate.FromEvents(events);
            aggregate.ApplyDecay(command.ActorId);

            // ApplyDecay may not produce events if confidence hasn't changed significantly
            if (aggregate.UncommittedEvents.Count == 0)
                return CommandResult.Ok(aggregate.Id, aggregate.Version, []);

            var sequences = await _eventStore.AppendEventsAsync(
                aggregate.Id,
                aggregate.UncommittedEvents.ToList(),
                aggregate.Version - 1,
                cancellationToken);

            // Publish to stream for projections
            await _streamPublisher.PublishUncommittedEventsAsync(aggregate, sequences, cancellationToken);

            aggregate.ClearUncommittedEvents();

            _logger.LogDebug("Applied decay to memory {MemoryId}", aggregate.Id);
            return CommandResult.Ok(aggregate.Id, aggregate.Version, sequences);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply decay to memory {MemoryId}", command.MemoryId);
            return CommandResult.Fail(ex.Message);
        }
    }
}

/// <summary>
/// Handler for ArchiveMemoryCommand.
/// </summary>
public sealed class ArchiveMemoryCommandHandler : ICommandHandler<ArchiveMemoryCommand>
{
    private readonly IEventStore _eventStore;
    private readonly IEventStreamPublisher _streamPublisher;
    private readonly ILogger<ArchiveMemoryCommandHandler> _logger;

    public ArchiveMemoryCommandHandler(
        IEventStore eventStore,
        IEventStreamPublisher streamPublisher,
        ILogger<ArchiveMemoryCommandHandler> logger)
    {
        _eventStore = eventStore;
        _streamPublisher = streamPublisher;
        _logger = logger;
    }

    public async Task<CommandResult> HandleAsync(ArchiveMemoryCommand command, CancellationToken cancellationToken = default)
    {
        try
        {
            var events = await _eventStore.ReadStreamAsync(command.MemoryId, cancellationToken);
            if (events.Count == 0)
                return CommandResult.Fail($"Memory {command.MemoryId} not found");

            var aggregate = MemoryAggregate.FromEvents(events);
            aggregate.Archive(command.Reason, command.AccessCount, command.LastAccessedAt, command.ActorId);

            // Archive may not produce events if already archived
            if (aggregate.UncommittedEvents.Count == 0)
                return CommandResult.Ok(aggregate.Id, aggregate.Version, []);

            var sequences = await _eventStore.AppendEventsAsync(
                aggregate.Id,
                aggregate.UncommittedEvents.ToList(),
                aggregate.Version - 1,
                cancellationToken);

            // Publish to stream for projections
            await _streamPublisher.PublishUncommittedEventsAsync(aggregate, sequences, cancellationToken);

            aggregate.ClearUncommittedEvents();

            _logger.LogInformation("Archived memory {MemoryId}", aggregate.Id);
            return CommandResult.Ok(aggregate.Id, aggregate.Version, sequences);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to archive memory {MemoryId}", command.MemoryId);
            return CommandResult.Fail(ex.Message);
        }
    }
}

/// <summary>
/// Handler for RecallMemoryCommand.
/// </summary>
public sealed class RecallMemoryCommandHandler : ICommandHandler<RecallMemoryCommand>
{
    private readonly IEventStore _eventStore;
    private readonly IEventStreamPublisher _streamPublisher;
    private readonly ILogger<RecallMemoryCommandHandler> _logger;

    public RecallMemoryCommandHandler(
        IEventStore eventStore,
        IEventStreamPublisher streamPublisher,
        ILogger<RecallMemoryCommandHandler> logger)
    {
        _eventStore = eventStore;
        _streamPublisher = streamPublisher;
        _logger = logger;
    }

    public async Task<CommandResult> HandleAsync(RecallMemoryCommand command, CancellationToken cancellationToken = default)
    {
        try
        {
            var events = await _eventStore.ReadStreamAsync(command.MemoryId, cancellationToken);
            if (events.Count == 0)
                return CommandResult.Fail($"Memory {command.MemoryId} not found");

            var aggregate = MemoryAggregate.FromEvents(events);
            aggregate.Recall(command.Query, command.SimilarityScore, command.Context, command.SessionId, command.ActorId);

            if (aggregate.UncommittedEvents.Count == 0)
                return CommandResult.Ok(aggregate.Id, aggregate.Version, []);

            var sequences = await _eventStore.AppendEventsAsync(
                aggregate.Id,
                aggregate.UncommittedEvents.ToList(),
                aggregate.Version - 1,
                cancellationToken);

            await _streamPublisher.PublishUncommittedEventsAsync(aggregate, sequences, cancellationToken);
            aggregate.ClearUncommittedEvents();

            _logger.LogDebug("Recorded recall for memory {MemoryId}", aggregate.Id);
            return CommandResult.Ok(aggregate.Id, aggregate.Version, sequences);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to record recall for memory {MemoryId}", command.MemoryId);
            return CommandResult.Fail(ex.Message);
        }
    }
}

/// <summary>
/// Handler for IgnoreMemoryCommand.
/// </summary>
public sealed class IgnoreMemoryCommandHandler : ICommandHandler<IgnoreMemoryCommand>
{
    private readonly IEventStore _eventStore;
    private readonly IEventStreamPublisher _streamPublisher;
    private readonly ILogger<IgnoreMemoryCommandHandler> _logger;

    public IgnoreMemoryCommandHandler(
        IEventStore eventStore,
        IEventStreamPublisher streamPublisher,
        ILogger<IgnoreMemoryCommandHandler> logger)
    {
        _eventStore = eventStore;
        _streamPublisher = streamPublisher;
        _logger = logger;
    }

    public async Task<CommandResult> HandleAsync(IgnoreMemoryCommand command, CancellationToken cancellationToken = default)
    {
        try
        {
            var events = await _eventStore.ReadStreamAsync(command.MemoryId, cancellationToken);
            if (events.Count == 0)
                return CommandResult.Fail($"Memory {command.MemoryId} not found");

            var aggregate = MemoryAggregate.FromEvents(events);
            aggregate.Ignore(command.Query, command.Reason, command.SessionId, command.ActorId);

            if (aggregate.UncommittedEvents.Count == 0)
                return CommandResult.Ok(aggregate.Id, aggregate.Version, []);

            var sequences = await _eventStore.AppendEventsAsync(
                aggregate.Id,
                aggregate.UncommittedEvents.ToList(),
                aggregate.Version - 1,
                cancellationToken);

            await _streamPublisher.PublishUncommittedEventsAsync(aggregate, sequences, cancellationToken);
            aggregate.ClearUncommittedEvents();

            _logger.LogDebug("Recorded ignore for memory {MemoryId}", aggregate.Id);
            return CommandResult.Ok(aggregate.Id, aggregate.Version, sequences);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to record ignore for memory {MemoryId}", command.MemoryId);
            return CommandResult.Fail(ex.Message);
        }
    }
}

/// <summary>
/// Handler for MarkContradictionCommand.
/// </summary>
public sealed class MarkContradictionCommandHandler : ICommandHandler<MarkContradictionCommand>
{
    private readonly IEventStore _eventStore;
    private readonly IEventStreamPublisher _streamPublisher;
    private readonly ILogger<MarkContradictionCommandHandler> _logger;

    public MarkContradictionCommandHandler(
        IEventStore eventStore,
        IEventStreamPublisher streamPublisher,
        ILogger<MarkContradictionCommandHandler> logger)
    {
        _eventStore = eventStore;
        _streamPublisher = streamPublisher;
        _logger = logger;
    }

    public async Task<CommandResult> HandleAsync(MarkContradictionCommand command, CancellationToken cancellationToken = default)
    {
        try
        {
            var events = await _eventStore.ReadStreamAsync(command.MemoryId, cancellationToken);
            if (events.Count == 0)
                return CommandResult.Fail($"Memory {command.MemoryId} not found");

            var aggregate = MemoryAggregate.FromEvents(events);
            aggregate.MarkContradiction(
                command.ContradictingMemoryIds,
                command.ContradictionType,
                command.DetectionMethod,
                command.ContradictionConfidence,
                command.ActorId);

            if (aggregate.UncommittedEvents.Count == 0)
                return CommandResult.Ok(aggregate.Id, aggregate.Version, []);

            var sequences = await _eventStore.AppendEventsAsync(
                aggregate.Id,
                aggregate.UncommittedEvents.ToList(),
                aggregate.Version - 1,
                cancellationToken);

            await _streamPublisher.PublishUncommittedEventsAsync(aggregate, sequences, cancellationToken);
            aggregate.ClearUncommittedEvents();

            _logger.LogInformation("Marked contradiction for memory {MemoryId}", aggregate.Id);
            return CommandResult.Ok(aggregate.Id, aggregate.Version, sequences);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to mark contradiction for memory {MemoryId}", command.MemoryId);
            return CommandResult.Fail(ex.Message);
        }
    }
}

/// <summary>
/// Handler for ExpireMemoryCommand.
/// </summary>
public sealed class ExpireMemoryCommandHandler : ICommandHandler<ExpireMemoryCommand>
{
    private readonly IEventStore _eventStore;
    private readonly IEventStreamPublisher _streamPublisher;
    private readonly ILogger<ExpireMemoryCommandHandler> _logger;

    public ExpireMemoryCommandHandler(
        IEventStore eventStore,
        IEventStreamPublisher streamPublisher,
        ILogger<ExpireMemoryCommandHandler> logger)
    {
        _eventStore = eventStore;
        _streamPublisher = streamPublisher;
        _logger = logger;
    }

    public async Task<CommandResult> HandleAsync(ExpireMemoryCommand command, CancellationToken cancellationToken = default)
    {
        try
        {
            var events = await _eventStore.ReadStreamAsync(command.MemoryId, cancellationToken);
            if (events.Count == 0)
                return CommandResult.Fail($"Memory {command.MemoryId} not found");

            var aggregate = MemoryAggregate.FromEvents(events);
            aggregate.Expire(command.ExpirationPolicy, command.OriginalTtlDays, command.ActorId);

            if (aggregate.UncommittedEvents.Count == 0)
                return CommandResult.Ok(aggregate.Id, aggregate.Version, []);

            var sequences = await _eventStore.AppendEventsAsync(
                aggregate.Id,
                aggregate.UncommittedEvents.ToList(),
                aggregate.Version - 1,
                cancellationToken);

            await _streamPublisher.PublishUncommittedEventsAsync(aggregate, sequences, cancellationToken);
            aggregate.ClearUncommittedEvents();

            _logger.LogInformation("Expired memory {MemoryId}", aggregate.Id);
            return CommandResult.Ok(aggregate.Id, aggregate.Version, sequences);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to expire memory {MemoryId}", command.MemoryId);
            return CommandResult.Fail(ex.Message);
        }
    }
}

/// <summary>
/// Handler for SplitMemoryCommand.
/// </summary>
public sealed class SplitMemoryCommandHandler : ICommandHandler<SplitMemoryCommand>
{
    private readonly IEventStore _eventStore;
    private readonly IEventStreamPublisher _streamPublisher;
    private readonly ILogger<SplitMemoryCommandHandler> _logger;

    public SplitMemoryCommandHandler(
        IEventStore eventStore,
        IEventStreamPublisher streamPublisher,
        ILogger<SplitMemoryCommandHandler> logger)
    {
        _eventStore = eventStore;
        _streamPublisher = streamPublisher;
        _logger = logger;
    }

    public async Task<CommandResult> HandleAsync(SplitMemoryCommand command, CancellationToken cancellationToken = default)
    {
        try
        {
            var events = await _eventStore.ReadStreamAsync(command.MemoryId, cancellationToken);
            if (events.Count == 0)
                return CommandResult.Fail($"Memory {command.MemoryId} not found");

            var aggregate = MemoryAggregate.FromEvents(events);
            aggregate.Split(command.ChildMemoryIds, command.SplitStrategy, command.Reason, command.ActorId);

            if (aggregate.UncommittedEvents.Count == 0)
                return CommandResult.Ok(aggregate.Id, aggregate.Version, []);

            var sequences = await _eventStore.AppendEventsAsync(
                aggregate.Id,
                aggregate.UncommittedEvents.ToList(),
                aggregate.Version - 1,
                cancellationToken);

            await _streamPublisher.PublishUncommittedEventsAsync(aggregate, sequences, cancellationToken);
            aggregate.ClearUncommittedEvents();

            _logger.LogInformation("Split memory {MemoryId} into {ChildCount} children", aggregate.Id, command.ChildMemoryIds.Length);
            return CommandResult.Ok(aggregate.Id, aggregate.Version, sequences);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to split memory {MemoryId}", command.MemoryId);
            return CommandResult.Fail(ex.Message);
        }
    }
}
