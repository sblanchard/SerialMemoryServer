using MassTransit;
using SerialMemory.Contracts.Events;

namespace SerialMemory.Infrastructure;

/// <summary>
/// Production-grade event publisher using MassTransit.
/// Provides reliable message delivery with correlation IDs for distributed tracing.
/// </summary>
public class MassTransitEventPublisher(IPublishEndpoint publishEndpoint)
{
    /// <summary>
    /// Publishes a ContextUpdated event.
    /// MassTransit handles:
    /// - Automatic serialization
    /// - Correlation ID propagation
    /// - Retry policies (configured at bus level)
    /// - Error queues (dead letter queue)
    /// </summary>
    public async Task PublishContextUpdatedAsync(
        string key,
        string? value = null,
        string? source = null,
        Dictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        var @event = new ContextUpdated
        {
            CorrelationId = Guid.CreateVersion7(), // For distributed tracing
            Key = key,
            Value = value,
            Source = source ?? "api",
            Metadata = metadata
        };

        // MassTransit publish - fire and forget with reliability
        await publishEndpoint.Publish(@event, cancellationToken);
    }

    /// <summary>
    /// Publishes a ContextDeleted event.
    /// </summary>
    public async Task PublishContextDeletedAsync(
        string key,
        string? source = null,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        var @event = new ContextDeleted
        {
            CorrelationId = Guid.NewGuid(),
            Key = key,
            Source = source ?? "api",
            Reason = reason
        };

        await publishEndpoint.Publish(@event, cancellationToken);
    }
}
