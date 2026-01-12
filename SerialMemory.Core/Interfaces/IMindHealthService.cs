namespace SerialMemory.Core.Interfaces;

/// <summary>
/// Self-awareness service for tracking agent cognitive health.
/// Monitors confidence drift, hallucination rate, contradiction frequency.
/// </summary>
public interface IMindHealthService
{
    // ==========================================
    // CONFIDENCE DRIFT
    // ==========================================

    /// <summary>
    /// Record a confidence observation for an operation.
    /// </summary>
    Task RecordConfidenceAsync(ConfidenceObservation observation, CancellationToken ct = default);

    /// <summary>
    /// Get confidence drift analysis over time.
    /// </summary>
    Task<ConfidenceDriftAnalysis> GetConfidenceDriftAsync(int days = 30, CancellationToken ct = default);

    /// <summary>
    /// Get confidence trend for a specific operation type.
    /// </summary>
    Task<ConfidenceTrend> GetConfidenceTrendAsync(string operationType, int days = 30, CancellationToken ct = default);

    // ==========================================
    // HALLUCINATION TRACKING
    // ==========================================

    /// <summary>
    /// Record a potential hallucination event.
    /// </summary>
    Task RecordHallucinationAsync(HallucinationEvent evt, CancellationToken ct = default);

    /// <summary>
    /// Get hallucination rate analysis.
    /// </summary>
    Task<HallucinationAnalysis> GetHallucinationAnalysisAsync(int days = 30, CancellationToken ct = default);

    /// <summary>
    /// Flag a memory as hallucinated (post-hoc detection).
    /// </summary>
    Task FlagAsHallucinationAsync(Guid memoryId, string reason, float severity, CancellationToken ct = default);

    /// <summary>
    /// Get recent unresolved hallucination events for display.
    /// </summary>
    Task<IReadOnlyList<HallucinationEvent>> GetRecentHallucinationEventsAsync(int limit = 20, CancellationToken ct = default);

    // ==========================================
    // CONTRADICTION TRACKING
    // ==========================================

    /// <summary>
    /// Record a contradiction detection event.
    /// </summary>
    Task RecordContradictionAsync(ContradictionEvent evt, CancellationToken ct = default);

    /// <summary>
    /// Get contradiction frequency analysis.
    /// </summary>
    Task<ContradictionAnalysis> GetContradictionAnalysisAsync(int days = 30, CancellationToken ct = default);

    /// <summary>
    /// Get unresolved contradictions.
    /// </summary>
    Task<IReadOnlyList<ContradictionEvent>> GetUnresolvedContradictionsAsync(int limit = 100, CancellationToken ct = default);

    // ==========================================
    // AGGREGATE MIND HEALTH
    // ==========================================

    /// <summary>
    /// Get overall mind health snapshot.
    /// </summary>
    Task<MindHealthSnapshot> GetHealthSnapshotAsync(CancellationToken ct = default);

    /// <summary>
    /// Get mind health trends over time.
    /// </summary>
    Task<MindHealthTrends> GetHealthTrendsAsync(int days = 30, CancellationToken ct = default);

    /// <summary>
    /// Get daily health scores.
    /// </summary>
    Task<IReadOnlyList<DailyHealthScore>> GetDailyScoresAsync(int days = 30, CancellationToken ct = default);

    /// <summary>
    /// Compute/refresh daily health score for a specific tenant and date.
    /// This triggers recalculation of all health metrics.
    /// </summary>
    Task ComputeDailyHealthAsync(Guid tenantId, DateOnly date, CancellationToken ct = default);
}

// ==========================================
// CONFIDENCE MODELS
// ==========================================

public sealed class ConfidenceObservation
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public required string OperationType { get; init; }
    public required float PredictedConfidence { get; init; }
    public float? ActualAccuracy { get; init; }
    public float Drift => ActualAccuracy.HasValue ? PredictedConfidence - ActualAccuracy.Value : 0;
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public Guid? MemoryId { get; init; }
    public string? Context { get; init; }
}

public sealed class ConfidenceDriftAnalysis
{
    public float OverallDrift { get; init; }
    public float OverconfidenceRate { get; init; }
    public float UnderconfidenceRate { get; init; }
    public float CalibrationScore { get; init; }
    public int TotalObservations { get; init; }
    public Dictionary<string, float> DriftByOperation { get; init; } = new();
    public List<ConfidenceBucket> CalibrationCurve { get; init; } = new();
    public DateTimeOffset AnalyzedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class ConfidenceBucket
{
    public float PredictedRange { get; init; }
    public float ActualAccuracy { get; init; }
    public int Count { get; init; }
}

public sealed class ConfidenceTrend
{
    public required string OperationType { get; init; }
    public float CurrentDrift { get; init; }
    public float TrendDirection { get; init; }
    public List<DriftPoint> History { get; init; } = new();
}

public sealed class DriftPoint
{
    public DateTimeOffset Date { get; init; }
    public float Drift { get; init; }
    public int Observations { get; init; }
}

// ==========================================
// HALLUCINATION MODELS
// ==========================================

public sealed class HallucinationEvent
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid? MemoryId { get; init; }
    public required HallucinationType Type { get; init; }
    public required float Severity { get; init; }
    public required string Description { get; init; }
    public string? Evidence { get; init; }
    public string? GroundTruth { get; init; }
    public DateTimeOffset DetectedAt { get; init; } = DateTimeOffset.UtcNow;
    public HallucinationSource Source { get; init; } = HallucinationSource.AutoDetected;
    public bool Resolved { get; init; }
    public string? Resolution { get; init; }
}

