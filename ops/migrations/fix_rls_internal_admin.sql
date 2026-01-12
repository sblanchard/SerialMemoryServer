-- =============================================================================
-- UNIFIED RLS FIX: Internal Admin Bypass for All System Tables
-- =============================================================================
-- This migration fixes the RLS policies across ALL tables to support:
-- 1. Tenant isolation (app.tenant_id) for user operations
-- 2. Internal admin bypass (app.role = 'internal_admin') for system operations
--
-- RUN THIS AFTER ALL OTHER SCHEMA MIGRATIONS
-- =============================================================================

BEGIN;

-- =============================================================================
-- STEP 1: Create helper functions for role checking
-- =============================================================================

-- Function to check if current session has internal admin role
CREATE OR REPLACE FUNCTION is_internal_admin()
RETURNS BOOLEAN AS $$
BEGIN
    RETURN COALESCE(current_setting('app.role', true), '') = 'internal_admin';
END;
$$ LANGUAGE plpgsql STABLE;

-- Function to set internal admin role
CREATE OR REPLACE FUNCTION set_internal_admin_role()
RETURNS VOID AS $$
BEGIN
    PERFORM set_config('app.role', 'internal_admin', true);
END;
$$ LANGUAGE plpgsql SECURITY DEFINER;

-- Function to clear internal admin role
CREATE OR REPLACE FUNCTION clear_internal_admin_role()
RETURNS VOID AS $$
BEGIN
    PERFORM set_config('app.role', '', true);
END;
$$ LANGUAGE plpgsql SECURITY DEFINER;

-- Grant execute to application role
GRANT EXECUTE ON FUNCTION is_internal_admin() TO contextdb_app;
GRANT EXECUTE ON FUNCTION set_internal_admin_role() TO contextdb_app;
GRANT EXECUTE ON FUNCTION clear_internal_admin_role() TO contextdb_app;

-- =============================================================================
-- STEP 2: Create unified RLS check function
-- =============================================================================

-- Unified RLS check that allows internal admin OR matching tenant
CREATE OR REPLACE FUNCTION rls_check_access(p_tenant_id UUID)
RETURNS BOOLEAN AS $$
BEGIN
    -- Internal admin bypasses all RLS
    IF COALESCE(current_setting('app.role', true), '') = 'internal_admin' THEN
        RETURN TRUE;
    END IF;

    -- Otherwise check tenant_id
    RETURN p_tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid;
END;
$$ LANGUAGE plpgsql STABLE;

-- For tables with TEXT tenant_id
CREATE OR REPLACE FUNCTION rls_check_access_text(p_tenant_id TEXT)
RETURNS BOOLEAN AS $$
BEGIN
    -- Internal admin bypasses all RLS
    IF COALESCE(current_setting('app.role', true), '') = 'internal_admin' THEN
        RETURN TRUE;
    END IF;

    -- Otherwise check tenant_id
    RETURN p_tenant_id = current_setting('app.tenant_id', true);
END;
$$ LANGUAGE plpgsql STABLE;

-- For system tables with optional tenant_id (NULL = global)
CREATE OR REPLACE FUNCTION rls_check_system_access(p_tenant_id UUID)
RETURNS BOOLEAN AS $$
BEGIN
    -- Internal admin bypasses all RLS
    IF COALESCE(current_setting('app.role', true), '') = 'internal_admin' THEN
        RETURN TRUE;
    END IF;

    -- Global records (NULL tenant_id) require internal admin
    IF p_tenant_id IS NULL THEN
        RETURN FALSE;
    END IF;

    -- Otherwise check tenant_id
    RETURN p_tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid;
END;
$$ LANGUAGE plpgsql STABLE;

GRANT EXECUTE ON FUNCTION rls_check_access(UUID) TO contextdb_app;
GRANT EXECUTE ON FUNCTION rls_check_access_text(TEXT) TO contextdb_app;
GRANT EXECUTE ON FUNCTION rls_check_system_access(UUID) TO contextdb_app;

-- =============================================================================
-- STEP 3: Fix EMERGENCY_CUTOFFS table (Control Room)
-- =============================================================================

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'emergency_cutoffs') THEN
        -- Enable RLS if not already enabled
        EXECUTE 'ALTER TABLE emergency_cutoffs ENABLE ROW LEVEL SECURITY';

        -- Drop existing policies
        DROP POLICY IF EXISTS tenant_isolation_emergency_cutoffs ON emergency_cutoffs;
        DROP POLICY IF EXISTS internal_admin_emergency_cutoffs ON emergency_cutoffs;
        DROP POLICY IF EXISTS service_bypass_emergency_cutoffs ON emergency_cutoffs;

        -- Create unified policy with internal admin bypass
        CREATE POLICY unified_emergency_cutoffs ON emergency_cutoffs
            FOR ALL
            USING (rls_check_system_access(tenant_id))
            WITH CHECK (rls_check_system_access(tenant_id));

        RAISE NOTICE 'Fixed RLS on emergency_cutoffs';
    END IF;
