namespace SerialMemory.EventSourcing.CQRS;

/// <summary>
/// Interface for command handlers.
/// No MediatR - direct handler registration.
/// </summary>
/// <typeparam name="TCommand">Command type</typeparam>
public interface ICommandHandler<in TCommand> where TCommand : ICommand
{
    Task<CommandResult> HandleAsync(TCommand command, CancellationToken cancellationToken = default);
}

/// <summary>
/// Command dispatcher for routing commands to handlers.
/// Simple service locator pattern without MediatR.
/// </summary>
public interface ICommandDispatcher
{
    Task<CommandResult> DispatchAsync<TCommand>(TCommand command, CancellationToken cancellationToken = default)
        where TCommand : ICommand;
}

/// <summary>
/// Simple command dispatcher implementation using DI.
/// </summary>
public sealed class CommandDispatcher : ICommandDispatcher
{
    private readonly IServiceProvider _serviceProvider;

    public CommandDispatcher(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public async Task<CommandResult> DispatchAsync<TCommand>(TCommand command, CancellationToken cancellationToken = default)
        where TCommand : ICommand
    {
        var handler = _serviceProvider.GetService(typeof(ICommandHandler<TCommand>)) as ICommandHandler<TCommand>
            ?? throw new InvalidOperationException($"No handler registered for {typeof(TCommand).Name}");

        return await handler.HandleAsync(command, cancellationToken);
    }
}
