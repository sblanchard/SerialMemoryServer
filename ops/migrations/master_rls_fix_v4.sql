-- =============================================================================
-- MASTER RLS FIX v4 - SerialMemory
-- Comprehensive Row-Level Security Policy Repair
-- =============================================================================
-- This migration:
-- 1. Creates unified tenant check functions supporting ALL data types
-- 2. Drops all existing RLS policies
-- 3. Enables RLS on all tenant tables
-- 4. Creates consistent policies with internal_admin bypass
-- =============================================================================

BEGIN;

-- =============================================================================
-- STEP 1: CREATE UNIFIED TENANT CHECK FUNCTIONS
-- =============================================================================

-- Universal function for UUID tenant_id columns
CREATE OR REPLACE FUNCTION rls_tenant_check(p_row_tenant_id UUID)
RETURNS BOOLEAN
LANGUAGE plpgsql
STABLE
AS $$
DECLARE
    v_role TEXT;
    v_tenant_id TEXT;
BEGIN
    -- Check for internal admin bypass
    v_role := current_setting('app.role', true);
    IF v_role = 'internal_admin' THEN
        RETURN TRUE;
    END IF;

    -- Check tenant match (try both session variables)
    v_tenant_id := COALESCE(
        current_setting('app.current_tenant_id', true),
        current_setting('app.tenant_id', true)
    );

    IF v_tenant_id IS NULL OR v_tenant_id = '' THEN
        RETURN FALSE;
    END IF;

    RETURN p_row_tenant_id::TEXT = v_tenant_id;
END;
$$;

-- Universal function for TEXT/VARCHAR tenant_id columns
CREATE OR REPLACE FUNCTION rls_tenant_check_text(p_row_tenant_id TEXT)
RETURNS BOOLEAN
LANGUAGE plpgsql
STABLE
AS $$
DECLARE
    v_role TEXT;
    v_tenant_id TEXT;
BEGIN
    -- Check for internal admin bypass
    v_role := current_setting('app.role', true);
    IF v_role = 'internal_admin' THEN
        RETURN TRUE;
    END IF;

    -- Check tenant match
    v_tenant_id := COALESCE(
        current_setting('app.current_tenant_id', true),
        current_setting('app.tenant_id', true)
    );

    IF v_tenant_id IS NULL OR v_tenant_id = '' THEN
        RETURN FALSE;
    END IF;

    RETURN p_row_tenant_id = v_tenant_id;
END;
$$;

-- Alias for backward compatibility
CREATE OR REPLACE FUNCTION rls_tenant_check_varchar(p_row_tenant_id VARCHAR)
RETURNS BOOLEAN
LANGUAGE plpgsql
STABLE
AS $$
BEGIN
    RETURN rls_tenant_check_text(p_row_tenant_id::TEXT);
END;
$$;

-- Internal admin only check
CREATE OR REPLACE FUNCTION is_internal_admin()
RETURNS BOOLEAN
LANGUAGE plpgsql
STABLE
AS $$
BEGIN
    RETURN current_setting('app.role', true) = 'internal_admin';
END;
$$;

-- =============================================================================
-- STEP 2: DROP ALL EXISTING RLS POLICIES
-- =============================================================================

DO $$
DECLARE
    r RECORD;
BEGIN
    FOR r IN (
        SELECT schemaname, tablename, policyname
        FROM pg_policies
        WHERE schemaname = 'public'
    ) LOOP
        EXECUTE format('DROP POLICY IF EXISTS %I ON %I.%I', r.policyname, r.schemaname, r.tablename);
        RAISE NOTICE 'Dropped policy: % on %.%', r.policyname, r.schemaname, r.tablename;
    END LOOP;
END $$;

-- =============================================================================
-- STEP 3: ENABLE RLS ON ALL TENANT TABLES (excluding views)
-- =============================================================================

DO $$
DECLARE
    r RECORD;