END $$;

-- =============================================================================
-- STEP 4: Fix PRIVACY_AUDIT_ENTRIES table (Privacy & Integrity)
-- =============================================================================

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'privacy_audit_entries') THEN
        EXECUTE 'ALTER TABLE privacy_audit_entries ENABLE ROW LEVEL SECURITY';

        DROP POLICY IF EXISTS tenant_isolation_privacy_audit_entries ON privacy_audit_entries;
        DROP POLICY IF EXISTS internal_admin_privacy_audit_entries ON privacy_audit_entries;

        CREATE POLICY unified_privacy_audit_entries ON privacy_audit_entries
            FOR ALL
            USING (rls_check_access_text(tenant_id))
            WITH CHECK (rls_check_access_text(tenant_id));

        RAISE NOTICE 'Fixed RLS on privacy_audit_entries';
    END IF;
END $$;

-- =============================================================================
-- STEP 5: Fix ADMIN_ACTIONS table
-- =============================================================================

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'admin_actions') THEN
        EXECUTE 'ALTER TABLE admin_actions ENABLE ROW LEVEL SECURITY';

        DROP POLICY IF EXISTS tenant_isolation_admin_actions ON admin_actions;
        DROP POLICY IF EXISTS service_bypass_admin_actions ON admin_actions;
        DROP POLICY IF EXISTS internal_admin_admin_actions ON admin_actions;

        CREATE POLICY unified_admin_actions ON admin_actions
            FOR ALL
            USING (rls_check_system_access(tenant_id))
            WITH CHECK (rls_check_system_access(tenant_id));

        RAISE NOTICE 'Fixed RLS on admin_actions';
    END IF;
END $$;

-- =============================================================================
-- STEP 6: Fix MIND HEALTH tables
-- =============================================================================

DO $$
BEGIN
    -- mind_confidence_observations
    IF EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'mind_confidence_observations') THEN
        EXECUTE 'ALTER TABLE mind_confidence_observations ENABLE ROW LEVEL SECURITY';
        DROP POLICY IF EXISTS tenant_isolation_mind_confidence ON mind_confidence_observations;
        DROP POLICY IF EXISTS unified_mind_confidence ON mind_confidence_observations;

        CREATE POLICY unified_mind_confidence ON mind_confidence_observations
            FOR ALL
            USING (rls_check_access(tenant_id))
            WITH CHECK (rls_check_access(tenant_id));
        RAISE NOTICE 'Fixed RLS on mind_confidence_observations';
    END IF;

    -- mind_hallucination_events
    IF EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'mind_hallucination_events') THEN
        EXECUTE 'ALTER TABLE mind_hallucination_events ENABLE ROW LEVEL SECURITY';
        DROP POLICY IF EXISTS tenant_isolation_mind_hallucination ON mind_hallucination_events;
        DROP POLICY IF EXISTS unified_mind_hallucination ON mind_hallucination_events;

        CREATE POLICY unified_mind_hallucination ON mind_hallucination_events
            FOR ALL
            USING (rls_check_access(tenant_id))
            WITH CHECK (rls_check_access(tenant_id));
        RAISE NOTICE 'Fixed RLS on mind_hallucination_events';
    END IF;

    -- mind_contradiction_events
    IF EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'mind_contradiction_events') THEN
        EXECUTE 'ALTER TABLE mind_contradiction_events ENABLE ROW LEVEL SECURITY';
        DROP POLICY IF EXISTS tenant_isolation_mind_contradiction ON mind_contradiction_events;
        DROP POLICY IF EXISTS unified_mind_contradiction ON mind_contradiction_events;

        CREATE POLICY unified_mind_contradiction ON mind_contradiction_events
            FOR ALL
            USING (rls_check_access(tenant_id))
            WITH CHECK (rls_check_access(tenant_id));
        RAISE NOTICE 'Fixed RLS on mind_contradiction_events';
    END IF;

    -- mind_daily_scores
    IF EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'mind_daily_scores') THEN
        EXECUTE 'ALTER TABLE mind_daily_scores ENABLE ROW LEVEL SECURITY';
        DROP POLICY IF EXISTS tenant_isolation_mind_daily_scores ON mind_daily_scores;
        DROP POLICY IF EXISTS unified_mind_daily_scores ON mind_daily_scores;

        CREATE POLICY unified_mind_daily_scores ON mind_daily_scores
            FOR ALL
            USING (rls_check_access(tenant_id))
            WITH CHECK (rls_check_access(tenant_id));
        RAISE NOTICE 'Fixed RLS on mind_daily_scores';
    END IF;
