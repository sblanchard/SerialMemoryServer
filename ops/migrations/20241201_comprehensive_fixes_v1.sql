-- =============================================================================
-- COMPREHENSIVE FIXES MIGRATION v1
-- SerialMemory Dashboard & API Fixes
-- Addresses all 11 critical issues across the ecosystem
-- =============================================================================
-- Issues Fixed:
-- 1. Traces Page - Recent Events DESC order
-- 2. Conflicts & Contradictions - Detection scan
-- 3. Mutation Console - Slider fetch errors
-- 4. Mind Health - 30-day trend and calibration
-- 5. Privacy & Integrity - Chain verification
-- 6. Memory Timeline - Time travel
-- 7. Shadow Branches - Create branch 404
-- 8. Reasoning Dashboard - SignalR and DAG
-- 9. API Keys - Retrieval and clipboard
-- 10. Billing - Checkout session and plans
-- 11. Self-Host Admin Stats for SaaS mode
-- =============================================================================

BEGIN;

-- =============================================================================
-- STEP 1: ENSURE REQUIRED TABLES EXIST
-- =============================================================================

-- Conflict log table for storing detected contradictions
CREATE TABLE IF NOT EXISTS conflict_log (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL,
    memory_a UUID NOT NULL REFERENCES memories(id),
    memory_b UUID NOT NULL REFERENCES memories(id),
    conflict_type VARCHAR(100) NOT NULL,
    severity FLOAT NOT NULL DEFAULT 0.5,
    description TEXT,
    entity_involved UUID REFERENCES entities(id),
    detected_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    status VARCHAR(50) NOT NULL DEFAULT 'pending',
    resolved_at TIMESTAMPTZ,
    resolution_strategy VARCHAR(100),
    resolution_notes TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    metadata JSONB
);

-- Mind health daily scores (for 30-day trends)
CREATE TABLE IF NOT EXISTS mind_health_daily (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL,
    date DATE NOT NULL,
    overall_score FLOAT NOT NULL,
    confidence_drift FLOAT NOT NULL DEFAULT 0,
    l3_fact_stability FLOAT NOT NULL DEFAULT 1.0,
    entity_consistency FLOAT NOT NULL DEFAULT 1.0,
    memory_decay_rate FLOAT NOT NULL DEFAULT 0,
    anomaly_count INT NOT NULL DEFAULT 0,
    total_memories INT NOT NULL DEFAULT 0,
    active_contradictions INT NOT NULL DEFAULT 0,
    hallucination_rate FLOAT NOT NULL DEFAULT 0,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE(tenant_id, date)
);

-- Mind calibration settings and history
CREATE TABLE IF NOT EXISTS mind_calibration (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL,
    calibration_date TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    baseline_confidence FLOAT NOT NULL DEFAULT 0.5,
    decay_half_life_days INT NOT NULL DEFAULT 30,
    hallucination_threshold FLOAT NOT NULL DEFAULT 0.1,
    contradiction_threshold FLOAT NOT NULL DEFAULT 0.85,
    auto_resolve_enabled BOOLEAN NOT NULL DEFAULT FALSE,
    calibration_method VARCHAR(100) NOT NULL DEFAULT 'default',
    model_version VARCHAR(50),
    notes TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE(tenant_id, calibration_date)
);

