namespace SerialMemory.Core.Interfaces;

/// <summary>
/// Interface for writing events to the event sourcing system.
/// Handles both memory-specific events and cross-cutting system events.
/// </summary>
public interface IEventWriter
{
    /// <summary>
    /// Emits a memory event to both memory_events and event_log tables.
    /// </summary>
    Task<Guid> EmitMemoryEventAsync(
        Guid tenantId,
        Guid memoryId,
        string eventType,
        object eventData,
        string actor = "system",
        object? metadata = null,
        CancellationToken ct = default);

    /// <summary>
    /// Emits a system event to the event_log table (for non-memory events).
    /// </summary>
    Task<Guid> EmitSystemEventAsync(
        Guid tenantId,
        string eventType,
        object payload,
        string actor = "system",
        string category = "system",
        string severity = "info",
        Guid? memoryId = null,
        Guid? correlationId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Emits a LayerGenerated event when classification completes a layer.
    /// </summary>
    Task<Guid> EmitLayerGeneratedAsync(
        Guid tenantId,
        Guid memoryId,
        string layer,
        object layerContent,
        string modelName,
        int durationMs,
        decimal confidence,
        string actor = "classification_worker",
        CancellationToken ct = default);

    /// <summary>
    /// Emits a ConflictDetected event when contradictions are found.
    /// </summary>
    Task<Guid> EmitConflictDetectedAsync(
        Guid tenantId,
        Guid memoryAId,
        Guid memoryBId,
        decimal similarityScore,
        string contradictionType,
        string? explanation = null,
        string actor = "conflict_worker",
        CancellationToken ct = default);

    /// <summary>
    /// Emits a ReasoningExecuted event when reasoning engine completes.
    /// </summary>
    Task<Guid> EmitReasoningExecutedAsync(
        Guid tenantId,
        Guid traceId,
        string reasoningType,
        int stepCount,
        int durationMs,
        string status,
        object? result = null,
        string actor = "reasoning_worker",
        CancellationToken ct = default);

    /// <summary>
    /// Emits a BranchCreated event when a shadow branch is created.
    /// </summary>
    Task<Guid> EmitBranchCreatedAsync(
        Guid tenantId,
        Guid branchId,
        string branchName,
        string? sourceBranch = "main",
        string? description = null,
        string actor = "system",
        CancellationToken ct = default);

    /// <summary>
    /// Creates a timeline snapshot and emits TimelineSnapshotCreated event.
    /// </summary>
    Task<Guid> CreateTimelineSnapshotAsync(
        Guid tenantId,
        Guid memoryId,
        Guid eventId,
        string eventType,
        string content,
        string layer,
        decimal confidence,
        object? metadata = null,
        CancellationToken ct = default);

    /// <summary>
    /// Computes and stores mind health metrics for a tenant.
    /// </summary>
    Task<Guid> ComputeMindHealthAsync(Guid tenantId, CancellationToken ct = default);
}
