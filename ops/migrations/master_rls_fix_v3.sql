-- =============================================================================
-- MASTER RLS FIX V3 - Complete Multi-Tenant Security
-- =============================================================================
-- This migration fixes ALL RLS issues across the entire system:
-- 1. Unified policy pattern with internal_admin bypass
-- 2. Consistent session variable naming (app.tenant_id AND app.current_tenant_id)
-- 3. Policies for ALL tables that need them
-- 4. Fixes tables with RLS enabled but NO policies (currently blocking all access)
-- =============================================================================

BEGIN;

-- =============================================================================
-- STEP 1: Create/update helper functions
-- =============================================================================

-- Universal RLS check function that handles both session variables
CREATE OR REPLACE FUNCTION rls_tenant_check(p_row_tenant_id UUID)
RETURNS BOOLEAN AS $$
DECLARE
    v_role TEXT;
    v_tenant_id UUID;
BEGIN
    -- Check for internal_admin bypass first
    v_role := COALESCE(current_setting('app.role', true), '');
    IF v_role = 'internal_admin' THEN
        RETURN TRUE;
    END IF;

    -- Try app.current_tenant_id first (new style)
    v_tenant_id := NULLIF(current_setting('app.current_tenant_id', true), '')::uuid;
    IF v_tenant_id IS NOT NULL THEN
        RETURN p_row_tenant_id = v_tenant_id;
    END IF;

    -- Fall back to app.tenant_id (legacy style)
    v_tenant_id := NULLIF(current_setting('app.tenant_id', true), '')::uuid;
    IF v_tenant_id IS NOT NULL THEN
        RETURN p_row_tenant_id = v_tenant_id;
    END IF;

    -- No tenant context = deny access (fail secure)
    RETURN FALSE;
EXCEPTION WHEN OTHERS THEN
    RETURN FALSE;
END;
$$ LANGUAGE plpgsql STABLE SECURITY DEFINER;

-- Universal RLS check for varchar tenant_id columns
CREATE OR REPLACE FUNCTION rls_tenant_check_varchar(p_row_tenant_id VARCHAR)
RETURNS BOOLEAN AS $$
DECLARE
    v_role TEXT;
    v_tenant_str TEXT;
BEGIN
    -- Check for internal_admin bypass first
    v_role := COALESCE(current_setting('app.role', true), '');
    IF v_role = 'internal_admin' THEN
        RETURN TRUE;
    END IF;

    -- Try app.current_tenant_id first
    v_tenant_str := NULLIF(current_setting('app.current_tenant_id', true), '');
    IF v_tenant_str IS NOT NULL THEN
        RETURN p_row_tenant_id = v_tenant_str;
    END IF;

    -- Fall back to app.tenant_id
    v_tenant_str := NULLIF(current_setting('app.tenant_id', true), '');
    IF v_tenant_str IS NOT NULL THEN
        RETURN p_row_tenant_id = v_tenant_str;
    END IF;

    -- No tenant context = deny
    RETURN FALSE;
EXCEPTION WHEN OTHERS THEN
    RETURN FALSE;
END;
$$ LANGUAGE plpgsql STABLE SECURITY DEFINER;

-- Function to check if current session has internal_admin role
CREATE OR REPLACE FUNCTION is_internal_admin()
RETURNS BOOLEAN AS $$
BEGIN
    RETURN COALESCE(current_setting('app.role', true), '') = 'internal_admin';
END;
$$ LANGUAGE plpgsql STABLE;

-- Function to set BOTH tenant context variables (for backward compatibility)
CREATE OR REPLACE FUNCTION set_tenant_context(p_tenant_id UUID)
RETURNS VOID AS $$
BEGIN
    PERFORM set_config('app.tenant_id', p_tenant_id::text, true);
    PERFORM set_config('app.current_tenant_id', p_tenant_id::text, true);
END;
$$ LANGUAGE plpgsql SECURITY DEFINER;

-- Function to set internal admin role + tenant context
CREATE OR REPLACE FUNCTION set_internal_context(p_tenant_id UUID DEFAULT NULL)
RETURNS VOID AS $$
BEGIN
    PERFORM set_config('app.role', 'internal_admin', true);
    IF p_tenant_id IS NOT NULL THEN
        PERFORM set_config('app.tenant_id', p_tenant_id::text, true);
        PERFORM set_config('app.current_tenant_id', p_tenant_id::text, true);
    END IF;
END;
$$ LANGUAGE plpgsql SECURITY DEFINER;

-- =============================================================================
-- STEP 2: Enable RLS on all tenant-scoped tables
-- =============================================================================

-- Core data tables
ALTER TABLE memories ENABLE ROW LEVEL SECURITY;
ALTER TABLE entities ENABLE ROW LEVEL SECURITY;
ALTER TABLE entity_relationships ENABLE ROW LEVEL SECURITY;
ALTER TABLE memory_entities ENABLE ROW LEVEL SECURITY;
ALTER TABLE user_personas ENABLE ROW LEVEL SECURITY;
ALTER TABLE conversation_sessions ENABLE ROW LEVEL SECURITY;