-- Memory mutations table for time-travel debugging
CREATE TABLE IF NOT EXISTS memory_mutations (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL,
    memory_id UUID NOT NULL REFERENCES memories(id),
    sequence_number BIGSERIAL NOT NULL,
    mutation_type VARCHAR(50) NOT NULL,
    old_content TEXT,
    new_content TEXT,
    old_embedding vector(768),
    new_embedding vector(768),
    old_metadata JSONB,
    new_metadata JSONB,
    diff JSONB,
    actor_id VARCHAR(255),
    actor_type VARCHAR(50) NOT NULL DEFAULT 'user',
    reason TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Memory state snapshots for replay
CREATE TABLE IF NOT EXISTS memory_states (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL,
    memory_id UUID NOT NULL REFERENCES memories(id),
    sequence_number BIGINT NOT NULL,
    state JSONB NOT NULL,
    snapshot_type VARCHAR(50) NOT NULL DEFAULT 'full',
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE(memory_id, sequence_number)
);

-- Memory timeline entries
CREATE TABLE IF NOT EXISTS memory_timeline (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL,
    memory_id UUID NOT NULL REFERENCES memories(id),
    event_type VARCHAR(100) NOT NULL,
    event_data JSONB,
    version_number INT NOT NULL DEFAULT 1,
    previous_entry_id UUID REFERENCES memory_timeline(id),
    actor_id VARCHAR(255),
    occurred_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Reasoning executions table (if not exists)
CREATE TABLE IF NOT EXISTS reasoning_executions (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL,
    user_id VARCHAR(255) NOT NULL,
    input_type VARCHAR(50) NOT NULL,
    input_reference TEXT,
    input_hash VARCHAR(64),
    status VARCHAR(50) NOT NULL DEFAULT 'pending',
    started_at TIMESTAMPTZ,
    completed_at TIMESTAMPTZ,
    models_used INT NOT NULL DEFAULT 0,
    successful_models INT NOT NULL DEFAULT 0,
    total_duration_ms INT,
    overall_confidence FLOAT,
    insight_count INT NOT NULL DEFAULT 0,
    disagreement_count INT NOT NULL DEFAULT 0,
    error_message TEXT,
    error_details JSONB,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Reasoning traces table (if not exists)
CREATE TABLE IF NOT EXISTS reasoning_traces (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    execution_id UUID NOT NULL REFERENCES reasoning_executions(id) ON DELETE CASCADE,
    tenant_id UUID NOT NULL,
    step_id VARCHAR(100) NOT NULL,
    parent_step_ids TEXT[] NOT NULL DEFAULT '{}',
    step_order INT NOT NULL,
    role VARCHAR(50) NOT NULL,
    model_name VARCHAR(100),
    model_version VARCHAR(50),
    started_at TIMESTAMPTZ,
    completed_at TIMESTAMPTZ,
    duration_ms INT,
    success BOOLEAN NOT NULL DEFAULT TRUE,
    input_context JSONB,
    output_raw TEXT,
    output_parsed JSONB,
    error_message TEXT,
    error_code VARCHAR(50),
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Reasoning insights table (if not exists)
CREATE TABLE IF NOT EXISTS reasoning_insights (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    execution_id UUID NOT NULL REFERENCES reasoning_executions(id) ON DELETE CASCADE,
    tenant_id UUID NOT NULL,
    insight_type VARCHAR(100) NOT NULL,
    content TEXT NOT NULL,
    confidence FLOAT NOT NULL DEFAULT 0.5,
    source_models TEXT[] NOT NULL DEFAULT '{}',
    agreement_count INT NOT NULL DEFAULT 1,
    related_entity_ids UUID[] NOT NULL DEFAULT '{}',
    related_memory_ids UUID[] NOT NULL DEFAULT '{}',
    metadata JSONB,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Reasoning disagreements table (if not exists)
CREATE TABLE IF NOT EXISTS reasoning_disagreements (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    execution_id UUID NOT NULL REFERENCES reasoning_executions(id) ON DELETE CASCADE,
    tenant_id UUID NOT NULL,
    topic TEXT NOT NULL,
    model_a VARCHAR(100) NOT NULL,
    position_a TEXT NOT NULL,
    model_b VARCHAR(100) NOT NULL,
    position_b TEXT NOT NULL,
    severity VARCHAR(20) NOT NULL DEFAULT 'medium',
    resolved BOOLEAN NOT NULL DEFAULT FALSE,
    resolution TEXT,
    resolved_at TIMESTAMPTZ,
    resolved_by VARCHAR(255),
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- =============================================================================
-- FIX: Ensure tenant_plans has required columns before inserting
-- =============================================================================
DO $$
BEGIN
    -- Add rate_limit_rpm if missing (rename from rate_limit_per_minute if exists)
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'tenant_plans' AND column_name = 'rate_limit_rpm') THEN
        IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'tenant_plans' AND column_name = 'rate_limit_per_minute') THEN
            ALTER TABLE tenant_plans RENAME COLUMN rate_limit_per_minute TO rate_limit_rpm;
        ELSE
            ALTER TABLE tenant_plans ADD COLUMN rate_limit_rpm INTEGER DEFAULT 60;
        END IF;
    END IF;

    -- Add rate_limit_rpd if missing
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'tenant_plans' AND column_name = 'rate_limit_rpd') THEN
        ALTER TABLE tenant_plans ADD COLUMN rate_limit_rpd INTEGER DEFAULT 10000;
    END IF;

    -- Add features if missing
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'tenant_plans' AND column_name = 'features') THEN
        ALTER TABLE tenant_plans ADD COLUMN features JSONB DEFAULT '[]'::jsonb;
    END IF;

    -- Add updated_at if missing
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'tenant_plans' AND column_name = 'updated_at') THEN
        ALTER TABLE tenant_plans ADD COLUMN updated_at TIMESTAMPTZ DEFAULT NOW();
    END IF;
END $$;

-- Pro Plus and Team plans for billing
INSERT INTO tenant_plans (id, plan_name, display_name, credits_per_cycle, cycle_days, max_memories, max_entities, rate_limit_rpm, rate_limit_rpd, features, is_active, created_at)
VALUES
    (gen_random_uuid(), 'pro_plus', 'Pro Plus', 200000, 30, 500000, 100000, 600, 500000, '["priority_support", "advanced_analytics", "custom_integrations", "sso", "dedicated_embeddings"]'::jsonb, TRUE, NOW()),
    (gen_random_uuid(), 'team', 'Enterprise Team', 1000000, 30, NULL, NULL, 2000, 2000000, '["priority_support", "advanced_analytics", "custom_integrations", "sso", "dedicated_embeddings", "dedicated_infrastructure", "sla_99_9"]'::jsonb, TRUE, NOW())
ON CONFLICT (plan_name) DO UPDATE SET
    display_name = EXCLUDED.display_name,
    credits_per_cycle = EXCLUDED.credits_per_cycle,
    features = EXCLUDED.features,
    is_active = TRUE,
    updated_at = NOW();

-- =============================================================================
-- FIX: Ensure stripe_price_mapping table exists
-- =============================================================================
CREATE TABLE IF NOT EXISTS stripe_price_mapping (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    plan_name TEXT NOT NULL UNIQUE,
    stripe_price_id TEXT NOT NULL DEFAULT '',
    stripe_product_id TEXT NOT NULL DEFAULT '',
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Stripe price mapping for Pro Plus
INSERT INTO stripe_price_mapping (id, plan_name, stripe_price_id, stripe_product_id, is_active, created_at)
VALUES
    (gen_random_uuid(), 'pro_plus', '', '', TRUE, NOW()),
    (gen_random_uuid(), 'team', '', '', TRUE, NOW())
ON CONFLICT (plan_name) DO NOTHING;

-- =============================================================================
-- STEP 2: CREATE INDEXES FOR PERFORMANCE
-- =============================================================================

CREATE INDEX IF NOT EXISTS idx_conflict_log_tenant ON conflict_log(tenant_id);
CREATE INDEX IF NOT EXISTS idx_conflict_log_status ON conflict_log(status, tenant_id);
CREATE INDEX IF NOT EXISTS idx_conflict_log_detected ON conflict_log(detected_at DESC);

CREATE INDEX IF NOT EXISTS idx_mind_health_daily_tenant ON mind_health_daily(tenant_id, date DESC);
CREATE INDEX IF NOT EXISTS idx_mind_calibration_tenant ON mind_calibration(tenant_id, calibration_date DESC);

CREATE INDEX IF NOT EXISTS idx_memory_mutations_tenant ON memory_mutations(tenant_id);
CREATE INDEX IF NOT EXISTS idx_memory_mutations_memory ON memory_mutations(memory_id, sequence_number);
CREATE INDEX IF NOT EXISTS idx_memory_mutations_seq ON memory_mutations(sequence_number DESC);

CREATE INDEX IF NOT EXISTS idx_memory_states_memory ON memory_states(memory_id, sequence_number);
CREATE INDEX IF NOT EXISTS idx_memory_timeline_memory ON memory_timeline(memory_id, occurred_at DESC);

CREATE INDEX IF NOT EXISTS idx_reasoning_executions_tenant ON reasoning_executions(tenant_id, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_reasoning_traces_execution ON reasoning_traces(execution_id, step_order);
CREATE INDEX IF NOT EXISTS idx_reasoning_insights_execution ON reasoning_insights(execution_id);

-- =============================================================================
-- STEP 3: CREATE REQUIRED ENUM TYPES (IF NOT EXIST)
-- =============================================================================

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_type WHERE typname = 'reasoning_status') THEN
        CREATE TYPE reasoning_status AS ENUM ('pending', 'running', 'completed', 'failed', 'cancelled');
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_type WHERE typname = 'reasoning_role') THEN
        CREATE TYPE reasoning_role AS ENUM ('structural', 'risk', 'optimization', 'contradiction', 'synthesis');
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_type WHERE typname = 'conflict_status') THEN
        CREATE TYPE conflict_status AS ENUM ('pending', 'reviewing', 'resolved', 'dismissed');
    END IF;
