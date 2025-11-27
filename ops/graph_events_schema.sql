-- Graph Events Schema for SerialMemory
-- Real-time graph activity instrumentation

-- ============================================================================
-- ENUMS
-- ============================================================================

CREATE TYPE graph_event_type AS ENUM (
    'NodeCreated',
    'NodeUpdated',
    'NodeDeleted',
    'EdgeCreated',
    'EdgeUpdated',
    'EdgeDeleted',
    'NodeMerged',
    'EdgeStrengthened'
);

-- ============================================================================
-- GRAPH EVENTS TABLE (Append-Only)
-- ============================================================================

CREATE TABLE graph_events (
    event_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    event_type graph_event_type NOT NULL,

    -- Node events
    node_id UUID,
    node_name VARCHAR(500),
    node_type VARCHAR(100),

    -- Edge events
    edge_id UUID,
    edge_type VARCHAR(100),
    source_node_id UUID,
    target_node_id UUID,
    source_node_name VARCHAR(500),
    target_node_name VARCHAR(500),

    -- Change tracking
    previous_state JSONB,                           -- State before change
    new_state JSONB,                                -- State after change

    -- Confidence/weight for edges
    confidence DECIMAL(4,3),

    -- Context
    triggered_by VARCHAR(255) DEFAULT 'system',     -- What triggered this event
    memory_id UUID,                                 -- Related memory (if any)
    session_id UUID,                                -- Related session (if any)

    -- Timestamps
    occurred_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    -- Metadata
    metadata JSONB DEFAULT '{}'
);

-- Indexes for querying
CREATE INDEX idx_graph_events_type ON graph_events (event_type);
CREATE INDEX idx_graph_events_node ON graph_events (node_id) WHERE node_id IS NOT NULL;
CREATE INDEX idx_graph_events_edge ON graph_events (edge_id) WHERE edge_id IS NOT NULL;
CREATE INDEX idx_graph_events_occurred ON graph_events (occurred_at DESC);
CREATE INDEX idx_graph_events_source ON graph_events (source_node_id) WHERE source_node_id IS NOT NULL;
CREATE INDEX idx_graph_events_target ON graph_events (target_node_id) WHERE target_node_id IS NOT NULL;

-- Partial indexes for specific event types
CREATE INDEX idx_graph_events_node_created ON graph_events (occurred_at DESC) WHERE event_type = 'NodeCreated';
CREATE INDEX idx_graph_events_edge_created ON graph_events (occurred_at DESC) WHERE event_type = 'EdgeCreated';

-- ============================================================================
-- GRAPH TOPOLOGY SNAPSHOT (Materialized for fast queries)
-- ============================================================================

CREATE TABLE graph_topology_stats (
    stat_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    computed_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    -- Node statistics
    total_nodes INTEGER NOT NULL DEFAULT 0,
    nodes_by_type JSONB DEFAULT '{}',

    -- Edge statistics
    total_edges INTEGER NOT NULL DEFAULT 0,
    edges_by_type JSONB DEFAULT '{}',

    -- Connectivity
    avg_degree DECIMAL(6,2) DEFAULT 0,
    max_degree INTEGER DEFAULT 0,
    isolated_nodes INTEGER DEFAULT 0,

    -- Growth metrics
    nodes_last_24h INTEGER DEFAULT 0,
    edges_last_24h INTEGER DEFAULT 0,
    nodes_last_7d INTEGER DEFAULT 0,
    edges_last_7d INTEGER DEFAULT 0
);

CREATE INDEX idx_graph_topology_computed ON graph_topology_stats (computed_at DESC);

-- ============================================================================
-- GRAPH METRICS HOURLY AGGREGATION
-- ============================================================================

CREATE TABLE graph_metrics_hourly (
    bucket_hour TIMESTAMPTZ NOT NULL,
    event_type graph_event_type NOT NULL,
    event_count INTEGER NOT NULL DEFAULT 0,

    PRIMARY KEY (bucket_hour, event_type)
);

CREATE INDEX idx_graph_metrics_bucket ON graph_metrics_hourly (bucket_hour DESC);

-- ============================================================================
-- VIEWS
-- ============================================================================

-- Recent graph activity (last 24 hours)
CREATE VIEW v_recent_graph_events AS
SELECT
    event_id,
    event_type,
    node_id,
    node_name,
    node_type,
    edge_id,
    edge_type,
    source_node_id,
    target_node_id,
    source_node_name,
    target_node_name,
    confidence,
    occurred_at,
    triggered_by