-- Event sourcing & timeline
ALTER TABLE memory_events ENABLE ROW LEVEL SECURITY;
ALTER TABLE memory_traces ENABLE ROW LEVEL SECURITY;
ALTER TABLE memory_state_timeline ENABLE ROW LEVEL SECURITY;

-- Shadow branches
ALTER TABLE shadow_branches ENABLE ROW LEVEL SECURITY;
ALTER TABLE shadow_memories ENABLE ROW LEVEL SECURITY;
ALTER TABLE shadow_entities ENABLE ROW LEVEL SECURITY;
ALTER TABLE shadow_relationships ENABLE ROW LEVEL SECURITY;
ALTER TABLE shadow_promotion_log ENABLE ROW LEVEL SECURITY;

-- Reasoning
ALTER TABLE reasoning_runs ENABLE ROW LEVEL SECURITY;
ALTER TABLE reasoning_steps ENABLE ROW LEVEL SECURITY;
ALTER TABLE reasoning_findings ENABLE ROW LEVEL SECURITY;
ALTER TABLE reasoning_results ENABLE ROW LEVEL SECURITY;
ALTER TABLE dual_pass_reasoning ENABLE ROW LEVEL SECURITY;

-- Mind health
ALTER TABLE mind_confidence_observations ENABLE ROW LEVEL SECURITY;
ALTER TABLE mind_hallucination_events ENABLE ROW LEVEL SECURITY;
ALTER TABLE mind_contradiction_events ENABLE ROW LEVEL SECURITY;
ALTER TABLE mind_daily_scores ENABLE ROW LEVEL SECURITY;

-- Graph
ALTER TABLE graph_events ENABLE ROW LEVEL SECURITY;
ALTER TABLE graph_topology_stats ENABLE ROW LEVEL SECURITY;
ALTER TABLE graph_metrics_hourly ENABLE ROW LEVEL SECURITY;
ALTER TABLE knowledge_graph_nodes ENABLE ROW LEVEL SECURITY;

-- Memory layers & classification
ALTER TABLE memory_layers ENABLE ROW LEVEL SECURITY;
ALTER TABLE memory_classification_events ENABLE ROW LEVEL SECURITY;
ALTER TABLE memory_processing_queue ENABLE ROW LEVEL SECURITY;
ALTER TABLE layer_transition_queue ENABLE ROW LEVEL SECURITY;

-- Billing & usage
ALTER TABLE usage_events ENABLE ROW LEVEL SECURITY;
ALTER TABLE usage_daily_rollups ENABLE ROW LEVEL SECURITY;
ALTER TABLE usage_alerts ENABLE ROW LEVEL SECURITY;
ALTER TABLE billing_cycles ENABLE ROW LEVEL SECURITY;
ALTER TABLE credit_adjustments ENABLE ROW LEVEL SECURITY;

-- Security & audit
ALTER TABLE privacy_audit_entries ENABLE ROW LEVEL SECURITY;
ALTER TABLE privacy_audit_proofs ENABLE ROW LEVEL SECURITY;
ALTER TABLE security_events ENABLE ROW LEVEL SECURITY;
ALTER TABLE security_scans ENABLE ROW LEVEL SECURITY;
ALTER TABLE admin_actions ENABLE ROW LEVEL SECURITY;

-- Control room
ALTER TABLE emergency_cutoffs ENABLE ROW LEVEL SECURITY;

-- Integrity
ALTER TABLE integrity_chain_anchors ENABLE ROW LEVEL SECURITY;
ALTER TABLE integrity_verification_runs ENABLE ROW LEVEL SECURITY;
ALTER TABLE integrity_check_results ENABLE ROW LEVEL SECURITY;

-- Self-healing
ALTER TABLE self_healing_logs ENABLE ROW LEVEL SECURITY;
ALTER TABLE self_healing_recommendations ENABLE ROW LEVEL SECURITY;
ALTER TABLE contradiction_reports ENABLE ROW LEVEL SECURITY;
ALTER TABLE memory_conflicts ENABLE ROW LEVEL SECURITY;
ALTER TABLE memory_contradictions ENABLE ROW LEVEL SECURITY;

-- Jobs & maintenance
ALTER TABLE supervised_jobs ENABLE ROW LEVEL SECURITY;
ALTER TABLE memory_recrawl_jobs ENABLE ROW LEVEL SECURITY;
ALTER TABLE memory_reembed_jobs ENABLE ROW LEVEL SECURITY;
ALTER TABLE maintenance_tasks ENABLE ROW LEVEL SECURITY;
ALTER TABLE maintenance_task_logs ENABLE ROW LEVEL SECURITY;

-- Tenant management (NO RLS - system tables)
-- tenants, tenant_users, tenant_settings, tenant_plans, etc. are system tables

-- =============================================================================
-- STEP 3: Drop ALL existing tenant isolation policies (clean slate)
-- =============================================================================

DO $$
DECLARE
    r RECORD;
