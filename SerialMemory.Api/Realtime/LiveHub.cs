using Microsoft.AspNetCore.SignalR;

namespace SerialMemory.Api.Realtime;

/// <summary>
/// Real-time SignalR hub for live streaming of reasoning, security, and graph events.
/// </summary>
public sealed class LiveHub : Hub
{
    private readonly ILogger<LiveHub> _logger;

    public LiveHub(ILogger<LiveHub> logger)
    {
        _logger = logger;
    }

    public override async Task OnConnectedAsync()
    {
        _logger.LogInformation("Client connected: {ConnectionId}", Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation("Client disconnected: {ConnectionId}", Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Subscribe to a specific stream (reasoning.progress, reasoning.results, security.anomalies, graph.changes)
    /// </summary>
    public async Task Subscribe(string stream)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, stream);
        _logger.LogDebug("Client {ConnectionId} subscribed to {Stream}", Context.ConnectionId, stream);
        await Clients.Caller.SendAsync("Subscribed", stream);
    }

    /// <summary>
    /// Unsubscribe from a stream
    /// </summary>
    public async Task Unsubscribe(string stream)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, stream);
        _logger.LogDebug("Client {ConnectionId} unsubscribed from {Stream}", Context.ConnectionId, stream);
        await Clients.Caller.SendAsync("Unsubscribed", stream);
    }

    /// <summary>
    /// Subscribe to all streams at once
    /// </summary>
    public async Task SubscribeAll()
    {
        var streams = new[]
        {
            "reasoning.progress", "reasoning.results", "security.anomalies", "graph.changes", "jobs.progress",
            "introspection.full", "introspection.jobs", "introspection.traces", "introspection.security", "introspection.mutations"
        };
        foreach (var stream in streams)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, stream);
        }
        _logger.LogDebug("Client {ConnectionId} subscribed to all streams", Context.ConnectionId);
        await Clients.Caller.SendAsync("SubscribedAll", streams);
    }

    /// <summary>
    /// Subscribe to introspection streams only
    /// </summary>
    public async Task SubscribeIntrospection()
    {
        var streams = new[]
        {
            "introspection.full", "introspection.jobs", "introspection.traces",
            "introspection.security", "introspection.mutations"
        };
        foreach (var stream in streams)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, stream);
        }
        _logger.LogDebug("Client {ConnectionId} subscribed to introspection streams", Context.ConnectionId);
        await Clients.Caller.SendAsync("SubscribedIntrospection", streams);
    }
}