END $$;

-- =============================================================================
-- STEP 4: ENABLE RLS ON NEW TABLES
-- =============================================================================

ALTER TABLE conflict_log ENABLE ROW LEVEL SECURITY;
ALTER TABLE conflict_log FORCE ROW LEVEL SECURITY;

ALTER TABLE mind_health_daily ENABLE ROW LEVEL SECURITY;
ALTER TABLE mind_health_daily FORCE ROW LEVEL SECURITY;

ALTER TABLE mind_calibration ENABLE ROW LEVEL SECURITY;
ALTER TABLE mind_calibration FORCE ROW LEVEL SECURITY;

ALTER TABLE memory_mutations ENABLE ROW LEVEL SECURITY;
ALTER TABLE memory_mutations FORCE ROW LEVEL SECURITY;

ALTER TABLE memory_states ENABLE ROW LEVEL SECURITY;
ALTER TABLE memory_states FORCE ROW LEVEL SECURITY;

ALTER TABLE memory_timeline ENABLE ROW LEVEL SECURITY;
ALTER TABLE memory_timeline FORCE ROW LEVEL SECURITY;

ALTER TABLE reasoning_executions ENABLE ROW LEVEL SECURITY;
ALTER TABLE reasoning_executions FORCE ROW LEVEL SECURITY;

ALTER TABLE reasoning_traces ENABLE ROW LEVEL SECURITY;
ALTER TABLE reasoning_traces FORCE ROW LEVEL SECURITY;

ALTER TABLE reasoning_insights ENABLE ROW LEVEL SECURITY;
ALTER TABLE reasoning_insights FORCE ROW LEVEL SECURITY;

ALTER TABLE reasoning_disagreements ENABLE ROW LEVEL SECURITY;
ALTER TABLE reasoning_disagreements FORCE ROW LEVEL SECURITY;

-- =============================================================================
-- STEP 5: CREATE RLS POLICIES FOR NEW TABLES
-- =============================================================================

-- Drop existing policies if they exist
DO $$
DECLARE
    tables_to_fix TEXT[] := ARRAY[
        'conflict_log', 'mind_health_daily', 'mind_calibration',
        'memory_mutations', 'memory_states', 'memory_timeline',
        'reasoning_executions', 'reasoning_traces', 'reasoning_insights', 'reasoning_disagreements'
    ];
    t TEXT;
BEGIN
    FOREACH t IN ARRAY tables_to_fix LOOP
        EXECUTE format('DROP POLICY IF EXISTS rls_%I ON %I', t, t);
    END LOOP;
END $$;

-- Create unified RLS policies for all new tables
CREATE POLICY rls_conflict_log ON conflict_log FOR ALL
    USING (rls_tenant_check(tenant_id))
    WITH CHECK (rls_tenant_check(tenant_id));

