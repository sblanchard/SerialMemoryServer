using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using SerialMemory.Core.Interfaces;

namespace SerialMemory.EventSourcing.Store;

/// <summary>
/// High-performance event writer that writes events to both memory_events and event_log tables.
/// Uses internal admin role for RLS bypass during worker operations.
/// Broadcasts events via SignalR after successful persistence.
/// </summary>
public sealed class EventWriter(
    NpgsqlDataSource dataSource,
    ILogger<EventWriter> logger,
    ILiveEventEmitter? eventEmitter = null) : IEventWriter
{
    private readonly NpgsqlDataSource _dataSource = dataSource;
    private readonly ILiveEventEmitter? _eventEmitter = eventEmitter;
    private readonly ILogger<EventWriter> _logger = logger;

    /// <summary>
    /// Emits a memory event to both memory_events and event_log tables.
    /// Uses internal admin role for RLS bypass.
    /// </summary>
    public async Task<Guid> EmitMemoryEventAsync(
        Guid tenantId,
        Guid memoryId,
        string eventType,
        object eventData,
        string actor = "system",
        object? metadata = null,
        CancellationToken ct = default)
    {
        var eventDataJson = JsonSerializer.Serialize(eventData);
        var metadataJson = metadata != null ? JsonSerializer.Serialize(metadata) : "{}";

        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        // Set internal admin role for RLS bypass
        await conn.ExecuteAsync("SELECT set_config('app.role', 'internal_admin', false)");
        await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @TenantId, false)",
            new { TenantId = tenantId.ToString() });

        var eventId = await conn.ExecuteScalarAsync<Guid>(
            "SELECT emit_memory_event(@TenantId, @MemoryId, @EventType, @EventData::jsonb, @Actor, @Metadata::jsonb)",
            new
            {
                TenantId = tenantId,
                MemoryId = memoryId,
                EventType = eventType,
                EventData = eventDataJson,
                Actor = actor,
                Metadata = metadataJson
            });

        _logger.LogDebug("Emitted {EventType} for memory {MemoryId} in tenant {TenantId}",
            eventType, memoryId, tenantId);

        // Broadcast via SignalR if available
        await BroadcastEventAsync(tenantId, memoryId, eventType, eventData, actor, ct);

        return eventId;
    }

    /// <summary>
    /// Emits a system event to the event_log table (for non-memory events).
    /// </summary>
    public async Task<Guid> EmitSystemEventAsync(
        Guid tenantId,
        string eventType,
        object payload,
        string actor = "system",
        string category = "system",
        string severity = "info",
        Guid? memoryId = null,
        Guid? correlationId = null,
        CancellationToken ct = default)
    {
        var payloadJson = JsonSerializer.Serialize(payload);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        // Set internal admin role for RLS bypass
        await conn.ExecuteAsync("SELECT set_config('app.role', 'internal_admin', false)");
        await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @TenantId, false)",
            new { TenantId = tenantId.ToString() });

        var eventId = await conn.ExecuteScalarAsync<Guid>(
            """
            SELECT emit_system_event(
                @TenantId, @EventType, @Payload::jsonb, @Actor,
                @Category, @Severity, @MemoryId, @CorrelationId
            )
            """,
            new
            {
                TenantId = tenantId,
                EventType = eventType,
                Payload = payloadJson,
                Actor = actor,
                Category = category,
                Severity = severity,
                MemoryId = memoryId,
                CorrelationId = correlationId
            });

        _logger.LogDebug("Emitted system event {EventType} for tenant {TenantId} (category: {Category})",
            eventType, tenantId, category);

        // Broadcast system events too
        await BroadcastSystemEventAsync(tenantId, eventType, payload, category, ct);

        return eventId;
    }

    /// <summary>
    /// Emits a LayerGenerated event when classification completes a layer.
    /// </summary>
    public async Task<Guid> EmitLayerGeneratedAsync(
        Guid tenantId,
        Guid memoryId,
        string layer,
        object layerContent,
        string modelName,
        int durationMs,
        decimal confidence,
        string actor = "classification_worker",
        CancellationToken ct = default)
    {
        var eventData = new
        {
            layer,
            model_name = modelName,
            duration_ms = durationMs,
            confidence,
            content_preview = GetContentPreview(layerContent)
        };

        return await EmitMemoryEventAsync(
            tenantId, memoryId, "LayerGenerated", eventData, actor, null, ct);
    }

    /// <summary>
    /// Emits a ConflictDetected event when contradictions are found.
    /// </summary>
    public async Task<Guid> EmitConflictDetectedAsync(
        Guid tenantId,
        Guid memoryAId,
        Guid memoryBId,
        decimal similarityScore,
        string contradictionType,
        string? explanation = null,
        string actor = "conflict_worker",
        CancellationToken ct = default)
    {
        var eventData = new
        {
            memory_a_id = memoryAId,
            memory_b_id = memoryBId,
            similarity_score = similarityScore,
            contradiction_type = contradictionType,
            explanation
        };

        return await EmitSystemEventAsync(
            tenantId, "ConflictDetected", eventData, actor, "integrity", "warning", memoryAId, null, ct);
    }

    /// <summary>
    /// Emits a ReasoningExecuted event when reasoning engine completes.
    /// </summary>
    public async Task<Guid> EmitReasoningExecutedAsync(
        Guid tenantId,
        Guid traceId,
        string reasoningType,
        int stepCount,
        int durationMs,
        string status,
        object? result = null,
        string actor = "reasoning_worker",
        CancellationToken ct = default)
    {
        var eventData = new
        {
            trace_id = traceId,
            reasoning_type = reasoningType,
            step_count = stepCount,
            duration_ms = durationMs,
            status,
            result_preview = result != null ? GetContentPreview(result) : null
        };

        return await EmitSystemEventAsync(
            tenantId, "ReasoningExecuted", eventData, actor, "reasoning", "info", null, traceId, ct);
    }

    /// <summary>
    /// Emits a BranchCreated event when a shadow branch is created.
    /// </summary>
    public async Task<Guid> EmitBranchCreatedAsync(
        Guid tenantId,
        Guid branchId,
        string branchName,
        string? sourceBranch = "main",
        string? description = null,
        string actor = "system",
        CancellationToken ct = default)
    {
        var eventData = new
        {
            branch_id = branchId,
            branch_name = branchName,
            source_branch = sourceBranch,
            description
        };

        return await EmitSystemEventAsync(
            tenantId, "BranchCreated", eventData, actor, "branch", "info", null, null, ct);
    }

    /// <summary>
    /// Emits a TimelineSnapshotCreated event and creates the snapshot.
    /// </summary>
    public async Task<Guid> CreateTimelineSnapshotAsync(
        Guid tenantId,
        Guid memoryId,
        Guid eventId,
        string eventType,
        string content,
        string layer,
        decimal confidence,
        object? metadata = null,
        CancellationToken ct = default)
    {
        var metadataJson = metadata != null ? JsonSerializer.Serialize(metadata) : "{}";

        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        // Set internal admin role for RLS bypass
        await conn.ExecuteAsync("SELECT set_config('app.role', 'internal_admin', false)");
        await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @TenantId, false)",
            new { TenantId = tenantId.ToString() });

        var snapshotId = await conn.ExecuteScalarAsync<Guid>(
            "SELECT create_timeline_snapshot(@TenantId, @MemoryId, @EventId, @EventType, @Content, @Layer, @Confidence, @Metadata::jsonb)",
            new
            {
                TenantId = tenantId,
                MemoryId = memoryId,
                EventId = eventId,
                EventType = eventType,
                Content = content,
                Layer = layer,
                Confidence = confidence,
                Metadata = metadataJson
            });

        // Emit event for the snapshot
        await EmitSystemEventAsync(
            tenantId, "TimelineSnapshotCreated",
            new { snapshot_id = snapshotId, memory_id = memoryId, event_type = eventType },
            "timeline_service", "timeline", "info", memoryId, null, ct);

        return snapshotId;
    }

    /// <summary>
    /// Computes and stores mind health metrics for a tenant.
    /// </summary>
    public async Task<Guid> ComputeMindHealthAsync(Guid tenantId, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        // Set internal admin role for RLS bypass
        await conn.ExecuteAsync("SELECT set_config('app.role', 'internal_admin', false)");
        await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @TenantId, false)",
            new { TenantId = tenantId.ToString() });

        var healthId = await conn.ExecuteScalarAsync<Guid>(
            "SELECT compute_mind_health(@TenantId)",
            new { TenantId = tenantId });

        _logger.LogDebug("Computed mind health for tenant {TenantId}: {HealthId}", tenantId, healthId);

        return healthId;
    }

    /// <summary>
    /// Broadcasts memory events to connected SignalR clients.
    /// </summary>
    private async Task BroadcastEventAsync(
        Guid tenantId,
        Guid memoryId,
        string eventType,
        object eventData,
        string actor,
        CancellationToken ct)
    {
        if (_eventEmitter == null) return;

        try
        {
            // Use MemoryEventBroadcast for memory-specific events
            var memoryEvent = new MemoryEventBroadcast
            {
                EventId = Guid.CreateVersion7(),
                TenantId = tenantId.ToString(),
                MemoryId = memoryId,
                EventType = eventType,
                Actor = actor,
                Payload = eventData,
                Timestamp = DateTimeOffset.UtcNow
            };

            await _eventEmitter.EmitMemoryEventAsync(memoryEvent);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to broadcast memory event {EventType} for {MemoryId}",
                eventType, memoryId);
        }
    }

    /// <summary>
    /// Broadcasts system events to connected SignalR clients.
    /// </summary>
    private async Task BroadcastSystemEventAsync(
        Guid tenantId,
        string eventType,
        object payload,
        string category,
        CancellationToken ct)
    {
        if (_eventEmitter == null) return;

        try
        {
            // Use RecentEventBroadcast for system events
            var recentEvent = new RecentEventBroadcast
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId.ToString(),
                EventType = eventType,
                Category = category,
                Actor = "system",
                Payload = payload,
                CreatedAt = DateTimeOffset.UtcNow
            };

            await _eventEmitter.EmitRecentEventAsync(recentEvent);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to broadcast system event {EventType}", eventType);
        }
    }

    /// <summary>
    /// Gets a preview of content for logging/events (first 200 chars).
    /// </summary>
    private static string GetContentPreview(object content)
    {
        var json = JsonSerializer.Serialize(content);
        return json.Length > 200 ? json[..200] + "..." : json;
    }
}
