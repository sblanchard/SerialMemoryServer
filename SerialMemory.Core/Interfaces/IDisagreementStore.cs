using SerialMemory.Core.Models;

namespace SerialMemory.Core.Interfaces;

/// <summary>
/// Store for model disagreement records and reasoning run history.
/// </summary>
public interface IDisagreementStore
{
    /// <summary>Store a complete reasoning run with disagreements.</summary>
    Task<Guid> StoreReasoningRunAsync(
        MultiModelReasoningResult result,
        string? projectFilter = null,
        Guid? memoryId = null,
        string tenantId = "self",
        CancellationToken ct = default);

    /// <summary>Get recent reasoning runs.</summary>
    Task<List<ReasoningRunSummary>> GetRecentRunsAsync(
        string tenantId = "self",
        int limit = 50,
        CancellationToken ct = default);

    /// <summary>Get disagreements for a specific run.</summary>
    Task<List<ModelDisagreement>> GetDisagreementsForRunAsync(
        Guid runId,
        CancellationToken ct = default);

    /// <summary>Get disagreement trends over time.</summary>
    Task<List<DisagreementTrend>> GetDisagreementTrendsAsync(
        string tenantId = "self",
        int days = 7,
        CancellationToken ct = default);

    /// <summary>Get confidence history.</summary>
    Task<List<ConfidenceHistoryEntry>> GetConfidenceHistoryAsync(
        string tenantId = "self",
        int limit = 100,
        CancellationToken ct = default);
}

/// <summary>Summary of a reasoning run.</summary>
public sealed class ReasoningRunSummary
{
    public Guid Id { get; init; }
    public string InputHash { get; init; } = "";
    public int ModelsUsed { get; init; }
    public int SuccessfulModels { get; init; }
    public int TotalDurationMs { get; init; }
    public float OverallConfidence { get; init; }
    public int InsightCount { get; init; }
    public int DisagreementCount { get; init; }
    public float? AvgDisagreementScore { get; init; }
    public string? ProjectFilter { get; init; }
    public Guid? MemoryId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>Disagreement trend for a model pair.</summary>
public sealed class DisagreementTrend
{
    public string ModelPair { get; init; } = "";
    public float AvgDisagreement { get; init; }
    public float MaxDisagreement { get; init; }
    public int OccurrenceCount { get; init; }
}

/// <summary>Confidence history entry.</summary>
public sealed class ConfidenceHistoryEntry
{
    public Guid RunId { get; init; }
    public float OverallConfidence { get; init; }
    public int ModelsUsed { get; init; }
    public int SuccessfulModels { get; init; }
    public int DisagreementCount { get; init; }
    public float? AvgDisagreement { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