BEGIN
    FOR r IN (
        SELECT policyname, tablename
        FROM pg_policies
        WHERE schemaname = 'public'
        AND (policyname LIKE 'tenant_isolation_%'
             OR policyname LIKE 'unified_%'
             OR policyname LIKE '%_tenant_policy'
             OR policyname LIKE '%_tenant_isolation')
    )
    LOOP
        EXECUTE format('DROP POLICY IF EXISTS %I ON %I', r.policyname, r.tablename);
    END LOOP;
END $$;

-- =============================================================================
-- STEP 4: Create unified policies with internal_admin bypass
-- =============================================================================

-- MEMORIES
CREATE POLICY rls_memories ON memories FOR ALL
    USING (rls_tenant_check(tenant_id))
    WITH CHECK (rls_tenant_check(tenant_id));

-- ENTITIES
CREATE POLICY rls_entities ON entities FOR ALL
    USING (rls_tenant_check(tenant_id))
    WITH CHECK (rls_tenant_check(tenant_id));

-- ENTITY_RELATIONSHIPS
CREATE POLICY rls_entity_relationships ON entity_relationships FOR ALL
    USING (rls_tenant_check(tenant_id))
    WITH CHECK (rls_tenant_check(tenant_id));

-- MEMORY_ENTITIES
CREATE POLICY rls_memory_entities ON memory_entities FOR ALL
    USING (rls_tenant_check(tenant_id))
    WITH CHECK (rls_tenant_check(tenant_id));

-- USER_PERSONAS
CREATE POLICY rls_user_personas ON user_personas FOR ALL
    USING (rls_tenant_check(tenant_id))
    WITH CHECK (rls_tenant_check(tenant_id));

-- CONVERSATION_SESSIONS
CREATE POLICY rls_conversation_sessions ON conversation_sessions FOR ALL
    USING (rls_tenant_check(tenant_id))
    WITH CHECK (rls_tenant_check(tenant_id));

-- MEMORY_EVENTS (event sourcing - uses tenant_id column if exists)
DO $$ BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'memory_events' AND column_name = 'tenant_id') THEN
        EXECUTE 'CREATE POLICY rls_memory_events ON memory_events FOR ALL USING (rls_tenant_check(tenant_id)) WITH CHECK (rls_tenant_check(tenant_id))';
    ELSE
        -- If no tenant_id column, allow internal_admin only
        EXECUTE 'CREATE POLICY rls_memory_events ON memory_events FOR ALL USING (is_internal_admin()) WITH CHECK (is_internal_admin())';
    END IF;
END $$;

-- MEMORY_TRACES
DO $$ BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'memory_traces' AND column_name = 'tenant_id') THEN
        EXECUTE 'CREATE POLICY rls_memory_traces ON memory_traces FOR ALL USING (rls_tenant_check(tenant_id)) WITH CHECK (rls_tenant_check(tenant_id))';
    ELSE
        EXECUTE 'CREATE POLICY rls_memory_traces ON memory_traces FOR ALL USING (is_internal_admin()) WITH CHECK (is_internal_admin())';
    END IF;
END $$;

-- MEMORY_STATE_TIMELINE
DO $$ BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'memory_state_timeline' AND column_name = 'tenant_id') THEN
        EXECUTE 'CREATE POLICY rls_memory_state_timeline ON memory_state_timeline FOR ALL USING (rls_tenant_check(tenant_id)) WITH CHECK (rls_tenant_check(tenant_id))';
    ELSE
        EXECUTE 'CREATE POLICY rls_memory_state_timeline ON memory_state_timeline FOR ALL USING (is_internal_admin()) WITH CHECK (is_internal_admin())';
    END IF;
END $$;

-- SHADOW_BRANCHES (uses tenant_id_uuid or tenant_id)
DO $$ BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'shadow_branches' AND column_name = 'tenant_id_uuid') THEN
        EXECUTE 'CREATE POLICY rls_shadow_branches ON shadow_branches FOR ALL USING (rls_tenant_check(tenant_id_uuid)) WITH CHECK (rls_tenant_check(tenant_id_uuid))';
    ELSIF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'shadow_branches' AND column_name = 'tenant_id' AND data_type = 'uuid') THEN
        EXECUTE 'CREATE POLICY rls_shadow_branches ON shadow_branches FOR ALL USING (rls_tenant_check(tenant_id)) WITH CHECK (rls_tenant_check(tenant_id))';
    ELSIF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'shadow_branches' AND column_name = 'tenant_id') THEN
        EXECUTE 'CREATE POLICY rls_shadow_branches ON shadow_branches FOR ALL USING (rls_tenant_check_varchar(tenant_id)) WITH CHECK (rls_tenant_check_varchar(tenant_id))';
    END IF;
END $$;