END $$;

-- =============================================================================
-- STEP 7: Fix USAGE/BILLING tables
-- =============================================================================

DO $$
BEGIN
    -- usage_events
    IF EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'usage_events') THEN
        EXECUTE 'ALTER TABLE usage_events ENABLE ROW LEVEL SECURITY';
        DROP POLICY IF EXISTS tenant_isolation_usage_events ON usage_events;
        DROP POLICY IF EXISTS unified_usage_events ON usage_events;

        CREATE POLICY unified_usage_events ON usage_events
            FOR ALL
            USING (rls_check_access_text(tenant_id))
            WITH CHECK (rls_check_access_text(tenant_id));
        RAISE NOTICE 'Fixed RLS on usage_events';
    END IF;

    -- billing_cycles
    IF EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'billing_cycles') THEN
        EXECUTE 'ALTER TABLE billing_cycles ENABLE ROW LEVEL SECURITY';
        DROP POLICY IF EXISTS tenant_isolation_billing_cycles ON billing_cycles;
        DROP POLICY IF EXISTS unified_billing_cycles ON billing_cycles;

        CREATE POLICY unified_billing_cycles ON billing_cycles
            FOR ALL
            USING (rls_check_access_text(tenant_id))
            WITH CHECK (rls_check_access_text(tenant_id));
        RAISE NOTICE 'Fixed RLS on billing_cycles';
    END IF;

    -- usage_daily_rollups
    IF EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'usage_daily_rollups') THEN
        EXECUTE 'ALTER TABLE usage_daily_rollups ENABLE ROW LEVEL SECURITY';
        DROP POLICY IF EXISTS tenant_isolation_usage_daily_rollups ON usage_daily_rollups;
        DROP POLICY IF EXISTS unified_usage_daily_rollups ON usage_daily_rollups;

        CREATE POLICY unified_usage_daily_rollups ON usage_daily_rollups
            FOR ALL
            USING (rls_check_access_text(tenant_id))
            WITH CHECK (rls_check_access_text(tenant_id));
        RAISE NOTICE 'Fixed RLS on usage_daily_rollups';
    END IF;

    -- usage_alerts
    IF EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'usage_alerts') THEN
        EXECUTE 'ALTER TABLE usage_alerts ENABLE ROW LEVEL SECURITY';
        DROP POLICY IF EXISTS tenant_isolation_usage_alerts ON usage_alerts;
        DROP POLICY IF EXISTS unified_usage_alerts ON usage_alerts;

        CREATE POLICY unified_usage_alerts ON usage_alerts
            FOR ALL
            USING (rls_check_access_text(tenant_id))
            WITH CHECK (rls_check_access_text(tenant_id));
        RAISE NOTICE 'Fixed RLS on usage_alerts';
    END IF;
END $$;

-- =============================================================================
-- STEP 8: Fix CORE TENANT tables (memories, entities, etc.)
-- =============================================================================