FROM graph_events
WHERE occurred_at > NOW() - INTERVAL '24 hours'
ORDER BY occurred_at DESC;

-- Current graph summary
CREATE VIEW v_graph_summary AS
SELECT
    (SELECT COUNT(DISTINCT node_id) FROM graph_events WHERE node_id IS NOT NULL AND event_type IN ('NodeCreated', 'NodeUpdated')) AS total_nodes_touched,
    (SELECT COUNT(DISTINCT edge_id) FROM graph_events WHERE edge_id IS NOT NULL AND event_type IN ('EdgeCreated', 'EdgeUpdated')) AS total_edges_touched,
    (SELECT COUNT(*) FROM graph_events WHERE occurred_at > NOW() - INTERVAL '1 hour') AS events_last_hour,
    (SELECT COUNT(*) FROM graph_events WHERE occurred_at > NOW() - INTERVAL '24 hours') AS events_last_24h,
    (SELECT MAX(occurred_at) FROM graph_events) AS last_event_at;

-- Node activity summary
CREATE VIEW v_node_activity AS
SELECT
    node_id,
    node_name,
    node_type,
    COUNT(*) AS event_count,
    MAX(occurred_at) AS last_activity,
    MIN(occurred_at) AS first_seen
FROM graph_events
WHERE node_id IS NOT NULL
GROUP BY node_id, node_name, node_type
ORDER BY event_count DESC;

-- Edge activity summary
CREATE VIEW v_edge_activity AS
SELECT
    edge_id,
    edge_type,
    source_node_id,
    target_node_id,
    source_node_name,
    target_node_name,
    COUNT(*) AS event_count,
    MAX(confidence) AS max_confidence,
    MAX(occurred_at) AS last_activity
FROM graph_events
WHERE edge_id IS NOT NULL
GROUP BY edge_id, edge_type, source_node_id, target_node_id, source_node_name, target_node_name
ORDER BY event_count DESC;

-- ============================================================================
-- FUNCTIONS
-- ============================================================================

-- Function to log graph event
CREATE OR REPLACE FUNCTION log_graph_event(
    p_event_type graph_event_type,
    p_node_id UUID DEFAULT NULL,
    p_node_name VARCHAR(500) DEFAULT NULL,
    p_node_type VARCHAR(100) DEFAULT NULL,
    p_edge_id UUID DEFAULT NULL,
    p_edge_type VARCHAR(100) DEFAULT NULL,
    p_source_node_id UUID DEFAULT NULL,
    p_target_node_id UUID DEFAULT NULL,
    p_source_node_name VARCHAR(500) DEFAULT NULL,
    p_target_node_name VARCHAR(500) DEFAULT NULL,
    p_confidence DECIMAL(4,3) DEFAULT NULL,
    p_triggered_by VARCHAR(255) DEFAULT 'system',
    p_memory_id UUID DEFAULT NULL,
    p_metadata JSONB DEFAULT '{}'
) RETURNS UUID AS $$
DECLARE
    v_event_id UUID;
BEGIN
    INSERT INTO graph_events (
        event_type, node_id, node_name, node_type,
        edge_id, edge_type, source_node_id, target_node_id,
        source_node_name, target_node_name, confidence,
        triggered_by, memory_id, metadata
    ) VALUES (
        p_event_type, p_node_id, p_node_name, p_node_type,
        p_edge_id, p_edge_type, p_source_node_id, p_target_node_id,
        p_source_node_name, p_target_node_name, p_confidence,
        p_triggered_by, p_memory_id, p_metadata
    ) RETURNING event_id INTO v_event_id;

    RETURN v_event_id;
END;
$$ LANGUAGE plpgsql;

-- Trigger function to auto-update hourly metrics
CREATE OR REPLACE FUNCTION update_graph_metrics_hourly() RETURNS TRIGGER AS $$
BEGIN
    INSERT INTO graph_metrics_hourly (bucket_hour, event_type, event_count)
    VALUES (date_trunc('hour', NEW.occurred_at), NEW.event_type, 1)
    ON CONFLICT (bucket_hour, event_type)
    DO UPDATE SET event_count = graph_metrics_hourly.event_count + 1;

    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

-- Trigger to auto-update metrics on event insert
CREATE TRIGGER tr_graph_event_metrics
    AFTER INSERT ON graph_events
    FOR EACH ROW
    EXECUTE FUNCTION update_graph_metrics_hourly();