BEGIN
    FOR r IN (
        SELECT DISTINCT c.table_name
        FROM information_schema.columns c
        JOIN information_schema.tables t ON c.table_name = t.table_name AND c.table_schema = t.table_schema
        WHERE c.table_schema = 'public'
          AND c.column_name = 'tenant_id'
          AND t.table_type = 'BASE TABLE'
    ) LOOP
        EXECUTE format('ALTER TABLE %I ENABLE ROW LEVEL SECURITY', r.table_name);
        EXECUTE format('ALTER TABLE %I FORCE ROW LEVEL SECURITY', r.table_name);
        RAISE NOTICE 'Enabled RLS on: %', r.table_name;
    END LOOP;
END $$;

-- =============================================================================
-- STEP 4: CREATE POLICIES DYNAMICALLY BASED ON COLUMN TYPE
-- =============================================================================

-- Generic policy creator that handles UUID vs TEXT (skips views)
CREATE OR REPLACE FUNCTION create_rls_policy(
    p_table_name TEXT,
    p_policy_name TEXT,
    p_internal_write_only BOOLEAN DEFAULT FALSE
)
RETURNS VOID
LANGUAGE plpgsql
AS $$
DECLARE
    v_dtype TEXT;
    v_table_type TEXT;
    v_check_func TEXT;
BEGIN
    -- Check if this is a view (skip views)
    SELECT table_type INTO v_table_type
    FROM information_schema.tables
    WHERE table_schema = 'public' AND table_name = p_table_name;

    IF v_table_type IS NULL THEN
        RAISE NOTICE 'Table/view % does not exist, skipping', p_table_name;
        RETURN;
    END IF;

    IF v_table_type != 'BASE TABLE' THEN
        RAISE NOTICE 'Skipping % (is a %)', p_table_name, v_table_type;
        RETURN;
    END IF;

    -- Get the data type of tenant_id
    SELECT data_type INTO v_dtype
    FROM information_schema.columns
    WHERE table_schema = 'public'
      AND table_name = p_table_name
      AND column_name = 'tenant_id';

    IF v_dtype IS NULL THEN
        RAISE NOTICE 'Table % has no tenant_id column, skipping', p_table_name;
        RETURN;
    END IF;

    -- Determine which check function to use
    IF v_dtype = 'uuid' THEN
        v_check_func := 'rls_tenant_check(tenant_id)';
    ELSE
        v_check_func := 'rls_tenant_check_text(tenant_id)';
    END IF;

    -- Create the policy
    IF p_internal_write_only THEN
        -- Read by tenant, write by internal_admin only
        EXECUTE format(
            'CREATE POLICY %I ON %I FOR ALL USING (%s) WITH CHECK (is_internal_admin())',
            p_policy_name, p_table_name, v_check_func
        );
    ELSE
        -- Full CRUD by tenant
        EXECUTE format(
            'CREATE POLICY %I ON %I FOR ALL USING (%s) WITH CHECK (%s)',
            p_policy_name, p_table_name, v_check_func, v_check_func
        );
    END IF;

    RAISE NOTICE 'Created policy % on % (type: %)', p_policy_name, p_table_name, v_dtype;
END;
$$;

-- =============================================================================
-- STEP 5: CREATE ALL POLICIES
-- =============================================================================

-- CORE MEMORY TABLES (full CRUD)
SELECT create_rls_policy('memories', 'rls_memories');
SELECT create_rls_policy('entities', 'rls_entities');
SELECT create_rls_policy('entity_relationships', 'rls_entity_relationships');
SELECT create_rls_policy('memory_entities', 'rls_memory_entities');
SELECT create_rls_policy('memory_events', 'rls_memory_events');
SELECT create_rls_policy('conversation_sessions', 'rls_conversation_sessions');
SELECT create_rls_policy('memory_layers', 'rls_memory_layers');
SELECT create_rls_policy('memory_classification_events', 'rls_memory_classification_events');
SELECT create_rls_policy('memory_processing_queue', 'rls_memory_processing_queue');
SELECT create_rls_policy('layer_transition_queue', 'rls_layer_transition_queue');

