-- ============================================================================
-- SerialMemory SaaS: Abuse Protection Schema
-- Rate limiting, payload validation, and cost protection tables
-- ============================================================================

-- Rate limit hits tracking (for monitoring and abuse detection)
CREATE TABLE IF NOT EXISTS rate_limit_hits (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    operation VARCHAR(100) NOT NULL,
    hit_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    request_count INT NOT NULL DEFAULT 1,
    window_type VARCHAR(20) NOT NULL DEFAULT 'minute',  -- 'second', 'minute', 'hour', 'day'
    limit_exceeded INT NOT NULL DEFAULT 0,
    client_ip INET,
    user_agent TEXT,
    metadata JSONB DEFAULT '{}'
);

CREATE INDEX idx_rate_limit_hits_tenant ON rate_limit_hits(tenant_id);
CREATE INDEX idx_rate_limit_hits_time ON rate_limit_hits(hit_at DESC);
CREATE INDEX idx_rate_limit_hits_operation ON rate_limit_hits(operation);
CREATE INDEX idx_rate_limit_hits_tenant_time ON rate_limit_hits(tenant_id, hit_at DESC);

-- Enable RLS
ALTER TABLE rate_limit_hits ENABLE ROW LEVEL SECURITY;

CREATE POLICY rate_limit_hits_tenant_isolation ON rate_limit_hits
    FOR ALL
    USING (tenant_id = current_setting('app.current_tenant_id')::uuid);

-- ============================================================================
-- Emergency cutoffs for cost protection
-- ============================================================================

CREATE TABLE IF NOT EXISTS emergency_cutoffs (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    reason TEXT NOT NULL,
    triggered_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    triggered_by VARCHAR(100),  -- 'system', 'admin:user@example.com', etc.
    ends_at TIMESTAMPTZ,  -- NULL = indefinite
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    lifted_at TIMESTAMPTZ,
    lift_reason TEXT,
    lifted_by VARCHAR(100),
    metadata JSONB DEFAULT '{}'
);

CREATE INDEX idx_emergency_cutoffs_tenant ON emergency_cutoffs(tenant_id);
CREATE INDEX idx_emergency_cutoffs_active ON emergency_cutoffs(tenant_id, is_active) WHERE is_active = TRUE;
CREATE UNIQUE INDEX idx_emergency_cutoffs_tenant_active ON emergency_cutoffs(tenant_id) WHERE is_active = TRUE;

-- Enable RLS
ALTER TABLE emergency_cutoffs ENABLE ROW LEVEL SECURITY;

CREATE POLICY emergency_cutoffs_tenant_isolation ON emergency_cutoffs
    FOR ALL
    USING (tenant_id = current_setting('app.current_tenant_id')::uuid);

-- ============================================================================
-- Cost records for tracking consumption
-- ============================================================================

CREATE TABLE IF NOT EXISTS cost_records (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    operation VARCHAR(100) NOT NULL,
    cost DECIMAL(10, 4) NOT NULL,
    recorded_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    billing_cycle_id UUID REFERENCES billing_cycles(id),
    metadata JSONB DEFAULT '{}'
);

CREATE INDEX idx_cost_records_tenant ON cost_records(tenant_id);
CREATE INDEX idx_cost_records_time ON cost_records(recorded_at DESC);
CREATE INDEX idx_cost_records_tenant_time ON cost_records(tenant_id, recorded_at DESC);
CREATE INDEX idx_cost_records_billing_cycle ON cost_records(billing_cycle_id);
CREATE INDEX idx_cost_records_operation ON cost_records(operation);

-- Enable RLS
ALTER TABLE cost_records ENABLE ROW LEVEL SECURITY;

CREATE POLICY cost_records_tenant_isolation ON cost_records
    FOR ALL
    USING (tenant_id = current_setting('app.current_tenant_id')::uuid);

-- ============================================================================
-- Budget alert thresholds per tenant
-- ============================================================================

