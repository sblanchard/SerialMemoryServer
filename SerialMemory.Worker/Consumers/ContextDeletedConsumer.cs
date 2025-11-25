using MassTransit;
using SerialMemory.Contracts.Events;
using Microsoft.Extensions.Logging;

namespace SerialMemory.Worker.Consumers;

/// <summary>
/// MassTransit consumer for ContextDeleted events.
/// Could be extended to:
/// - Archive deleted contexts
/// - Trigger cleanup workflows
/// - Send notifications
/// </summary>
public class ContextDeletedConsumer : IConsumer<ContextDeleted>
{
    private readonly ILogger<ContextDeletedConsumer> _logger;

    public ContextDeletedConsumer(ILogger<ContextDeletedConsumer> logger)
    {
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<ContextDeleted> context)
    {
        var message = context.Message;

        _logger.LogInformation(
            "[MassTransit Consumer] Processing ContextDeleted: Key={Key}, CorrelationId={CorrelationId}, Reason={Reason}",
            message.Key,
            message.CorrelationId,
            message.Reason ?? "not specified");

        // TODO: Implement deletion logic (archive, cleanup, notifications, etc.)
        await Task.CompletedTask;
    }
}
