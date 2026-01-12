namespace SerialMemory.Core.Interfaces;

/// <summary>
/// ML-style anomaly detection service that scans for memory integrity issues.
/// Uses statistical thresholds, z-scores, and weighted scoring - no external ML required.
/// </summary>
public interface IAnomalyDetectionService
{
    /// <summary>
    /// Scans all anomaly-related tables and returns classified findings.
    /// </summary>
    Task<IReadOnlyList<AnomalyFinding>> DetectAnomaliesAsync(
        Guid? tenantId = null,
        int maxResults = 100,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets aggregated statistics about anomalies.
    /// </summary>
    Task<AnomalyStats> GetAnomalyStatsAsync(
        Guid? tenantId = null,
        int daysBack = 30,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets trend data for anomaly visualization.
    /// </summary>
    Task<IReadOnlyList<AnomalyTrendPoint>> GetAnomalyTrendAsync(
        Guid? tenantId = null,
        int daysBack = 30,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Context for retrieving information about an anomaly.
/// </summary>
public sealed record AnomalyContext(
    Guid? MemoryId,
    Guid? EntityId,
    string AnomalyType,
    DateTimeOffset? StartTime,
    DateTimeOffset? EndTime);

/// <summary>
/// Result of context retrieval for an anomaly.
/// </summary>
public sealed record AnomalyContextResult(
    IReadOnlyList<ScoredMemory> RelatedMemories,
    IReadOnlyList<AnomalyEntityInfo> LinkedEntities,
    IReadOnlyList<AnomalyRelationshipInfo> Relationships,
    IReadOnlyList<MemoryEventInfo> RecentEvents,
    TimeSpan Duration);

/// <summary>
/// Basic entity information for anomaly context.
/// </summary>
public sealed record AnomalyEntityInfo(
    Guid EntityId,
    string Name,
    string EntityType,
    int MemoryCount,
    int RelationshipCount);

/// <summary>
/// Basic relationship information for anomaly context.
/// </summary>
public sealed record AnomalyRelationshipInfo(
    Guid RelationshipId,
    Guid SourceEntityId,
    string SourceEntityName,
    Guid TargetEntityId,
    string TargetEntityName,
    string RelationType);

/// <summary>
/// Memory event information.
/// </summary>
public sealed record MemoryEventInfo(
    Guid EventId,
    Guid MemoryId,
    string EventType,
    DateTimeOffset OccurredAt,
    string? Details);

/// <summary>
/// A detected anomaly finding.
/// </summary>
public sealed record AnomalyFinding(
    Guid Id,
    string Type,
    string Category,
    double SeverityScore,
    Guid? MemoryId,
    Guid? EntityId,
    Guid? TenantId,
    DateTimeOffset DetectedAt,
    string Description,
    Dictionary<string, object>? Evidence);

/// <summary>
/// Anomaly type categories.
/// </summary>
public static class AnomalyTypes
{
    public const string OrphanedGraphNode = "OrphanedGraphNode";
    public const string ConflictCluster = "ConflictCluster";
    public const string HighHallucinationRegion = "HighHallucinationRegion";
    public const string DriftSpike = "DriftSpike";
    public const string RepeatedContradiction = "RepeatedContradiction";
    public const string IntegrityFailure = "IntegrityFailure";
    public const string PerformanceAnomaly = "PerformanceAnomaly";
    public const string SecurityAnomaly = "SecurityAnomaly";
    public const string ConfidenceDecayCluster = "ConfidenceDecayCluster";
    public const string UnvalidatedMemory = "UnvalidatedMemory";
}

/// <summary>
/// Aggregated anomaly statistics.
/// </summary>
public sealed record AnomalyStats(
    int TotalAnomalies,
    int CriticalCount,
    int HighCount,
    int MediumCount,
    int LowCount,
    Dictionary<string, int> ByType,
    Dictionary<string, int> ByCategory,
    double AvgSeverity,
    DateTimeOffset? LastDetectedAt);

/// <summary>
/// A trend point for anomaly visualization.
/// </summary>
public sealed record AnomalyTrendPoint(
    DateTimeOffset Date,
    int Count,
    double AvgSeverity,
    Dictionary<string, int> ByType);