CREATE TABLE IF NOT EXISTS budget_alert_thresholds (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    percentage DECIMAL(5, 2) NOT NULL,  -- 50.00, 75.00, 90.00, 100.00
    alert_type VARCHAR(50) NOT NULL,     -- 'email', 'webhook', 'both'
    enabled BOOLEAN NOT NULL DEFAULT TRUE,
    notification_email VARCHAR(255),
    webhook_url TEXT,
    last_triggered_at TIMESTAMPTZ,
    trigger_count INT NOT NULL DEFAULT 0,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT unique_tenant_percentage UNIQUE (tenant_id, percentage)
);

CREATE INDEX idx_budget_alert_thresholds_tenant ON budget_alert_thresholds(tenant_id);
CREATE INDEX idx_budget_alert_thresholds_enabled ON budget_alert_thresholds(tenant_id, enabled) WHERE enabled = TRUE;

-- Enable RLS
ALTER TABLE budget_alert_thresholds ENABLE ROW LEVEL SECURITY;

CREATE POLICY budget_alert_thresholds_tenant_isolation ON budget_alert_thresholds
    FOR ALL
    USING (tenant_id = current_setting('app.current_tenant_id')::uuid);

-- ============================================================================
-- Payload violation logs
-- ============================================================================

CREATE TABLE IF NOT EXISTS payload_violations (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    violation_type VARCHAR(50) NOT NULL,  -- 'content_too_large', 'prohibited_pattern', etc.
    violation_code VARCHAR(50) NOT NULL,
    message TEXT NOT NULL,
    actual_value TEXT,
    max_allowed TEXT,
    endpoint VARCHAR(200),
    client_ip INET,
    occurred_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    metadata JSONB DEFAULT '{}'
);

CREATE INDEX idx_payload_violations_tenant ON payload_violations(tenant_id);
CREATE INDEX idx_payload_violations_time ON payload_violations(occurred_at DESC);
CREATE INDEX idx_payload_violations_type ON payload_violations(violation_type);
CREATE INDEX idx_payload_violations_tenant_time ON payload_violations(tenant_id, occurred_at DESC);

-- Enable RLS
ALTER TABLE payload_violations ENABLE ROW LEVEL SECURITY;

CREATE POLICY payload_violations_tenant_isolation ON payload_violations
    FOR ALL
    USING (tenant_id = current_setting('app.current_tenant_id')::uuid);

-- ============================================================================
-- Abuse detection patterns
-- ============================================================================

CREATE TABLE IF NOT EXISTS abuse_patterns (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    pattern_type VARCHAR(50) NOT NULL,  -- 'rate_spike', 'cost_spike', 'repeated_violations'
    severity VARCHAR(20) NOT NULL,       -- 'info', 'warning', 'critical', 'emergency'
    description TEXT NOT NULL,
    detected_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    auto_action_taken VARCHAR(50),       -- 'none', 'rate_limited', 'cutoff'
    resolved_at TIMESTAMPTZ,
    resolved_by VARCHAR(100),
    resolution_notes TEXT,
    metadata JSONB DEFAULT '{}'
);

CREATE INDEX idx_abuse_patterns_tenant ON abuse_patterns(tenant_id);
CREATE INDEX idx_abuse_patterns_time ON abuse_patterns(detected_at DESC);
CREATE INDEX idx_abuse_patterns_severity ON abuse_patterns(severity);
CREATE INDEX idx_abuse_patterns_unresolved ON abuse_patterns(tenant_id) WHERE resolved_at IS NULL;

-- Enable RLS
ALTER TABLE abuse_patterns ENABLE ROW LEVEL SECURITY;

CREATE POLICY abuse_patterns_tenant_isolation ON abuse_patterns
    FOR ALL
    USING (tenant_id = current_setting('app.current_tenant_id')::uuid);

-- ============================================================================
-- Utility Functions
-- ============================================================================