CREATE POLICY rls_mind_health_daily ON mind_health_daily FOR ALL
    USING (rls_tenant_check(tenant_id))
    WITH CHECK (rls_tenant_check(tenant_id));

CREATE POLICY rls_mind_calibration ON mind_calibration FOR ALL
    USING (rls_tenant_check(tenant_id))
    WITH CHECK (rls_tenant_check(tenant_id));

CREATE POLICY rls_memory_mutations ON memory_mutations FOR ALL
    USING (rls_tenant_check(tenant_id))
    WITH CHECK (rls_tenant_check(tenant_id));

CREATE POLICY rls_memory_states ON memory_states FOR ALL
    USING (rls_tenant_check(tenant_id))
    WITH CHECK (rls_tenant_check(tenant_id));

CREATE POLICY rls_memory_timeline ON memory_timeline FOR ALL
    USING (rls_tenant_check(tenant_id))
    WITH CHECK (rls_tenant_check(tenant_id));

CREATE POLICY rls_reasoning_executions ON reasoning_executions FOR ALL
    USING (rls_tenant_check(tenant_id))
    WITH CHECK (rls_tenant_check(tenant_id));

CREATE POLICY rls_reasoning_traces ON reasoning_traces FOR ALL
    USING (rls_tenant_check(tenant_id))
    WITH CHECK (rls_tenant_check(tenant_id));

CREATE POLICY rls_reasoning_insights ON reasoning_insights FOR ALL
    USING (rls_tenant_check(tenant_id))
    WITH CHECK (rls_tenant_check(tenant_id));

CREATE POLICY rls_reasoning_disagreements ON reasoning_disagreements FOR ALL
    USING (rls_tenant_check(tenant_id))
    WITH CHECK (rls_tenant_check(tenant_id));

-- =============================================================================
-- STEP 6: FIX privacy_audit_entries RLS FOR READ ACCESS
-- =============================================================================

-- Allow tenants to READ their own privacy audit entries (important for verification)
DROP POLICY IF EXISTS rls_privacy_audit_entries ON privacy_audit_entries;
CREATE POLICY rls_privacy_audit_entries ON privacy_audit_entries FOR ALL
    USING (rls_tenant_check(tenant_id) OR is_internal_admin())
    WITH CHECK (is_internal_admin());

DROP POLICY IF EXISTS rls_privacy_audit_proofs ON privacy_audit_proofs;
CREATE POLICY rls_privacy_audit_proofs ON privacy_audit_proofs FOR ALL
    USING (rls_tenant_check(tenant_id) OR is_internal_admin())
    WITH CHECK (is_internal_admin());

-- =============================================================================
-- STEP 7: CREATE HELPER FUNCTIONS
-- =============================================================================

-- Function to get memory events in DESC order (for Traces page)
CREATE OR REPLACE FUNCTION get_memory_events_desc(
    p_tenant_id UUID,
    p_limit INT DEFAULT 100,
    p_offset INT DEFAULT 0
)
RETURNS TABLE (
    id UUID,
    memory_id UUID,
    event_type VARCHAR,
    event_data JSONB,
    created_at TIMESTAMPTZ,
    actor_id VARCHAR,
    sequence_number BIGINT
)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = public
AS $$
BEGIN
    -- Set tenant context for RLS
    PERFORM set_config('app.current_tenant_id', p_tenant_id::text, true);

    RETURN QUERY
    SELECT
        me.id,
        me.memory_id,
        me.event_type,
        me.event_data,
        me.created_at,
        me.actor_id,
        me.sequence_number
    FROM memory_events me
    WHERE me.tenant_id = p_tenant_id
    ORDER BY me.created_at DESC, me.sequence_number DESC
    LIMIT p_limit
    OFFSET p_offset;
END;
$$;

-- Function to run contradiction detection scan
CREATE OR REPLACE FUNCTION run_contradiction_scan(p_tenant_id UUID)
RETURNS TABLE (
    conflicts_detected INT,
    conflicts_resolved INT,
    scan_duration_ms INT
)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = public
AS $$
DECLARE
    v_start TIMESTAMPTZ := clock_timestamp();
    v_detected INT := 0;
    v_resolved INT := 0;
    r RECORD;