DO $$
BEGIN
    -- memories
    IF EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'memories') THEN
        EXECUTE 'ALTER TABLE memories ENABLE ROW LEVEL SECURITY';
        DROP POLICY IF EXISTS tenant_isolation_memories ON memories;
        DROP POLICY IF EXISTS service_bypass_memories ON memories;
        DROP POLICY IF EXISTS unified_memories ON memories;

        CREATE POLICY unified_memories ON memories
            FOR ALL
            USING (rls_check_access(tenant_id))
            WITH CHECK (rls_check_access(tenant_id));
        RAISE NOTICE 'Fixed RLS on memories';
    END IF;

    -- entities
    IF EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'entities') THEN
        EXECUTE 'ALTER TABLE entities ENABLE ROW LEVEL SECURITY';
        DROP POLICY IF EXISTS tenant_isolation_entities ON entities;
        DROP POLICY IF EXISTS service_bypass_entities ON entities;
        DROP POLICY IF EXISTS unified_entities ON entities;

        CREATE POLICY unified_entities ON entities
            FOR ALL
            USING (rls_check_access(tenant_id))
            WITH CHECK (rls_check_access(tenant_id));
        RAISE NOTICE 'Fixed RLS on entities';
    END IF;

    -- entity_relationships
    IF EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'entity_relationships') THEN
        EXECUTE 'ALTER TABLE entity_relationships ENABLE ROW LEVEL SECURITY';
        DROP POLICY IF EXISTS tenant_isolation_entity_relationships ON entity_relationships;
        DROP POLICY IF EXISTS service_bypass_entity_relationships ON entity_relationships;
        DROP POLICY IF EXISTS unified_entity_relationships ON entity_relationships;

        CREATE POLICY unified_entity_relationships ON entity_relationships
            FOR ALL
            USING (rls_check_access(tenant_id))
            WITH CHECK (rls_check_access(tenant_id));
        RAISE NOTICE 'Fixed RLS on entity_relationships';
    END IF;

    -- memory_entities
    IF EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'memory_entities') THEN
        EXECUTE 'ALTER TABLE memory_entities ENABLE ROW LEVEL SECURITY';
        DROP POLICY IF EXISTS tenant_isolation_memory_entities ON memory_entities;
        DROP POLICY IF EXISTS service_bypass_memory_entities ON memory_entities;
        DROP POLICY IF EXISTS unified_memory_entities ON memory_entities;

        CREATE POLICY unified_memory_entities ON memory_entities
            FOR ALL
            USING (rls_check_access(tenant_id))
            WITH CHECK (rls_check_access(tenant_id));
        RAISE NOTICE 'Fixed RLS on memory_entities';
    END IF;

    -- user_personas
    IF EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'user_personas') THEN
        EXECUTE 'ALTER TABLE user_personas ENABLE ROW LEVEL SECURITY';
        DROP POLICY IF EXISTS tenant_isolation_user_personas ON user_personas;
        DROP POLICY IF EXISTS service_bypass_user_personas ON user_personas;
        DROP POLICY IF EXISTS unified_user_personas ON user_personas;

        CREATE POLICY unified_user_personas ON user_personas
            FOR ALL
            USING (rls_check_access(tenant_id))
            WITH CHECK (rls_check_access(tenant_id));
        RAISE NOTICE 'Fixed RLS on user_personas';
    END IF;

    -- conversation_sessions
    IF EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'conversation_sessions') THEN
        EXECUTE 'ALTER TABLE conversation_sessions ENABLE ROW LEVEL SECURITY';
        DROP POLICY IF EXISTS tenant_isolation_conversation_sessions ON conversation_sessions;
        DROP POLICY IF EXISTS service_bypass_conversation_sessions ON conversation_sessions;
        DROP POLICY IF EXISTS unified_conversation_sessions ON conversation_sessions;

        CREATE POLICY unified_conversation_sessions ON conversation_sessions
            FOR ALL
            USING (rls_check_access(tenant_id))
            WITH CHECK (rls_check_access(tenant_id));
        RAISE NOTICE 'Fixed RLS on conversation_sessions';
    END IF;
END $$;

-- =============================================================================
-- STEP 9: Fix SHADOW MEMORY tables (Shadow Branches)
-- =============================================================================