-- Get rate limit statistics for a tenant
CREATE OR REPLACE FUNCTION get_rate_limit_stats(
    p_tenant_id UUID,
    p_hours INT DEFAULT 24
)
RETURNS TABLE (
    operation VARCHAR(100),
    total_hits BIGINT,
    exceeded_count BIGINT,
    avg_per_hour NUMERIC,
    max_in_window INT
) AS $$
BEGIN
    RETURN QUERY
    SELECT
        rlh.operation,
        COUNT(*)::BIGINT AS total_hits,
        SUM(CASE WHEN rlh.limit_exceeded > 0 THEN 1 ELSE 0 END)::BIGINT AS exceeded_count,
        ROUND(COUNT(*)::NUMERIC / p_hours, 2) AS avg_per_hour,
        MAX(rlh.request_count) AS max_in_window
    FROM rate_limit_hits rlh
    WHERE rlh.tenant_id = p_tenant_id
      AND rlh.hit_at >= NOW() - (p_hours || ' hours')::INTERVAL
    GROUP BY rlh.operation
    ORDER BY total_hits DESC;
END;
$$ LANGUAGE plpgsql;

-- Get cost summary for a tenant
CREATE OR REPLACE FUNCTION get_cost_summary(
    p_tenant_id UUID,
    p_days INT DEFAULT 30
)
RETURNS TABLE (
    operation VARCHAR(100),
    total_cost DECIMAL(12, 4),
    record_count BIGINT,
    avg_cost DECIMAL(10, 4),
    max_cost DECIMAL(10, 4)
) AS $$
BEGIN
    RETURN QUERY
    SELECT
        cr.operation,
        SUM(cr.cost) AS total_cost,
        COUNT(*)::BIGINT AS record_count,
        ROUND(AVG(cr.cost), 4) AS avg_cost,
        MAX(cr.cost) AS max_cost
    FROM cost_records cr
    WHERE cr.tenant_id = p_tenant_id
      AND cr.recorded_at >= NOW() - (p_days || ' days')::INTERVAL
    GROUP BY cr.operation
    ORDER BY total_cost DESC;
END;
$$ LANGUAGE plpgsql;

-- Detect abuse patterns
CREATE OR REPLACE FUNCTION detect_abuse_patterns(
    p_tenant_id UUID
)
RETURNS TABLE (
    pattern_type VARCHAR(50),
    severity VARCHAR(20),
    description TEXT
) AS $$
DECLARE
    v_rate_spike_count INT;
    v_violation_count INT;
    v_cost_spike DECIMAL;
