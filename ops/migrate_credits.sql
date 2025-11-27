-- Credit-based usage system migration
-- This adds credit accounting for SaaS metering
-- DEPENDENCY: Requires usage_metering_schema.sql (for billing_cycles table)

-- =============================================================================
-- PLAN TIERS
-- =============================================================================
-- Free:       5,000 credits/month
-- Pro:       100,000 credits/month
-- Team:      500,000 credits/month
-- Enterprise: custom

-- =============================================================================
-- CREDIT UNIT MODEL (per MCP tool)
-- =============================================================================
-- memory_ingest:           1.0
-- memory_search:           0.25
-- memory_multi_hop_search: 2.0
-- memory_about_user:       0.1
-- memory_update:           0.5
-- memory_delete:           0.2
-- memory_merge:            1.5
-- memory_split:            1.5
-- memory_decay:            0.1
-- memory_reinforce:        0.2
-- memory_expire:           0.2
-- crawl_relationships:     1.0
-- export_memories:         5.0
-- export_workspace:       10.0
-- export_graph:            5.0
-- reembed_memories:        5.0

-- =============================================================================
-- TENANT PLANS TABLE
-- =============================================================================
CREATE TABLE IF NOT EXISTS tenant_plans (
    tenant_id UUID PRIMARY KEY REFERENCES tenants(id) ON DELETE CASCADE,
    plan_name TEXT NOT NULL DEFAULT 'free',
    stripe_price_id TEXT,
    stripe_subscription_id TEXT,
    monthly_credit_limit INTEGER NOT NULL DEFAULT 5000,
    credits_remaining NUMERIC(12,2) NOT NULL DEFAULT 5000,
    credits_used_this_cycle NUMERIC(12,2) NOT NULL DEFAULT 0,
    cycle_start_at TIMESTAMPTZ NOT NULL DEFAULT date_trunc('month', NOW()),
    cycle_reset_at TIMESTAMPTZ NOT NULL DEFAULT date_trunc('month', NOW()) + INTERVAL '1 month',
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    CONSTRAINT valid_plan_name CHECK (plan_name IN ('free', 'pro', 'team', 'enterprise')),
    CONSTRAINT credits_non_negative CHECK (credits_remaining >= 0)
);

-- Index for Stripe lookups
CREATE INDEX IF NOT EXISTS idx_tenant_plans_stripe_subscription
    ON tenant_plans(stripe_subscription_id) WHERE stripe_subscription_id IS NOT NULL;

CREATE INDEX IF NOT EXISTS idx_tenant_plans_stripe_price
    ON tenant_plans(stripe_price_id) WHERE stripe_price_id IS NOT NULL;

CREATE INDEX IF NOT EXISTS idx_tenant_plans_reset
    ON tenant_plans(cycle_reset_at);