DO $$
BEGIN
    -- shadow_branches
    IF EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'shadow_branches') THEN
        EXECUTE 'ALTER TABLE shadow_branches ENABLE ROW LEVEL SECURITY';
        DROP POLICY IF EXISTS tenant_isolation_shadow_branches ON shadow_branches;
        DROP POLICY IF EXISTS unified_shadow_branches ON shadow_branches;

        CREATE POLICY unified_shadow_branches ON shadow_branches
            FOR ALL
            USING (rls_check_access(tenant_id))
            WITH CHECK (rls_check_access(tenant_id));
        RAISE NOTICE 'Fixed RLS on shadow_branches';
    END IF;

    -- shadow_memories
    IF EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'shadow_memories') THEN
        EXECUTE 'ALTER TABLE shadow_memories ENABLE ROW LEVEL SECURITY';
        DROP POLICY IF EXISTS tenant_isolation_shadow_memories ON shadow_memories;
        DROP POLICY IF EXISTS unified_shadow_memories ON shadow_memories;

        CREATE POLICY unified_shadow_memories ON shadow_memories
            FOR ALL
            USING (rls_check_access(tenant_id))
            WITH CHECK (rls_check_access(tenant_id));
        RAISE NOTICE 'Fixed RLS on shadow_memories';
    END IF;

    -- shadow_entities
    IF EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'shadow_entities') THEN
        EXECUTE 'ALTER TABLE shadow_entities ENABLE ROW LEVEL SECURITY';
        DROP POLICY IF EXISTS tenant_isolation_shadow_entities ON shadow_entities;
        DROP POLICY IF EXISTS unified_shadow_entities ON shadow_entities;

        CREATE POLICY unified_shadow_entities ON shadow_entities
            FOR ALL
            USING (rls_check_access(tenant_id))
            WITH CHECK (rls_check_access(tenant_id));
        RAISE NOTICE 'Fixed RLS on shadow_entities';
    END IF;

    -- shadow_relationships
    IF EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'shadow_relationships') THEN
        EXECUTE 'ALTER TABLE shadow_relationships ENABLE ROW LEVEL SECURITY';
        DROP POLICY IF EXISTS tenant_isolation_shadow_relationships ON shadow_relationships;
        DROP POLICY IF EXISTS unified_shadow_relationships ON shadow_relationships;

        CREATE POLICY unified_shadow_relationships ON shadow_relationships
            FOR ALL
            USING (rls_check_access(tenant_id))
            WITH CHECK (rls_check_access(tenant_id));
        RAISE NOTICE 'Fixed RLS on shadow_relationships';
    END IF;

    -- shadow_promotion_log
    IF EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'shadow_promotion_log') THEN
        EXECUTE 'ALTER TABLE shadow_promotion_log ENABLE ROW LEVEL SECURITY';
        DROP POLICY IF EXISTS tenant_isolation_shadow_promotion_log ON shadow_promotion_log;
        DROP POLICY IF EXISTS unified_shadow_promotion_log ON shadow_promotion_log;

        CREATE POLICY unified_shadow_promotion_log ON shadow_promotion_log
            FOR ALL
            USING (rls_check_access(tenant_id))
            WITH CHECK (rls_check_access(tenant_id));
        RAISE NOTICE 'Fixed RLS on shadow_promotion_log';
    END IF;
END $$;

-- =============================================================================
-- STEP 10: Fix GRAPH/SECURITY EVENT tables
-- =============================================================================

DO $$
BEGIN
    -- graph_events
    IF EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'graph_events') THEN
        EXECUTE 'ALTER TABLE graph_events ENABLE ROW LEVEL SECURITY';
        DROP POLICY IF EXISTS tenant_isolation_graph_events ON graph_events;
        DROP POLICY IF EXISTS unified_graph_events ON graph_events;

        CREATE POLICY unified_graph_events ON graph_events
            FOR ALL
            USING (rls_check_access(tenant_id))
            WITH CHECK (rls_check_access(tenant_id));
        RAISE NOTICE 'Fixed RLS on graph_events';
    END IF;

    -- security_events
    IF EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'security_events') THEN
        EXECUTE 'ALTER TABLE security_events ENABLE ROW LEVEL SECURITY';
        DROP POLICY IF EXISTS tenant_isolation_security_events ON security_events;
        DROP POLICY IF EXISTS unified_security_events ON security_events;

        CREATE POLICY unified_security_events ON security_events
            FOR ALL
            USING (rls_check_access(tenant_id))
            WITH CHECK (rls_check_access(tenant_id));
        RAISE NOTICE 'Fixed RLS on security_events';
    END IF;

    -- security_scans
    IF EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'security_scans') THEN
        EXECUTE 'ALTER TABLE security_scans ENABLE ROW LEVEL SECURITY';
        DROP POLICY IF EXISTS tenant_isolation_security_scans ON security_scans;
        DROP POLICY IF EXISTS unified_security_scans ON security_scans;

        CREATE POLICY unified_security_scans ON security_scans
            FOR ALL
            USING (rls_check_access(tenant_id))
            WITH CHECK (rls_check_access(tenant_id));
        RAISE NOTICE 'Fixed RLS on security_scans';
    END IF;
END $$;

-- =============================================================================
-- STEP 11: Fix PRIVACY/INTEGRITY tables
-- =============================================================================

