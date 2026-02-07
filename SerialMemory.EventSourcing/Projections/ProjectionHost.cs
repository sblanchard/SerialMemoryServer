using Microsoft.Extensions.Logging;
using SerialMemory.EventSourcing.Store;

namespace SerialMemory.EventSourcing.Projections;

/// <summary>
/// Hosts and runs projections against the event store.
/// Supports catch-up and live processing.
/// </summary>
public sealed class ProjectionHost(
    IEventStore eventStore,
    IEnumerable<IProjection> projections,
    ILogger<ProjectionHost> logger)
    : IProjectionHost, IDisposable
{
    private readonly IReadOnlyList<IProjection> _projections = [.. projections];
    private readonly CancellationTokenSource _cts = new();
    private Task? _processingTask;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Starting projection host with {Count} projections", _projections.Count);

        // Catch up each projection
        foreach (var projection in _projections)
        {
            await CatchUpProjectionAsync(projection, cancellationToken);
        }

        // Start live processing
        _processingTask = ProcessLiveEventsAsync(_cts.Token);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Stopping projection host");
        await _cts.CancelAsync();

        if (_processingTask != null)
        {
            try
            {
                await _processingTask.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
        }
    }

    public async Task RebuildProjectionAsync(string projectionName, CancellationToken cancellationToken = default)
    {
        var projection = _projections.FirstOrDefault(p => p.ProjectionName == projectionName)
            ?? throw new ArgumentException($"Projection not found: {projectionName}");

        logger.LogInformation("Rebuilding projection: {Name}", projectionName);

        // Reset checkpoint
        await projection.SaveCheckpointAsync(0, cancellationToken);

        // Catch up from beginning
        await CatchUpProjectionAsync(projection, cancellationToken);

        logger.LogInformation("Rebuild complete for projection: {Name}", projectionName);
    }

    private async Task CatchUpProjectionAsync(IProjection projection, CancellationToken cancellationToken)
    {
        var checkpoint = await projection.GetCheckpointAsync(cancellationToken);
        logger.LogInformation("Catching up projection {Name} from sequence {Sequence}",
            projection.ProjectionName, checkpoint);

        var processedCount = 0;
        var lastSequence = checkpoint;

        while (!cancellationToken.IsCancellationRequested)
        {
            var events = await eventStore.ReadAllAsync(lastSequence, 1000, cancellationToken);
            if (events.Count == 0) break;

            foreach (var @event in events)
            {
                try
                {
                    await projection.ApplyAsync(@event, cancellationToken);
                    processedCount++;

                    // Save checkpoint periodically
                    if (processedCount % 100 == 0)
                    {
                        await projection.SaveCheckpointAsync(lastSequence, cancellationToken);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "Skipping corrupted event {EventId} (seq {Sequence}) for projection {Name}: {Message}",
                        @event.EventId, @event.GlobalSequence, projection.ProjectionName, ex.Message);
                    // Skip this event and continue - don't fail the entire projection
                }

                // Always advance sequence even if event processing failed (to skip past bad events)
                lastSequence = @event.GlobalSequence;
            }
        }

        // Save final checkpoint
        if (processedCount > 0)
        {
            await projection.SaveCheckpointAsync(lastSequence, cancellationToken);
        }

        logger.LogInformation("Caught up projection {Name}, processed {Count} events",
            projection.ProjectionName, processedCount);
    }

    private async Task ProcessLiveEventsAsync(CancellationToken cancellationToken)
    {
        // Find minimum checkpoint across all projections
        var minCheckpoint = long.MaxValue;
        foreach (var projection in _projections)
        {
            var checkpoint = await projection.GetCheckpointAsync(cancellationToken);
            minCheckpoint = Math.Min(minCheckpoint, checkpoint);
        }

        if (minCheckpoint == long.MaxValue) minCheckpoint = 0;

        logger.LogInformation("Starting live event processing from sequence {Sequence}", minCheckpoint);

        await foreach (var @event in eventStore.SubscribeAsync(minCheckpoint, cancellationToken))
        {
            foreach (var projection in _projections)
            {
                try
                {
                    var checkpoint = await projection.GetCheckpointAsync(cancellationToken);
                    if (@event.GlobalSequence > checkpoint)
                    {
                        await projection.ApplyAsync(@event, cancellationToken);
                        await projection.SaveCheckpointAsync(@event.GlobalSequence, cancellationToken);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error processing live event {EventId} for projection {Name}",
                        @event.EventId, projection.ProjectionName);
                }
            }
        }
    }

    public void Dispose()
    {
        _cts.Dispose();
    }
}
