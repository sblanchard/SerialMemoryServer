using Microsoft.Extensions.Logging;
using SerialMemory.Core.Interfaces;
using SerialMemory.EventSourcing.Aggregates;
using SerialMemory.EventSourcing.Store;
using SerialMemory.EventSourcing.Streaming;

namespace SerialMemory.EventSourcing.CQRS;

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
            foreach (var @event in aggregate.UncommittedEvents)
            {
                await _streamPublisher.PublishToStreamAsync(new StoredEvent
                {
                    EventId = @event.EventId,
                    StreamId = @event.StreamId,
                    EventType = @event.EventType,
                    EventVersion = @event.EventVersion,
                    GlobalSequence = sequences[0],
                    EventData = System.Text.Json.JsonSerializer.Serialize(@event),
                    Metadata = "{}",
                    CreatedAt = @event.CreatedAt,
                    CreatedBy = @event.CreatedBy,
                    ContentHash = @event.ContentHash
                }, cancellationToken);
            }

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