-- SHADOW_MEMORIES
DO $$ BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'shadow_memories' AND column_name = 'tenant_id_uuid') THEN
        EXECUTE 'CREATE POLICY rls_shadow_memories ON shadow_memories FOR ALL USING (rls_tenant_check(tenant_id_uuid)) WITH CHECK (rls_tenant_check(tenant_id_uuid))';
    ELSIF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'shadow_memories' AND column_name = 'tenant_id') THEN
        EXECUTE 'CREATE POLICY rls_shadow_memories ON shadow_memories FOR ALL USING (rls_tenant_check_varchar(tenant_id)) WITH CHECK (rls_tenant_check_varchar(tenant_id))';
    END IF;
END $$;

-- SHADOW_ENTITIES
DO $$ BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'shadow_entities' AND column_name = 'tenant_id_uuid') THEN
        EXECUTE 'CREATE POLICY rls_shadow_entities ON shadow_entities FOR ALL USING (rls_tenant_check(tenant_id_uuid)) WITH CHECK (rls_tenant_check(tenant_id_uuid))';
    ELSIF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'shadow_entities' AND column_name = 'tenant_id') THEN
        EXECUTE 'CREATE POLICY rls_shadow_entities ON shadow_entities FOR ALL USING (rls_tenant_check_varchar(tenant_id)) WITH CHECK (rls_tenant_check_varchar(tenant_id))';
    END IF;
END $$;

-- SHADOW_RELATIONSHIPS
DO $$ BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'shadow_relationships' AND column_name = 'tenant_id_uuid') THEN
        EXECUTE 'CREATE POLICY rls_shadow_relationships ON shadow_relationships FOR ALL USING (rls_tenant_check(tenant_id_uuid)) WITH CHECK (rls_tenant_check(tenant_id_uuid))';
    ELSIF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'shadow_relationships' AND column_name = 'tenant_id') THEN
        EXECUTE 'CREATE POLICY rls_shadow_relationships ON shadow_relationships FOR ALL USING (rls_tenant_check_varchar(tenant_id)) WITH CHECK (rls_tenant_check_varchar(tenant_id))';
    END IF;
END $$;

-- SHADOW_PROMOTION_LOG
DO $$ BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'shadow_promotion_log' AND column_name = 'tenant_id_uuid') THEN
        EXECUTE 'CREATE POLICY rls_shadow_promotion_log ON shadow_promotion_log FOR ALL USING (rls_tenant_check(tenant_id_uuid)) WITH CHECK (rls_tenant_check(tenant_id_uuid))';
    ELSIF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'shadow_promotion_log' AND column_name = 'tenant_id') THEN
        EXECUTE 'CREATE POLICY rls_shadow_promotion_log ON shadow_promotion_log FOR ALL USING (rls_tenant_check_varchar(tenant_id)) WITH CHECK (rls_tenant_check_varchar(tenant_id))';
    END IF;
END $$;

-- REASONING_RUNS
DO $$
DECLARE
    v_dtype text;
BEGIN
    SELECT data_type INTO v_dtype FROM information_schema.columns
    WHERE table_name = 'reasoning_runs' AND column_name = 'tenant_id';

    IF v_dtype IS NULL THEN
        EXECUTE 'CREATE POLICY rls_reasoning_runs ON reasoning_runs FOR ALL USING (is_internal_admin()) WITH CHECK (is_internal_admin())';
    ELSIF v_dtype = 'uuid' THEN
        EXECUTE 'CREATE POLICY rls_reasoning_runs ON reasoning_runs FOR ALL USING (rls_tenant_check(tenant_id)) WITH CHECK (rls_tenant_check(tenant_id))';
    ELSE
        EXECUTE 'CREATE POLICY rls_reasoning_runs ON reasoning_runs FOR ALL USING (rls_tenant_check_varchar(tenant_id)) WITH CHECK (rls_tenant_check_varchar(tenant_id))';
    END IF;
END $$;

-- REASONING_STEPS
DO $$
DECLARE
    v_dtype text;
BEGIN
    SELECT data_type INTO v_dtype FROM information_schema.columns
    WHERE table_name = 'reasoning_steps' AND column_name = 'tenant_id';

    IF v_dtype IS NULL THEN
        EXECUTE 'CREATE POLICY rls_reasoning_steps ON reasoning_steps FOR ALL USING (is_internal_admin()) WITH CHECK (is_internal_admin())';
    ELSIF v_dtype = 'uuid' THEN
        EXECUTE 'CREATE POLICY rls_reasoning_steps ON reasoning_steps FOR ALL USING (rls_tenant_check(tenant_id)) WITH CHECK (rls_tenant_check(tenant_id))';
    ELSE
        EXECUTE 'CREATE POLICY rls_reasoning_steps ON reasoning_steps FOR ALL USING (rls_tenant_check_varchar(tenant_id)) WITH CHECK (rls_tenant_check_varchar(tenant_id))';
    END IF;
END $$;

-- REASONING_FINDINGS
DO $$
DECLARE
    v_dtype text;
