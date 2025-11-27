using Microsoft.AspNetCore.SignalR;
using SerialMemory.Core.Interfaces;

namespace SerialMemory.Api.Realtime;

/// <summary>
/// SignalR implementation of ILiveEventEmitter for real-time event broadcasting.
/// </summary>
public sealed class LiveEventEmitter : ILiveEventEmitter
{
    private readonly IHubContext<LiveHub> _hubContext;
    private readonly ILogger<LiveEventEmitter> _logger;

    public LiveEventEmitter(IHubContext<LiveHub> hubContext, ILogger<LiveEventEmitter> logger)
    {
        _hubContext = hubContext;
        _logger = logger;
    }

    public async Task EmitReasoningProgressAsync(ReasoningProgressEvent evt)
    {
        try
        {
            await _hubContext.Clients.Group("reasoning.progress").SendAsync("ReasoningProgress", evt);
            _logger.LogDebug("Emitted reasoning progress: {TraceId} step {Step}", evt.TraceId, evt.StepNumber);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to emit reasoning progress event");
        }
    }

    public async Task EmitReasoningResultAsync(ReasoningResultEvent evt)
    {
        try
        {
            await _hubContext.Clients.Group("reasoning.results").SendAsync("ReasoningResult", evt);
            _logger.LogDebug("Emitted reasoning result: {TraceId} status {Status}", evt.TraceId, evt.Status);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to emit reasoning result event");
        }
    }

    public async Task EmitSecurityAnomalyAsync(SecurityAnomalyEvent evt)
    {
        try
        {
            await _hubContext.Clients.Group("security.anomalies").SendAsync("SecurityAnomaly", evt);
            _logger.LogDebug("Emitted security anomaly: {EventType} status {Status}", evt.EventType, evt.Status);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to emit security anomaly event");
        }
    }

    public async Task EmitGraphChangeAsync(GraphChangeEvent evt)
    {
        try
        {
            await _hubContext.Clients.Group("graph.changes").SendAsync("GraphChange", evt);
            _logger.LogDebug("Emitted graph change: {EventType}", evt.EventType);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to emit graph change event");
        }
    }

    public async Task EmitJobProgressAsync(JobProgressEvent evt)
    {
        try
        {
            await _hubContext.Clients.Group("jobs.progress").SendAsync("JobProgress", evt);
            _logger.LogDebug("Emitted job progress: {JobId} {Status} {Progress}%",
                evt.JobId, evt.Status, evt.ProgressPercent);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to emit job progress event");
        }
    }

    public async Task EmitJobCompletedAsync(JobCompletedEvent evt)
    {
        try
        {
            await _hubContext.Clients.Group("jobs.progress").SendAsync("JobCompleted", evt);
            _logger.LogDebug("Emitted job completed: {JobId} {Status} in {DurationMs}ms",
                evt.JobId, evt.Status, evt.DurationMs);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to emit job completed event");
        }
    }

    // ==========================================
    // SYSTEM INTROSPECTION EVENTS
    // ==========================================

    public async Task EmitIntrospectionAsync(IntrospectionSnapshot snapshot)
    {
        try
        {
            await _hubContext.Clients.Group("introspection.full").SendAsync("Introspection", snapshot);
            _logger.LogDebug("Emitted full introspection snapshot");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to emit introspection snapshot");
        }
    }

    public async Task EmitJobBacklogAsync(JobBacklogSnapshot backlog)
    {
        try
        {
            await _hubContext.Clients.Group("introspection.jobs").SendAsync("JobBacklog", backlog);
            _logger.LogDebug("Emitted job backlog: {Queued} queued, {InProgress} in progress",
                backlog.TotalQueued, backlog.TotalInProgress);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to emit job backlog");
        }
    }

    public async Task EmitActiveTracesAsync(ActiveTracesSnapshot traces)
    {
        try
        {
            await _hubContext.Clients.Group("introspection.traces").SendAsync("ActiveTraces", traces);
            _logger.LogDebug("Emitted active traces: {Active} active, {Pending} pending",
                traces.TotalActive, traces.TotalPending);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to emit active traces");
        }
    }

    public async Task EmitSecurityScanStateAsync(SecurityScanSnapshot scan)
    {
        try
        {
            await _hubContext.Clients.Group("introspection.security").SendAsync("SecurityScanState", scan);
            _logger.LogDebug("Emitted security scan state: {Status}, {ActiveScans} active scans",
                scan.OverallStatus, scan.ActiveScans);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to emit security scan state");
        }
    }

    public async Task EmitGraphMutationAsync(GraphMutationSnapshot mutation)
    {
        try
        {
            await _hubContext.Clients.Group("introspection.mutations").SendAsync("GraphMutations", mutation);
            _logger.LogDebug("Emitted graph mutations: seq {Seq}, {Rate}/sec",
                mutation.LastSequenceNumber, mutation.MutationsPerSecond);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to emit graph mutations");
        }
    }
}
