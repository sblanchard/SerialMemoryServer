namespace SerialMemory.Core.Interfaces;

/// <summary>
/// Service for classifying memory content through L0-L4 layers.
/// </summary>
public interface IClassificationService
{
    /// <summary>
    /// Classifies memory content for a specific layer.
    /// </summary>
    /// <param name="content">The raw memory content</param>
    /// <param name="layer">The target layer (L0-L4)</param>
    /// <param name="previousLayerContent">Content from the previous layer (null for L0)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Classification result with structured content</returns>
    Task<ClassificationResult> ClassifyAsync(
        string content,
        MemoryLayer layer,
        string? previousLayerContent,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Memory layers in the cognitive hierarchy.
/// Each memory belongs to exactly one layer.
/// Layer transitions are explicit events.
/// </summary>
public enum MemoryLayer
{
    /// <summary>Raw transcript or input data</summary>
    L0_RAW = 0,

    /// <summary>Contextual understanding of raw input</summary>
    L1_CONTEXT = 1,

    /// <summary>Summarized information</summary>
    L2_SUMMARY = 2,

    /// <summary>Extracted knowledge and facts</summary>
    L3_KNOWLEDGE = 3,

    /// <summary>Heuristics and learned patterns</summary>
    L4_HEURISTIC = 4
}

/// <summary>
/// Result from classifying a memory layer.
/// </summary>
public record ClassificationResult
{
    /// <summary>Structured JSON content for this layer</summary>
    public required string ContentJson { get; init; }

    /// <summary>Name/version of the model that produced this</summary>
    public required string ModelName { get; init; }

    /// <summary>Confidence score (0-1)</summary>
    public decimal? Confidence { get; init; }

    /// <summary>Knowledge nodes extracted (for L3/L4)</summary>
    public List<KnowledgeNode>? KnowledgeNodes { get; init; }
}

/// <summary>
/// A knowledge graph node extracted from L3/L4 classification.
/// </summary>
public record KnowledgeNode
{
    /// <summary>Type: 'fact', 'entity', 'relationship', 'rule'</summary>
    public required string NodeType { get; init; }

    /// <summary>Subject of the knowledge triple</summary>
    public required string Subject { get; init; }

    /// <summary>Predicate/relationship</summary>
    public string? Predicate { get; init; }

    /// <summary>Object of the knowledge triple</summary>
    public string? Object { get; init; }

    /// <summary>Confidence score (0-1)</summary>
    public decimal? Confidence { get; init; }

    /// <summary>Source text evidence</summary>
    public string? Evidence { get; init; }

    /// <summary>Additional metadata as JSON</summary>
    public string? Metadata { get; init; }
}