-- Function to compute topology stats
CREATE OR REPLACE FUNCTION compute_graph_topology_stats() RETURNS UUID AS $$
DECLARE
    v_stat_id UUID;
    v_total_nodes INTEGER;
    v_total_edges INTEGER;
    v_nodes_by_type JSONB;
    v_edges_by_type JSONB;
    v_avg_degree DECIMAL(6,2);
    v_max_degree INTEGER;
    v_isolated INTEGER;
    v_nodes_24h INTEGER;
    v_edges_24h INTEGER;
    v_nodes_7d INTEGER;
    v_edges_7d INTEGER;
BEGIN
    -- Count nodes from entities table
    SELECT COUNT(*) INTO v_total_nodes FROM entities;

    -- Count edges from entity_relationships table
    SELECT COUNT(*) INTO v_total_edges FROM entity_relationships;

    -- Nodes by type
    SELECT COALESCE(jsonb_object_agg(entity_type, cnt), '{}')
    INTO v_nodes_by_type
    FROM (SELECT entity_type, COUNT(*) as cnt FROM entities GROUP BY entity_type) t;

    -- Edges by type
    SELECT COALESCE(jsonb_object_agg(relationship_type, cnt), '{}')
    INTO v_edges_by_type
    FROM (SELECT relationship_type, COUNT(*) as cnt FROM entity_relationships GROUP BY relationship_type) t;

    -- Degree statistics
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
        COALESCE(AVG(degree), 0),
        COALESCE(MAX(degree), 0),
        COUNT(*) FILTER (WHERE degree = 0)
    INTO v_avg_degree, v_max_degree, v_isolated
    FROM degrees;

    -- Growth metrics (using graph_events)
    SELECT COUNT(*) INTO v_nodes_24h
    FROM graph_events
    WHERE event_type = 'NodeCreated' AND occurred_at > NOW() - INTERVAL '24 hours';

    SELECT COUNT(*) INTO v_edges_24h
    FROM graph_events
    WHERE event_type = 'EdgeCreated' AND occurred_at > NOW() - INTERVAL '24 hours';

    SELECT COUNT(*) INTO v_nodes_7d
    FROM graph_events
    WHERE event_type = 'NodeCreated' AND occurred_at > NOW() - INTERVAL '7 days';

    SELECT COUNT(*) INTO v_edges_7d
    FROM graph_events
    WHERE event_type = 'EdgeCreated' AND occurred_at > NOW() - INTERVAL '7 days';

    -- Insert new stats
    INSERT INTO graph_topology_stats (
        total_nodes, nodes_by_type, total_edges, edges_by_type,
        avg_degree, max_degree, isolated_nodes,
        nodes_last_24h, edges_last_24h, nodes_last_7d, edges_last_7d
    ) VALUES (
        v_total_nodes, v_nodes_by_type, v_total_edges, v_edges_by_type,
        v_avg_degree, v_max_degree, v_isolated,
        v_nodes_24h, v_edges_24h, v_nodes_7d, v_edges_7d
    ) RETURNING stat_id INTO v_stat_id;

    RETURN v_stat_id;
END;
$$ LANGUAGE plpgsql;

-- Function to get graph metrics for a time range
CREATE OR REPLACE FUNCTION get_graph_metrics(
    p_from TIMESTAMPTZ DEFAULT NOW() - INTERVAL '24 hours',
    p_to TIMESTAMPTZ DEFAULT NOW()
) RETURNS TABLE (
    event_type graph_event_type,
    total_count BIGINT
) AS $$
BEGIN
    RETURN QUERY
    SELECT
        e.event_type,
        COUNT(*)::BIGINT AS total_count
    FROM graph_events e
    WHERE e.occurred_at BETWEEN p_from AND p_to
    GROUP BY e.event_type
    ORDER BY total_count DESC;
END;
$$ LANGUAGE plpgsql;

-- ============================================================================
-- COMMENTS
-- ============================================================================

COMMENT ON TABLE graph_events IS 'Append-only log of all graph mutations including node and edge operations';
COMMENT ON TABLE graph_topology_stats IS 'Periodic snapshots of graph topology statistics';
COMMENT ON TABLE graph_metrics_hourly IS 'Pre-aggregated hourly metrics for fast dashboard queries';
COMMENT ON FUNCTION log_graph_event IS 'Helper function to log graph events with consistent format';
COMMENT ON FUNCTION compute_graph_topology_stats IS 'Computes current graph topology statistics and stores snapshot';
