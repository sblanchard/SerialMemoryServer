namespace SerialMemory.Core.Interfaces;

/// <summary>
/// Service for generating and managing healing recommendations.
/// </summary>
public interface IHealingRecommendationService
{
    /// <summary>
    /// Analyzes anomalies and generates healing recommendations.
    /// Does NOT execute any actions - only generates recommendations.
    /// </summary>
    Task<IReadOnlyList<HealingRecommendation>> AnalyzeAndRecommendAsync(
        Guid? tenantId = null,
        int maxRecommendations = 50,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets pending recommendations for a tenant.
    /// </summary>
    Task<IReadOnlyList<HealingRecommendation>> GetPendingRecommendationsAsync(
        Guid? tenantId = null,
        int limit = 50,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies a specific recommendation (with safety checks).
    /// </summary>
    Task<HealingActionResult> ApplyRecommendationAsync(
        Guid recommendationId,
        Guid? tenantId = null,
        string? appliedBy = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Dismisses a recommendation without applying it.
    /// </summary>
    Task<bool> DismissRecommendationAsync(
        Guid recommendationId,
        string reason,
        Guid? tenantId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets recommendation statistics.
    /// </summary>
    Task<RecommendationStats> GetRecommendationStatsAsync(
        Guid? tenantId = null,
        int daysBack = 30,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A healing recommendation generated from anomaly analysis.
/// </summary>
public sealed record HealingRecommendation
{
    public Guid Id { get; init; }
    public string AnomalyType { get; init; } = "";
    public Guid? AnomalyId { get; init; }
    public double Confidence { get; init; }
    public double Severity { get; init; }
    public string RecommendedAction { get; init; } = "";
    public Guid? TargetMemoryId { get; init; }
    public Guid? TargetEntityId { get; init; }
    public Guid? TenantId { get; init; }
    public string Reason { get; init; } = "";
    public RecommendationStatus Status { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? AppliedAt { get; init; }
    public string? AppliedBy { get; init; }
    public string? DismissedReason { get; init; }
    public Dictionary<string, object>? Context { get; init; }
}

/// <summary>
/// Possible healing actions.
/// </summary>
public static class HealingActions
{
    public const string DecayConfidence = "DecayConfidence";
    public const string ReembedMemory = "ReembedMemory";
    public const string RecrawlEntities = "RecrawlEntities";
    public const string MergeDuplicateNodes = "MergeDuplicateNodes";
    public const string FlagForReview = "FlagForReview";
    public const string InvalidateMemory = "InvalidateMemory";
    public const string ReinforceMemory = "ReinforceMemory";
    public const string SplitMemory = "SplitMemory";
    public const string ResolveContradiction = "ResolveContradiction";
    public const string RecomputeRelationships = "RecomputeRelationships";
}

/// <summary>
/// Risk level of healing actions.
/// </summary>
public enum HealingActionRisk
{
    VeryLow,   // Safe to auto-apply in lab/selfhost mode
    Low,       // Safe with tenant consent
    Medium,    // Requires explicit approval
    High,      // Manual review required
    Critical   // Never auto-apply
}

/// <summary>
/// Status of a recommendation.
/// </summary>
public enum RecommendationStatus
{
    Pending,
    Applied,
    Dismissed,
    Failed,
    Expired
}

/// <summary>
/// Result of applying a healing action.
/// </summary>
public sealed record HealingActionResult(
    bool Success,
    string? Error,
    Guid RecommendationId,
    string Action,
    Guid? TargetId,
    string? Details,
    long DurationMs);

/// <summary>
/// Recommendation statistics.
/// </summary>
public sealed record RecommendationStats(
    int TotalRecommendations,
    int PendingCount,
    int AppliedCount,
    int DismissedCount,
    int FailedCount,
    Dictionary<string, int> ByAction,
    Dictionary<string, int> ByAnomalyType,
    double AvgConfidence,
    DateTimeOffset? LastGeneratedAt,
    DateTimeOffset? LastAppliedAt);

/// <summary>
/// Action risk mapping.
/// </summary>
public static class HealingActionRiskMap
{
    public static HealingActionRisk GetRisk(string action) => action switch
    {
        HealingActions.FlagForReview => HealingActionRisk.VeryLow,
        HealingActions.DecayConfidence => HealingActionRisk.Low,
        HealingActions.ReinforceMemory => HealingActionRisk.Low,
        HealingActions.ReembedMemory => HealingActionRisk.Low,
        HealingActions.RecrawlEntities => HealingActionRisk.Low,
        HealingActions.RecomputeRelationships => HealingActionRisk.Medium,
        HealingActions.MergeDuplicateNodes => HealingActionRisk.Medium,
        HealingActions.ResolveContradiction => HealingActionRisk.Medium,
        HealingActions.SplitMemory => HealingActionRisk.High,
        HealingActions.InvalidateMemory => HealingActionRisk.Critical,
        _ => HealingActionRisk.High
    };

    public static bool CanAutoApply(string action, bool isLabTenant, bool isSelfHosted)
    {
        var risk = GetRisk(action);

        // VeryLow can always auto-apply
        if (risk == HealingActionRisk.VeryLow)
            return true;

        // Low can auto-apply in lab/selfhost mode
        if (risk == HealingActionRisk.Low && (isLabTenant || isSelfHosted))
            return true;

        return false;
    }
}
