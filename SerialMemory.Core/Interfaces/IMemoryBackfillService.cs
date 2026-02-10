namespace SerialMemory.Core.Interfaces;

/// <summary>
/// Service for detecting and processing unprocessed historical memories.
/// Ensures 100% of memories have L1-L4 layers.
/// </summary>
public interface IMemoryBackfillService
{
    /// <summary>
    /// Gets statistics about unprocessed memories across all tenants.
    /// </summary>
    Task<BackfillStatistics> GetBackfillStatisticsAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets unprocessed memory IDs (missing any layer up to L4).
    /// </summary>
    Task<IReadOnlyList<UnprocessedMemory>> GetUnprocessedMemoriesAsync(
        int limit = 100,
        Guid? tenantId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Enqueues unprocessed memories for classification.
    /// Returns the number of memories enqueued.
    /// </summary>
    Task<int> EnqueueUnprocessedMemoriesAsync(
        int batchSize = 50,
        Guid? tenantId = null,
        int priority = 10,
        CancellationToken ct = default);

    /// <summary>
    /// Processes a single memory through all missing layers with idempotency.
    /// Safe to call multiple times - only generates missing layers.
    /// </summary>
    Task<LayerProcessingResult> ProcessMemoryWithIdempotencyAsync(
        Guid tenantId,
        Guid memoryId,
        int maxRetries = 3,
        CancellationToken ct = default);

    /// <summary>
    /// Runs a full backfill pass processing all unprocessed memories.
    /// Returns statistics about what was processed.
    /// </summary>
    Task<BackfillResult> RunFullBackfillAsync(
        int batchSize = 25,
        int maxConcurrency = 1,
        Guid? tenantId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Checks if a memory has all layers complete (L0 through L4).
    /// </summary>
    Task<bool> IsMemoryFullyProcessedAsync(Guid tenantId, Guid memoryId, CancellationToken ct = default);

    /// <summary>
    /// Gets the current layer status for a memory.
    /// </summary>
    Task<MemoryLayerStatus> GetMemoryLayerStatusAsync(Guid tenantId, Guid memoryId, CancellationToken ct = default);

    /// <summary>
    /// Verifies integrity of a memory's layers (hash validation).
    /// </summary>
    Task<IntegrityCheckResult> VerifyMemoryIntegrityAsync(Guid tenantId, Guid memoryId, CancellationToken ct = default);

    /// <summary>
    /// Repairs a memory by regenerating failed or missing layers.
    /// </summary>
    Task<LayerProcessingResult> RepairMemoryAsync(
        Guid tenantId,
        Guid memoryId,
        bool forceRegenerate = false,
        CancellationToken ct = default);
}

/// <summary>
/// Statistics about backfill state.
/// </summary>
public sealed record BackfillStatistics
{
    public int TotalMemories { get; init; }
    public int FullyProcessedMemories { get; init; }
    public int PartiallyProcessedMemories { get; init; }
    public int UnprocessedMemories { get; init; }
    public int FailedMemories { get; init; }
    public int QueuedMemories { get; init; }
    public Dictionary<string, int> ByLayer { get; init; } = new();
    public Dictionary<Guid, int> ByTenant { get; init; } = new();
    public DateTimeOffset ComputedAt { get; init; } = DateTimeOffset.UtcNow;

    public decimal CompletionPercentage => TotalMemories > 0
        ? Math.Round((decimal)FullyProcessedMemories / TotalMemories * 100, 2)
        : 100;
}

/// <summary>
/// Represents an unprocessed memory.
/// </summary>
public sealed record UnprocessedMemory
{
    public Guid MemoryId { get; init; }
    public Guid TenantId { get; init; }
    public string CurrentLayer { get; init; } = "L0_RAW";
    public string[] MissingLayers { get; init; } = [];
    public bool HasL0 { get; init; }
    public bool HasL1 { get; init; }
    public bool HasL2 { get; init; }
    public bool HasL3 { get; init; }
    public bool HasL4 { get; init; }
    public int RetryCount { get; init; }
    public string? LastError { get; init; }
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// Timestamp of the most recently created layer. Null when no layers exist yet.
    /// Used for dwell-time filtering — memories whose highest layer was created
    /// too recently are still dwelling and should not be returned for processing.
    /// </summary>
    public DateTimeOffset? HighestLayerCreatedAt { get; init; }
}

/// <summary>
/// Result of processing layers for a memory.
/// </summary>
public sealed record LayerProcessingResult
{
    public Guid MemoryId { get; init; }
    public bool Success { get; init; }
    public string[] LayersGenerated { get; init; } = [];
    public string[] LayersSkipped { get; init; } = [];
    public string[] LayersFailed { get; init; } = [];
    public Dictionary<string, string> Errors { get; init; } = new();
    public int TotalDurationMs { get; init; }
    public bool IsFullyProcessed { get; init; }
}

/// <summary>
/// Result of a full backfill operation.
/// </summary>
public sealed record BackfillResult
{
    public int TotalMemoriesProcessed { get; init; }
    public int SuccessfullyCompleted { get; init; }
    public int PartiallyCompleted { get; init; }
    public int Failed { get; init; }
    public int Skipped { get; init; }
    public int TotalLayersGenerated { get; init; }
    public TimeSpan Duration { get; init; }
    public List<string> Errors { get; init; } = [];
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset CompletedAt { get; init; }
}

/// <summary>
/// Status of layers for a memory.
/// </summary>
public sealed record MemoryLayerStatus
{
    public Guid MemoryId { get; init; }
    public Guid TenantId { get; init; }
    public Dictionary<string, LayerInfo?> Layers { get; init; } = new();
    public string HighestLayer { get; init; } = "NONE";
    public bool IsComplete { get; init; }
    public string[] MissingLayers { get; init; } = [];
}

/// <summary>
/// Information about a single layer.
/// </summary>
public sealed record LayerInfo
{
    public Guid LayerId { get; init; }
    public string Layer { get; init; } = "";
    public string ContentHash { get; init; } = "";
    public string? ModelName { get; init; }
    public decimal? Confidence { get; init; }
    public int? DurationMs { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public bool IsCurrent { get; init; }
    public int Version { get; init; }
}

/// <summary>
/// Result of integrity verification.
/// </summary>
public sealed record IntegrityCheckResult
{
    public Guid MemoryId { get; init; }
    public bool IsValid { get; init; }
    public List<string> Issues { get; init; } = [];
    public Dictionary<string, bool> LayerHashValid { get; init; } = new();
    public DateTimeOffset CheckedAt { get; init; } = DateTimeOffset.UtcNow;
}