BEGIN
    -- Set tenant context for RLS
    PERFORM set_config('app.current_tenant_id', p_tenant_id::text, true);
    PERFORM set_config('app.role', 'internal_admin', true);

    -- Find L3 fact memories with high similarity but different content
    FOR r IN (
        SELECT
            m1.id AS memory_a,
            m2.id AS memory_b,
            1 - (m1.embedding <=> m2.embedding) AS similarity,
            m1.content AS content_a,
            m2.content AS content_b
        FROM memories m1
        JOIN memories m2 ON m1.id < m2.id
            AND m1.tenant_id = m2.tenant_id
            AND m1.layer = 'L3_KNOWLEDGE'
            AND m2.layer = 'L3_KNOWLEDGE'
        WHERE m1.tenant_id = p_tenant_id
            AND m1.embedding IS NOT NULL
            AND m2.embedding IS NOT NULL
            AND 1 - (m1.embedding <=> m2.embedding) > 0.85
            AND m1.content != m2.content
            AND NOT EXISTS (
                SELECT 1 FROM conflict_log cl
                WHERE cl.tenant_id = p_tenant_id
                    AND ((cl.memory_a = m1.id AND cl.memory_b = m2.id)
                         OR (cl.memory_a = m2.id AND cl.memory_b = m1.id))
                    AND cl.status != 'resolved'
            )
        LIMIT 100
    )
    LOOP
        -- Insert detected conflict
        INSERT INTO conflict_log (
            tenant_id, memory_a, memory_b, conflict_type, severity, description, detected_at, status
        ) VALUES (
            p_tenant_id, r.memory_a, r.memory_b,
            'semantic_contradiction',
            (r.similarity - 0.85) / 0.15, -- Normalize severity
            format('High similarity (%s) but different content', round(r.similarity::numeric, 3)),
            NOW(),
            'pending'
        );
        v_detected := v_detected + 1;
    END LOOP;

    -- Also detect entity-value mismatches
    FOR r IN (
        SELECT DISTINCT
            me1.memory_id AS memory_a,
            me2.memory_id AS memory_b,
            e.name AS entity_name
        FROM memory_entities me1
        JOIN memory_entities me2 ON me1.entity_id = me2.entity_id AND me1.memory_id < me2.memory_id
        JOIN entities e ON me1.entity_id = e.id
        JOIN memories m1 ON me1.memory_id = m1.id
        JOIN memories m2 ON me2.memory_id = m2.id
        WHERE me1.tenant_id = p_tenant_id
            AND m1.layer = 'L3_KNOWLEDGE'
            AND m2.layer = 'L3_KNOWLEDGE'
            AND NOT EXISTS (
                SELECT 1 FROM conflict_log cl
                WHERE cl.tenant_id = p_tenant_id
                    AND cl.entity_involved = me1.entity_id
                    AND cl.status != 'resolved'
            )
        LIMIT 50
    )
    LOOP
        -- Check if content actually contradicts
        IF EXISTS (
            SELECT 1 FROM memories m1, memories m2
            WHERE m1.id = r.memory_a AND m2.id = r.memory_b
                AND m1.content LIKE '%not%' || r.entity_name || '%'
                AND m2.content NOT LIKE '%not%'
        ) THEN
            INSERT INTO conflict_log (
                tenant_id, memory_a, memory_b, conflict_type, severity, description, entity_involved, detected_at, status
            ) VALUES (
                p_tenant_id, r.memory_a, r.memory_b,
                'entity_value_mismatch',
                0.7,
                format('Conflicting statements about entity: %s', r.entity_name),
                (SELECT entity_id FROM memory_entities WHERE memory_id = r.memory_a LIMIT 1),
                NOW(),
                'pending'
            );
            v_detected := v_detected + 1;
        END IF;
    END LOOP;

    RETURN QUERY SELECT
        v_detected,
        v_resolved,
        EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::INT;
END;
$$;

-- Function to get mind health trend over N days
CREATE OR REPLACE FUNCTION get_mind_health_trend(
    p_tenant_id UUID,
    p_days INT DEFAULT 30
)
RETURNS TABLE (
    date DATE,
    overall_score FLOAT,
    confidence_drift FLOAT,
    l3_stability FLOAT,
    entity_consistency FLOAT,
    decay_rate FLOAT,
    anomaly_count INT
)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = public
AS $$
BEGIN
    RETURN QUERY
    SELECT
        mhd.date,
        mhd.overall_score,
        mhd.confidence_drift,
        mhd.l3_fact_stability,
        mhd.entity_consistency,
        mhd.memory_decay_rate,
        mhd.anomaly_count
    FROM mind_health_daily mhd
    WHERE mhd.tenant_id = p_tenant_id
        AND mhd.date >= CURRENT_DATE - p_days
    ORDER BY mhd.date ASC;
END;
$$;

-- Function to compute and store daily mind health score
CREATE OR REPLACE FUNCTION compute_daily_mind_health(p_tenant_id UUID, p_date DATE DEFAULT CURRENT_DATE)
RETURNS VOID
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = public
AS $$
DECLARE
    v_overall FLOAT;
    v_drift FLOAT := 0;
    v_l3_stability FLOAT := 1.0;
    v_entity_consistency FLOAT := 1.0;
    v_decay_rate FLOAT := 0;
    v_anomaly_count INT := 0;
    v_total_memories INT := 0;
    v_contradictions INT := 0;
    v_hallucination_rate FLOAT := 0;
