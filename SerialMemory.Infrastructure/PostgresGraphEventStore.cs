using System.Text.Json;
using Dapper;
using Npgsql;
using SerialMemory.Core.Interfaces;
using SerialMemory.Core.Models;

namespace SerialMemory.Infrastructure;

/// <summary>
/// PostgreSQL implementation of graph event storage
/// </summary>
public class PostgresGraphEventStore : IGraphEventStore
{
    private readonly string _connectionString;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public PostgresGraphEventStore(string connectionString)
    {
        _connectionString = connectionString;
    }

    private NpgsqlConnection CreateConnection() => new(_connectionString);

    public async Task<Guid> LogEventAsync(GraphEvent graphEvent, CancellationToken ct = default)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        var sql = """
            INSERT INTO graph_events (
                event_id, event_type,
                node_id, node_name, node_type,
                edge_id, edge_type, source_node_id, target_node_id,
                source_node_name, target_node_name,
                previous_state, new_state, confidence,
                triggered_by, memory_id, session_id, occurred_at, metadata
            ) VALUES (
                @EventId, @EventType::graph_event_type,
                @NodeId, @NodeName, @NodeType,
                @EdgeId, @EdgeType, @SourceNodeId, @TargetNodeId,
                @SourceNodeName, @TargetNodeName,
                @PreviousState::jsonb, @NewState::jsonb, @Confidence,
                @TriggeredBy, @MemoryId, @SessionId, @OccurredAt, @Metadata::jsonb
            )
            RETURNING event_id
            """;