-- =============================================================================
-- USAGE EVENTS TABLE (audit trail)
-- =============================================================================
CREATE TABLE IF NOT EXISTS usage_events (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    workspace_id TEXT NOT NULL DEFAULT 'default',
    tool_name TEXT NOT NULL,
    credits_used NUMERIC(6,2) NOT NULL,
    credits_remaining_after NUMERIC(12,2) NOT NULL,
    success BOOLEAN NOT NULL DEFAULT true,
    metadata JSONB,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Partition by month for efficient queries and cleanup
-- Note: In production, you'd want proper partitioning
CREATE INDEX IF NOT EXISTS idx_usage_events_tenant_created
    ON usage_events(tenant_id, created_at DESC);

CREATE INDEX IF NOT EXISTS idx_usage_events_tool
    ON usage_events(tool_name, created_at DESC);

CREATE INDEX IF NOT EXISTS idx_usage_events_created
    ON usage_events(created_at DESC);

-- =============================================================================
-- USAGE ALERTS TABLE (tracks sent alerts to prevent spam)
-- =============================================================================
CREATE TABLE IF NOT EXISTS usage_alerts (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    billing_cycle_id UUID NOT NULL REFERENCES billing_cycles(id) ON DELETE CASCADE,
    alert_type TEXT NOT NULL,  -- CreditWarning75, CreditWarning90, CreditLimitReached, etc.
    threshold_percent INTEGER NOT NULL,
    current_percent NUMERIC(5,2) NOT NULL,
    message TEXT NOT NULL,
    triggered_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    acknowledged_at TIMESTAMPTZ,

    -- Unique constraint: one alert per type per billing cycle
    CONSTRAINT unique_alert_per_cycle UNIQUE (billing_cycle_id, alert_type)
);

CREATE INDEX IF NOT EXISTS idx_usage_alerts_billing_cycle
    ON usage_alerts(billing_cycle_id);

CREATE INDEX IF NOT EXISTS idx_usage_alerts_unacknowledged
    ON usage_alerts(billing_cycle_id) WHERE acknowledged_at IS NULL;

-- =============================================================================
-- CREDIT COST LOOKUP TABLE
-- =============================================================================
CREATE TABLE IF NOT EXISTS credit_costs (
    tool_name TEXT PRIMARY KEY,
    credits NUMERIC(6,2) NOT NULL,
    description TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Insert default credit costs
INSERT INTO credit_costs (tool_name, credits, description) VALUES
    ('memory_ingest', 1.0, 'Add a new memory with entity extraction'),
    ('memory_search', 0.25, 'Semantic/text/hybrid search'),
    ('memory_multi_hop_search', 2.0, 'Multi-hop knowledge graph traversal'),
    ('memory_about_user', 0.1, 'Retrieve user persona'),
    ('memory_update', 0.5, 'Update memory content'),
    ('memory_delete', 0.2, 'Soft delete a memory'),
    ('memory_merge', 1.5, 'Merge multiple memories'),
    ('memory_split', 1.5, 'Split memory into children'),
    ('memory_decay', 0.1, 'Apply time-based decay'),
    ('memory_reinforce', 0.2, 'Reinforce/validate a memory'),
    ('memory_expire', 0.2, 'Expire a memory by TTL'),
    ('crawl_relationships', 1.0, 'Extract relationships from memories'),
    ('export_memories', 5.0, 'Export memories to file'),
    ('export_workspace', 10.0, 'Export entire workspace'),
    ('export_graph', 5.0, 'Export knowledge graph'),
    ('reembed_memories', 5.0, 'Re-generate embeddings'),
    ('set_user_persona', 0.1, 'Set user persona attribute'),
    ('initialise_conversation_session', 0.1, 'Start conversation session'),
    ('end_conversation_session', 0.0, 'End conversation session'),
    ('get_integrations', 0.0, 'List integrations'),
    ('import_from_core', 2.0, 'Import from CORE format'),
    ('get_graph_statistics', 0.0, 'Get graph statistics'),
    ('get_model_info', 0.0, 'Get embedding model info'),
    ('memory_trace', 0.1, 'Get memory event history'),
    ('memory_lineage', 0.2, 'Trace memory ancestry'),
    ('memory_explain', 0.1, 'Explain memory state'),
    ('memory_conflicts', 0.1, 'Find memory conflicts'),
    ('detect_contradictions', 0.5, 'Detect contradicting memories'),
    ('detect_hallucinations', 0.5, 'Flag potential hallucinations'),
    ('verify_memory_integrity', 0.2, 'Verify content hashes'),
    ('scan_loops', 0.2, 'Detect causal loops'),
    ('export_user_profile', 1.0, 'Export user profile')
ON CONFLICT (tool_name) DO UPDATE SET
    credits = EXCLUDED.credits,
    description = EXCLUDED.description;

-- =============================================================================
-- PLAN LIMITS LOOKUP TABLE
-- =============================================================================
CREATE TABLE IF NOT EXISTS plan_limits (
    plan_name TEXT PRIMARY KEY,
    monthly_credits INTEGER NOT NULL,
    stripe_price_id TEXT,
    description TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

INSERT INTO plan_limits (plan_name, monthly_credits, stripe_price_id, description) VALUES
    ('free', 5000, NULL, 'Free tier - 5,000 credits/month'),
    ('pro', 100000, NULL, 'Pro tier - 100,000 credits/month'),
    ('team', 500000, NULL, 'Team tier - 500,000 credits/month'),
    ('enterprise', 10000000, NULL, 'Enterprise tier - custom limits')
ON CONFLICT (plan_name) DO UPDATE SET
    monthly_credits = EXCLUDED.monthly_credits,
    description = EXCLUDED.description;

-- =============================================================================
-- FUNCTIONS
-- =============================================================================

-- Function to get credit cost for a tool
CREATE OR REPLACE FUNCTION get_credit_cost(p_tool_name TEXT)
RETURNS NUMERIC(6,2) AS $$
BEGIN
    RETURN COALESCE(
        (SELECT credits FROM credit_costs WHERE tool_name = p_tool_name),
        0.0
    );
END;
$$ LANGUAGE plpgsql STABLE;

-- Function to atomically consume credits
-- Returns: success, credits consumed, remaining credits, usage percentage
CREATE OR REPLACE FUNCTION consume_credits(
    p_tenant_id UUID,
    p_workspace_id TEXT,
    p_event_type TEXT,
    p_credits NUMERIC(6,2),
    p_memory_id UUID DEFAULT NULL,
    p_user_id TEXT DEFAULT NULL,
    p_session_id UUID DEFAULT NULL,
    p_latency_ms INTEGER DEFAULT NULL,
    p_success BOOLEAN DEFAULT TRUE,
    p_error_message TEXT DEFAULT NULL,
    p_metadata TEXT DEFAULT NULL
)
RETURNS TABLE (
    success BOOLEAN,
    credits_consumed NUMERIC(6,2),
    credits_remaining NUMERIC(12,2),
    usage_percent NUMERIC(5,2),
    error_message TEXT
) AS $$
DECLARE
    v_current_credits NUMERIC(12,2);
    v_new_credits NUMERIC(12,2);
    v_monthly_limit INTEGER;
    v_credits_used NUMERIC(12,2);
    v_usage_pct NUMERIC(5,2);
BEGIN
    -- Lock the row and check credits
    SELECT tp.credits_remaining, tp.monthly_credit_limit, tp.credits_used_this_cycle
    INTO v_current_credits, v_monthly_limit, v_credits_used
    FROM tenant_plans tp
    WHERE tp.tenant_id = p_tenant_id
    FOR UPDATE;

    -- If no plan exists, create free tier
    IF NOT FOUND THEN
        INSERT INTO tenant_plans (tenant_id, plan_name, monthly_credit_limit, credits_remaining)
        VALUES (p_tenant_id, 'free', 5000, 5000)
        RETURNING credits_remaining, monthly_credit_limit, credits_used_this_cycle
        INTO v_current_credits, v_monthly_limit, v_credits_used;
    END IF;

    -- Deduct credits (even if going negative for tracking)
    v_new_credits := v_current_credits - p_credits;
    v_credits_used := v_credits_used + p_credits;
    v_usage_pct := CASE WHEN v_monthly_limit > 0
                       THEN (v_credits_used::NUMERIC / v_monthly_limit * 100)
                       ELSE 0 END;

    UPDATE tenant_plans
    SET
        credits_remaining = v_new_credits,
        credits_used_this_cycle = v_credits_used,
        updated_at = NOW()
    WHERE tenant_id = p_tenant_id;

    -- Record usage event
    INSERT INTO usage_events (tenant_id, workspace_id, tool_name, credits_used, credits_remaining_after, success, metadata)
    VALUES (p_tenant_id, p_workspace_id, p_event_type, p_credits, v_new_credits, p_success,
        jsonb_build_object(
            'memory_id', p_memory_id,
            'user_id', p_user_id,
            'session_id', p_session_id,
            'latency_ms', p_latency_ms,
            'error_message', p_error_message
        ) || COALESCE(p_metadata::jsonb, '{}'::jsonb));

    RETURN QUERY SELECT
        true AS success,
        p_credits AS credits_consumed,
        v_new_credits AS credits_remaining,
        v_usage_pct AS usage_percent,
        NULL::TEXT AS error_message;
END;
$$ LANGUAGE plpgsql;

-- Legacy wrapper for simple consume_credits calls
CREATE OR REPLACE FUNCTION consume_credits_simple(
    p_tenant_id UUID,
    p_tool_name TEXT,
    p_workspace_id TEXT DEFAULT 'default',
    p_metadata JSONB DEFAULT NULL
)
RETURNS TABLE (
    success BOOLEAN,
    credits_charged NUMERIC(6,2),
    credits_remaining NUMERIC(12,2),
    error_code TEXT,
    error_message TEXT
) AS $$
DECLARE
    v_cost NUMERIC(6,2);
    v_result RECORD;
BEGIN
    -- Get the cost for this tool
    v_cost := get_credit_cost(p_tool_name);

    -- If tool is free, allow without decrementing
    IF v_cost = 0 THEN
        RETURN QUERY SELECT
            true AS success,
            0.0::NUMERIC(6,2) AS credits_charged,
            (SELECT tp.credits_remaining FROM tenant_plans tp WHERE tp.tenant_id = p_tenant_id)::NUMERIC(12,2),
            NULL::TEXT AS error_code,
            NULL::TEXT AS error_message;
        RETURN;
    END IF;

    -- Call the main consume_credits function
    SELECT * INTO v_result FROM consume_credits(
        p_tenant_id, p_workspace_id, p_tool_name, v_cost,
        NULL, NULL, NULL, NULL, TRUE, NULL, p_metadata::TEXT
    );

    RETURN QUERY SELECT
        v_result.success,
        v_result.credits_consumed,
        v_result.credits_remaining,
        CASE WHEN NOT v_result.success THEN 'INSUFFICIENT_CREDITS' ELSE NULL END,
        v_result.error_message;
END;
$$ LANGUAGE plpgsql;

-- Function to refund credits (for failed operations)
CREATE OR REPLACE FUNCTION refund_credits(
    p_tenant_id UUID,
    p_tool_name TEXT,
    p_workspace_id TEXT DEFAULT 'default'
)
RETURNS NUMERIC(12,2) AS $$
DECLARE
    v_cost NUMERIC(6,2);
    v_new_credits NUMERIC(12,2);
BEGIN
    v_cost := get_credit_cost(p_tool_name);

    IF v_cost = 0 THEN
        RETURN (SELECT credits_remaining FROM tenant_plans WHERE tenant_id = p_tenant_id);
    END IF;

    UPDATE tenant_plans
    SET
        credits_remaining = credits_remaining + v_cost,
        credits_used_this_cycle = GREATEST(0, credits_used_this_cycle - v_cost),
        updated_at = NOW()
    WHERE tenant_id = p_tenant_id
    RETURNING credits_remaining INTO v_new_credits;

    -- Record refund event
    INSERT INTO usage_events (tenant_id, workspace_id, tool_name, credits_used, credits_remaining_after, success, metadata)
    VALUES (p_tenant_id, p_workspace_id, p_tool_name, -v_cost, v_new_credits, true,
        jsonb_build_object('type', 'refund'));

    RETURN v_new_credits;
END;
$$ LANGUAGE plpgsql;

-- Function to reset credits at billing cycle
CREATE OR REPLACE FUNCTION reset_tenant_credits(p_tenant_id UUID)
RETURNS void AS $$
BEGIN
    UPDATE tenant_plans
    SET
        credits_remaining = monthly_credit_limit,
        credits_used_this_cycle = 0,
        cycle_start_at = NOW(),
        cycle_reset_at = NOW() + INTERVAL '1 month',
        updated_at = NOW()
    WHERE tenant_id = p_tenant_id;
END;
$$ LANGUAGE plpgsql;

-- Function to upgrade/downgrade plan
CREATE OR REPLACE FUNCTION update_tenant_plan(
    p_tenant_id UUID,
    p_new_plan TEXT,
    p_stripe_price_id TEXT DEFAULT NULL,
    p_stripe_subscription_id TEXT DEFAULT NULL
)
RETURNS void AS $$
DECLARE
    v_new_limit INTEGER;
    v_current_used NUMERIC(12,2);
    v_new_remaining NUMERIC(12,2);
BEGIN
    -- Get new plan limit
    SELECT monthly_credits INTO v_new_limit
    FROM plan_limits WHERE plan_name = p_new_plan;

    IF NOT FOUND THEN
        RAISE EXCEPTION 'Invalid plan name: %', p_new_plan;
    END IF;

    -- Get current usage
    SELECT credits_used_this_cycle INTO v_current_used
    FROM tenant_plans WHERE tenant_id = p_tenant_id;

    -- Calculate new remaining (grant full new limit minus what's been used)
    v_new_remaining := GREATEST(0, v_new_limit - COALESCE(v_current_used, 0));

    -- Upsert plan
    INSERT INTO tenant_plans (
        tenant_id, plan_name, stripe_price_id, stripe_subscription_id,
        monthly_credit_limit, credits_remaining
    )
    VALUES (
        p_tenant_id, p_new_plan, p_stripe_price_id, p_stripe_subscription_id,
        v_new_limit, v_new_remaining
    )
    ON CONFLICT (tenant_id) DO UPDATE SET
        plan_name = p_new_plan,
        stripe_price_id = COALESCE(p_stripe_price_id, tenant_plans.stripe_price_id),
        stripe_subscription_id = COALESCE(p_stripe_subscription_id, tenant_plans.stripe_subscription_id),
        monthly_credit_limit = v_new_limit,
        credits_remaining = v_new_remaining,
        updated_at = NOW();
END;
$$ LANGUAGE plpgsql;

-- Function to check if alert should be sent
CREATE OR REPLACE FUNCTION should_send_usage_alert(
    p_tenant_id UUID,
    p_threshold INTEGER
)
RETURNS BOOLEAN AS $$
DECLARE
    v_cycle_start TIMESTAMPTZ;
    v_already_sent BOOLEAN;
BEGIN
    SELECT cycle_start_at INTO v_cycle_start
    FROM tenant_plans WHERE tenant_id = p_tenant_id;

    IF NOT FOUND THEN
        RETURN FALSE;
    END IF;

    SELECT EXISTS(
        SELECT 1 FROM usage_alerts
        WHERE tenant_id = p_tenant_id
          AND threshold_percent = p_threshold
          AND cycle_start_at = v_cycle_start
    ) INTO v_already_sent;

    RETURN NOT v_already_sent;
END;
$$ LANGUAGE plpgsql;

-- Function to record that alert was sent
CREATE OR REPLACE FUNCTION record_usage_alert(
    p_tenant_id UUID,
    p_threshold INTEGER,
    p_alert_type TEXT DEFAULT 'email'
)
RETURNS void AS $$
BEGIN
    INSERT INTO usage_alerts (tenant_id, threshold_percent, cycle_start_at, alert_type)
    SELECT p_tenant_id, p_threshold, cycle_start_at, p_alert_type
    FROM tenant_plans WHERE tenant_id = p_tenant_id
    ON CONFLICT (tenant_id, threshold_percent, cycle_start_at) DO NOTHING;
END;
$$ LANGUAGE plpgsql;

-- =============================================================================
-- RLS POLICIES
-- =============================================================================
ALTER TABLE tenant_plans ENABLE ROW LEVEL SECURITY;
ALTER TABLE usage_events ENABLE ROW LEVEL SECURITY;
ALTER TABLE usage_alerts ENABLE ROW LEVEL SECURITY;

-- tenant_plans: tenants can only see their own plan
CREATE POLICY tenant_plans_tenant_isolation ON tenant_plans
    FOR ALL
    USING (tenant_id = current_setting('app.tenant_id', true)::uuid);

-- usage_events: tenants can only see their own events
CREATE POLICY usage_events_tenant_isolation ON usage_events
    FOR ALL
    USING (tenant_id = current_setting('app.tenant_id', true)::uuid);

-- usage_alerts: tenants can only see their own alerts
CREATE POLICY usage_alerts_tenant_isolation ON usage_alerts
    FOR ALL
    USING (tenant_id = current_setting('app.tenant_id', true)::uuid);

-- Grant permissions
GRANT SELECT, INSERT, UPDATE ON tenant_plans TO contextdb_app;
GRANT SELECT, INSERT ON usage_events TO contextdb_app;
GRANT SELECT, INSERT ON usage_alerts TO contextdb_app;
GRANT SELECT ON credit_costs TO contextdb_app;
GRANT SELECT ON plan_limits TO contextdb_app;
GRANT EXECUTE ON FUNCTION consume_credits TO contextdb_app;
GRANT EXECUTE ON FUNCTION consume_credits_simple TO contextdb_app;
GRANT EXECUTE ON FUNCTION refund_credits TO contextdb_app;
GRANT EXECUTE ON FUNCTION get_credit_cost TO contextdb_app;
GRANT EXECUTE ON FUNCTION reset_tenant_credits TO contextdb_app;
GRANT EXECUTE ON FUNCTION update_tenant_plan TO contextdb_app;
GRANT EXECUTE ON FUNCTION should_send_usage_alert TO contextdb_app;
GRANT EXECUTE ON FUNCTION record_usage_alert TO contextdb_app;

-- =============================================================================
-- Initialize default tenant for self-hosted mode
-- =============================================================================
INSERT INTO tenant_plans (tenant_id, plan_name, monthly_credit_limit, credits_remaining)
VALUES ('00000000-0000-0000-0000-000000000000', 'enterprise', 10000000, 10000000)
ON CONFLICT (tenant_id) DO NOTHING;

COMMENT ON TABLE tenant_plans IS 'Credit allocation and billing plan for each tenant';
COMMENT ON TABLE usage_events IS 'Audit trail of all credit-consuming operations';
COMMENT ON TABLE usage_alerts IS 'Tracks which quota alerts have been sent to prevent spam';
COMMENT ON TABLE credit_costs IS 'Maps MCP tools to credit costs';
COMMENT ON TABLE plan_limits IS 'Defines credit limits for each plan tier';
COMMENT ON FUNCTION consume_credits IS 'Atomically deducts credits and records usage event';
COMMENT ON FUNCTION refund_credits IS 'Returns credits for failed operations';