BEGIN
    -- Set tenant context
    PERFORM set_config('app.current_tenant_id', p_tenant_id::text, true);
    PERFORM set_config('app.role', 'internal_admin', true);

    -- Count total memories
    SELECT COUNT(*) INTO v_total_memories FROM memories WHERE tenant_id = p_tenant_id;

    -- Calculate confidence drift from recent observations
    SELECT COALESCE(AVG(drift), 0) INTO v_drift
    FROM mind_confidence_observations
    WHERE tenant_id = p_tenant_id
        AND timestamp >= p_date - INTERVAL '1 day'
        AND timestamp < p_date + INTERVAL '1 day';

    -- Calculate L3 stability (ratio of unchanged L3 facts)
    SELECT COALESCE(
        1.0 - (COUNT(*)::FLOAT / NULLIF((SELECT COUNT(*) FROM memories WHERE tenant_id = p_tenant_id AND layer = 'L3_KNOWLEDGE'), 0)),
        1.0
    ) INTO v_l3_stability
    FROM memory_events
    WHERE tenant_id = p_tenant_id
        AND event_type IN ('updated', 'invalidated')
        AND created_at >= p_date - INTERVAL '1 day'
        AND memory_id IN (SELECT id FROM memories WHERE layer = 'L3_KNOWLEDGE');

    -- Calculate entity consistency
    SELECT COALESCE(
        1.0 - (COUNT(DISTINCT cl.entity_involved)::FLOAT / NULLIF((SELECT COUNT(*) FROM entities WHERE tenant_id = p_tenant_id), 0)),
        1.0
    ) INTO v_entity_consistency
    FROM conflict_log cl
    WHERE cl.tenant_id = p_tenant_id
        AND cl.entity_involved IS NOT NULL
        AND cl.status = 'pending';

    -- Calculate memory decay rate
    SELECT COALESCE(AVG(1.0 - confidence), 0) INTO v_decay_rate
    FROM memories
    WHERE tenant_id = p_tenant_id
        AND confidence < 1.0;

    -- Count anomalies (low confidence, flagged memories)
    SELECT COUNT(*) INTO v_anomaly_count
    FROM memories
    WHERE tenant_id = p_tenant_id
        AND (confidence < 0.3 OR (metadata->>'hallucination_flagged')::boolean = true);

    -- Count active contradictions
    SELECT COUNT(*) INTO v_contradictions
    FROM conflict_log
    WHERE tenant_id = p_tenant_id AND status = 'pending';

    -- Calculate hallucination rate
    SELECT COALESCE(
        COUNT(*)::FLOAT / NULLIF(v_total_memories, 0),
        0
    ) INTO v_hallucination_rate
    FROM mind_hallucination_events
    WHERE tenant_id = p_tenant_id
        AND detected_at >= p_date - INTERVAL '7 days';

    -- Calculate overall score (weighted average)
    v_overall := (
        (1.0 - ABS(v_drift)) * 0.2 +
        v_l3_stability * 0.25 +
        v_entity_consistency * 0.2 +
        (1.0 - v_decay_rate) * 0.15 +
        (1.0 - LEAST(v_anomaly_count::FLOAT / NULLIF(v_total_memories, 0) * 10, 1.0)) * 0.1 +
        (1.0 - v_hallucination_rate * 10) * 0.1
    );

    -- Clamp to 0-1
    v_overall := GREATEST(0, LEAST(1, v_overall));

    -- Upsert daily score
    INSERT INTO mind_health_daily (
        tenant_id, date, overall_score, confidence_drift, l3_fact_stability,
        entity_consistency, memory_decay_rate, anomaly_count, total_memories,
        active_contradictions, hallucination_rate
    ) VALUES (
        p_tenant_id, p_date, v_overall, v_drift, v_l3_stability,
        v_entity_consistency, v_decay_rate, v_anomaly_count, v_total_memories,
        v_contradictions, v_hallucination_rate
    )
    ON CONFLICT (tenant_id, date) DO UPDATE SET
        overall_score = EXCLUDED.overall_score,
        confidence_drift = EXCLUDED.confidence_drift,
        l3_fact_stability = EXCLUDED.l3_fact_stability,
        entity_consistency = EXCLUDED.entity_consistency,
        memory_decay_rate = EXCLUDED.memory_decay_rate,
        anomaly_count = EXCLUDED.anomaly_count,
        total_memories = EXCLUDED.total_memories,
        active_contradictions = EXCLUDED.active_contradictions,
        hallucination_rate = EXCLUDED.hallucination_rate;
END;
$$;

-- Function to get memory timeline with time-travel capability
CREATE OR REPLACE FUNCTION get_memory_timeline(
    p_memory_id UUID,
    p_from TIMESTAMPTZ DEFAULT NULL,
    p_to TIMESTAMPTZ DEFAULT NULL
)
RETURNS TABLE (
    id UUID,
    event_type VARCHAR,
    event_data JSONB,
    version_number INT,
    actor_id VARCHAR,
    occurred_at TIMESTAMPTZ
)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = public
AS $$
BEGIN
    RETURN QUERY
    SELECT
        mt.id,
        mt.event_type,
        mt.event_data,
        mt.version_number,
        mt.actor_id,
        mt.occurred_at
    FROM memory_timeline mt
    WHERE mt.memory_id = p_memory_id
        AND (p_from IS NULL OR mt.occurred_at >= p_from)
        AND (p_to IS NULL OR mt.occurred_at <= p_to)
    ORDER BY mt.occurred_at ASC, mt.version_number ASC;
END;
$$;

-- Function to replay mutations to a specific sequence number
CREATE OR REPLACE FUNCTION replay_mutations_to_seq(
    p_memory_id UUID,
    p_target_seq BIGINT
)
RETURNS JSONB
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = public
AS $$
DECLARE
    v_state JSONB;
    r RECORD;
