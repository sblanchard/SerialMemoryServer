namespace SerialMemory.EventSourcing.Retrieval;

/// <summary>
/// Multi-axis retrieval score components.
/// Final score is a weighted aggregation with contradiction penalty.
/// </summary>
public sealed record RetrievalScore
{
    /// <summary>Semantic similarity from vector search (0.0-1.0)</summary>
    public float SemanticScore { get; init; }

    /// <summary>Recency score based on age (0.0-1.0)</summary>
    public float RecencyScore { get; init; }

    /// <summary>Current confidence after decay (0.0-1.0)</summary>
    public float ConfidenceScore { get; init; }

    /// <summary>User-specific affinity score (0.0-1.0)</summary>
    public float UserAffinityScore { get; init; }

    /// <summary>Match to current directives/goals (0.0-1.0)</summary>
    public float DirectiveMatchScore { get; init; }

    /// <summary>Number of contradictions detected</summary>
    public int ContradictionCount { get; init; }

    /// <summary>Compute final weighted score</summary>
    public float ComputeFinalScore(RetrievalWeights weights)
    {
        var baseScore =
            (SemanticScore * weights.SemanticWeight) +
            (RecencyScore * weights.RecencyWeight) +
            (ConfidenceScore * weights.ConfidenceWeight) +
            (UserAffinityScore * weights.UserAffinityWeight) +
            (DirectiveMatchScore * weights.DirectiveWeight);

        var penalty = ContradictionCount * weights.ContradictionPenalty;

        return Math.Max(0f, baseScore - penalty);
    }
}

/// <summary>
/// Weights for multi-axis retrieval scoring.
/// Must sum to 1.0 for base weights.
/// </summary>
public sealed record RetrievalWeights
{
    public float SemanticWeight { get; init; } = 0.35f;
    public float RecencyWeight { get; init; } = 0.15f;
    public float ConfidenceWeight { get; init; } = 0.20f;
    public float UserAffinityWeight { get; init; } = 0.15f;
    public float DirectiveWeight { get; init; } = 0.15f;
    public float ContradictionPenalty { get; init; } = 0.10f;

    /// <summary>Default weights for general retrieval</summary>
    public static RetrievalWeights Default => new();

    /// <summary>Weights emphasizing semantic similarity</summary>
    public static RetrievalWeights SemanticFocused => new()
    {
        SemanticWeight = 0.50f,
        RecencyWeight = 0.10f,
        ConfidenceWeight = 0.15f,
        UserAffinityWeight = 0.10f,
        DirectiveWeight = 0.15f
    };

    /// <summary>Weights emphasizing recency</summary>
    public static RetrievalWeights RecencyFocused => new()
    {
        SemanticWeight = 0.25f,
        RecencyWeight = 0.35f,
        ConfidenceWeight = 0.15f,
        UserAffinityWeight = 0.10f,
        DirectiveWeight = 0.15f
    };

    /// <summary>Weights emphasizing user affinity</summary>
    public static RetrievalWeights UserFocused => new()
    {
        SemanticWeight = 0.25f,
        RecencyWeight = 0.10f,
        ConfidenceWeight = 0.20f,
        UserAffinityWeight = 0.30f,
        DirectiveWeight = 0.15f
    };
}

/// <summary>
/// Query parameters for memory retrieval.
/// </summary>
public sealed record RetrievalQuery
{
    /// <summary>Natural language query text</summary>
    public required string QueryText { get; init; }

    /// <summary>Pre-computed query embedding (optional)</summary>
    public float[]? QueryEmbedding { get; init; }

    /// <summary>User ID for affinity scoring</summary>
    public string UserId { get; init; } = "default_user";

    /// <summary>Current directives/goals for matching</summary>
    public string[] CurrentDirectives { get; init; } = [];

    /// <summary>Maximum results to return</summary>
    public int MaxResults { get; init; } = 10;

    /// <summary>Minimum final score threshold</summary>
    public float MinScore { get; init; } = 0.0f;

    /// <summary>Filter by memory layers</summary>
    public Events.MemoryLayer[]? LayerFilter { get; init; }

    /// <summary>Only include active memories</summary>
    public bool ActiveOnly { get; init; } = true;

    /// <summary>Weights for scoring</summary>
    public RetrievalWeights Weights { get; init; } = RetrievalWeights.Default;

    /// <summary>Enable hybrid (vector + text) search</summary>
    public bool HybridSearch { get; init; } = true;
}

/// <summary>
/// Result from memory retrieval.
/// </summary>
public sealed record RetrievalResult
{
    public required Guid MemoryId { get; init; }
    public required string Content { get; init; }
    public required Events.MemoryLayer Layer { get; init; }
    public required RetrievalScore Score { get; init; }
    public required float FinalScore { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public Guid[] CausalParents { get; init; } = [];
    public string[] Tags { get; init; } = [];
    public Dictionary<string, object> Metadata { get; init; } = [];
}