DO $$
BEGIN
    -- tenant_privacy_settings
    IF EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'tenant_privacy_settings') THEN
        EXECUTE 'ALTER TABLE tenant_privacy_settings ENABLE ROW LEVEL SECURITY';
        DROP POLICY IF EXISTS tenant_isolation_tenant_privacy_settings ON tenant_privacy_settings;
        DROP POLICY IF EXISTS unified_tenant_privacy_settings ON tenant_privacy_settings;

        CREATE POLICY unified_tenant_privacy_settings ON tenant_privacy_settings
            FOR ALL
            USING (rls_check_access(tenant_id))
            WITH CHECK (rls_check_access(tenant_id));
        RAISE NOTICE 'Fixed RLS on tenant_privacy_settings';
    END IF;

    -- privacy_audit_proofs
    IF EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'privacy_audit_proofs') THEN
        EXECUTE 'ALTER TABLE privacy_audit_proofs ENABLE ROW LEVEL SECURITY';
        DROP POLICY IF EXISTS tenant_isolation_privacy_audit_proofs ON privacy_audit_proofs;
        DROP POLICY IF EXISTS unified_privacy_audit_proofs ON privacy_audit_proofs;

        CREATE POLICY unified_privacy_audit_proofs ON privacy_audit_proofs
            FOR ALL
            USING (rls_check_access_text(tenant_id))
            WITH CHECK (rls_check_access_text(tenant_id));
        RAISE NOTICE 'Fixed RLS on privacy_audit_proofs';
    END IF;

    -- integrity_verification_runs
    IF EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'integrity_verification_runs') THEN
        EXECUTE 'ALTER TABLE integrity_verification_runs ENABLE ROW LEVEL SECURITY';
        DROP POLICY IF EXISTS tenant_isolation_integrity_verification_runs ON integrity_verification_runs;
        DROP POLICY IF EXISTS unified_integrity_verification_runs ON integrity_verification_runs;

        CREATE POLICY unified_integrity_verification_runs ON integrity_verification_runs
            FOR ALL
            USING (rls_check_access(tenant_id))
            WITH CHECK (rls_check_access(tenant_id));
        RAISE NOTICE 'Fixed RLS on integrity_verification_runs';
    END IF;

    -- integrity_chain_anchors
    IF EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'integrity_chain_anchors') THEN
        EXECUTE 'ALTER TABLE integrity_chain_anchors ENABLE ROW LEVEL SECURITY';
        DROP POLICY IF EXISTS tenant_isolation_integrity_chain_anchors ON integrity_chain_anchors;
        DROP POLICY IF EXISTS unified_integrity_chain_anchors ON integrity_chain_anchors;

        CREATE POLICY unified_integrity_chain_anchors ON integrity_chain_anchors
            FOR ALL
            USING (rls_check_access(tenant_id))
            WITH CHECK (rls_check_access(tenant_id));
        RAISE NOTICE 'Fixed RLS on integrity_chain_anchors';
    END IF;
END $$;

-- =============================================================================
-- STEP 12: Fix REASONING tables
-- =============================================================================

DO $$
BEGIN
    -- reasoning_executions (if exists)
    IF EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'reasoning_executions') THEN
        EXECUTE 'ALTER TABLE reasoning_executions ENABLE ROW LEVEL SECURITY';
        DROP POLICY IF EXISTS tenant_isolation_reasoning_executions ON reasoning_executions;
        DROP POLICY IF EXISTS unified_reasoning_executions ON reasoning_executions;

        CREATE POLICY unified_reasoning_executions ON reasoning_executions
            FOR ALL
            USING (rls_check_access(tenant_id))
            WITH CHECK (rls_check_access(tenant_id));
        RAISE NOTICE 'Fixed RLS on reasoning_executions';
    END IF;

    -- agent_traces
    IF EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'agent_traces') THEN
        EXECUTE 'ALTER TABLE agent_traces ENABLE ROW LEVEL SECURITY';
        DROP POLICY IF EXISTS tenant_isolation_agent_traces ON agent_traces;
        DROP POLICY IF EXISTS unified_agent_traces ON agent_traces;

        CREATE POLICY unified_agent_traces ON agent_traces
            FOR ALL
            USING (rls_check_access(tenant_id))
            WITH CHECK (rls_check_access(tenant_id));
        RAISE NOTICE 'Fixed RLS on agent_traces';
    END IF;
END $$;

-- =============================================================================
-- STEP 13: Fix SELF-HEALING tables
-- =============================================================================

