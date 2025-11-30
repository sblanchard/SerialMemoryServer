namespace SerialMemory.Core.Interfaces;

/// <summary>
/// Service for emitting real-time events to connected clients.
/// Inject this into services that need to broadcast live events.
/// </summary>
public interface ILiveEventEmitter
{
    // Memory events (NEW - for Recent Events dashboard)
    Task EmitMemoryEventAsync(MemoryEventBroadcast evt);
    Task EmitRecentEventAsync(RecentEventBroadcast evt);
    Task EmitTimelineUpdateAsync(TimelineUpdateBroadcast evt);
    Task EmitMindHealthUpdateAsync(MindHealthBroadcast evt);
    Task EmitConflictDetectedAsync(ConflictBroadcast evt);

    // Reasoning events
    Task EmitReasoningProgressAsync(ReasoningProgressEvent evt);
    Task EmitReasoningResultAsync(ReasoningResultEvent evt);

    // Security events
    Task EmitSecurityAnomalyAsync(SecurityAnomalyEvent evt);

    // Graph events
    Task EmitGraphChangeAsync(GraphChangeEvent evt);

    // Job progress events
    Task EmitJobProgressAsync(JobProgressEvent evt);
    Task EmitJobCompletedAsync(JobCompletedEvent evt);

    // System introspection events (real-time)
    Task EmitIntrospectionAsync(IntrospectionSnapshot snapshot);
    Task EmitJobBacklogAsync(JobBacklogSnapshot backlog);
    Task EmitActiveTracesAsync(ActiveTracesSnapshot traces);
    Task EmitSecurityScanStateAsync(SecurityScanSnapshot scan);
    Task EmitGraphMutationAsync(GraphMutationSnapshot mutation);
}

// ==========================================
// MEMORY EVENT BROADCASTS (NEW)
// ==========================================

