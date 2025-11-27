namespace SerialMemory.Core.Models;

/// <summary>
/// Graph event types for tracking node and edge mutations
/// </summary>
public enum GraphEventType
{
    NodeCreated,
    NodeUpdated,
    NodeDeleted,
    EdgeCreated,
    EdgeUpdated,
    EdgeDeleted,
    NodeMerged,
    EdgeStrengthened
}

/// <summary>
/// A graph event representing a mutation to the knowledge graph
/// </summary>
public class GraphEvent
{
    public Guid EventId { get; set; } = Guid.CreateVersion7();
    public GraphEventType EventType { get; set; }

    // Node events
    public Guid? NodeId { get; set; }
    public string? NodeName { get; set; }
    public string? NodeType { get; set; }

    // Edge events
    public Guid? EdgeId { get; set; }
    public string? EdgeType { get; set; }
    public Guid? SourceNodeId { get; set; }
    public Guid? TargetNodeId { get; set; }
    public string? SourceNodeName { get; set; }
    public string? TargetNodeName { get; set; }

    // Change tracking
    public Dictionary<string, object>? PreviousState { get; set; }
    public Dictionary<string, object>? NewState { get; set; }

    // Confidence/weight for edges
    public float? Confidence { get; set; }

    // Context
    public string TriggeredBy { get; set; } = "system";
    public Guid? MemoryId { get; set; }
    public Guid? SessionId { get; set; }

    // Timestamps
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;

    // Metadata
    public Dictionary<string, object>? Metadata { get; set; }
}

/// <summary>
/// Graph topology statistics snapshot
/// </summary>
public class GraphTopologyStats
{
    public Guid StatId { get; set; } = Guid.CreateVersion7();
    public DateTimeOffset ComputedAt { get; set; } = DateTimeOffset.UtcNow;

    // Node statistics
    public int TotalNodes { get; set; }
    public Dictionary<string, int> NodesByType { get; set; } = new();

    // Edge statistics
    public int TotalEdges { get; set; }
    public Dictionary<string, int> EdgesByType { get; set; } = new();

    // Connectivity
    public double AvgDegree { get; set; }
    public int MaxDegree { get; set; }
    public int IsolatedNodes { get; set; }

    // Growth metrics
    public int NodesLast24Hours { get; set; }
    public int EdgesLast24Hours { get; set; }
    public int NodesLast7Days { get; set; }
    public int EdgesLast7Days { get; set; }
}

/// <summary>
/// Graph metrics summary for a time range
/// </summary>
public class GraphMetrics
{
    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset FromTime { get; set; }
    public DateTimeOffset ToTime { get; set; }

    // Event counts by type
    public int NodesCreated { get; set; }
    public int NodesUpdated { get; set; }
    public int NodesDeleted { get; set; }
    public int EdgesCreated { get; set; }
    public int EdgesUpdated { get; set; }
    public int EdgesDeleted { get; set; }

    // Totals
    public int TotalEvents { get; set; }
    public int EventsLastHour { get; set; }
    public int EventsLast24Hours { get; set; }

    // Last activity
    public DateTimeOffset? LastEventAt { get; set; }
}

/// <summary>
/// Real-time graph topology for UI visualization
/// </summary>
public class GraphTopology
{
    public List<GraphNode> Nodes { get; set; } = [];
    public List<GraphEdge> Edges { get; set; } = [];
    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A node in the graph topology
/// </summary>
public class GraphNode
{
    public required Guid Id { get; set; }
    public required string Name { get; set; }
    public required string Type { get; set; }
    public DateTime CreatedAt { get; set; }
    public int InDegree { get; set; }
    public int OutDegree { get; set; }
    public int TotalDegree => InDegree + OutDegree;
    public Dictionary<string, object>? Metadata { get; set; }
}

/// <summary>
/// An edge in the graph topology
/// </summary>
public class GraphEdge
{
    public required Guid Id { get; set; }
    public required Guid SourceId { get; set; }
    public required Guid TargetId { get; set; }
    public required string Type { get; set; }
    public float Confidence { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? SourceName { get; set; }
    public string? TargetName { get; set; }
    public Dictionary<string, object>? Metadata { get; set; }
}
