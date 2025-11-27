-- Power User Schema
-- Direct memory manipulation, conflict tracking, mutation logging
-- NO GUARD RAILS. NO SAFE MODE.

-- Memory conflicts table
CREATE TABLE IF NOT EXISTS memory_conflicts (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    memory_a_id UUID NOT NULL REFERENCES memories(id) ON DELETE CASCADE,
    memory_b_id UUID NOT NULL REFERENCES memories(id) ON DELETE CASCADE,
    severity REAL NOT NULL DEFAULT 0.5,
    conflict_type VARCHAR(50) DEFAULT 'contradiction',
    reason TEXT,
    semantic_similarity REAL DEFAULT 0.0,
    detected_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    is_resolved BOOLEAN NOT NULL DEFAULT FALSE,
    resolved_by UUID,
    resolution TEXT,
    resolved_at TIMESTAMPTZ,
    CONSTRAINT unique_conflict UNIQUE (memory_a_id, memory_b_id)
);

-- Indexes for conflict queries
CREATE INDEX IF NOT EXISTS idx_conflicts_severity ON memory_conflicts(severity DESC);
CREATE INDEX IF NOT EXISTS idx_conflicts_unresolved ON memory_conflicts(is_resolved) WHERE NOT is_resolved;
CREATE INDEX IF NOT EXISTS idx_conflicts_memory_a ON memory_conflicts(memory_a_id);
CREATE INDEX IF NOT EXISTS idx_conflicts_memory_b ON memory_conflicts(memory_b_id);
CREATE INDEX IF NOT EXISTS idx_conflicts_detected ON memory_conflicts(detected_at DESC);

-- Graph events table (if not exists)
CREATE TABLE IF NOT EXISTS graph_events (
    event_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    sequence_number BIGSERIAL NOT NULL,
    event_type VARCHAR(50) NOT NULL,
    node_id UUID,
    edge_id UUID,
    source_node_id UUID,
    target_node_id UUID,
    node_name VARCHAR(500),
    node_type VARCHAR(100),
    edge_type VARCHAR(100),
    confidence REAL,
    occurred_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    previous_state JSONB,
    new_state JSONB,
    triggered_by VARCHAR(100),
    memory_id UUID,
    session_id UUID
);

CREATE INDEX IF NOT EXISTS idx_graph_events_seq ON graph_events(sequence_number);
CREATE INDEX IF NOT EXISTS idx_graph_events_type ON graph_events(event_type);
CREATE INDEX IF NOT EXISTS idx_graph_events_node ON graph_events(node_id);
CREATE INDEX IF NOT EXISTS idx_graph_events_edge ON graph_events(edge_id);
CREATE INDEX IF NOT EXISTS idx_graph_events_time ON graph_events(occurred_at DESC);

-- Memory events table (if not exists)
CREATE TABLE IF NOT EXISTS memory_events (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    sequence_number BIGSERIAL NOT NULL,
    event_type VARCHAR(50) NOT NULL,
    memory_id UUID REFERENCES memories(id) ON DELETE CASCADE,
    timestamp TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    payload JSONB NOT NULL DEFAULT '{}',
    actor_id VARCHAR(100),
    correlation_id VARCHAR(100)
);

CREATE INDEX IF NOT EXISTS idx_memory_events_seq ON memory_events(sequence_number);
CREATE INDEX IF NOT EXISTS idx_memory_events_type ON memory_events(event_type);
CREATE INDEX IF NOT EXISTS idx_memory_events_memory ON memory_events(memory_id);
CREATE INDEX IF NOT EXISTS idx_memory_events_time ON memory_events(timestamp DESC);

-- Power user audit log (tracks all dangerous operations)
CREATE TABLE IF NOT EXISTS power_user_audit (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    operation VARCHAR(100) NOT NULL,
    target_id UUID,
    target_type VARCHAR(50),
    actor_id VARCHAR(100),
    details JSONB NOT NULL DEFAULT '{}',
    occurred_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    ip_address VARCHAR(45),
    user_agent TEXT
);

CREATE INDEX IF NOT EXISTS idx_power_audit_time ON power_user_audit(occurred_at DESC);
CREATE INDEX IF NOT EXISTS idx_power_audit_op ON power_user_audit(operation);
CREATE INDEX IF NOT EXISTS idx_power_audit_target ON power_user_audit(target_id);

-- Function to log power user operations
CREATE OR REPLACE FUNCTION log_power_user_operation(
    p_operation VARCHAR(100),
    p_target_id UUID,
    p_target_type VARCHAR(50),
    p_actor_id VARCHAR(100),
    p_details JSONB
)
RETURNS UUID AS $$
DECLARE
    v_id UUID;
