-- Usage Metering Schema Migration
-- Adds tables for tracking API usage, daily rollups, billing cycles, and tenant plans

-- Create usage event types enum
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_type WHERE typname = 'usage_event_type') THEN
        CREATE TYPE usage_event_type AS ENUM (
            'memory_ingest',
            'memory_search',
            'memory_multi_hop_search',
            'memory_update',
            'memory_delete',
            'memory_merge',
            'memory_split',
            'memory_decay',
            'memory_reinforce',
            'memory_expire',
            'crawl_relationships',
            'get_graph_statistics',
            'export_workspace',
            'export_memories',
            'export_graph',
            'export_user_profile',
            'reembed_memories',
            'get_model_info',
            'memory_about_user',
            'set_user_persona',
            'initialise_session',
            'end_session',
            'instantiate_context',
            'get_integrations',
            'import_from_core',
            'memory_trace',
            'memory_lineage',
            'memory_explain',
            'memory_conflicts',
            'detect_contradictions',
            'detect_hallucinations',
            'verify_memory_integrity',
            'scan_loops',
            'engineering_analyze',
            'engineering_visualize',
            'engineering_reason',
            'llm_chat',
            'embedding',
            'entity_extraction'
        );
    END IF;
END
$$;

-- Tenant plans (defines credit limits and quotas)
CREATE TABLE IF NOT EXISTS tenant_plans (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    plan_name TEXT NOT NULL UNIQUE,
    display_name TEXT NOT NULL,
    credits_per_cycle DECIMAL(12, 2) NOT NULL DEFAULT 1000.00,
    cycle_days INTEGER NOT NULL DEFAULT 30,
    max_memories INTEGER,
    max_entities INTEGER,
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    metadata JSONB,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Billing cycles (tracks credit usage periods)
CREATE TABLE IF NOT EXISTS billing_cycles (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id TEXT NOT NULL,
    workspace_id TEXT NOT NULL,
    plan_id UUID REFERENCES tenant_plans(id),
    cycle_start TIMESTAMPTZ NOT NULL,
    cycle_end TIMESTAMPTZ NOT NULL,
    credits_allocated DECIMAL(12, 2) NOT NULL DEFAULT 0,
    credits_used DECIMAL(12, 2) NOT NULL DEFAULT 0,
    credits_remaining DECIMAL(12, 2) GENERATED ALWAYS AS (credits_allocated - credits_used) STORED,
    is_current BOOLEAN NOT NULL DEFAULT TRUE,
    closed_at TIMESTAMPTZ,
    metadata JSONB,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Partial unique index for current billing cycle per tenant/workspace
CREATE UNIQUE INDEX IF NOT EXISTS idx_billing_cycles_current_unique
    ON billing_cycles(tenant_id, workspace_id) WHERE is_current = TRUE;

-- Usage events (individual API call records)
CREATE TABLE IF NOT EXISTS usage_events (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id TEXT NOT NULL,
    workspace_id TEXT NOT NULL,
    billing_cycle_id UUID REFERENCES billing_cycles(id),
    event_type usage_event_type NOT NULL,
    credits_consumed DECIMAL(8, 4) NOT NULL,
    event_timestamp TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    memory_id UUID,
    user_id TEXT,
    session_id UUID,
    latency_ms INTEGER,
    success BOOLEAN NOT NULL DEFAULT TRUE,
    error_message TEXT,
    metadata JSONB
);

-- Partitioning for usage_events by timestamp (monthly partitions)
-- Note: In production, you'd use pg_partman or declarative partitioning
CREATE INDEX IF NOT EXISTS idx_usage_events_timestamp ON usage_events(event_timestamp DESC);
CREATE INDEX IF NOT EXISTS idx_usage_events_tenant_workspace ON usage_events(tenant_id, workspace_id, event_timestamp DESC);
CREATE INDEX IF NOT EXISTS idx_usage_events_event_type ON usage_events(event_type);
CREATE INDEX IF NOT EXISTS idx_usage_events_billing_cycle ON usage_events(billing_cycle_id);

-- Usage daily rollups (pre-aggregated daily statistics)
CREATE TABLE IF NOT EXISTS usage_daily_rollups (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id TEXT NOT NULL,
    workspace_id TEXT NOT NULL,
    rollup_date DATE NOT NULL,
    event_type usage_event_type NOT NULL,
    event_count INTEGER NOT NULL DEFAULT 0,
    total_credits DECIMAL(12, 4) NOT NULL DEFAULT 0,
    success_count INTEGER NOT NULL DEFAULT 0,
    failure_count INTEGER NOT NULL DEFAULT 0,
    avg_latency_ms DECIMAL(8, 2),
    p95_latency_ms INTEGER,
    p99_latency_ms INTEGER,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    CONSTRAINT usage_daily_rollups_unique UNIQUE (tenant_id, workspace_id, rollup_date, event_type)
);

CREATE INDEX IF NOT EXISTS idx_usage_daily_rollups_tenant ON usage_daily_rollups(tenant_id, workspace_id, rollup_date DESC);
CREATE INDEX IF NOT EXISTS idx_usage_daily_rollups_date ON usage_daily_rollups(rollup_date DESC);

-- Billing cycle indexes
CREATE INDEX IF NOT EXISTS idx_billing_cycles_tenant ON billing_cycles(tenant_id, workspace_id);
CREATE INDEX IF NOT EXISTS idx_billing_cycles_dates ON billing_cycles(cycle_start, cycle_end);

-- Enable RLS on usage/billing tables
ALTER TABLE usage_events ENABLE ROW LEVEL SECURITY;
ALTER TABLE billing_cycles ENABLE ROW LEVEL SECURITY;
ALTER TABLE usage_daily_rollups ENABLE ROW LEVEL SECURITY;

-- Create RLS policies for usage/billing tables
DROP POLICY IF EXISTS tenant_isolation_usage_events ON usage_events;
CREATE POLICY tenant_isolation_usage_events ON usage_events
    FOR ALL
    USING (tenant_id = current_setting('app.tenant_id', true))
    WITH CHECK (tenant_id = current_setting('app.tenant_id', true));

DROP POLICY IF EXISTS tenant_isolation_billing_cycles ON billing_cycles;
CREATE POLICY tenant_isolation_billing_cycles ON billing_cycles
    FOR ALL
    USING (tenant_id = current_setting('app.tenant_id', true))
    WITH CHECK (tenant_id = current_setting('app.tenant_id', true));

DROP POLICY IF EXISTS tenant_isolation_usage_daily_rollups ON usage_daily_rollups;
CREATE POLICY tenant_isolation_usage_daily_rollups ON usage_daily_rollups
    FOR ALL
    USING (tenant_id = current_setting('app.tenant_id', true))
    WITH CHECK (tenant_id = current_setting('app.tenant_id', true));

-- Insert default plans
INSERT INTO tenant_plans (plan_name, display_name, credits_per_cycle, cycle_days, max_memories, max_entities)
VALUES
    ('free', 'Free Tier', 100.00, 30, 1000, 500),
    ('starter', 'Starter', 1000.00, 30, 10000, 5000),
    ('professional', 'Professional', 10000.00, 30, 100000, 50000),
    ('enterprise', 'Enterprise', 100000.00, 30, NULL, NULL),
    ('self', 'Self-Hosted', 999999999.00, 30, NULL, NULL)
ON CONFLICT (plan_name) DO NOTHING;

-- Function to update credits_used on billing_cycle when usage_events are inserted
CREATE OR REPLACE FUNCTION update_billing_cycle_credits()
RETURNS TRIGGER AS $$
BEGIN
    IF NEW.billing_cycle_id IS NOT NULL THEN
        UPDATE billing_cycles
        SET credits_used = credits_used + NEW.credits_consumed
        WHERE id = NEW.billing_cycle_id;
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

-- Trigger to auto-update billing cycle credits
DROP TRIGGER IF EXISTS trg_update_billing_credits ON usage_events;
CREATE TRIGGER trg_update_billing_credits
    AFTER INSERT ON usage_events
    FOR EACH ROW
    EXECUTE FUNCTION update_billing_cycle_credits();

-- Function to create or get current billing cycle
CREATE OR REPLACE FUNCTION get_or_create_billing_cycle(
    p_tenant_id TEXT,
    p_workspace_id TEXT
)
RETURNS UUID AS $$
DECLARE
    v_cycle_id UUID;
    v_plan_id UUID;
    v_credits DECIMAL(12, 2);
    v_cycle_days INTEGER;
BEGIN
    -- Try to get existing current cycle
    SELECT id INTO v_cycle_id
    FROM billing_cycles
    WHERE tenant_id = p_tenant_id
      AND workspace_id = p_workspace_id
      AND is_current = TRUE
      AND cycle_end > NOW();

    IF v_cycle_id IS NOT NULL THEN
        RETURN v_cycle_id;
    END IF;

    -- Close any expired current cycles
    UPDATE billing_cycles
    SET is_current = FALSE, closed_at = NOW()
    WHERE tenant_id = p_tenant_id
      AND workspace_id = p_workspace_id
      AND is_current = TRUE
      AND cycle_end <= NOW();

    -- Get plan for tenant (default to 'self' for self-hosted)
    SELECT id, credits_per_cycle, cycle_days INTO v_plan_id, v_credits, v_cycle_days
    FROM tenant_plans
    WHERE plan_name = 'self' AND is_active = TRUE
    LIMIT 1;

    -- Create new billing cycle
    INSERT INTO billing_cycles (tenant_id, workspace_id, plan_id, cycle_start, cycle_end, credits_allocated)
    VALUES (
        p_tenant_id,
        p_workspace_id,
        v_plan_id,
        NOW(),
        NOW() + (v_cycle_days || ' days')::INTERVAL,
        v_credits
    )
    RETURNING id INTO v_cycle_id;

    RETURN v_cycle_id;
END;
$$ LANGUAGE plpgsql;

-- Function to aggregate daily usage into rollups (run nightly via cron/scheduler)
CREATE OR REPLACE FUNCTION aggregate_daily_usage(p_date DATE DEFAULT CURRENT_DATE - 1)
RETURNS INTEGER AS $$
DECLARE
    v_rows_affected INTEGER;
BEGIN
    INSERT INTO usage_daily_rollups (
        tenant_id,
        workspace_id,
        rollup_date,
        event_type,
        event_count,
        total_credits,
        success_count,
        failure_count,
        avg_latency_ms,
        p95_latency_ms,
        p99_latency_ms
    )
    SELECT
        tenant_id,
        workspace_id,
        p_date,
        event_type,
        COUNT(*)::INTEGER,
        SUM(credits_consumed),
        COUNT(*) FILTER (WHERE success = TRUE)::INTEGER,
        COUNT(*) FILTER (WHERE success = FALSE)::INTEGER,
        AVG(latency_ms),
        PERCENTILE_CONT(0.95) WITHIN GROUP (ORDER BY latency_ms)::INTEGER,
        PERCENTILE_CONT(0.99) WITHIN GROUP (ORDER BY latency_ms)::INTEGER
    FROM usage_events
    WHERE event_timestamp >= p_date::TIMESTAMPTZ
      AND event_timestamp < (p_date + 1)::TIMESTAMPTZ
    GROUP BY tenant_id, workspace_id, event_type
    ON CONFLICT (tenant_id, workspace_id, rollup_date, event_type)
    DO UPDATE SET
        event_count = EXCLUDED.event_count,
        total_credits = EXCLUDED.total_credits,
        success_count = EXCLUDED.success_count,
        failure_count = EXCLUDED.failure_count,
        avg_latency_ms = EXCLUDED.avg_latency_ms,
        p95_latency_ms = EXCLUDED.p95_latency_ms,
        p99_latency_ms = EXCLUDED.p99_latency_ms,
        updated_at = NOW();

    GET DIAGNOSTICS v_rows_affected = ROW_COUNT;
    RETURN v_rows_affected;
END;
$$ LANGUAGE plpgsql;

-- Trigger to update updated_at on tenant_plans
CREATE OR REPLACE FUNCTION update_updated_at_column()
RETURNS TRIGGER AS $$
BEGIN
    NEW.updated_at = NOW();
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS update_tenant_plans_updated_at ON tenant_plans;
CREATE TRIGGER update_tenant_plans_updated_at
    BEFORE UPDATE ON tenant_plans
    FOR EACH ROW
    EXECUTE FUNCTION update_updated_at_column();

DROP TRIGGER IF EXISTS update_usage_daily_rollups_updated_at ON usage_daily_rollups;
CREATE TRIGGER update_usage_daily_rollups_updated_at
    BEFORE UPDATE ON usage_daily_rollups
    FOR EACH ROW
    EXECUTE FUNCTION update_updated_at_column();