DO $$
BEGIN
    -- self_healing_recommendations
    IF EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'self_healing_recommendations') THEN
        EXECUTE 'ALTER TABLE self_healing_recommendations ENABLE ROW LEVEL SECURITY';
        DROP POLICY IF EXISTS unified_self_healing_recommendations ON self_healing_recommendations;

        CREATE POLICY unified_self_healing_recommendations ON self_healing_recommendations
            FOR ALL
            USING (rls_check_access(tenant_id))
            WITH CHECK (rls_check_access(tenant_id));
        RAISE NOTICE 'Fixed RLS on self_healing_recommendations';
    END IF;

    -- memory_reembed_jobs
    IF EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'memory_reembed_jobs') THEN
        EXECUTE 'ALTER TABLE memory_reembed_jobs ENABLE ROW LEVEL SECURITY';
        DROP POLICY IF EXISTS unified_memory_reembed_jobs ON memory_reembed_jobs;

        CREATE POLICY unified_memory_reembed_jobs ON memory_reembed_jobs
            FOR ALL
            USING (rls_check_access(tenant_id))
            WITH CHECK (rls_check_access(tenant_id));
        RAISE NOTICE 'Fixed RLS on memory_reembed_jobs';
    END IF;

    -- memory_recrawl_jobs
    IF EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'memory_recrawl_jobs') THEN
        EXECUTE 'ALTER TABLE memory_recrawl_jobs ENABLE ROW LEVEL SECURITY';
        DROP POLICY IF EXISTS unified_memory_recrawl_jobs ON memory_recrawl_jobs;

        CREATE POLICY unified_memory_recrawl_jobs ON memory_recrawl_jobs
            FOR ALL
            USING (rls_check_access(tenant_id))
            WITH CHECK (rls_check_access(tenant_id));
        RAISE NOTICE 'Fixed RLS on memory_recrawl_jobs';
    END IF;

    -- manual_review_items
    IF EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'manual_review_items') THEN
        EXECUTE 'ALTER TABLE manual_review_items ENABLE ROW LEVEL SECURITY';
        DROP POLICY IF EXISTS unified_manual_review_items ON manual_review_items;

        CREATE POLICY unified_manual_review_items ON manual_review_items
            FOR ALL
            USING (rls_check_access(tenant_id))
            WITH CHECK (rls_check_access(tenant_id));
        RAISE NOTICE 'Fixed RLS on manual_review_items';
    END IF;
END $$;

-- =============================================================================
-- STEP 14: Fix ABUSE PROTECTION tables
-- =============================================================================

DO $$
BEGIN
    -- rate_limit_hits
    IF EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'rate_limit_hits') THEN
        EXECUTE 'ALTER TABLE rate_limit_hits ENABLE ROW LEVEL SECURITY';
        DROP POLICY IF EXISTS unified_rate_limit_hits ON rate_limit_hits;

        CREATE POLICY unified_rate_limit_hits ON rate_limit_hits
            FOR ALL
            USING (rls_check_access(tenant_id))
            WITH CHECK (rls_check_access(tenant_id));
        RAISE NOTICE 'Fixed RLS on rate_limit_hits';
    END IF;

    -- cost_records
    IF EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'cost_records') THEN
        EXECUTE 'ALTER TABLE cost_records ENABLE ROW LEVEL SECURITY';
        DROP POLICY IF EXISTS unified_cost_records ON cost_records;

        CREATE POLICY unified_cost_records ON cost_records
            FOR ALL
            USING (rls_check_access(tenant_id))
            WITH CHECK (rls_check_access(tenant_id));
        RAISE NOTICE 'Fixed RLS on cost_records';
    END IF;

    -- budget_alert_thresholds
    IF EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'budget_alert_thresholds') THEN
        EXECUTE 'ALTER TABLE budget_alert_thresholds ENABLE ROW LEVEL SECURITY';
        DROP POLICY IF EXISTS unified_budget_alert_thresholds ON budget_alert_thresholds;

        CREATE POLICY unified_budget_alert_thresholds ON budget_alert_thresholds
            FOR ALL
            USING (rls_check_access(tenant_id))
            WITH CHECK (rls_check_access(tenant_id));
        RAISE NOTICE 'Fixed RLS on budget_alert_thresholds';
    END IF;

    -- payload_violations
    IF EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'payload_violations') THEN
        EXECUTE 'ALTER TABLE payload_violations ENABLE ROW LEVEL SECURITY';
        DROP POLICY IF EXISTS unified_payload_violations ON payload_violations;

        CREATE POLICY unified_payload_violations ON payload_violations
            FOR ALL
            USING (rls_check_access(tenant_id))
            WITH CHECK (rls_check_access(tenant_id));
        RAISE NOTICE 'Fixed RLS on payload_violations';
    END IF;

    -- abuse_patterns
    IF EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'abuse_patterns') THEN
        EXECUTE 'ALTER TABLE abuse_patterns ENABLE ROW LEVEL SECURITY';
        DROP POLICY IF EXISTS unified_abuse_patterns ON abuse_patterns;

        CREATE POLICY unified_abuse_patterns ON abuse_patterns
            FOR ALL
            USING (rls_check_access(tenant_id))
            WITH CHECK (rls_check_access(tenant_id));
        RAISE NOTICE 'Fixed RLS on abuse_patterns';
    END IF;