        return await conn.ExecuteScalarAsync<Guid>(sql, new
        {
            graphEvent.EventId,
            EventType = graphEvent.EventType.ToString(),
            graphEvent.NodeId,
            graphEvent.NodeName,
            graphEvent.NodeType,
            graphEvent.EdgeId,
            graphEvent.EdgeType,
            graphEvent.SourceNodeId,
            graphEvent.TargetNodeId,
            graphEvent.SourceNodeName,
            graphEvent.TargetNodeName,
            PreviousState = graphEvent.PreviousState != null ? JsonSerializer.Serialize(graphEvent.PreviousState, JsonOptions) : null,
            NewState = graphEvent.NewState != null ? JsonSerializer.Serialize(graphEvent.NewState, JsonOptions) : null,
            graphEvent.Confidence,
            graphEvent.TriggeredBy,
            graphEvent.MemoryId,
            graphEvent.SessionId,
            graphEvent.OccurredAt,
            Metadata = graphEvent.Metadata != null ? JsonSerializer.Serialize(graphEvent.Metadata, JsonOptions) : "{}"
        });
    }

    public async Task LogEventsAsync(IEnumerable<GraphEvent> events, CancellationToken ct = default)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);
        await using var transaction = await conn.BeginTransactionAsync(ct);

        try
        {
            foreach (var evt in events)
            {
                var sql = """
                    INSERT INTO graph_events (
                        event_id, event_type,
                        node_id, node_name, node_type,
                        edge_id, edge_type, source_node_id, target_node_id,
                        source_node_name, target_node_name,
                        previous_state, new_state, confidence,
                        triggered_by, memory_id, session_id, occurred_at, metadata
                    ) VALUES (
                        @EventId, @EventType::graph_event_type,
                        @NodeId, @NodeName, @NodeType,
                        @EdgeId, @EdgeType, @SourceNodeId, @TargetNodeId,
                        @SourceNodeName, @TargetNodeName,
                        @PreviousState::jsonb, @NewState::jsonb, @Confidence,
                        @TriggeredBy, @MemoryId, @SessionId, @OccurredAt, @Metadata::jsonb
                    )
                    """;

                await conn.ExecuteAsync(sql, new
                {
                    evt.EventId,
                    EventType = evt.EventType.ToString(),
                    evt.NodeId,
                    evt.NodeName,
                    evt.NodeType,
                    evt.EdgeId,
                    evt.EdgeType,
                    evt.SourceNodeId,
                    evt.TargetNodeId,
                    evt.SourceNodeName,
                    evt.TargetNodeName,
                    PreviousState = evt.PreviousState != null ? JsonSerializer.Serialize(evt.PreviousState, JsonOptions) : null,
                    NewState = evt.NewState != null ? JsonSerializer.Serialize(evt.NewState, JsonOptions) : null,
                    evt.Confidence,
                    evt.TriggeredBy,
                    evt.MemoryId,
                    evt.SessionId,
                    evt.OccurredAt,
                    Metadata = evt.Metadata != null ? JsonSerializer.Serialize(evt.Metadata, JsonOptions) : "{}"
                }, transaction);
            }

            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<List<GraphEvent>> GetRecentEventsAsync(int limit = 100, CancellationToken ct = default)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        // Use actual database schema (id, created_utc, event_type as text)
        var sql = """
            SELECT id as event_id, event_type,
                   node_id, node_type,
                   edge_id, edge_type,
                   created_utc as occurred_at, metadata
            FROM graph_events
            ORDER BY created_utc DESC
            LIMIT @Limit
            """;

        var rows = await conn.QueryAsync<GraphEventRowSimple>(sql, new { Limit = limit });
        return rows.Select(MapFromSimpleRow).ToList();
    }

    public async Task<List<GraphEvent>> GetEventsByTypeAsync(GraphEventType eventType, int limit = 100, CancellationToken ct = default)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        // Map enum to database event_type format (lowercase with underscore)
        var dbEventType = eventType.ToString() switch
        {
            "NodeCreated" => "node_created",
            "NodeUpdated" => "node_updated",
            "NodeDeleted" => "node_deleted",
            "EdgeCreated" => "edge_created",
            "EdgeUpdated" => "edge_updated",
            "EdgeDeleted" => "edge_deleted",
            _ => eventType.ToString().ToLower()
        };

        var sql = """
            SELECT id as event_id, event_type,
                   node_id, node_type,
                   edge_id, edge_type,
                   created_utc as occurred_at, metadata
            FROM graph_events
            WHERE event_type = @EventType
            ORDER BY created_utc DESC
            LIMIT @Limit
            """;

        var rows = await conn.QueryAsync<GraphEventRowSimple>(sql, new { EventType = dbEventType, Limit = limit });
        return rows.Select(MapFromSimpleRow).ToList();
    }

    public async Task<List<GraphEvent>> GetEventsForNodeAsync(Guid nodeId, CancellationToken ct = default)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        var sql = """
            SELECT id as event_id, event_type,
                   node_id, node_type,
                   edge_id, edge_type,
                   created_utc as occurred_at, metadata
            FROM graph_events
            WHERE node_id = @NodeId
            ORDER BY created_utc DESC
            """;

        var rows = await conn.QueryAsync<GraphEventRowSimple>(sql, new { NodeId = nodeId });
        return rows.Select(MapFromSimpleRow).ToList();
    }

    public async Task<List<GraphEvent>> GetEventsForEdgeAsync(Guid edgeId, CancellationToken ct = default)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        var sql = """
            SELECT id as event_id, event_type,
                   node_id, node_type,
                   edge_id, edge_type,
                   created_utc as occurred_at, metadata
            FROM graph_events
            WHERE edge_id = @EdgeId
            ORDER BY created_utc DESC
            """;

        var rows = await conn.QueryAsync<GraphEventRowSimple>(sql, new { EdgeId = edgeId });
        return rows.Select(MapFromSimpleRow).ToList();
    }

    public async Task<List<GraphEvent>> GetEventsInTimeRangeAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        var sql = """
            SELECT id as event_id, event_type,
                   node_id, node_type,
                   edge_id, edge_type,
                   created_utc as occurred_at, metadata
            FROM graph_events
            WHERE created_utc BETWEEN @From AND @To
            ORDER BY created_utc DESC
            """;

        var rows = await conn.QueryAsync<GraphEventRowSimple>(sql, new { From = from, To = to });
        return rows.Select(MapFromSimpleRow).ToList();
    }

    public async Task<GraphTopology> GetTopologyAsync(int nodeLimit = 100, int edgeLimit = 200, CancellationToken ct = default)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        // Get nodes from entities table
        var nodesSql = """
            SELECT
                e.id,
                e.name,
                e.entity_type as type,
                e.created_at,
                e.metadata::text,
                COALESCE(out_d.cnt, 0) as out_degree,
                COALESCE(in_d.cnt, 0) as in_degree
            FROM entities e
            LEFT JOIN (
                SELECT source_entity_id, COUNT(*) as cnt
                FROM entity_relationships
                GROUP BY source_entity_id
            ) out_d ON e.id = out_d.source_entity_id
            LEFT JOIN (
                SELECT target_entity_id, COUNT(*) as cnt
                FROM entity_relationships
                GROUP BY target_entity_id
            ) in_d ON e.id = in_d.target_entity_id
            ORDER BY COALESCE(out_d.cnt, 0) + COALESCE(in_d.cnt, 0) DESC
            LIMIT @Limit
            """;

        var nodeRows = await conn.QueryAsync<dynamic>(nodesSql, new { Limit = nodeLimit });
        var nodes = nodeRows.Select(row => new GraphNode
        {
            Id = row.id,
            Name = row.name,
            Type = row.type,
            CreatedAt = row.created_at,
            OutDegree = (int)row.out_degree,
            InDegree = (int)row.in_degree,
            Metadata = row.metadata != null ? JsonSerializer.Deserialize<Dictionary<string, object>>(row.metadata.ToString(), JsonOptions) : null
        }).ToList();

        var nodeIds = nodes.Select(n => n.Id).ToHashSet();

        // Get edges from entity_relationships table
        var edgesSql = """
            SELECT
                er.id,
                er.source_entity_id,
                er.target_entity_id,
                er.relationship_type as type,
                er.confidence,
                er.created_at,
                er.metadata::text,
                s.name as source_name,
                t.name as target_name
            FROM entity_relationships er
            JOIN entities s ON er.source_entity_id = s.id
            JOIN entities t ON er.target_entity_id = t.id
            ORDER BY er.created_at DESC
            LIMIT @Limit
            """;

        var edgeRows = await conn.QueryAsync<dynamic>(edgesSql, new { Limit = edgeLimit });
        var edges = edgeRows
            .Where(row => nodeIds.Contains((Guid)row.source_entity_id) && nodeIds.Contains((Guid)row.target_entity_id))
            .Select(row => new GraphEdge
            {
                Id = row.id,
                SourceId = row.source_entity_id,
                TargetId = row.target_entity_id,
                Type = row.type,
                Confidence = (float)row.confidence,
                CreatedAt = row.created_at,
                SourceName = row.source_name,
                TargetName = row.target_name,
                Metadata = row.metadata != null ? JsonSerializer.Deserialize<Dictionary<string, object>>(row.metadata.ToString(), JsonOptions) : null
            }).ToList();

        return new GraphTopology
        {
            Nodes = nodes,
            Edges = edges,
            GeneratedAt = DateTimeOffset.UtcNow
        };
    }

    public async Task<GraphTopologyStats> ComputeTopologyStatsAsync(CancellationToken ct = default)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        var stats = new GraphTopologyStats();

        // Total counts
        stats.TotalNodes = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM entities");
        stats.TotalEdges = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM entity_relationships");

        // Nodes by type
        var nodesByType = await conn.QueryAsync<(string type, int count)>(
            "SELECT entity_type, COUNT(*)::int FROM entities GROUP BY entity_type");
        stats.NodesByType = nodesByType.ToDictionary(x => x.type, x => x.count);

        // Edges by type
        var edgesByType = await conn.QueryAsync<(string type, int count)>(
            "SELECT relationship_type, COUNT(*)::int FROM entity_relationships GROUP BY relationship_type");
        stats.EdgesByType = edgesByType.ToDictionary(x => x.type, x => x.count);

        // Degree statistics
        var degreeSql = """
            WITH degrees AS (
                SELECT e.id,
                       COALESCE(out_d.cnt, 0) + COALESCE(in_d.cnt, 0) as degree
                FROM entities e
                LEFT JOIN (
                    SELECT source_entity_id, COUNT(*) as cnt
                    FROM entity_relationships
                    GROUP BY source_entity_id
                ) out_d ON e.id = out_d.source_entity_id
                LEFT JOIN (
                    SELECT target_entity_id, COUNT(*) as cnt
                    FROM entity_relationships
                    GROUP BY target_entity_id
                ) in_d ON e.id = in_d.target_entity_id
            )
            SELECT
                COALESCE(AVG(degree), 0) as avg_degree,
                COALESCE(MAX(degree), 0) as max_degree,
                COUNT(*) FILTER (WHERE degree = 0) as isolated
            FROM degrees
            """;

        var degreeStats = await conn.QuerySingleAsync<(double avg_degree, int max_degree, int isolated)>(degreeSql);
        stats.AvgDegree = degreeStats.avg_degree;
        stats.MaxDegree = degreeStats.max_degree;
        stats.IsolatedNodes = degreeStats.isolated;

        // Growth metrics from graph_events (using actual column names)
        stats.NodesLast24Hours = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM graph_events WHERE event_type = 'node_created' AND created_utc > NOW() - INTERVAL '24 hours'");
        stats.EdgesLast24Hours = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM graph_events WHERE event_type = 'edge_created' AND created_utc > NOW() - INTERVAL '24 hours'");
        stats.NodesLast7Days = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM graph_events WHERE event_type = 'node_created' AND created_utc > NOW() - INTERVAL '7 days'");
        stats.EdgesLast7Days = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM graph_events WHERE event_type = 'edge_created' AND created_utc > NOW() - INTERVAL '7 days'");

        // Store snapshot
        var insertSql = """
            INSERT INTO graph_topology_stats (
                total_nodes, nodes_by_type, total_edges, edges_by_type,
                avg_degree, max_degree, isolated_nodes,
                nodes_last_24h, edges_last_24h, nodes_last_7d, edges_last_7d
            ) VALUES (
                @TotalNodes, @NodesByType::jsonb, @TotalEdges, @EdgesByType::jsonb,
                @AvgDegree, @MaxDegree, @IsolatedNodes,
                @NodesLast24Hours, @EdgesLast24Hours, @NodesLast7Days, @EdgesLast7Days
            )
            RETURNING stat_id, computed_at
            """;

        var result = await conn.QuerySingleAsync<(Guid stat_id, DateTimeOffset computed_at)>(insertSql, new
        {
            stats.TotalNodes,
            NodesByType = JsonSerializer.Serialize(stats.NodesByType, JsonOptions),
            stats.TotalEdges,
            EdgesByType = JsonSerializer.Serialize(stats.EdgesByType, JsonOptions),
            stats.AvgDegree,
            stats.MaxDegree,
            IsolatedNodes = stats.IsolatedNodes,
            stats.NodesLast24Hours,
            stats.EdgesLast24Hours,
            stats.NodesLast7Days,
            stats.EdgesLast7Days
        });

        stats.StatId = result.stat_id;
        stats.ComputedAt = result.computed_at;

        return stats;
    }

    public async Task<GraphTopologyStats?> GetLatestTopologyStatsAsync(CancellationToken ct = default)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        var sql = """
            SELECT stat_id, computed_at,
                   total_nodes, nodes_by_type, total_edges, edges_by_type,
                   avg_degree, max_degree, isolated_nodes,
                   nodes_last_24h, edges_last_24h, nodes_last_7d, edges_last_7d
            FROM graph_topology_stats
            ORDER BY computed_at DESC
            LIMIT 1
            """;

        var row = await conn.QuerySingleOrDefaultAsync<dynamic>(sql);
        if (row == null) return null;

        var nodesByType = new Dictionary<string, int>();
        var edgesByType = new Dictionary<string, int>();

        if (row.nodes_by_type != null)
        {
            string json = row.nodes_by_type.ToString();
            nodesByType = JsonSerializer.Deserialize<Dictionary<string, int>>(json, JsonOptions) ?? new Dictionary<string, int>();
        }

        if (row.edges_by_type != null)
        {
            string json = row.edges_by_type.ToString();
            edgesByType = JsonSerializer.Deserialize<Dictionary<string, int>>(json, JsonOptions) ?? new Dictionary<string, int>();
        }

        return new GraphTopologyStats
        {
            StatId = row.stat_id,
            ComputedAt = row.computed_at,
            TotalNodes = row.total_nodes,
            NodesByType = nodesByType,
            TotalEdges = row.total_edges,
            EdgesByType = edgesByType,
            AvgDegree = (double)row.avg_degree,
            MaxDegree = row.max_degree,
            IsolatedNodes = row.isolated_nodes,
            NodesLast24Hours = row.nodes_last_24h,
            EdgesLast24Hours = row.edges_last_24h,
            NodesLast7Days = row.nodes_last_7d,
            EdgesLast7Days = row.edges_last_7d
        };
    }

    public async Task<GraphMetrics> GetMetricsAsync(CancellationToken ct = default)
    {
        return await GetMetricsForTimeRangeAsync(DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow, ct);
    }

    public async Task<GraphMetrics> GetMetricsForTimeRangeAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        var metrics = new GraphMetrics
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            FromTime = from,
            ToTime = to
        };

        // Event counts by type (using actual column name and values)
        var countsSql = """
            SELECT event_type, COUNT(*)::int as count
            FROM graph_events
            WHERE created_utc BETWEEN @From AND @To
            GROUP BY event_type
            """;

        var counts = await conn.QueryAsync<(string event_type, int count)>(countsSql, new { From = from, To = to });

        foreach (var (eventType, count) in counts)
        {
            metrics.TotalEvents += count;
            switch (eventType)
            {
                case "node_created": metrics.NodesCreated = count; break;
                case "node_updated": metrics.NodesUpdated = count; break;
                case "node_deleted": metrics.NodesDeleted = count; break;
                case "edge_created": metrics.EdgesCreated = count; break;
                case "edge_updated": metrics.EdgesUpdated = count; break;
                case "edge_deleted": metrics.EdgesDeleted = count; break;
            }
        }

        // Time-based metrics (using actual column name)
        metrics.EventsLastHour = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM graph_events WHERE created_utc > NOW() - INTERVAL '1 hour'");
        metrics.EventsLast24Hours = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM graph_events WHERE created_utc > NOW() - INTERVAL '24 hours'");

        metrics.LastEventAt = await conn.ExecuteScalarAsync<DateTimeOffset?>(
            "SELECT MAX(created_utc) FROM graph_events");

        return metrics;
    }

    public async Task<List<GraphEvent>> GetNodeActivityAsync(Guid nodeId, int limit = 50, CancellationToken ct = default)
    {
        return await GetEventsForNodeAsync(nodeId, ct);
    }

    public async Task<List<GraphEvent>> GetEdgeActivityAsync(Guid edgeId, int limit = 50, CancellationToken ct = default)
    {
        return await GetEventsForEdgeAsync(edgeId, ct);
    }

    // Simplified helper row type for actual database schema
    private class GraphEventRowSimple
    {
        public Guid event_id { get; set; }
        public string event_type { get; set; } = "";
        public Guid? node_id { get; set; }
        public string? node_type { get; set; }
        public Guid? edge_id { get; set; }
        public string? edge_type { get; set; }
        public DateTimeOffset occurred_at { get; set; }
        public string? metadata { get; set; }
    }

    private static GraphEvent MapFromSimpleRow(GraphEventRowSimple row)
    {
        // Map database event_type to enum
        var eventType = row.event_type switch
        {
            "node_created" => GraphEventType.NodeCreated,
            "node_updated" => GraphEventType.NodeUpdated,
            "node_deleted" => GraphEventType.NodeDeleted,
            "edge_created" => GraphEventType.EdgeCreated,
            "edge_updated" => GraphEventType.EdgeUpdated,
            "edge_deleted" => GraphEventType.EdgeDeleted,
            "batch_import" => GraphEventType.NodeCreated, // Map to closest equivalent
            "relationship_discovered" => GraphEventType.EdgeCreated, // Map to closest equivalent
            _ => GraphEventType.NodeCreated
        };

        return new GraphEvent
        {
            EventId = row.event_id,
            EventType = eventType,
            NodeId = row.node_id,
            NodeType = row.node_type,
            EdgeId = row.edge_id,
            EdgeType = row.edge_type,
            OccurredAt = row.occurred_at,
            TriggeredBy = "system",
            Metadata = string.IsNullOrEmpty(row.metadata) ? null : JsonSerializer.Deserialize<Dictionary<string, object>>(row.metadata, JsonOptions)
        };
    }

    // Helper row type for Dapper (full schema - not currently used)
    private class GraphEventRow
    {
        public Guid event_id { get; set; }
        public string event_type { get; set; } = "";
        public Guid? node_id { get; set; }
        public string? node_name { get; set; }
        public string? node_type { get; set; }
        public Guid? edge_id { get; set; }
        public string? edge_type { get; set; }
        public Guid? source_node_id { get; set; }
        public Guid? target_node_id { get; set; }
        public string? source_node_name { get; set; }
        public string? target_node_name { get; set; }
        public string? previous_state { get; set; }
        public string? new_state { get; set; }
        public float? confidence { get; set; }
        public string triggered_by { get; set; } = "";
        public Guid? memory_id { get; set; }
        public Guid? session_id { get; set; }
        public DateTimeOffset occurred_at { get; set; }
        public string? metadata { get; set; }
    }

    private static GraphEvent MapFromRow(GraphEventRow row) => new()
    {
        EventId = row.event_id,
        EventType = Enum.Parse<GraphEventType>(row.event_type),
        NodeId = row.node_id,
        NodeName = row.node_name,
        NodeType = row.node_type,
        EdgeId = row.edge_id,
        EdgeType = row.edge_type,
        SourceNodeId = row.source_node_id,
        TargetNodeId = row.target_node_id,
        SourceNodeName = row.source_node_name,
        TargetNodeName = row.target_node_name,
        PreviousState = string.IsNullOrEmpty(row.previous_state) ? null : JsonSerializer.Deserialize<Dictionary<string, object>>(row.previous_state, JsonOptions),
        NewState = string.IsNullOrEmpty(row.new_state) ? null : JsonSerializer.Deserialize<Dictionary<string, object>>(row.new_state, JsonOptions),
        Confidence = row.confidence,
        TriggeredBy = row.triggered_by,
        MemoryId = row.memory_id,
        SessionId = row.session_id,
        OccurredAt = row.occurred_at,
        Metadata = string.IsNullOrEmpty(row.metadata) ? null : JsonSerializer.Deserialize<Dictionary<string, object>>(row.metadata, JsonOptions)
    };
}
