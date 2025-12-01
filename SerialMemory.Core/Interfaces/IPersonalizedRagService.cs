namespace SerialMemory.Core.Interfaces;

/// <summary>
/// Personalized RAG (Retrieval-Augmented Generation) service.
/// Uses L2 summaries as the primary retrieval surface, enriched with L1/L3/L4 context.
/// </summary>
public interface IPersonalizedRagService
{
    /// <summary>
    /// Answers a user query using personalized RAG over their memories.
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="userId">User identifier within the tenant</param>
    /// <param name="query">Natural language query</param>
    /// <param name="options">Optional RAG configuration</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>RAG result with answer and supporting memories</returns>
    Task<RagResult> AnswerAsync(
        Guid tenantId,
        string userId,
        string query,
        RagOptions? options = null,
        CancellationToken ct = default);

    /// <summary>
    /// Searches memories using L2 embeddings without generating an answer.
    /// Useful for retrieval-only use cases.
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="query">Natural language query</param>
    /// <param name="limit">Maximum memories to return</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>List of retrieved memories with enriched context</returns>
    Task<IReadOnlyList<RetrievedMemory>> SearchMemoriesAsync(
        Guid tenantId,
        string query,
        int limit = 10,
        CancellationToken ct = default);
}

/// <summary>
/// Result from a personalized RAG query.
/// </summary>
public sealed class RagResult
{
    /// <summary>
    /// The generated answer grounded in user memories.
    /// </summary>
    public string Answer { get; init; } = default!;

    /// <summary>
    /// Memories that were retrieved and used for the answer.
    /// </summary>
    public IReadOnlyList<RetrievedMemory> Memories { get; init; } = [];

    /// <summary>
    /// Optional reasoning trace explaining how memories influenced the answer.
    /// </summary>
    public string? ReasoningTrace { get; init; }

    /// <summary>
    /// Model used for answer generation.
    /// </summary>
    public string ModelName { get; init; } = default!;

    /// <summary>
    /// Total latency in milliseconds.
    /// </summary>
    public int LatencyMs { get; init; }

    /// <summary>
    /// Query ID for tracking/caching.
    /// </summary>
    public Guid QueryId { get; init; }
}

/// <summary>
/// A memory retrieved for RAG, enriched with all layers.
/// </summary>
public sealed class RetrievedMemory
{
    /// <summary>
    /// Memory identifier.
    /// </summary>
    public Guid MemoryId { get; init; }

    /// <summary>
    /// L2 summary text (primary retrieval content).
    /// </summary>
    public string L2Summary { get; init; } = default!;

    /// <summary>
    /// L2 one-liner if available.
    /// </summary>
    public string? L2OneLiner { get; init; }

    /// <summary>
    /// L1 context information (speaker, intent, sentiment, etc.).
    /// </summary>
    public string? L1Context { get; init; }

    /// <summary>
    /// L3 extracted facts as human-readable strings.
    /// </summary>
    public IReadOnlyList<string> L3Facts { get; init; } = [];

    /// <summary>
    /// L4 heuristics/patterns (preferences, habits, tendencies).
    /// </summary>
    public IReadOnlyList<string> L4Heuristics { get; init; } = [];

    /// <summary>
    /// Keywords from L2.
    /// </summary>
    public IReadOnlyList<string> Keywords { get; init; } = [];

    /// <summary>
    /// Similarity score to the query (0.0 - 1.0).
    /// </summary>
    public decimal Similarity { get; init; }

    /// <summary>
    /// Importance score from L2.
    /// </summary>
    public decimal Importance { get; init; }

    /// <summary>
    /// When the original memory was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
/// Configuration options for RAG queries.
/// </summary>
public sealed class RagOptions
{
    /// <summary>
    /// Maximum number of memories to retrieve. Default: 5.
    /// </summary>
    public int MaxMemories { get; init; } = 5;

    /// <summary>
    /// Whether to include L1 context in retrieval. Default: true.
    /// </summary>
    public bool IncludeL1 { get; init; } = true;

    /// <summary>
    /// Whether to include L3 facts in retrieval. Default: true.
    /// </summary>
    public bool IncludeL3 { get; init; } = true;

    /// <summary>
    /// Whether to include L4 heuristics in retrieval. Default: true.
    /// </summary>
    public bool IncludeL4 { get; init; } = true;

    /// <summary>
    /// Minimum similarity threshold (0.0 - 1.0). Default: 0.5.
    /// </summary>
    public decimal SimilarityThreshold { get; init; } = 0.5m;

    /// <summary>
    /// LLM temperature for answer generation. Default: 0.3.
    /// </summary>
    public float Temperature { get; init; } = 0.3f;

    /// <summary>
    /// Maximum tokens for the answer. Default: 1000.
    /// </summary>
    public int MaxAnswerTokens { get; init; } = 1000;

    /// <summary>
    /// Whether to include reasoning trace in the response. Default: false.
    /// </summary>
    public bool IncludeReasoningTrace { get; init; } = false;

    /// <summary>
    /// Optional system prompt override for the RAG answer generation.
    /// </summary>
    public string? SystemPromptOverride { get; init; }
}