BEGIN
    SELECT data_type INTO v_dtype FROM information_schema.columns
    WHERE table_name = 'reasoning_findings' AND column_name = 'tenant_id';

    IF v_dtype IS NULL THEN
        EXECUTE 'CREATE POLICY rls_reasoning_findings ON reasoning_findings FOR ALL USING (is_internal_admin()) WITH CHECK (is_internal_admin())';
    ELSIF v_dtype = 'uuid' THEN
        EXECUTE 'CREATE POLICY rls_reasoning_findings ON reasoning_findings FOR ALL USING (rls_tenant_check(tenant_id)) WITH CHECK (rls_tenant_check(tenant_id))';
    ELSE
        EXECUTE 'CREATE POLICY rls_reasoning_findings ON reasoning_findings FOR ALL USING (rls_tenant_check_varchar(tenant_id)) WITH CHECK (rls_tenant_check_varchar(tenant_id))';
    END IF;
END $$;

-- REASONING_RESULTS
DO $$
DECLARE
    v_dtype text;
BEGIN
    SELECT data_type INTO v_dtype FROM information_schema.columns
    WHERE table_name = 'reasoning_results' AND column_name = 'tenant_id';

    IF v_dtype IS NULL THEN
        EXECUTE 'CREATE POLICY rls_reasoning_results ON reasoning_results FOR ALL USING (is_internal_admin()) WITH CHECK (is_internal_admin())';
    ELSIF v_dtype = 'uuid' THEN
        EXECUTE 'CREATE POLICY rls_reasoning_results ON reasoning_results FOR ALL USING (rls_tenant_check(tenant_id)) WITH CHECK (rls_tenant_check(tenant_id))';
    ELSE
        EXECUTE 'CREATE POLICY rls_reasoning_results ON reasoning_results FOR ALL USING (rls_tenant_check_varchar(tenant_id)) WITH CHECK (rls_tenant_check_varchar(tenant_id))';
    END IF;
END $$;

-- DUAL_PASS_REASONING
DO $$
DECLARE
    v_dtype text;
BEGIN
    SELECT data_type INTO v_dtype FROM information_schema.columns
    WHERE table_name = 'dual_pass_reasoning' AND column_name = 'tenant_id';

    IF v_dtype IS NULL THEN
        EXECUTE 'CREATE POLICY rls_dual_pass_reasoning ON dual_pass_reasoning FOR ALL USING (is_internal_admin()) WITH CHECK (is_internal_admin())';
    ELSIF v_dtype = 'uuid' THEN
        EXECUTE 'CREATE POLICY rls_dual_pass_reasoning ON dual_pass_reasoning FOR ALL USING (rls_tenant_check(tenant_id)) WITH CHECK (rls_tenant_check(tenant_id))';
    ELSE
        EXECUTE 'CREATE POLICY rls_dual_pass_reasoning ON dual_pass_reasoning FOR ALL USING (rls_tenant_check_varchar(tenant_id)) WITH CHECK (rls_tenant_check_varchar(tenant_id))';
    END IF;
END $$;

-- MIND HEALTH TABLES
CREATE POLICY rls_mind_confidence ON mind_confidence_observations FOR ALL
    USING (rls_tenant_check(tenant_id))
    WITH CHECK (rls_tenant_check(tenant_id));

CREATE POLICY rls_mind_hallucination ON mind_hallucination_events FOR ALL
    USING (rls_tenant_check(tenant_id))
    WITH CHECK (rls_tenant_check(tenant_id));

CREATE POLICY rls_mind_contradiction ON mind_contradiction_events FOR ALL
    USING (rls_tenant_check(tenant_id))
    WITH CHECK (rls_tenant_check(tenant_id));

CREATE POLICY rls_mind_daily_scores ON mind_daily_scores FOR ALL
    USING (rls_tenant_check(tenant_id))
    WITH CHECK (rls_tenant_check(tenant_id));

-- GRAPH TABLES (dynamic type detection - some have TEXT tenant_id)
DO $$
DECLARE
    v_dtype text;
BEGIN
    SELECT data_type INTO v_dtype FROM information_schema.columns
    WHERE table_name = 'graph_events' AND column_name = 'tenant_id';

    IF v_dtype IS NULL THEN
        EXECUTE 'CREATE POLICY rls_graph_events ON graph_events FOR ALL USING (is_internal_admin()) WITH CHECK (is_internal_admin())';
    ELSIF v_dtype = 'uuid' THEN
        EXECUTE 'CREATE POLICY rls_graph_events ON graph_events FOR ALL USING (rls_tenant_check(tenant_id)) WITH CHECK (rls_tenant_check(tenant_id))';
    ELSE
        EXECUTE 'CREATE POLICY rls_graph_events ON graph_events FOR ALL USING (rls_tenant_check_varchar(tenant_id)) WITH CHECK (rls_tenant_check_varchar(tenant_id))';
    END IF;
END $$;

DO $$
DECLARE
    v_dtype text;
