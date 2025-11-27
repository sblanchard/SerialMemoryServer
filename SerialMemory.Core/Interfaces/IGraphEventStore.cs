using SerialMemory.Core.Models;

namespace SerialMemory.Core.Interfaces;

/// <summary>
/// Repository for graph events and topology
/// </summary>
public interface IGraphEventStore
{
    // Event logging
    Task<Guid> LogEventAsync(GraphEvent graphEvent, CancellationToken ct = default);
    Task LogEventsAsync(IEnumerable<GraphEvent> events, CancellationToken ct = default);

    // Event queries
    Task<List<GraphEvent>> GetRecentEventsAsync(int limit = 100, CancellationToken ct = default);
    Task<List<GraphEvent>> GetEventsByTypeAsync(GraphEventType eventType, int limit = 100, CancellationToken ct = default);
    Task<List<GraphEvent>> GetEventsForNodeAsync(Guid nodeId, CancellationToken ct = default);
    Task<List<GraphEvent>> GetEventsForEdgeAsync(Guid edgeId, CancellationToken ct = default);
    Task<List<GraphEvent>> GetEventsInTimeRangeAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);

    // Topology
    Task<GraphTopology> GetTopologyAsync(int nodeLimit = 100, int edgeLimit = 200, CancellationToken ct = default);
    Task<GraphTopologyStats> ComputeTopologyStatsAsync(CancellationToken ct = default);
    Task<GraphTopologyStats?> GetLatestTopologyStatsAsync(CancellationToken ct = default);

    // Metrics
    Task<GraphMetrics> GetMetricsAsync(CancellationToken ct = default);
    Task<GraphMetrics> GetMetricsForTimeRangeAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);

    // Node/Edge activity
    Task<List<GraphEvent>> GetNodeActivityAsync(Guid nodeId, int limit = 50, CancellationToken ct = default);
    Task<List<GraphEvent>> GetEdgeActivityAsync(Guid edgeId, int limit = 50, CancellationToken ct = default);
}