BEGIN
    INSERT INTO power_user_audit (operation, target_id, target_type, actor_id, details)
    VALUES (p_operation, p_target_id, p_target_type, p_actor_id, p_details)
    RETURNING id INTO v_id;
    RETURN v_id;
END;
$$ LANGUAGE plpgsql;

-- Function to detect semantic contradictions
CREATE OR REPLACE FUNCTION detect_contradictions(
    p_similarity_threshold REAL DEFAULT 0.85,
    p_batch_size INT DEFAULT 100
)
RETURNS TABLE (
    memory_a UUID,
    memory_b UUID,
    similarity REAL
) AS $$
BEGIN
    RETURN QUERY
    WITH recent_memories AS (
        SELECT id, embedding, content
        FROM memories
        WHERE is_active = TRUE AND embedding IS NOT NULL
        ORDER BY created_at DESC
        LIMIT p_batch_size
    )
    SELECT
        m1.id AS memory_a,
        m2.id AS memory_b,
        1 - (m1.embedding <=> m2.embedding) AS similarity
    FROM recent_memories m1
    CROSS JOIN recent_memories m2
    WHERE m1.id < m2.id
        AND 1 - (m1.embedding <=> m2.embedding) >= p_similarity_threshold
    ORDER BY similarity DESC;
END;
$$ LANGUAGE plpgsql;

-- Function to get conflict cluster for a memory
CREATE OR REPLACE FUNCTION get_conflict_cluster(p_memory_id UUID, p_max_depth INT DEFAULT 3)
RETURNS TABLE (
    memory_id UUID,
    depth INT,
    path UUID[]
) AS $$
WITH RECURSIVE cluster AS (
    SELECT
        p_memory_id AS memory_id,
        0 AS depth,
        ARRAY[p_memory_id] AS path

    UNION ALL

    SELECT
        CASE
            WHEN c.memory_a_id = cl.memory_id THEN c.memory_b_id
            ELSE c.memory_a_id
        END AS memory_id,
        cl.depth + 1 AS depth,
        cl.path || CASE
            WHEN c.memory_a_id = cl.memory_id THEN c.memory_b_id
            ELSE c.memory_a_id
        END AS path
    FROM cluster cl
    JOIN memory_conflicts c ON (c.memory_a_id = cl.memory_id OR c.memory_b_id = cl.memory_id)
    WHERE cl.depth < p_max_depth
        AND NOT c.is_resolved
        AND NOT (CASE WHEN c.memory_a_id = cl.memory_id THEN c.memory_b_id ELSE c.memory_a_id END) = ANY(cl.path)
)
SELECT DISTINCT ON (memory_id) memory_id, depth, path
FROM cluster
ORDER BY memory_id, depth;
$$ LANGUAGE sql;

-- View for conflict statistics
CREATE OR REPLACE VIEW conflict_statistics AS
SELECT
    COUNT(*) AS total_conflicts,
    COUNT(*) FILTER (WHERE NOT is_resolved) AS unresolved_conflicts,
    COUNT(*) FILTER (WHERE severity >= 0.9) AS critical_conflicts,
    COUNT(*) FILTER (WHERE severity >= 0.7 AND severity < 0.9) AS high_severity,
    COUNT(*) FILTER (WHERE severity >= 0.4 AND severity < 0.7) AS medium_severity,
    COUNT(*) FILTER (WHERE severity < 0.4) AS low_severity,
    AVG(severity) AS avg_severity,
    MAX(severity) AS max_severity,
    MIN(detected_at) AS oldest_conflict,
    MAX(detected_at) AS newest_conflict
FROM memory_conflicts;

-- View for mutation statistics
CREATE OR REPLACE VIEW mutation_statistics AS
SELECT
    COUNT(*) AS total_mutations,
    COUNT(*) FILTER (WHERE event_type LIKE '%Created%') AS creates,
    COUNT(*) FILTER (WHERE event_type LIKE '%Updated%') AS updates,
    COUNT(*) FILTER (WHERE event_type LIKE '%Deleted%') AS deletes,
    COUNT(*) FILTER (WHERE occurred_at > NOW() - INTERVAL '1 hour') AS last_hour,
    COUNT(*) FILTER (WHERE occurred_at > NOW() - INTERVAL '24 hours') AS last_24h,
    MAX(occurred_at) AS last_mutation
FROM graph_events;
