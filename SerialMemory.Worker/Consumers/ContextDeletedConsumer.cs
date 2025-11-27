using MassTransit;
using SerialMemory.Contracts.Events;

namespace SerialMemory.Worker.Consumers;

/// <summary>
/// MassTransit consumer for ContextDeleted events.
/// Could be extended to:
/// - Archive deleted contexts
/// - Trigger cleanup workflows
/// - Send notifications
/// </summary>
public class ContextDeletedConsumer(ILogger<ContextDeletedConsumer> logger) : IConsumer<ContextDeleted>
{
    public async Task Consume(ConsumeContext<ContextDeleted> context)
    {
        var message = context.Message;

        logger.LogInformation(
            "[MassTransit Consumer] Processing ContextDeleted: Key={Key}, CorrelationId={CorrelationId}, Reason={Reason}",
            message.Key,
            message.CorrelationId,
            message.Reason ?? "not specified");

        // TODO: Implement deletion logic (archive, cleanup, notifications, etc.)
        await Task.CompletedTask;
    }
}