public enum HallucinationType
{
    FactualError,
    EntityConfusion,
    TemporalConfusion,
    RelationshipError,
    AttributionError,
    Confabulation,
    SourceInvention
}

public enum HallucinationSource
{
    AutoDetected,
    UserReported,
    ValidationCheck,
    ContradictionResolution
}

public sealed class HallucinationAnalysis
{
    public float HallucinationRate { get; init; }
    public int TotalEvents { get; init; }
    public int ResolvedCount { get; init; }
    public float AvgSeverity { get; init; }
    public Dictionary<HallucinationType, int> ByType { get; init; } = new();
    public Dictionary<HallucinationSource, int> BySource { get; init; } = new();
    public List<HallucinationTrendPoint> Trend { get; init; } = new();
    public DateTimeOffset AnalyzedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class HallucinationTrendPoint
{
    public DateTimeOffset Date { get; init; }
    public float Rate { get; init; }
    public int Count { get; init; }
}

// ==========================================
// CONTRADICTION MODELS
// ==========================================

public sealed class ContradictionEvent
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public required Guid MemoryA { get; init; }
    public required Guid MemoryB { get; init; }
    public required ContradictionType Type { get; init; }
    public required float Severity { get; init; }
    public required string Description { get; init; }
    public float SimilarityScore { get; init; }
    public DateTimeOffset DetectedAt { get; init; } = DateTimeOffset.UtcNow;
    public ContradictionStatus Status { get; init; } = ContradictionStatus.Unresolved;
    public ContradictionResolution? Resolution { get; init; }
    public DateTimeOffset? ResolvedAt { get; init; }
}

public enum ContradictionType
{
    DirectConflict,
    TemporalInconsistency,
    AttributeConflict,
    RelationshipConflict,
    SourceDisagreement,
    LogicalInconsistency
}

public enum ContradictionStatus
{
    Unresolved,
    Investigating,
    Resolved,
    Accepted
}

public sealed class ContradictionResolution
{
    public Guid? WinnerId { get; init; }
    public required string Strategy { get; init; }
    public string? Rationale { get; init; }
    public Guid? MergedIntoId { get; init; }
}

public sealed class ContradictionAnalysis
{
    public float ContradictionRate { get; init; }
    public int TotalContradictions { get; init; }
    public int UnresolvedCount { get; init; }
    public float AvgSeverity { get; init; }
    public float ResolutionRate { get; init; }
    public TimeSpan AvgTimeToResolve { get; init; }
    public Dictionary<ContradictionType, int> ByType { get; init; } = new();
    public List<ContradictionTrendPoint> Trend { get; init; } = new();
    public DateTimeOffset AnalyzedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class ContradictionTrendPoint
{
    public DateTimeOffset Date { get; init; }
    public float Rate { get; init; }
    public int Detected { get; init; }
    public int Resolved { get; init; }
}

// ==========================================
// AGGREGATE HEALTH MODELS
// ==========================================

public sealed class MindHealthSnapshot
{
    public float OverallScore { get; init; }
    public HealthStatus Status { get; init; }
    public float ConfidenceCalibration { get; init; }
    public float HallucinationRate { get; init; }
    public float ContradictionRate { get; init; }
    public int ActiveIssues { get; init; }
    public List<HealthAlert> Alerts { get; init; } = new();
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

public enum HealthStatus
{
    NoData,     // No memories yet - show zeros
    Excellent,
    Good,
    Fair,
    Degraded,
    Critical
}

public sealed class HealthAlert
{
    public required AlertSeverity Severity { get; init; }
    public required string Category { get; init; }
    public required string Message { get; init; }
    public string? Recommendation { get; init; }
}

public enum AlertSeverity
{
    Info,
    Warning,
    Error,
    Critical
}

public sealed class MindHealthTrends
{
    public TrendDirection OverallTrend { get; init; }
    public TrendDirection ConfidenceTrend { get; init; }
    public TrendDirection HallucinationTrend { get; init; }
    public TrendDirection ContradictionTrend { get; init; }
    public List<DailyHealthScore> DailyScores { get; init; } = new();
    public DateTimeOffset AnalyzedAt { get; init; } = DateTimeOffset.UtcNow;
}

public enum TrendDirection
{
    Improving,
    Stable,
    Declining,
    Volatile
}

public sealed class DailyHealthScore
{
    public DateOnly Date { get; init; }
    public float OverallScore { get; init; }
    public float ConfidenceScore { get; init; }
    public float HallucinationScore { get; init; }
    public float ContradictionScore { get; init; }
    public int Observations { get; init; }
}