BEGIN
    -- Get initial state (first snapshot before target seq)
    SELECT state INTO v_state
    FROM memory_states
    WHERE memory_id = p_memory_id AND sequence_number <= p_target_seq
    ORDER BY sequence_number DESC
    LIMIT 1;

    -- If no snapshot, start from empty state
    IF v_state IS NULL THEN
        SELECT jsonb_build_object(
            'id', m.id,
            'content', '',
            'confidence', 1.0,
            'metadata', '{}'::jsonb
        ) INTO v_state
        FROM memories m
        WHERE m.id = p_memory_id;
    END IF;

    -- Apply mutations up to target sequence
    FOR r IN (
        SELECT * FROM memory_mutations
        WHERE memory_id = p_memory_id
            AND sequence_number <= p_target_seq
            AND sequence_number > COALESCE(
                (SELECT MAX(sequence_number) FROM memory_states WHERE memory_id = p_memory_id AND sequence_number <= p_target_seq),
                0
            )
        ORDER BY sequence_number ASC
    )
    LOOP
        -- Apply each mutation
        CASE r.mutation_type
            WHEN 'content_update' THEN
                v_state := v_state || jsonb_build_object('content', r.new_content);
            WHEN 'metadata_update' THEN
                v_state := v_state || jsonb_build_object('metadata', r.new_metadata);
            WHEN 'confidence_update' THEN
                v_state := v_state || jsonb_build_object('confidence', (r.new_metadata->>'confidence')::float);
            WHEN 'embedding_update' THEN
                -- Skip embedding in state (too large)
                NULL;
            ELSE
                -- Apply diff if available
                IF r.diff IS NOT NULL THEN
                    v_state := v_state || r.diff;
                END IF;
        END CASE;
    END LOOP;

    RETURN v_state;
END;
$$;

-- Function to verify privacy chain integrity
CREATE OR REPLACE FUNCTION verify_privacy_chain(p_tenant_id UUID)
RETURNS TABLE (
    is_valid BOOLEAN,
    chain_length INT,
    first_entry_hash VARCHAR,
    last_entry_hash VARCHAR,
    broken_at_id UUID,
    error_message TEXT
)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = public
AS $$
DECLARE
    v_prev_hash VARCHAR := NULL;
    v_chain_length INT := 0;
    v_first_hash VARCHAR;
    v_last_hash VARCHAR;
    v_broken_id UUID;
    v_error TEXT;
    r RECORD;
BEGIN
    -- Set internal admin to bypass RLS for verification
    PERFORM set_config('app.role', 'internal_admin', true);

    FOR r IN (
        SELECT id, content_hash, previous_hash, created_at
        FROM privacy_audit_entries
        WHERE tenant_id = p_tenant_id
        ORDER BY created_at ASC
    )
    LOOP
        v_chain_length := v_chain_length + 1;

        IF v_chain_length = 1 THEN
            v_first_hash := r.content_hash;
        END IF;

        -- Verify chain continuity
        IF v_prev_hash IS NOT NULL AND r.previous_hash != v_prev_hash THEN
            v_broken_id := r.id;
            v_error := format('Chain broken at entry %s: expected previous_hash %s but got %s',
                r.id, v_prev_hash, r.previous_hash);
            RETURN QUERY SELECT FALSE, v_chain_length, v_first_hash, r.content_hash, v_broken_id, v_error;
            RETURN;
        END IF;

        v_prev_hash := r.content_hash;
        v_last_hash := r.content_hash;
    END LOOP;

    -- All entries verified
    RETURN QUERY SELECT TRUE, v_chain_length, v_first_hash, v_last_hash, NULL::UUID, NULL::TEXT;
END;
$$;

-- Function to get API keys with status calculation
CREATE OR REPLACE FUNCTION get_api_keys_with_status(p_tenant_id UUID, p_include_revoked BOOLEAN DEFAULT FALSE)
RETURNS TABLE (
    id UUID,
    name VARCHAR,
    key_prefix VARCHAR,
    scopes TEXT[],
    created_by VARCHAR,
    last_used_at TIMESTAMPTZ,
    expires_at TIMESTAMPTZ,
    revoked_at TIMESTAMPTZ,
    created_at TIMESTAMPTZ,
    status VARCHAR
)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = public
AS $$
BEGIN
    RETURN QUERY
    SELECT
        k.id,
        k.name,
        k.key_prefix,
        k.scopes,
        k.created_by,
        k.last_used_at,
        k.expires_at,
        k.revoked_at,
        k.created_at,
        CASE
            WHEN k.revoked_at IS NOT NULL THEN 'revoked'
            WHEN k.expires_at IS NOT NULL AND k.expires_at < NOW() THEN 'expired'
            ELSE 'active'
        END::VARCHAR AS status
    FROM tenant_api_keys k
    WHERE k.tenant_id = p_tenant_id
        AND (p_include_revoked OR k.revoked_at IS NULL)
    ORDER BY k.created_at DESC;
END;
$$;

-- Function for admin stats bypass (self-host admin in SaaS mode)
CREATE OR REPLACE FUNCTION get_system_metrics(p_user_id VARCHAR)
RETURNS JSONB
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = public
AS $$
DECLARE
    v_is_admin BOOLEAN;
    v_metrics JSONB;
BEGIN
    -- Check if user is root admin
    SELECT EXISTS (
        SELECT 1 FROM tenant_users
        WHERE user_id = p_user_id AND role = 'owner'
        AND tenant_id IN (
            SELECT id FROM tenants WHERE created_at = (SELECT MIN(created_at) FROM tenants)
        )
    ) INTO v_is_admin;

    IF NOT v_is_admin THEN
        RETURN jsonb_build_object('error', 'Unauthorized');
    END IF;

    -- Return system metrics (these would normally come from the API)
    RETURN jsonb_build_object(
        'authorized', TRUE,
        'db_status', jsonb_build_object(
            'total_memories', (SELECT COUNT(*) FROM memories),
            'total_entities', (SELECT COUNT(*) FROM entities),
            'total_tenants', (SELECT COUNT(*) FROM tenants),
            'active_sessions', (SELECT COUNT(*) FROM conversation_sessions WHERE ended_at IS NULL)
        )
    );
