-- Security Events Schema for SerialMemory
-- Real-time integrity monitoring and anomaly detection

-- ============================================================================
-- ENUMS
-- ============================================================================

CREATE TYPE security_event_type AS ENUM (
    -- Hash validation events
    'HashValidationPass',
    'HashValidationFail',
    'HashMismatch',

    -- Contradiction detection events
    'ContradictionDetected',
    'ContradictionResolved',
    'ContradictionIgnored',

    -- Loop detection events
    'CausalLoopDetected',
    'CausalLoopBroken',

    -- Integrity events
    'IntegrityCheckStarted',
    'IntegrityCheckCompleted',
    'IntegrityViolation',

    -- Anomaly events
    'AnomalyDetected',
    'HallucinationSuspected',

    -- Memory lifecycle events
    'MemoryReadValidated',
    'MemoryWriteValidated',
    'MemoryCorruptionDetected'
);

CREATE TYPE security_severity AS ENUM (
    'Info',
    'Warning',
    'Error',
    'Critical'
);

-- ============================================================================
-- SECURITY EVENTS TABLE (Append-Only)
-- ============================================================================

CREATE TABLE security_events (
    event_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    event_type security_event_type NOT NULL,
    severity security_severity NOT NULL DEFAULT 'Info',

    -- What was being checked
    memory_id UUID,                                  -- Related memory (if applicable)
    entity_id UUID,                                  -- Related entity (if applicable)

    -- Event details
    message TEXT NOT NULL,
    details JSONB DEFAULT '{}',                      -- Structured event data

    -- Hash validation specific
    expected_hash CHAR(64),
    actual_hash CHAR(64),

    -- Contradiction specific
    contradicting_memory_ids UUID[],
    contradiction_score DECIMAL(4,3),

    -- Loop detection specific
    loop_path UUID[],                                -- Memory IDs forming the loop
    loop_depth INTEGER,

    -- Timestamps and audit
    occurred_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    detected_by VARCHAR(255) DEFAULT 'system',       -- Component that detected
    session_id UUID,

    -- For aggregation
    scan_batch_id UUID                               -- Groups events from same scan
);

-- Indexes for querying
CREATE INDEX idx_security_events_type ON security_events (event_type);
CREATE INDEX idx_security_events_severity ON security_events (severity);
CREATE INDEX idx_security_events_memory ON security_events (memory_id) WHERE memory_id IS NOT NULL;
CREATE INDEX idx_security_events_occurred ON security_events (occurred_at DESC);
CREATE INDEX idx_security_events_batch ON security_events (scan_batch_id) WHERE scan_batch_id IS NOT NULL;

-- Partial indexes for fast anomaly queries
CREATE INDEX idx_security_events_failures ON security_events (occurred_at DESC)
    WHERE event_type IN ('HashValidationFail', 'HashMismatch', 'IntegrityViolation', 'MemoryCorruptionDetected');
CREATE INDEX idx_security_events_contradictions ON security_events (occurred_at DESC)
    WHERE event_type = 'ContradictionDetected';
CREATE INDEX idx_security_events_loops ON security_events (occurred_at DESC)
    WHERE event_type = 'CausalLoopDetected';

-- ============================================================================
-- SECURITY SCAN RUNS TABLE
-- ============================================================================

CREATE TABLE security_scans (
    scan_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    scan_type VARCHAR(50) NOT NULL,                  -- 'full', 'hash_validation', 'contradiction', 'loop'
    started_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    completed_at TIMESTAMPTZ,

    -- Scope
    memories_scanned INTEGER DEFAULT 0,
    entities_scanned INTEGER DEFAULT 0,
    relationships_scanned INTEGER DEFAULT 0,

    -- Results
    events_generated INTEGER DEFAULT 0,
    issues_found INTEGER DEFAULT 0,
    critical_issues INTEGER DEFAULT 0,

    -- Status
    status VARCHAR(20) NOT NULL DEFAULT 'running',   -- 'running', 'completed', 'failed', 'cancelled'
    error_message TEXT,

    -- Metadata
    triggered_by VARCHAR(255) DEFAULT 'scheduled',
    metadata JSONB DEFAULT '{}'
);

CREATE INDEX idx_security_scans_started ON security_scans (started_at DESC);
CREATE INDEX idx_security_scans_type ON security_scans (scan_type);
CREATE INDEX idx_security_scans_status ON security_scans (status);

-- ============================================================================
-- SECURITY METRICS AGGREGATION (Materialized for fast queries)
-- ============================================================================

CREATE TABLE security_metrics_hourly (
    bucket_hour TIMESTAMPTZ NOT NULL,
    event_type security_event_type NOT NULL,
    event_count INTEGER NOT NULL DEFAULT 0,

    PRIMARY KEY (bucket_hour, event_type)
);

CREATE INDEX idx_security_metrics_bucket ON security_metrics_hourly (bucket_hour DESC);

-- ============================================================================
-- VIEWS FOR QUICK ACCESS
-- ============================================================================

