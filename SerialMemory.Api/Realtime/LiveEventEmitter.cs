using Microsoft.AspNetCore.SignalR;
using SerialMemory.Core.Interfaces;

namespace SerialMemory.Api.Realtime;

/// <summary>
/// SignalR implementation of ILiveEventEmitter for real-time event broadcasting.
/// Supports memory events, reasoning events, security events, and graph changes.
/// </summary>
public sealed class LiveEventEmitter(IHubContext<LiveHub> hubContext, ILogger<LiveEventEmitter> logger)
    : ILiveEventEmitter
{
    // ==========================================
    // MEMORY EVENTS (NEW)
    // ==========================================

    public async Task EmitMemoryEventAsync(MemoryEventBroadcast evt)
    {
        try
        {
            // Broadcast to tenant-specific group
            await hubContext.Clients.Group($"tenant.{evt.TenantId}.events").SendAsync("MemoryEvent", evt);

            // Also broadcast to the general events group
            await hubContext.Clients.Group("events.all").SendAsync("MemoryEvent", evt);

            logger.LogDebug("Emitted memory event: {EventType} for memory {MemoryId}",
                evt.EventType, evt.MemoryId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to emit memory event");
        }
    }

    public async Task EmitRecentEventAsync(RecentEventBroadcast evt)
    {
        try
        {
            // Broadcast to tenant-specific recent events group
            await hubContext.Clients.Group($"tenant.{evt.TenantId}.recent").SendAsync("RecentEvent", evt);

            logger.LogDebug("Emitted recent event: {EventType} seq {Seq}", evt.EventType, evt.Seq);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to emit recent event");
        }
    }

    public async Task EmitTimelineUpdateAsync(TimelineUpdateBroadcast evt)
    {
        try
        {
            await hubContext.Clients.Group($"tenant.{evt.TenantId}.timeline").SendAsync("TimelineUpdate", evt);
            logger.LogDebug("Emitted timeline update for memory {MemoryId}", evt.MemoryId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to emit timeline update");
        }
    }

    public async Task EmitMindHealthUpdateAsync(MindHealthBroadcast evt)
    {
        try
        {
            await hubContext.Clients.Group($"tenant.{evt.TenantId}.health").SendAsync("MindHealthUpdate", evt);
            logger.LogDebug("Emitted mind health update: score {Score}", evt.HealthScore);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to emit mind health update");
        }
    }

    public async Task EmitConflictDetectedAsync(ConflictBroadcast evt)
    {
        try
        {
            await hubContext.Clients.Group($"tenant.{evt.TenantId}.conflicts").SendAsync("ConflictDetected", evt);
            logger.LogDebug("Emitted conflict detected between {MemoryA} and {MemoryB}",
                evt.MemoryAId, evt.MemoryBId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to emit conflict detected");
        }
    }

    public async Task EmitReasoningProgressAsync(ReasoningProgressEvent evt)
    {
        try
        {
            await hubContext.Clients.Group("reasoning.progress").SendAsync("ReasoningProgress", evt);
            logger.LogDebug("Emitted reasoning progress: {TraceId} step {Step}", evt.TraceId, evt.StepNumber);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to emit reasoning progress event");
        }
    }

    public async Task EmitReasoningResultAsync(ReasoningResultEvent evt)
    {
        try
        {
            await hubContext.Clients.Group("reasoning.results").SendAsync("ReasoningResult", evt);
            logger.LogDebug("Emitted reasoning result: {TraceId} status {Status}", evt.TraceId, evt.Status);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to emit reasoning result event");
        }
    }

    public async Task EmitSecurityAnomalyAsync(SecurityAnomalyEvent evt)
    {
        try
        {
            await hubContext.Clients.Group("security.anomalies").SendAsync("SecurityAnomaly", evt);
            logger.LogDebug("Emitted security anomaly: {EventType} status {Status}", evt.EventType, evt.Status);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to emit security anomaly event");
        }
    }

    public async Task EmitGraphChangeAsync(GraphChangeEvent evt)
    {
        try
        {
            await hubContext.Clients.Group("graph.changes").SendAsync("GraphChange", evt);
            logger.LogDebug("Emitted graph change: {EventType}", evt.EventType);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to emit graph change event");
        }
    }

    public async Task EmitJobProgressAsync(JobProgressEvent evt)
    {
        try
        {
            await hubContext.Clients.Group("jobs.progress").SendAsync("JobProgress", evt);
            logger.LogDebug("Emitted job progress: {JobId} {Status} {Progress}%",
                evt.JobId, evt.Status, evt.ProgressPercent);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to emit job progress event");
        }
    }

    public async Task EmitJobCompletedAsync(JobCompletedEvent evt)
    {
        try
        {
            await hubContext.Clients.Group("jobs.progress").SendAsync("JobCompleted", evt);
            logger.LogDebug("Emitted job completed: {JobId} {Status} in {DurationMs}ms",
                evt.JobId, evt.Status, evt.DurationMs);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to emit job completed event");
        }
    }

    // ==========================================
    // SYSTEM INTROSPECTION EVENTS
    // ==========================================

    public async Task EmitIntrospectionAsync(IntrospectionSnapshot snapshot)
    {
        try
        {
            await hubContext.Clients.Group("introspection.full").SendAsync("Introspection", snapshot);
            logger.LogDebug("Emitted full introspection snapshot");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to emit introspection snapshot");
        }
    }

    public async Task EmitJobBacklogAsync(JobBacklogSnapshot backlog)
    {
        try
        {
            await hubContext.Clients.Group("introspection.jobs").SendAsync("JobBacklog", backlog);
            logger.LogDebug("Emitted job backlog: {Queued} queued, {InProgress} in progress",
                backlog.TotalQueued, backlog.TotalInProgress);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to emit job backlog");
        }
    }

    public async Task EmitActiveTracesAsync(ActiveTracesSnapshot traces)
    {
        try
        {
            await hubContext.Clients.Group("introspection.traces").SendAsync("ActiveTraces", traces);
            logger.LogDebug("Emitted active traces: {Active} active, {Pending} pending",
                traces.TotalActive, traces.TotalPending);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to emit active traces");
        }
    }

    public async Task EmitSecurityScanStateAsync(SecurityScanSnapshot scan)
    {
        try
        {
            await hubContext.Clients.Group("introspection.security").SendAsync("SecurityScanState", scan);
            logger.LogDebug("Emitted security scan state: {Status}, {ActiveScans} active scans",
                scan.OverallStatus, scan.ActiveScans);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to emit security scan state");
        }
    }

    public async Task EmitGraphMutationAsync(GraphMutationSnapshot mutation)
    {
        try
        {
            await hubContext.Clients.Group("introspection.mutations").SendAsync("GraphMutations", mutation);
            logger.LogDebug("Emitted graph mutations: seq {Seq}, {Rate}/sec",
                mutation.LastSequenceNumber, mutation.MutationsPerSecond);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to emit graph mutations");
        }
    }
}