-- USER & TENANT MANAGEMENT (full CRUD)
SELECT create_rls_policy('tenant_users', 'rls_tenant_users');
SELECT create_rls_policy('tenant_api_keys', 'rls_tenant_api_keys');
SELECT create_rls_policy('tenant_settings', 'rls_tenant_settings');
SELECT create_rls_policy('tenant_privacy_settings', 'rls_tenant_privacy_settings');
SELECT create_rls_policy('tenant_secrets', 'rls_tenant_secrets');
SELECT create_rls_policy('tenant_workspaces', 'rls_tenant_workspaces');
SELECT create_rls_policy('user_personas', 'rls_user_personas');

-- GRAPH TABLES (full CRUD)
SELECT create_rls_policy('graph_events', 'rls_graph_events');
SELECT create_rls_policy('graph_event_stats', 'rls_graph_event_stats');
SELECT create_rls_policy('knowledge_graph_nodes', 'rls_knowledge_graph_nodes');

-- REASONING TABLES (full CRUD)
SELECT create_rls_policy('reasoning_runs', 'rls_reasoning_runs');
SELECT create_rls_policy('reasoning_results', 'rls_reasoning_results');
SELECT create_rls_policy('reasoning_stats', 'rls_reasoning_stats');
SELECT create_rls_policy('model_disagreements', 'rls_model_disagreements');

-- SHADOW BRANCH TABLES (full CRUD)
SELECT create_rls_policy('shadow_branches', 'rls_shadow_branches');
SELECT create_rls_policy('shadow_memories', 'rls_shadow_memories');
SELECT create_rls_policy('shadow_entities', 'rls_shadow_entities');
SELECT create_rls_policy('shadow_relationships', 'rls_shadow_relationships');
SELECT create_rls_policy('shadow_promotion_log', 'rls_shadow_promotion_log');

-- MIND HEALTH TABLES (full CRUD)
SELECT create_rls_policy('mind_confidence_observations', 'rls_mind_confidence');
SELECT create_rls_policy('mind_hallucination_events', 'rls_mind_hallucination');
SELECT create_rls_policy('mind_contradiction_events', 'rls_mind_contradiction');
SELECT create_rls_policy('mind_daily_scores', 'rls_mind_daily_scores');

-- BILLING & USAGE (full CRUD)
SELECT create_rls_policy('usage_events', 'rls_usage_events');
SELECT create_rls_policy('usage_daily_rollups', 'rls_usage_daily_rollups');
SELECT create_rls_policy('billing_cycles', 'rls_billing_cycles');
SELECT create_rls_policy('payment_history', 'rls_payment_history');
SELECT create_rls_policy('tenant_subscriptions', 'rls_tenant_subscriptions');
SELECT create_rls_policy('stripe_webhook_events', 'rls_stripe_webhook_events');
SELECT create_rls_policy('usage_forecasts', 'rls_usage_forecasts');
SELECT create_rls_policy('budget_alert_thresholds', 'rls_budget_alert_thresholds');
SELECT create_rls_policy('cost_records', 'rls_cost_records');
SELECT create_rls_policy('cost_recommendations', 'rls_cost_recommendations');

-- RATE LIMITING (full CRUD)
SELECT create_rls_policy('rate_limit_buckets', 'rls_rate_limit_buckets');
SELECT create_rls_policy('rate_limit_hits', 'rls_rate_limit_hits');

-- SELF HEALING (full CRUD)
SELECT create_rls_policy('self_healing_recommendations', 'rls_self_healing_recommendations');
SELECT create_rls_policy('learned_heuristics', 'rls_learned_heuristics');

-- AGENT/TRACE TABLES (full CRUD)
SELECT create_rls_policy('agent_traces', 'rls_agent_traces');
SELECT create_rls_policy('onboarding_events', 'rls_onboarding_events');
SELECT create_rls_policy('supervised_jobs', 'rls_supervised_jobs');