BEGIN
    SELECT data_type INTO v_dtype FROM information_schema.columns
    WHERE table_name = 'graph_topology_stats' AND column_name = 'tenant_id';

    IF v_dtype IS NULL THEN
        EXECUTE 'CREATE POLICY rls_graph_topology_stats ON graph_topology_stats FOR ALL USING (is_internal_admin()) WITH CHECK (is_internal_admin())';
    ELSIF v_dtype = 'uuid' THEN
        EXECUTE 'CREATE POLICY rls_graph_topology_stats ON graph_topology_stats FOR ALL USING (rls_tenant_check(tenant_id)) WITH CHECK (rls_tenant_check(tenant_id))';
    ELSE
        EXECUTE 'CREATE POLICY rls_graph_topology_stats ON graph_topology_stats FOR ALL USING (rls_tenant_check_varchar(tenant_id)) WITH CHECK (rls_tenant_check_varchar(tenant_id))';
    END IF;
END $$;

DO $$
DECLARE
    v_dtype text;
BEGIN
    SELECT data_type INTO v_dtype FROM information_schema.columns
    WHERE table_name = 'graph_metrics_hourly' AND column_name = 'tenant_id';

    IF v_dtype IS NULL THEN
        EXECUTE 'CREATE POLICY rls_graph_metrics_hourly ON graph_metrics_hourly FOR ALL USING (is_internal_admin()) WITH CHECK (is_internal_admin())';
    ELSIF v_dtype = 'uuid' THEN
        EXECUTE 'CREATE POLICY rls_graph_metrics_hourly ON graph_metrics_hourly FOR ALL USING (rls_tenant_check(tenant_id)) WITH CHECK (rls_tenant_check(tenant_id))';
    ELSE
        EXECUTE 'CREATE POLICY rls_graph_metrics_hourly ON graph_metrics_hourly FOR ALL USING (rls_tenant_check_varchar(tenant_id)) WITH CHECK (rls_tenant_check_varchar(tenant_id))';
    END IF;
END $$;

DO $$
DECLARE
    v_dtype text;
BEGIN
    SELECT data_type INTO v_dtype FROM information_schema.columns
    WHERE table_name = 'knowledge_graph_nodes' AND column_name = 'tenant_id';

    IF v_dtype IS NULL THEN
        EXECUTE 'CREATE POLICY rls_knowledge_graph_nodes ON knowledge_graph_nodes FOR ALL USING (is_internal_admin()) WITH CHECK (is_internal_admin())';
    ELSIF v_dtype = 'uuid' THEN
        EXECUTE 'CREATE POLICY rls_knowledge_graph_nodes ON knowledge_graph_nodes FOR ALL USING (rls_tenant_check(tenant_id)) WITH CHECK (rls_tenant_check(tenant_id))';
    ELSE
        EXECUTE 'CREATE POLICY rls_knowledge_graph_nodes ON knowledge_graph_nodes FOR ALL USING (rls_tenant_check_varchar(tenant_id)) WITH CHECK (rls_tenant_check_varchar(tenant_id))';
    END IF;
END $$;

-- MEMORY LAYERS & CLASSIFICATION
CREATE POLICY rls_memory_layers ON memory_layers FOR ALL
    USING (rls_tenant_check(tenant_id))
    WITH CHECK (rls_tenant_check(tenant_id));

CREATE POLICY rls_memory_classification_events ON memory_classification_events FOR ALL
    USING (rls_tenant_check(tenant_id))
    WITH CHECK (rls_tenant_check(tenant_id));

CREATE POLICY rls_memory_processing_queue ON memory_processing_queue FOR ALL
    USING (rls_tenant_check(tenant_id))
    WITH CHECK (rls_tenant_check(tenant_id));

DO $$ BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'layer_transition_queue' AND column_name = 'tenant_id') THEN
        EXECUTE 'CREATE POLICY rls_layer_transition_queue ON layer_transition_queue FOR ALL USING (rls_tenant_check(tenant_id)) WITH CHECK (rls_tenant_check(tenant_id))';
    END IF;
END $$;

-- BILLING & USAGE
CREATE POLICY rls_usage_events ON usage_events FOR ALL
    USING (rls_tenant_check(tenant_id))
    WITH CHECK (rls_tenant_check(tenant_id));

CREATE POLICY rls_usage_daily_rollups ON usage_daily_rollups FOR ALL
    USING (rls_tenant_check(tenant_id))
    WITH CHECK (rls_tenant_check(tenant_id));

CREATE POLICY rls_usage_alerts ON usage_alerts FOR ALL
    USING (rls_tenant_check(tenant_id))
    WITH CHECK (rls_tenant_check(tenant_id));

CREATE POLICY rls_billing_cycles ON billing_cycles FOR ALL
    USING (rls_tenant_check(tenant_id))
    WITH CHECK (rls_tenant_check(tenant_id));

DO $$ BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'credit_adjustments' AND column_name = 'tenant_id') THEN
        EXECUTE 'CREATE POLICY rls_credit_adjustments ON credit_adjustments FOR ALL USING (rls_tenant_check(tenant_id)) WITH CHECK (rls_tenant_check(tenant_id))';
    END IF;
END $$;

-- SECURITY & AUDIT
CREATE POLICY rls_privacy_audit_entries ON privacy_audit_entries FOR ALL
    USING (rls_tenant_check_varchar(tenant_id) OR is_internal_admin())
    WITH CHECK (is_internal_admin() OR rls_tenant_check_varchar(tenant_id));

