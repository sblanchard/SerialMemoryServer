using Microsoft.AspNetCore.SignalR;
using SerialMemory.Core.Deployment;
using SerialMemory.Core.Interfaces;

namespace SerialMemory.Api.Realtime;

/// <summary>
/// Real-time SignalR hub for live streaming of reasoning, security, and graph events.
/// Deployment-aware: In SaaS mode, streams are tenant-scoped. In SelfHosted mode, global streams are available.
/// </summary>
public sealed class LiveHub : Hub
{
    private readonly ILogger<LiveHub> _logger;
    private readonly IDeploymentContext _deploymentContext;
    private readonly ITenantContext _tenantContext;

    public LiveHub(
        ILogger<LiveHub> logger,
        IDeploymentContext deploymentContext,
        ITenantContext tenantContext)
    {
        _logger = logger;
        _deploymentContext = deploymentContext;
        _tenantContext = tenantContext;
    }

    public override async Task OnConnectedAsync()
    {
        _logger.LogInformation("Client connected: {ConnectionId} (Mode: {Mode})",
            Context.ConnectionId, _deploymentContext.Mode);

        // In SaaS mode, auto-subscribe to tenant-specific group
        if (_deploymentContext.IsSaaS)
        {
            var tenantGroup = $"tenant:{_tenantContext.TenantId}";
            await Groups.AddToGroupAsync(Context.ConnectionId, tenantGroup);
            _logger.LogDebug("Auto-subscribed {ConnectionId} to tenant group {TenantGroup}",
                Context.ConnectionId, tenantGroup);
        }

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation("Client disconnected: {ConnectionId}", Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Subscribe to a specific stream.
    /// In SaaS mode: streams are automatically tenant-scoped.
    /// In SelfHosted mode: global streams are available.
    /// </summary>
    public async Task Subscribe(string stream)
    {
        var effectiveStream = GetEffectiveStreamName(stream);
        await Groups.AddToGroupAsync(Context.ConnectionId, effectiveStream);
        _logger.LogDebug("Client {ConnectionId} subscribed to {Stream} (effective: {EffectiveStream})",
            Context.ConnectionId, stream, effectiveStream);
        await Clients.Caller.SendAsync("Subscribed", stream);
    }

    /// <summary>
    /// Unsubscribe from a stream
    /// </summary>
    public async Task Unsubscribe(string stream)
    {
        var effectiveStream = GetEffectiveStreamName(stream);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, effectiveStream);
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
            var effectiveStream = GetEffectiveStreamName(stream);
            await Groups.AddToGroupAsync(Context.ConnectionId, effectiveStream);
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
            var effectiveStream = GetEffectiveStreamName(stream);
            await Groups.AddToGroupAsync(Context.ConnectionId, effectiveStream);
        }
        _logger.LogDebug("Client {ConnectionId} subscribed to introspection streams", Context.ConnectionId);
        await Clients.Caller.SendAsync("SubscribedIntrospection", streams);
    }

    /// <summary>
    /// Subscribe to global admin streams (SelfHosted only).
    /// In SaaS mode, this is not available and will return an error.
    /// </summary>
    public async Task SubscribeGlobalAdmin()
    {
        if (_deploymentContext.IsSaaS)
        {
            await Clients.Caller.SendAsync("Error", new
            {
                code = "GlobalStreamsNotAvailable",
                message = "Global admin streams are only available in self-hosted mode."
            });
            return;
        }

        var globalStreams = new[]
        {
            "global.all", "global.admin", "global.security", "global.performance"
        };
        foreach (var stream in globalStreams)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, stream);
        }
        _logger.LogInformation("Client {ConnectionId} subscribed to global admin streams", Context.ConnectionId);
        await Clients.Caller.SendAsync("SubscribedGlobalAdmin", globalStreams);
    }

    /// <summary>
    /// Gets deployment info for the connected client.
    /// </summary>
    public async Task GetDeploymentInfo()
    {
        await Clients.Caller.SendAsync("DeploymentInfo", new
        {
            mode = _deploymentContext.Mode.ToString(),
            isSaaS = _deploymentContext.IsSaaS,
            isSelfHosted = _deploymentContext.IsSelfHosted,
            globalStreamsAvailable = _deploymentContext.IsSelfHosted,
            tenantScoped = _deploymentContext.IsSaaS,
            tenantId = _deploymentContext.IsSaaS ? _tenantContext.TenantId : null
        });
    }

    /// <summary>
    /// In SaaS mode, prefixes stream names with tenant ID for isolation.
    /// In SelfHosted mode, uses global streams.
    /// </summary>
    private string GetEffectiveStreamName(string stream)
    {
        if (_deploymentContext.IsSelfHosted)
        {
            return stream; // Global stream
        }

        // SaaS mode: tenant-scoped streams
        return $"tenant:{_tenantContext.TenantId}:{stream}";
    }
}