END $$;

-- =============================================================================
-- STEP 15: Fix JOB SUPERVISION tables
-- =============================================================================

DO $$
BEGIN
    -- supervised_jobs
    IF EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'supervised_jobs') THEN
        EXECUTE 'ALTER TABLE supervised_jobs ENABLE ROW LEVEL SECURITY';
        DROP POLICY IF EXISTS unified_supervised_jobs ON supervised_jobs;

        CREATE POLICY unified_supervised_jobs ON supervised_jobs
            FOR ALL
            USING (rls_check_access(tenant_id))
            WITH CHECK (rls_check_access(tenant_id));
        RAISE NOTICE 'Fixed RLS on supervised_jobs';
    END IF;
END $$;

-- =============================================================================
-- STEP 16: Fix ONBOARDING/EMAIL tables
-- =============================================================================

DO $$
BEGIN
    -- onboarding_events
    IF EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'onboarding_events') THEN
        EXECUTE 'ALTER TABLE onboarding_events ENABLE ROW LEVEL SECURITY';
        DROP POLICY IF EXISTS unified_onboarding_events ON onboarding_events;

        CREATE POLICY unified_onboarding_events ON onboarding_events
            FOR ALL
            USING (rls_check_access(tenant_id))
            WITH CHECK (rls_check_access(tenant_id));
        RAISE NOTICE 'Fixed RLS on onboarding_events';
    END IF;

    -- email_verification_tokens
    IF EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'email_verification_tokens') THEN
        EXECUTE 'ALTER TABLE email_verification_tokens ENABLE ROW LEVEL SECURITY';
        DROP POLICY IF EXISTS unified_email_verification_tokens ON email_verification_tokens;

        CREATE POLICY unified_email_verification_tokens ON email_verification_tokens
            FOR ALL
            USING (rls_check_access(tenant_id))
            WITH CHECK (rls_check_access(tenant_id));
        RAISE NOTICE 'Fixed RLS on email_verification_tokens';
    END IF;

    -- email_audit_log
    IF EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'email_audit_log') THEN
        EXECUTE 'ALTER TABLE email_audit_log ENABLE ROW LEVEL SECURITY';
        DROP POLICY IF EXISTS unified_email_audit_log ON email_audit_log;

        CREATE POLICY unified_email_audit_log ON email_audit_log
            FOR ALL
            USING (rls_check_access(tenant_id))
            WITH CHECK (rls_check_access(tenant_id));
        RAISE NOTICE 'Fixed RLS on email_audit_log';
    END IF;
END $$;

-- =============================================================================
-- STEP 17: Grant permissions to contextdb_app
-- =============================================================================

-- Grant permissions on new functions
GRANT EXECUTE ON FUNCTION is_internal_admin() TO contextdb_app;
GRANT EXECUTE ON FUNCTION set_internal_admin_role() TO contextdb_app;
GRANT EXECUTE ON FUNCTION clear_internal_admin_role() TO contextdb_app;
GRANT EXECUTE ON FUNCTION rls_check_access(UUID) TO contextdb_app;
GRANT EXECUTE ON FUNCTION rls_check_access_text(TEXT) TO contextdb_app;
GRANT EXECUTE ON FUNCTION rls_check_system_access(UUID) TO contextdb_app;

COMMIT;

-- =============================================================================
-- VERIFICATION: Test the internal admin bypass
-- =============================================================================

-- Test: Internal admin should be able to insert into emergency_cutoffs
DO $$
DECLARE
    v_test_id UUID := gen_random_uuid();
BEGIN
    -- Set internal admin role
    PERFORM set_config('app.role', 'internal_admin', true);

    -- Try to insert into emergency_cutoffs
    INSERT INTO emergency_cutoffs (id, tenant_id, reason, triggered_at, triggered_by, is_active, metadata)
    VALUES (v_test_id, NULL, 'RLS fix verification test', NOW(), 'system', FALSE, '{"test": true}'::jsonb);

    -- Clean up
    DELETE FROM emergency_cutoffs WHERE id = v_test_id;

    -- Clear role
    PERFORM set_config('app.role', '', true);

    RAISE NOTICE 'SUCCESS: Internal admin bypass verified for emergency_cutoffs';
EXCEPTION WHEN OTHERS THEN
    RAISE EXCEPTION 'FAILED: Internal admin bypass test failed: %', SQLERRM;
END $$;

RAISE NOTICE 'RLS fix migration completed successfully!';
