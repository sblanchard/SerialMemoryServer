namespace SerialMemory.Core.Models;

/// <summary>
/// Reasoning roles for multi-model orchestration
/// </summary>
public enum ReasoningRole
{
    Structural,    // Validates graph consistency
    Risk,          // Detects dangerous patterns
    Optimization,  // Finds improvements
    Contradiction, // Finds logical conflicts
    Summary        // Produces explainable output
}

/// <summary>
/// Type of multi-model insight
/// </summary>
public enum MultiModelInsightType
{
    Risk,
    Conflict,
    Optimization,
    Info
}

/// <summary>
/// Input for reasoning models
/// </summary>
public sealed class ReasoningInput
{
    public required string Content { get; init; }
    public Dictionary<string, object>? Context { get; init; }
    public List<Entity>? Entities { get; init; }
    public List<EntityRelationship>? Relationships { get; init; }
}

/// <summary>
/// Result from a single reasoning model
/// </summary>
public sealed class ModelResult
{
    public required string ModelName { get; init; }
    public required string Version { get; init; }
    public required ReasoningRole Role { get; init; }
    public bool Success { get; init; }
    public string? Error { get; init; }
    public int DurationMs { get; init; }
    public List<ModelInsight> Insights { get; init; } = [];
}

/// <summary>
/// Single insight from a model
/// </summary>
public sealed class ModelInsight
{
    public required MultiModelInsightType Type { get; init; }
    public required string Message { get; init; }
    public float Confidence { get; init; } = 1.0f;
    public List<Guid>? AffectedEntities { get; init; }
    public Dictionary<string, object>? Details { get; init; }
}

/// <summary>
/// Trace entry for a model execution
/// </summary>
public sealed record ModelTrace
{
    public required string Model { get; init; }
    public required string Version { get; init; }
    public required ReasoningRole Role { get; init; }
    public required int DurationMs { get; init; }
    public bool Success { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Merged insight from multiple models
/// </summary>
public sealed class MergedInsight
{
    public required MultiModelInsightType Type { get; init; }
    public required string Message { get; init; }
    public required float Confidence { get; init; }
    public required List<string> SourceModels { get; init; }
    public int AgreementCount { get; init; }
    public List<Guid>? AffectedEntities { get; init; }
}

/// <summary>
/// Complete result from multi-model reasoning
/// </summary>
public sealed class MultiModelReasoningResult
{
    public List<MergedInsight> Insights { get; init; } = [];
    public List<ModelTrace> Trace { get; init; } = [];
    public DateTime ReasonedAt { get; init; } = DateTime.UtcNow;
    public int TotalDurationMs { get; init; }
    public int ModelsUsed { get; init; }
    public int SuccessfulModels { get; init; }
    public string? InputHash { get; init; }

    /// <summary>Disagreement scores between model pairs</summary>
    public List<ModelDisagreement> Disagreements { get; init; } = [];

    /// <summary>Overall confidence based on model agreement</summary>
    public float OverallConfidence { get; init; }
}

/// <summary>
/// Tracks disagreement between two models on the same input
/// </summary>
public sealed class ModelDisagreement
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public required string PrimaryModel { get; init; }
    public required string SecondaryModel { get; init; }

    /// <summary>0.0 = complete agreement, 1.0 = complete disagreement</summary>
    public float DisagreementScore { get; init; }

    /// <summary>Insights from primary not found in secondary</summary>
    public List<string> PrimaryOnlyInsights { get; init; } = [];

    /// <summary>Insights from secondary not found in primary</summary>
    public List<string> SecondaryOnlyInsights { get; init; } = [];

    /// <summary>Insights where confidence differs significantly</summary>
    public List<ConfidenceDivergence> ConfidenceDivergences { get; init; } = [];

    public DateTimeOffset ComputedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Tracks confidence divergence for the same insight between models
/// </summary>
public sealed class ConfidenceDivergence
{
    public required string InsightMessage { get; init; }
    public float PrimaryConfidence { get; init; }
    public float SecondaryConfidence { get; init; }
    public float Delta => Math.Abs(PrimaryConfidence - SecondaryConfidence);
}