DO $$ BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'privacy_audit_proofs' AND column_name = 'tenant_id') THEN
        EXECUTE 'CREATE POLICY rls_privacy_audit_proofs ON privacy_audit_proofs FOR ALL USING (rls_tenant_check_varchar(tenant_id) OR is_internal_admin()) WITH CHECK (is_internal_admin() OR rls_tenant_check_varchar(tenant_id))';
    END IF;
END $$;

CREATE POLICY rls_security_events ON security_events FOR ALL
    USING (rls_tenant_check(tenant_id) OR is_internal_admin())
    WITH CHECK (is_internal_admin());

CREATE POLICY rls_security_scans ON security_scans FOR ALL
    USING (rls_tenant_check(tenant_id) OR is_internal_admin())
    WITH CHECK (is_internal_admin());

CREATE POLICY rls_admin_actions ON admin_actions FOR ALL
    USING (rls_tenant_check(tenant_id) OR is_internal_admin())
    WITH CHECK (is_internal_admin());

-- CONTROL ROOM - emergency_cutoffs (internal_admin only for write, read by tenant)
CREATE POLICY rls_emergency_cutoffs ON emergency_cutoffs FOR ALL
    USING (is_internal_admin() OR rls_tenant_check_varchar(tenant_id))
    WITH CHECK (is_internal_admin());

-- INTEGRITY TABLES
CREATE POLICY rls_integrity_chain_anchors ON integrity_chain_anchors FOR ALL
    USING (rls_tenant_check(tenant_id) OR is_internal_admin())
    WITH CHECK (is_internal_admin() OR rls_tenant_check(tenant_id));

CREATE POLICY rls_integrity_verification_runs ON integrity_verification_runs FOR ALL
    USING (rls_tenant_check(tenant_id) OR is_internal_admin())
    WITH CHECK (is_internal_admin() OR rls_tenant_check(tenant_id));

DO $$ BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'integrity_check_results' AND column_name = 'tenant_id') THEN
        EXECUTE 'CREATE POLICY rls_integrity_check_results ON integrity_check_results FOR ALL USING (rls_tenant_check(tenant_id) OR is_internal_admin()) WITH CHECK (is_internal_admin())';
    END IF;
END $$;

-- SELF-HEALING
DO $$ BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'self_healing_logs' AND column_name = 'tenant_id') THEN
        EXECUTE 'CREATE POLICY rls_self_healing_logs ON self_healing_logs FOR ALL USING (rls_tenant_check(tenant_id)) WITH CHECK (rls_tenant_check(tenant_id))';
    END IF;
END $$;

CREATE POLICY rls_self_healing_recommendations ON self_healing_recommendations FOR ALL
    USING (rls_tenant_check(tenant_id))
    WITH CHECK (rls_tenant_check(tenant_id));

DO $$ BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'contradiction_reports' AND column_name = 'tenant_id') THEN
        EXECUTE 'CREATE POLICY rls_contradiction_reports ON contradiction_reports FOR ALL USING (rls_tenant_check(tenant_id)) WITH CHECK (rls_tenant_check(tenant_id))';
    END IF;
END $$;

DO $$ BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'memory_conflicts' AND column_name = 'tenant_id') THEN
        EXECUTE 'CREATE POLICY rls_memory_conflicts ON memory_conflicts FOR ALL USING (rls_tenant_check(tenant_id)) WITH CHECK (rls_tenant_check(tenant_id))';
    END IF;
END $$;

DO $$ BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'memory_contradictions' AND column_name = 'tenant_id') THEN
        EXECUTE 'CREATE POLICY rls_memory_contradictions ON memory_contradictions FOR ALL USING (rls_tenant_check(tenant_id)) WITH CHECK (rls_tenant_check(tenant_id))';
    END IF;
END $$;

-- JOBS & MAINTENANCE
CREATE POLICY rls_supervised_jobs ON supervised_jobs FOR ALL
    USING (rls_tenant_check(tenant_id) OR is_internal_admin())
    WITH CHECK (rls_tenant_check(tenant_id) OR is_internal_admin());

DO $$ BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'memory_recrawl_jobs' AND column_name = 'tenant_id') THEN
        EXECUTE 'CREATE POLICY rls_memory_recrawl_jobs ON memory_recrawl_jobs FOR ALL USING (rls_tenant_check(tenant_id)) WITH CHECK (rls_tenant_check(tenant_id))';
    END IF;
END $$;

DO $$ BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'memory_reembed_jobs' AND column_name = 'tenant_id') THEN
        EXECUTE 'CREATE POLICY rls_memory_reembed_jobs ON memory_reembed_jobs FOR ALL USING (rls_tenant_check(tenant_id)) WITH CHECK (rls_tenant_check(tenant_id))';
    END IF;
END $$;

DO $$ BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'maintenance_tasks' AND column_name = 'tenant_id') THEN
        EXECUTE 'CREATE POLICY rls_maintenance_tasks ON maintenance_tasks FOR ALL USING (rls_tenant_check(tenant_id) OR is_internal_admin()) WITH CHECK (is_internal_admin())';
    END IF;