BEGIN
    -- Check for rate limit spike (>10 exceeded limits in last hour)
    SELECT COUNT(*) INTO v_rate_spike_count
    FROM rate_limit_hits
    WHERE tenant_id = p_tenant_id
      AND hit_at >= NOW() - INTERVAL '1 hour'
      AND limit_exceeded > 0;

    IF v_rate_spike_count > 10 THEN
        pattern_type := 'rate_spike';
        severity := CASE
            WHEN v_rate_spike_count > 50 THEN 'emergency'
            WHEN v_rate_spike_count > 25 THEN 'critical'
            ELSE 'warning'
        END;
        description := format('Rate limit exceeded %s times in the last hour', v_rate_spike_count);
        RETURN NEXT;
    END IF;

    -- Check for payload violations (>5 in last hour)
    SELECT COUNT(*) INTO v_violation_count
    FROM payload_violations
    WHERE tenant_id = p_tenant_id
      AND occurred_at >= NOW() - INTERVAL '1 hour';

    IF v_violation_count > 5 THEN
        pattern_type := 'repeated_violations';
        severity := CASE
            WHEN v_violation_count > 20 THEN 'critical'
            ELSE 'warning'
        END;
        description := format('%s payload violations in the last hour', v_violation_count);
        RETURN NEXT;
    END IF;

    -- Check for cost spike (>2x normal daily average)
    SELECT COALESCE(SUM(cost), 0) INTO v_cost_spike
    FROM cost_records
    WHERE tenant_id = p_tenant_id
      AND recorded_at >= CURRENT_DATE;

    -- Compare with 7-day average
    DECLARE
        v_avg_daily DECIMAL;
    BEGIN
        SELECT COALESCE(AVG(daily_cost), 0) INTO v_avg_daily
        FROM (
            SELECT DATE(recorded_at) AS day, SUM(cost) AS daily_cost
            FROM cost_records
            WHERE tenant_id = p_tenant_id
              AND recorded_at >= CURRENT_DATE - INTERVAL '7 days'
              AND recorded_at < CURRENT_DATE
            GROUP BY DATE(recorded_at)
        ) daily;

        IF v_avg_daily > 0 AND v_cost_spike > v_avg_daily * 2 THEN
            pattern_type := 'cost_spike';
            severity := CASE
                WHEN v_cost_spike > v_avg_daily * 5 THEN 'emergency'
                WHEN v_cost_spike > v_avg_daily * 3 THEN 'critical'
                ELSE 'warning'
            END;
            description := format('Daily cost %s is %sx the 7-day average %s',
                v_cost_spike, ROUND(v_cost_spike / v_avg_daily, 1), v_avg_daily);
            RETURN NEXT;
        END IF;
    END;
END;
$$ LANGUAGE plpgsql;

-- Cleanup old records
CREATE OR REPLACE FUNCTION cleanup_abuse_protection_records(
    p_rate_limit_days INT DEFAULT 7,
    p_cost_records_days INT DEFAULT 90,
    p_violations_days INT DEFAULT 30
)
RETURNS TABLE (
    table_name TEXT,
    deleted_count BIGINT
) AS $$
DECLARE
    v_count BIGINT;
BEGIN
    -- Cleanup rate limit hits
    DELETE FROM rate_limit_hits
    WHERE hit_at < NOW() - (p_rate_limit_days || ' days')::INTERVAL;
    GET DIAGNOSTICS v_count = ROW_COUNT;
    table_name := 'rate_limit_hits';
    deleted_count := v_count;
    RETURN NEXT;

    -- Cleanup cost records (keep longer for billing)
    DELETE FROM cost_records
    WHERE recorded_at < NOW() - (p_cost_records_days || ' days')::INTERVAL;
    GET DIAGNOSTICS v_count = ROW_COUNT;
    table_name := 'cost_records';
    deleted_count := v_count;
    RETURN NEXT;

    -- Cleanup payload violations
    DELETE FROM payload_violations
    WHERE occurred_at < NOW() - (p_violations_days || ' days')::INTERVAL;
    GET DIAGNOSTICS v_count = ROW_COUNT;
    table_name := 'payload_violations';
    deleted_count := v_count;
    RETURN NEXT;

    -- Cleanup resolved abuse patterns older than 90 days
    DELETE FROM abuse_patterns
    WHERE resolved_at IS NOT NULL
      AND resolved_at < NOW() - INTERVAL '90 days';
    GET DIAGNOSTICS v_count = ROW_COUNT;
    table_name := 'abuse_patterns';
    deleted_count := v_count;
    RETURN NEXT;
END;
$$ LANGUAGE plpgsql;

-- Comments
COMMENT ON TABLE rate_limit_hits IS 'Tracks rate limit hits per tenant for monitoring and abuse detection';
COMMENT ON TABLE emergency_cutoffs IS 'Emergency service cutoffs for cost protection';
COMMENT ON TABLE cost_records IS 'Cost consumption records per operation';
COMMENT ON TABLE budget_alert_thresholds IS 'Configurable budget alert thresholds per tenant';
COMMENT ON TABLE payload_violations IS 'Log of payload validation violations';
COMMENT ON TABLE abuse_patterns IS 'Detected abuse patterns and their resolution';