/// <summary>
/// Broadcast for memory-specific events (MemoryCreated, LayerGenerated, etc.)
/// </summary>
public sealed record MemoryEventBroadcast
{
    public Guid EventId { get; init; }
    public string TenantId { get; init; } = "";
    public Guid MemoryId { get; init; }
    public string EventType { get; init; } = "";
    public string Actor { get; init; } = "system";
    public object? Payload { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Broadcast for general recent events (for the Recent Events dashboard)
/// </summary>
public sealed record RecentEventBroadcast
{
    public Guid Id { get; init; }
    public string TenantId { get; init; } = "";
    public long Seq { get; init; }
    public string EventType { get; init; } = "";
    public Guid? MemoryId { get; init; }
    public string Actor { get; init; } = "";
    public string Category { get; init; } = "";
    public string Severity { get; init; } = "info";
    public object? Payload { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Broadcast for timeline updates (when a snapshot is created)
/// </summary>
public sealed record TimelineUpdateBroadcast
{
    public Guid SnapshotId { get; init; }
    public string TenantId { get; init; } = "";
    public Guid MemoryId { get; init; }
    public string EventType { get; init; } = "";
    public string Layer { get; init; } = "";
    public decimal Confidence { get; init; }
    public DateTimeOffset SnapshotAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Broadcast for mind health updates
/// </summary>
public sealed record MindHealthBroadcast
{
    public Guid HealthId { get; init; }
    public string TenantId { get; init; } = "";
    public int HealthScore { get; init; }
    public string HealthStatus { get; init; } = "healthy";
    public int TotalMemories { get; init; }
    public int ActiveMemories { get; init; }
    public int ContradictionsDetected { get; init; }
    public DateTimeOffset ComputedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Broadcast when a conflict/contradiction is detected
/// </summary>
public sealed record ConflictBroadcast
{
    public Guid ConflictId { get; init; }
    public string TenantId { get; init; } = "";
    public Guid MemoryAId { get; init; }
    public Guid MemoryBId { get; init; }
    public decimal SimilarityScore { get; init; }
    public string ContradictionType { get; init; } = "";
    public string? Explanation { get; init; }
    public DateTimeOffset DetectedAt { get; init; } = DateTimeOffset.UtcNow;
}

// Event DTOs - live streaming events
public sealed record ReasoningProgressEvent
{
    public Guid TraceId { get; init; }
    public string TenantId { get; init; } = "self";
    public int StepNumber { get; init; }
    public string StepType { get; init; } = "";
    public string Description { get; init; } = "";
    public int? DurationMs { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public object? Payload { get; init; }
}

public sealed record ReasoningResultEvent
{
    public Guid TraceId { get; init; }
    public string TenantId { get; init; } = "self";
    public string Status { get; init; } = ""; // completed, failed, cancelled
    public int TotalSteps { get; init; }
    public int TotalDurationMs { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public IReadOnlyList<ReasoningInsightDto>? Insights { get; init; }
}

public sealed record ReasoningInsightDto
{
    public Guid Id { get; init; }
    public string Type { get; init; } = "";
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public string Severity { get; init; } = "";
    public float Confidence { get; init; }
}

public sealed record SecurityAnomalyEvent
{
    public Guid EventId { get; init; }
    public string TenantId { get; init; } = "self";
    public string EventType { get; init; } = ""; // integrity_check, contradiction_scan, loop_detection, etc.
    public string Status { get; init; } = ""; // pass, fail, warning
    public string? TargetType { get; init; }
    public Guid? TargetId { get; init; }
    public string? Message { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public object? Details { get; init; }
}

public sealed record GraphChangeEvent
{
    public Guid EventId { get; init; }
    public string TenantId { get; init; } = "self";
    public string EventType { get; init; } = ""; // node_created, node_updated, edge_created, etc.
    public Guid? NodeId { get; init; }
    public Guid? EdgeId { get; init; }
    public string? NodeName { get; init; }
    public string? NodeType { get; init; }
    public string? EdgeType { get; init; }
    public string? SourceNodeId { get; init; }
    public string? TargetNodeId { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

// Job progress events for background workers
public sealed record JobProgressEvent
{
    public Guid JobId { get; init; }
    public string JobType { get; init; } = "";
    public string Status { get; init; } = "";
    public string TenantId { get; init; } = "self";
    public int TotalItems { get; init; }
    public int ProcessedItems { get; init; }
    public int FailedItems { get; init; }
    public double ProgressPercent => TotalItems > 0 ? Math.Round((double)ProcessedItems / TotalItems * 100, 1) : 0;
    public string? CurrentItem { get; init; }
    public string? Message { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record JobCompletedEvent
{
    public Guid JobId { get; init; }
    public string JobType { get; init; } = "";
    public string Status { get; init; } = "";
    public string TenantId { get; init; } = "self";
    public int TotalItems { get; init; }
    public int ProcessedItems { get; init; }
    public int FailedItems { get; init; }
    public int DurationMs { get; init; }
    public string? ErrorMessage { get; init; }
    public object? Result { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

// ==========================================
// SYSTEM INTROSPECTION EVENTS
// ==========================================

/// <summary>
/// Full system introspection snapshot - pushed periodically
/// </summary>
public sealed record IntrospectionSnapshot
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public JobBacklogSnapshot JobBacklog { get; init; } = new();
    public ActiveTracesSnapshot ActiveTraces { get; init; } = new();
    public SecurityScanSnapshot SecurityScan { get; init; } = new();
    public GraphMutationSnapshot GraphMutations { get; init; } = new();
    public SystemResourceSnapshot Resources { get; init; } = new();
}

/// <summary>
/// Job backlog depth - queued and in-progress work
/// </summary>
public sealed record JobBacklogSnapshot
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public int TotalQueued { get; init; }
    public int TotalInProgress { get; init; }
    public int TotalCompleted24h { get; init; }
    public int TotalFailed24h { get; init; }
    public double AvgWaitTimeMs { get; init; }
    public double AvgProcessTimeMs { get; init; }
    public IReadOnlyList<JobQueueStatus> Queues { get; init; } = [];
    public IReadOnlyList<ActiveJob> ActiveJobs { get; init; } = [];
}

public sealed record JobQueueStatus
{
    public required string QueueName { get; init; }
    public int Depth { get; init; }
    public int InProgress { get; init; }
    public int Workers { get; init; }
    public double ThroughputPerMin { get; init; }
    public DateTimeOffset? OldestItemAt { get; init; }
}

public sealed record ActiveJob
{
    public Guid JobId { get; init; }
    public required string JobType { get; init; }
    public required string Status { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public double ElapsedMs { get; init; }
    public int ProgressPercent { get; init; }
    public string? CurrentStep { get; init; }
}

/// <summary>
/// Active traces - currently running reasoning/analysis operations
/// </summary>
public sealed record ActiveTracesSnapshot
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public int TotalActive { get; init; }
    public int TotalPending { get; init; }
    public int CompletedLastHour { get; init; }
    public double AvgDurationMs { get; init; }
    public IReadOnlyList<ActiveTrace> Traces { get; init; } = [];
}

public sealed record ActiveTrace
{
    public Guid TraceId { get; init; }
    public required string TraceType { get; init; }
    public required string Status { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public double ElapsedMs { get; init; }
    public int StepsCompleted { get; init; }
    public int TotalSteps { get; init; }
    public string? CurrentOperation { get; init; }
    public Guid? TargetMemoryId { get; init; }
    public string? TenantId { get; init; }
}

/// <summary>
/// Security scan state - integrity checks, anomaly detection status
/// </summary>
public sealed record SecurityScanSnapshot
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public SecurityScanStatus OverallStatus { get; init; }
    public DateTimeOffset? LastFullScanAt { get; init; }
    public DateTimeOffset? NextScheduledScanAt { get; init; }
    public int ActiveScans { get; init; }
    public int IssuesDetected24h { get; init; }
    public int IssuesResolved24h { get; init; }
    public IReadOnlyList<SecurityScanState> Scans { get; init; } = [];
    public IReadOnlyList<SecurityIssue> RecentIssues { get; init; } = [];
}

public enum SecurityScanStatus
{
    Idle,
    Scanning,
    IssuesDetected,
    Critical,
    Unknown
}

public sealed record SecurityScanState
{
    public Guid ScanId { get; init; }
    public required string ScanType { get; init; }
    public required string Status { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public double ElapsedMs { get; init; }
    public int ItemsScanned { get; init; }
    public int TotalItems { get; init; }
    public int IssuesFound { get; init; }
    public string? CurrentTarget { get; init; }
}

public sealed record SecurityIssue
{
    public Guid IssueId { get; init; }
    public required string IssueType { get; init; }
    public required string Severity { get; init; }
    public required string Description { get; init; }
    public Guid? TargetId { get; init; }
    public string? TargetType { get; init; }
    public DateTimeOffset DetectedAt { get; init; }
    public bool Resolved { get; init; }
}

/// <summary>
/// Graph mutation flow - real-time changes to the knowledge graph
/// </summary>
public sealed record GraphMutationSnapshot
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public long LastSequenceNumber { get; init; }
    public int MutationsLastMinute { get; init; }
    public int MutationsLastHour { get; init; }
    public double MutationsPerSecond { get; init; }
    public IReadOnlyList<GraphMutationEvent> RecentMutations { get; init; } = [];
    public GraphMutationStats Stats { get; init; } = new();
}

public sealed record GraphMutationEvent
{
    public long SequenceNumber { get; init; }
    public Guid MutationId { get; init; }
    public required string MutationType { get; init; }
    public Guid? NodeId { get; init; }
    public Guid? EdgeId { get; init; }
    public string? NodeType { get; init; }
    public string? EdgeType { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public string? TenantId { get; init; }
}

public sealed record GraphMutationStats
{
    public int NodesCreated24h { get; init; }
    public int NodesUpdated24h { get; init; }
    public int NodesDeleted24h { get; init; }
    public int EdgesCreated24h { get; init; }
    public int EdgesDeleted24h { get; init; }
    public int TotalNodes { get; init; }
    public int TotalEdges { get; init; }
}

/// <summary>
/// System resource snapshot
/// </summary>
public sealed record SystemResourceSnapshot
{
    public double CpuPercent { get; init; }
    public long MemoryUsedMb { get; init; }
    public long MemoryTotalMb { get; init; }
    public int ActiveConnections { get; init; }
    public int ThreadPoolThreads { get; init; }
    public long GcTotalMemory { get; init; }
    public int Gen0Collections { get; init; }
    public int Gen1Collections { get; init; }
    public int Gen2Collections { get; init; }
}