END;
$$;

-- =============================================================================
-- STEP 8: ADD TRIGGER FOR AUTOMATIC MUTATION LOGGING
-- =============================================================================

CREATE OR REPLACE FUNCTION log_memory_mutation()
RETURNS TRIGGER
LANGUAGE plpgsql
SECURITY DEFINER
AS $$
BEGIN
    IF TG_OP = 'UPDATE' THEN
        INSERT INTO memory_mutations (
            tenant_id, memory_id, mutation_type,
            old_content, new_content,
            old_metadata, new_metadata,
            actor_id, actor_type
        ) VALUES (
            NEW.tenant_id, NEW.id,
            CASE
                WHEN OLD.content != NEW.content THEN 'content_update'
                WHEN OLD.metadata != NEW.metadata THEN 'metadata_update'
                WHEN OLD.confidence != NEW.confidence THEN 'confidence_update'
                ELSE 'general_update'
            END,
            OLD.content, NEW.content,
            OLD.metadata, NEW.metadata,
            current_setting('app.user_id', true),
            COALESCE(current_setting('app.actor_type', true), 'system')
        );

        -- Also add to timeline
        INSERT INTO memory_timeline (
            tenant_id, memory_id, event_type, event_data, version_number, actor_id
        )
        SELECT
            NEW.tenant_id, NEW.id, 'updated',
            jsonb_build_object(
                'old_content', LEFT(OLD.content, 200),
                'new_content', LEFT(NEW.content, 200),
                'confidence_change', NEW.confidence - OLD.confidence
            ),
            COALESCE((SELECT MAX(version_number) + 1 FROM memory_timeline WHERE memory_id = NEW.id), 1),
            current_setting('app.user_id', true);
    END IF;

    RETURN NEW;
END;
$$;

-- Create trigger if not exists
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'trg_memory_mutation_log') THEN
        CREATE TRIGGER trg_memory_mutation_log
            AFTER UPDATE ON memories
            FOR EACH ROW
            EXECUTE FUNCTION log_memory_mutation();
    END IF;
END $$;

-- =============================================================================
-- STEP 9: GRANT PERMISSIONS
-- =============================================================================

GRANT SELECT, INSERT, UPDATE, DELETE ON conflict_log TO postgres;
GRANT SELECT, INSERT, UPDATE, DELETE ON mind_health_daily TO postgres;
GRANT SELECT, INSERT, UPDATE, DELETE ON mind_calibration TO postgres;
GRANT SELECT, INSERT, UPDATE, DELETE ON memory_mutations TO postgres;
GRANT SELECT, INSERT, UPDATE, DELETE ON memory_states TO postgres;
GRANT SELECT, INSERT, UPDATE, DELETE ON memory_timeline TO postgres;
GRANT SELECT, INSERT, UPDATE, DELETE ON reasoning_executions TO postgres;
GRANT SELECT, INSERT, UPDATE, DELETE ON reasoning_traces TO postgres;
GRANT SELECT, INSERT, UPDATE, DELETE ON reasoning_insights TO postgres;
GRANT SELECT, INSERT, UPDATE, DELETE ON reasoning_disagreements TO postgres;

GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO postgres;

-- =============================================================================
-- VERIFICATION
-- =============================================================================

DO $$
DECLARE
    v_new_tables INT;
    v_new_policies INT;
    v_new_functions INT;
BEGIN
    SELECT COUNT(*) INTO v_new_tables
    FROM information_schema.tables
    WHERE table_schema = 'public'
        AND table_name IN (
            'conflict_log', 'mind_health_daily', 'mind_calibration',
            'memory_mutations', 'memory_states', 'memory_timeline',
            'reasoning_executions', 'reasoning_traces', 'reasoning_insights', 'reasoning_disagreements'
        );

    SELECT COUNT(*) INTO v_new_policies
    FROM pg_policies
    WHERE schemaname = 'public'
        AND tablename IN (
            'conflict_log', 'mind_health_daily', 'mind_calibration',
            'memory_mutations', 'memory_states', 'memory_timeline',
            'reasoning_executions', 'reasoning_traces', 'reasoning_insights', 'reasoning_disagreements'
        );

    SELECT COUNT(*) INTO v_new_functions
    FROM pg_proc p
    JOIN pg_namespace n ON p.pronamespace = n.oid
    WHERE n.nspname = 'public'
        AND p.proname IN (
            'get_memory_events_desc', 'run_contradiction_scan', 'get_mind_health_trend',
            'compute_daily_mind_health', 'get_memory_timeline', 'replay_mutations_to_seq',
            'verify_privacy_chain', 'get_api_keys_with_status', 'get_system_metrics'
        );

    RAISE NOTICE '==========================================';
    RAISE NOTICE 'Comprehensive Fixes Migration Complete';
    RAISE NOTICE '==========================================';
    RAISE NOTICE 'New tables created: %', v_new_tables;
    RAISE NOTICE 'RLS policies created: %', v_new_policies;
    RAISE NOTICE 'Helper functions created: %', v_new_functions;
    RAISE NOTICE '==========================================';
END $$;

COMMIT;
