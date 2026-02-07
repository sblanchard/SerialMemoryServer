-- Telemetry Schema for Reasoning, Security, and Graph Events
-- Run this after usage_metering_schema_v2.sql

-- ============================================
-- AGENT REASONING TRACES
-- ============================================

CREATE TABLE IF NOT EXISTS agent_traces (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id TEXT NOT NULL DEFAULT 'self',
    workspace_id TEXT NOT NULL DEFAULT 'default',
    trace_type TEXT NOT NULL,
    step_count INTEGER NOT NULL DEFAULT 0,
    duration_ms INTEGER,
    status TEXT NOT NULL DEFAULT 'running' CHECK (status IN ('running', 'completed', 'failed', 'cancelled')),
    metadata JSONB,
    created_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    completed_utc TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS idx_agent_traces_tenant ON agent_traces(tenant_id, workspace_id);
CREATE INDEX IF NOT EXISTS idx_agent_traces_created ON agent_traces(created_utc DESC);
CREATE INDEX IF NOT EXISTS idx_agent_traces_type ON agent_traces(trace_type);

CREATE TABLE IF NOT EXISTS agent_trace_steps (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    trace_id UUID NOT NULL REFERENCES agent_traces(id) ON DELETE CASCADE,
    step_number INTEGER NOT NULL,
    step_type TEXT NOT NULL CHECK (step_type IN ('plan_start', 'reasoning_step', 'tool_call', 'reflection', 'completion', 'error')),
    timestamp_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    duration_ms INTEGER,
    payload JSONB NOT NULL DEFAULT '{}',

    CONSTRAINT unique_trace_step UNIQUE (trace_id, step_number)
);

CREATE INDEX IF NOT EXISTS idx_agent_trace_steps_trace ON agent_trace_steps(trace_id);
CREATE INDEX IF NOT EXISTS idx_agent_trace_steps_type ON agent_trace_steps(step_type);

-- ============================================
-- SECURITY EVENTS
-- ============================================

CREATE TABLE IF NOT EXISTS security_events (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id TEXT NOT NULL DEFAULT 'self',
    workspace_id TEXT NOT NULL DEFAULT 'default',
    event_type TEXT NOT NULL CHECK (event_type IN (
        'integrity_check', 'contradiction_scan', 'loop_detection', 'hash_validation',
        'rls_check', 'access_denied', 'suspicious_activity', 'audit_anomaly'
    )),
    status TEXT NOT NULL CHECK (status IN ('pass', 'fail', 'warning', 'error')),
    target_type TEXT, -- e.g., 'memory', 'entity', 'relationship'
    target_id UUID,
    created_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    details JSONB NOT NULL DEFAULT '{}'
);

CREATE INDEX IF NOT EXISTS idx_security_events_tenant ON security_events(tenant_id, workspace_id);
CREATE INDEX IF NOT EXISTS idx_security_events_created ON security_events(created_utc DESC);
CREATE INDEX IF NOT EXISTS idx_security_events_type ON security_events(event_type);
CREATE INDEX IF NOT EXISTS idx_security_events_status ON security_events(status);

-- ============================================
-- GRAPH EVENTS (for tracking changes)
-- ============================================

CREATE TABLE IF NOT EXISTS graph_events (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id TEXT NOT NULL DEFAULT 'self',
    workspace_id TEXT NOT NULL DEFAULT 'default',
    event_type TEXT NOT NULL CHECK (event_type IN (
        'node_created', 'node_updated', 'node_deleted',
        'edge_created', 'edge_updated', 'edge_deleted',
        'batch_import', 'relationship_discovered',
        'node_merged', 'edge_strengthened'
    )),
    node_id UUID,
    edge_id UUID,
    node_type TEXT,
    edge_type TEXT,
    created_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    metadata JSONB
);

CREATE INDEX IF NOT EXISTS idx_graph_events_tenant ON graph_events(tenant_id, workspace_id);
CREATE INDEX IF NOT EXISTS idx_graph_events_created ON graph_events(created_utc DESC);
CREATE INDEX IF NOT EXISTS idx_graph_events_type ON graph_events(event_type);
CREATE INDEX IF NOT EXISTS idx_graph_events_node ON graph_events(node_id) WHERE node_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_graph_events_edge ON graph_events(edge_id) WHERE edge_id IS NOT NULL;

-- ============================================
-- HELPER FUNCTIONS
-- ============================================

-- Function to start a new agent trace
CREATE OR REPLACE FUNCTION start_agent_trace(
    p_tenant_id TEXT,
    p_workspace_id TEXT,
    p_trace_type TEXT,
    p_metadata JSONB DEFAULT NULL
)
RETURNS UUID AS $$
DECLARE
    v_trace_id UUID;
BEGIN
    INSERT INTO agent_traces (tenant_id, workspace_id, trace_type, metadata)
    VALUES (p_tenant_id, p_workspace_id, p_trace_type, p_metadata)
    RETURNING id INTO v_trace_id;

    RETURN v_trace_id;
END;
$$ LANGUAGE plpgsql;

-- Function to add a step to a trace
CREATE OR REPLACE FUNCTION add_trace_step(
    p_trace_id UUID,
    p_step_type TEXT,
    p_payload JSONB DEFAULT '{}',
    p_duration_ms INTEGER DEFAULT NULL
)
RETURNS UUID AS $$
DECLARE
    v_step_id UUID;
    v_step_number INTEGER;
BEGIN
    -- Get next step number
    SELECT COALESCE(MAX(step_number), 0) + 1 INTO v_step_number
    FROM agent_trace_steps
    WHERE trace_id = p_trace_id;

    INSERT INTO agent_trace_steps (trace_id, step_number, step_type, payload, duration_ms)
    VALUES (p_trace_id, v_step_number, p_step_type, p_payload, p_duration_ms)
    RETURNING id INTO v_step_id;

    -- Update step count in parent trace
    UPDATE agent_traces
    SET step_count = v_step_number
    WHERE id = p_trace_id;

    RETURN v_step_id;
END;
$$ LANGUAGE plpgsql;

-- Function to complete a trace
CREATE OR REPLACE FUNCTION complete_agent_trace(
    p_trace_id UUID,
    p_status TEXT DEFAULT 'completed',
    p_duration_ms INTEGER DEFAULT NULL
)
RETURNS VOID AS $$
BEGIN
    UPDATE agent_traces
    SET status = p_status,
        completed_utc = NOW(),
        duration_ms = p_duration_ms
    WHERE id = p_trace_id;
END;
$$ LANGUAGE plpgsql;

-- Function to log a security event
CREATE OR REPLACE FUNCTION log_security_event(
    p_tenant_id TEXT,
    p_workspace_id TEXT,
    p_event_type TEXT,
    p_status TEXT,
    p_target_type TEXT DEFAULT NULL,
    p_target_id UUID DEFAULT NULL,
    p_details JSONB DEFAULT '{}'
)
RETURNS UUID AS $$
DECLARE
    v_event_id UUID;
BEGIN
    INSERT INTO security_events (tenant_id, workspace_id, event_type, status, target_type, target_id, details)
    VALUES (p_tenant_id, p_workspace_id, p_event_type, p_status, p_target_type, p_target_id, p_details)
    RETURNING id INTO v_event_id;

    RETURN v_event_id;
END;
$$ LANGUAGE plpgsql;

-- Function to log a graph event
CREATE OR REPLACE FUNCTION log_graph_event(
    p_tenant_id TEXT,
    p_workspace_id TEXT,
    p_event_type TEXT,
    p_node_id UUID DEFAULT NULL,
    p_edge_id UUID DEFAULT NULL,
    p_node_type TEXT DEFAULT NULL,
    p_edge_type TEXT DEFAULT NULL,
    p_metadata JSONB DEFAULT NULL
)
RETURNS UUID AS $$
DECLARE
    v_event_id UUID;
BEGIN
    INSERT INTO graph_events (tenant_id, workspace_id, event_type, node_id, edge_id, node_type, edge_type, metadata)
    VALUES (p_tenant_id, p_workspace_id, p_event_type, p_node_id, p_edge_id, p_node_type, p_edge_type, p_metadata)
    RETURNING id INTO v_event_id;

    RETURN v_event_id;
END;
$$ LANGUAGE plpgsql;

-- ============================================
-- VIEWS FOR DASHBOARD QUERIES
-- ============================================

-- Reasoning stats view
CREATE OR REPLACE VIEW reasoning_stats AS
SELECT
    tenant_id,
    workspace_id,
    COUNT(*) as total_traces,
    AVG(step_count)::DECIMAL(10,1) as avg_steps,
    MAX(created_utc) as last_run_utc,
    COUNT(*) FILTER (WHERE status = 'completed') as completed_count,
    COUNT(*) FILTER (WHERE status = 'failed') as failed_count,
    AVG(duration_ms) FILTER (WHERE duration_ms IS NOT NULL) as avg_duration_ms
FROM agent_traces
WHERE created_utc > NOW() - INTERVAL '30 days'
GROUP BY tenant_id, workspace_id;

-- Security integrity view
CREATE OR REPLACE VIEW security_integrity AS
SELECT
    tenant_id,
    workspace_id,
    COUNT(*) as checked,
    COUNT(*) FILTER (WHERE status = 'fail') as failed,
    MAX(created_utc) as last_check_utc,
    COUNT(*) FILTER (WHERE event_type = 'integrity_check' AND status = 'pass') as integrity_pass,
    COUNT(*) FILTER (WHERE event_type = 'hash_validation' AND status = 'pass') as hash_valid
FROM security_events
WHERE created_utc > NOW() - INTERVAL '24 hours'
GROUP BY tenant_id, workspace_id;

-- Security anomalies view
CREATE OR REPLACE VIEW security_anomalies AS
SELECT
    tenant_id,
    workspace_id,
    COUNT(*) FILTER (WHERE event_type = 'contradiction_scan' AND status = 'fail') as contradictions,
    COUNT(*) FILTER (WHERE event_type = 'loop_detection' AND status = 'fail') as loops,
    COUNT(*) FILTER (WHERE event_type = 'suspicious_activity') as hallucinations
FROM security_events
WHERE created_utc > NOW() - INTERVAL '24 hours'
GROUP BY tenant_id, workspace_id;

-- Graph stats view
CREATE OR REPLACE VIEW graph_event_stats AS
SELECT
    tenant_id,
    workspace_id,
    COUNT(*) FILTER (WHERE event_type LIKE 'node_%') as node_events,
    COUNT(*) FILTER (WHERE event_type LIKE 'edge_%') as edge_events,
    COUNT(DISTINCT node_id) FILTER (WHERE node_id IS NOT NULL) as unique_nodes,
    COUNT(DISTINCT edge_id) FILTER (WHERE edge_id IS NOT NULL) as unique_edges,
    MAX(created_utc) as last_event_utc
FROM graph_events
WHERE created_utc > NOW() - INTERVAL '30 days'
GROUP BY tenant_id, workspace_id;
