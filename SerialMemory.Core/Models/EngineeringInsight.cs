namespace SerialMemory.Core.Models;

/// <summary>
/// Severity level for engineering insights
/// </summary>
public enum InsightSeverity
{
    Info,
    Warning,
    Risk,
    Conflict
}

/// <summary>
/// Category of engineering insight
/// </summary>
public enum InsightCategory
{
    PowerIntegrity,
    SignalIntegrity,
    DependencyCorruption,
    ThermalRisk,
    ProtocolMismatch,
    ComponentCompatibility
}

/// <summary>
/// Represents an engineering insight/issue detected by the reasoning engine
/// </summary>
public sealed class EngineeringInsight
{
    public required InsightSeverity Severity { get; init; }
    public required InsightCategory Category { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }

    /// <summary>
    /// Entities involved in this insight
    /// </summary>
    public List<InsightEntity> InvolvedEntities { get; init; } = [];

    /// <summary>
    /// Relationship that triggered this insight (if applicable)
    /// </summary>
    public Guid? RelationshipId { get; init; }

    /// <summary>
    /// Confidence score (0.0-1.0)
    /// </summary>
    public float Confidence { get; init; } = 1.0f;

    /// <summary>
    /// Suggested remediation actions
    /// </summary>
    public List<string> Recommendations { get; init; } = [];

    /// <summary>
    /// Technical details for diagnosis
    /// </summary>
    public Dictionary<string, object> Details { get; init; } = [];
}

/// <summary>
/// Entity reference within an insight
/// </summary>
public sealed class InsightEntity
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required string EntityType { get; init; }
    public string? Role { get; init; } // e.g., "source", "target", "affected"
}

/// <summary>
/// Result of engineering analysis
/// </summary>
public sealed class EngineeringAnalysisResult
{
    public DateTime AnalyzedAt { get; init; } = DateTime.UtcNow;
    public int EntitiesAnalyzed { get; init; }
    public int RelationshipsAnalyzed { get; init; }
    public List<EngineeringInsight> Insights { get; init; } = [];

    public int RiskCount => Insights.Count(i => i.Severity == InsightSeverity.Risk);
    public int ConflictCount => Insights.Count(i => i.Severity == InsightSeverity.Conflict);
    public int WarningCount => Insights.Count(i => i.Severity == InsightSeverity.Warning);
    public int InfoCount => Insights.Count(i => i.Severity == InsightSeverity.Info);
}
