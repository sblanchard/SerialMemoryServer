namespace SerialMemory.Core.Models;

/// <summary>
/// Visualization mode for graph projection
/// </summary>
public enum VisualizationMode
{
    Software,
    Hardware,
    Mixed
}

/// <summary>
/// Node in a graph visualization
/// </summary>
public sealed class VisualizationNode
{
    public required Guid Id { get; init; }
    public required string Label { get; init; }
    public required string Type { get; init; }
    public required string Category { get; init; } // "software" | "engineering"
    public Dictionary<string, object>? Metadata { get; init; }
}

/// <summary>
/// Link/edge in a graph visualization
/// </summary>
public sealed class VisualizationLink
{
    public required Guid Source { get; init; }
    public required Guid Target { get; init; }
    public required string Type { get; init; }
    public string? Category { get; init; }
    public float? Weight { get; init; }
}

/// <summary>
/// Risk/warning overlay for a node
/// </summary>
public sealed class VisualizationOverlay
{
    public required Guid EntityId { get; init; }
    public required string Type { get; init; } // "risk" | "warning" | "info" | "conflict"
    public required string Message { get; init; }
    public float? Confidence { get; init; }
}

/// <summary>
/// Complete graph visualization data structure
/// </summary>
public sealed class GraphVisualizationResult
{
    public List<VisualizationNode> Nodes { get; init; } = [];
    public List<VisualizationLink> Links { get; init; } = [];
    public List<VisualizationOverlay> Overlays { get; init; } = [];
    public VisualizationMode Mode { get; init; }
    public DateTime GeneratedAt { get; init; } = DateTime.UtcNow;
    public int TotalNodes => Nodes.Count;
    public int TotalLinks => Links.Count;
    public int TotalOverlays => Overlays.Count;
}