END $$;

DO $$ BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'maintenance_task_logs' AND column_name = 'tenant_id') THEN
        EXECUTE 'CREATE POLICY rls_maintenance_task_logs ON maintenance_task_logs FOR ALL USING (rls_tenant_check(tenant_id) OR is_internal_admin()) WITH CHECK (is_internal_admin())';
    END IF;
END $$;

-- =============================================================================
-- STEP 5: Add missing tenant_id columns where needed
-- =============================================================================

-- Add tenant_id to memory_events if missing
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'memory_events' AND column_name = 'tenant_id') THEN
        ALTER TABLE memory_events ADD COLUMN tenant_id UUID;
        CREATE INDEX IF NOT EXISTS idx_memory_events_tenant_id ON memory_events(tenant_id);
    END IF;
END $$;

-- Add tenant_id to memory_traces if missing
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'memory_traces' AND column_name = 'tenant_id') THEN
        ALTER TABLE memory_traces ADD COLUMN tenant_id UUID;
        CREATE INDEX IF NOT EXISTS idx_memory_traces_tenant_id ON memory_traces(tenant_id);
    END IF;
END $$;

-- Add tenant_id to reasoning_runs if missing
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'reasoning_runs' AND column_name = 'tenant_id') THEN
        ALTER TABLE reasoning_runs ADD COLUMN tenant_id UUID;
        CREATE INDEX IF NOT EXISTS idx_reasoning_runs_tenant_id ON reasoning_runs(tenant_id);
    END IF;
END $$;

-- Add tenant_id to reasoning_steps if missing
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'reasoning_steps' AND column_name = 'tenant_id') THEN
        ALTER TABLE reasoning_steps ADD COLUMN tenant_id UUID;
        CREATE INDEX IF NOT EXISTS idx_reasoning_steps_tenant_id ON reasoning_steps(tenant_id);
    END IF;
END $$;

-- Add tenant_id to contradiction_reports if missing
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'contradiction_reports' AND column_name = 'tenant_id') THEN
        ALTER TABLE contradiction_reports ADD COLUMN tenant_id UUID;
        CREATE INDEX IF NOT EXISTS idx_contradiction_reports_tenant_id ON contradiction_reports(tenant_id);
    END IF;
END $$;

-- =============================================================================
-- STEP 6: Verification function
-- =============================================================================

CREATE OR REPLACE FUNCTION verify_rls_configuration()
RETURNS TABLE(table_name TEXT, rls_enabled BOOLEAN, policy_count INTEGER, status TEXT) AS $$
DECLARE
    r RECORD;
BEGIN
    FOR r IN (
        SELECT
            c.relname::text as tbl,
            c.relrowsecurity as rls,
            (SELECT COUNT(*) FROM pg_policy p WHERE p.polrelid = c.oid)::integer as pol_cnt
        FROM pg_class c
        JOIN pg_namespace n ON c.relnamespace = n.oid
        WHERE n.nspname = 'public'
        AND c.relkind = 'r'
        AND c.relname IN (
            'memories', 'entities', 'entity_relationships', 'memory_entities',
            'user_personas', 'conversation_sessions', 'memory_events', 'memory_traces',
            'shadow_branches', 'shadow_memories', 'reasoning_runs', 'reasoning_steps',
            'mind_confidence_observations', 'mind_hallucination_events',
            'privacy_audit_entries', 'emergency_cutoffs', 'integrity_chain_anchors',
            'usage_events', 'billing_cycles', 'memory_layers', 'graph_events'
        )
        ORDER BY c.relname
    )
    LOOP
        table_name := r.tbl;
        rls_enabled := r.rls;
        policy_count := r.pol_cnt;

        IF r.rls AND r.pol_cnt > 0 THEN
            status := 'OK';
        ELSIF r.rls AND r.pol_cnt = 0 THEN
            status := 'BROKEN - RLS enabled but no policies';
        ELSIF NOT r.rls THEN
            status := 'WARNING - RLS not enabled';
        ELSE
            status := 'UNKNOWN';
        END IF;

        RETURN NEXT;
    END LOOP;
END;
$$ LANGUAGE plpgsql;

COMMIT;

-- =============================================================================
-- POST-MIGRATION: Run verification
-- =============================================================================
-- SELECT * FROM verify_rls_configuration();

-- =============================================================================
-- USAGE NOTES
-- =============================================================================
--
-- For internal services (workers, background jobs):
--   SELECT set_internal_context('tenant-uuid-here');
--   -- or --
--   SELECT set_config('app.role', 'internal_admin', true);
--   SELECT set_config('app.current_tenant_id', 'tenant-uuid', true);
--
-- For user API requests:
--   SELECT set_tenant_context('tenant-uuid-here');
--   -- or --
--   SELECT set_config('app.tenant_id', 'tenant-uuid', true);
--   SELECT set_config('app.current_tenant_id', 'tenant-uuid', true);
--