-- CRAWL/RECRAWL TABLES (full CRUD)
SELECT create_rls_policy('memory_recrawl_jobs', 'rls_memory_recrawl_jobs');
SELECT create_rls_policy('recrawl_failures', 'rls_recrawl_failures');

-- DATA EXPORT/DELETE (full CRUD)
SELECT create_rls_policy('deletion_requests', 'rls_deletion_requests');
SELECT create_rls_policy('export_requests', 'rls_export_requests');

-- ANOMALY/ABUSE (full CRUD)
SELECT create_rls_policy('anomaly_cache', 'rls_anomaly_cache');
SELECT create_rls_policy('abuse_patterns', 'rls_abuse_patterns');
SELECT create_rls_policy('payload_violations', 'rls_payload_violations');

-- AUTH TABLES (full CRUD)
SELECT create_rls_policy('email_verification_tokens', 'rls_email_verification_tokens');

-- ENGINEERING VIEWS (full CRUD)
SELECT create_rls_policy('v_engineering_entities', 'rls_v_engineering_entities');
SELECT create_rls_policy('v_physical_relationships', 'rls_v_physical_relationships');
SELECT create_rls_policy('v_software_entities', 'rls_v_software_entities');

-- SECURITY & AUDIT TABLES (internal write only)
SELECT create_rls_policy('audit_logs', 'rls_audit_logs', TRUE);
SELECT create_rls_policy('security_events', 'rls_security_events', TRUE);
SELECT create_rls_policy('admin_actions', 'rls_admin_actions', TRUE);
SELECT create_rls_policy('privacy_audit_entries', 'rls_privacy_audit_entries', TRUE);
SELECT create_rls_policy('privacy_audit_proofs', 'rls_privacy_audit_proofs', TRUE);

-- INTEGRITY TABLES (internal write only)
SELECT create_rls_policy('integrity_chain_anchors', 'rls_integrity_chain_anchors', TRUE);
SELECT create_rls_policy('integrity_verification_runs', 'rls_integrity_verification_runs', TRUE);

-- EMERGENCY CUTOFFS - Special case: internal_admin only for all operations
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'emergency_cutoffs') THEN
        EXECUTE 'CREATE POLICY rls_emergency_cutoffs ON emergency_cutoffs FOR ALL
                 USING (is_internal_admin() OR rls_tenant_check_text(tenant_id::text))
                 WITH CHECK (is_internal_admin())';
    END IF;
END $$;

-- USAGE ALERTS - Special case
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'usage_alerts') THEN
        IF EXISTS (SELECT 1 FROM information_schema.columns
                   WHERE table_name = 'usage_alerts' AND column_name = 'tenant_id') THEN
            EXECUTE 'CREATE POLICY rls_usage_alerts ON usage_alerts FOR ALL
                     USING (rls_tenant_check(tenant_id))
                     WITH CHECK (rls_tenant_check(tenant_id))';
        END IF;
    END IF;
END $$;

-- =============================================================================
-- STEP 6: CLEANUP HELPER FUNCTION
-- =============================================================================

DROP FUNCTION IF EXISTS create_rls_policy(TEXT, TEXT, BOOLEAN);

-- =============================================================================
-- STEP 7: VERIFICATION
-- =============================================================================

DO $$
DECLARE
    v_tables_with_rls INTEGER;
    v_policies INTEGER;
BEGIN
    SELECT COUNT(DISTINCT tablename) INTO v_tables_with_rls
    FROM pg_tables t
    JOIN pg_class c ON c.relname = t.tablename
    WHERE t.schemaname = 'public' AND c.relrowsecurity = true;

    SELECT COUNT(*) INTO v_policies FROM pg_policies WHERE schemaname = 'public';

    RAISE NOTICE '==========================================';
    RAISE NOTICE 'RLS Migration Complete';
    RAISE NOTICE '==========================================';
    RAISE NOTICE 'Tables with RLS enabled: %', v_tables_with_rls;
    RAISE NOTICE 'Policies created: %', v_policies;
    RAISE NOTICE '==========================================';
END $$;

COMMIT;
