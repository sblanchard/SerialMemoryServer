namespace SerialMemory.Core.Interfaces;

/// <summary>
/// Interface for triggering reactive self-healing scans.
/// </summary>
public interface ISelfHealingTrigger
{
    /// <summary>
    /// Triggers a reactive scan due to memory conflict.
    /// </summary>
    Task TriggerOnConflictAsync(Guid memoryId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Triggers a reactive scan due to integrity failure.
    /// </summary>
    Task TriggerOnIntegrityFailureAsync(Guid memoryId, string failureType, CancellationToken cancellationToken = default);

    /// <summary>
    /// Triggers a reactive scan due to hallucination detection.
    /// </summary>
    Task TriggerOnHallucinationAsync(Guid memoryId, float confidence, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets recent scan results for dashboard.
    /// </summary>
    Task<IReadOnlyList<HealingScanResult>> GetRecentScansAsync(int limit = 20, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets unresolved contradictions for dashboard.
    /// </summary>
    Task<IReadOnlyList<HealingContradictionResult>> GetUnresolvedContradictionsAsync(int limit = 50, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets self-healing statistics.
    /// </summary>
    Task<HealingStatsResult> GetStatsAsync(CancellationToken cancellationToken = default);
}

public sealed record HealingScanResult(
    Guid Id,
    string OperationType,
    string Result,
    int DurationMs,
    string WorkerId,
    DateTime CreatedAt);

public sealed record HealingContradictionResult(
    Guid ContradictionId,
    Guid MemoryIdA,
    Guid MemoryIdB,
    string ContradictionType,
    float Confidence,
    DateTime DetectedAt,
    string DetectionMethod);

public sealed record HealingStatsResult(
    int UnresolvedContradictions,
    int ResolvedContradictions,
    int TotalMerges,
    int ScansLast24Hours,
    DateTime? LastScanAt,
    bool AutoRepairEnabled);