-- Recent security events (last 24 hours)
CREATE VIEW v_recent_security_events AS
SELECT
    event_id,
    event_type,
    severity,
    memory_id,
    message,
    occurred_at,
    detected_by
FROM security_events
WHERE occurred_at > NOW() - INTERVAL '24 hours'
ORDER BY occurred_at DESC;

-- Current security status summary
CREATE VIEW v_security_status AS
SELECT
    (SELECT COUNT(*) FROM security_events WHERE occurred_at > NOW() - INTERVAL '1 hour') AS events_last_hour,
    (SELECT COUNT(*) FROM security_events WHERE event_type IN ('HashValidationFail', 'HashMismatch', 'IntegrityViolation') AND occurred_at > NOW() - INTERVAL '24 hours') AS hash_failures_24h,
    (SELECT COUNT(*) FROM security_events WHERE event_type = 'ContradictionDetected' AND occurred_at > NOW() - INTERVAL '24 hours') AS contradictions_24h,
    (SELECT COUNT(*) FROM security_events WHERE event_type = 'CausalLoopDetected' AND occurred_at > NOW() - INTERVAL '24 hours') AS loops_24h,
    (SELECT MAX(occurred_at) FROM security_events) AS last_event_at,
    (SELECT MAX(started_at) FROM security_scans WHERE status = 'completed') AS last_scan_at;

-- Active issues requiring attention
CREATE VIEW v_active_security_issues AS
SELECT
    event_id,
    event_type,
    severity,
    memory_id,
    message,
    details,
    occurred_at
FROM security_events
WHERE severity IN ('Error', 'Critical')
    AND occurred_at > NOW() - INTERVAL '7 days'
ORDER BY
    CASE severity
        WHEN 'Critical' THEN 1
        WHEN 'Error' THEN 2
        ELSE 3
    END,
    occurred_at DESC;

-- ============================================================================
-- FUNCTIONS
-- ============================================================================

-- Function to log security event
CREATE OR REPLACE FUNCTION log_security_event(
    p_event_type security_event_type,
    p_severity security_severity,
    p_message TEXT,
    p_memory_id UUID DEFAULT NULL,
    p_details JSONB DEFAULT '{}',
    p_detected_by VARCHAR(255) DEFAULT 'system',
    p_scan_batch_id UUID DEFAULT NULL
) RETURNS UUID AS $$
DECLARE
    v_event_id UUID;
BEGIN
    INSERT INTO security_events (
        event_type, severity, message, memory_id, details, detected_by, scan_batch_id
    ) VALUES (
        p_event_type, p_severity, p_message, p_memory_id, p_details, p_detected_by, p_scan_batch_id
    ) RETURNING event_id INTO v_event_id;

    RETURN v_event_id;
END;
$$ LANGUAGE plpgsql;

-- Function to update hourly metrics
CREATE OR REPLACE FUNCTION update_security_metrics_hourly() RETURNS TRIGGER AS $$
BEGIN
    INSERT INTO security_metrics_hourly (bucket_hour, event_type, event_count)
    VALUES (date_trunc('hour', NEW.occurred_at), NEW.event_type, 1)
    ON CONFLICT (bucket_hour, event_type)
    DO UPDATE SET event_count = security_metrics_hourly.event_count + 1;

    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

-- Trigger to auto-update metrics on event insert
CREATE TRIGGER tr_security_event_metrics
    AFTER INSERT ON security_events
    FOR EACH ROW
    EXECUTE FUNCTION update_security_metrics_hourly();

-- Function to get security metrics for a time range
CREATE OR REPLACE FUNCTION get_security_metrics(
    p_from TIMESTAMPTZ DEFAULT NOW() - INTERVAL '24 hours',
    p_to TIMESTAMPTZ DEFAULT NOW()
) RETURNS TABLE (
    event_type security_event_type,
    total_count BIGINT,
    critical_count BIGINT,
    error_count BIGINT,
    warning_count BIGINT
) AS $$
BEGIN
    RETURN QUERY
    SELECT
        e.event_type,
        COUNT(*)::BIGINT AS total_count,
        COUNT(*) FILTER (WHERE e.severity = 'Critical')::BIGINT AS critical_count,
        COUNT(*) FILTER (WHERE e.severity = 'Error')::BIGINT AS error_count,
        COUNT(*) FILTER (WHERE e.severity = 'Warning')::BIGINT AS warning_count
    FROM security_events e
    WHERE e.occurred_at BETWEEN p_from AND p_to
    GROUP BY e.event_type
    ORDER BY total_count DESC;
END;
$$ LANGUAGE plpgsql;

-- ============================================================================
-- COMMENTS
-- ============================================================================

COMMENT ON TABLE security_events IS 'Append-only log of all security-related events including hash validation, contradictions, and loop detection';
COMMENT ON TABLE security_scans IS 'Tracks security scan runs and their results';
COMMENT ON TABLE security_metrics_hourly IS 'Pre-aggregated hourly metrics for fast dashboard queries';
COMMENT ON FUNCTION log_security_event IS 'Helper function to log security events with consistent format';
COMMENT ON FUNCTION get_security_metrics IS 'Retrieves aggregated security metrics for a time range';
