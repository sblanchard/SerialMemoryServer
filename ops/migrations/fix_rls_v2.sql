-- =============================================================================
-- CORRECTED RLS FIX: Internal Admin Bypass for All System Tables
-- =============================================================================
-- This migration adds internal admin bypass to existing RLS policies.
-- Uses the existing app.current_tenant_id convention.
-- =============================================================================

-- =============================================================================
-- STEP 1: Create helper functions for internal admin role
-- =============================================================================

CREATE OR REPLACE FUNCTION is_internal_admin()
RETURNS BOOLEAN AS $$
BEGIN
    RETURN COALESCE(current_setting('app.role', true), '') = 'internal_admin';
END;
$$ LANGUAGE plpgsql STABLE;

CREATE OR REPLACE FUNCTION set_internal_admin_role()
RETURNS VOID AS $$
BEGIN
    PERFORM set_config('app.role', 'internal_admin', true);
END;
$$ LANGUAGE plpgsql;

-- =============================================================================
-- STEP 2: Fix EMERGENCY_CUTOFFS table (Control Room)
-- =============================================================================

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'emergency_cutoffs') THEN
        -- Drop existing policy
        DROP POLICY IF EXISTS emergency_cutoffs_tenant_isolation ON emergency_cutoffs;
        DROP POLICY IF EXISTS unified_emergency_cutoffs ON emergency_cutoffs;

        -- Create new policy with internal admin bypass
        CREATE POLICY unified_emergency_cutoffs ON emergency_cutoffs
            FOR ALL
            USING (
                COALESCE(current_setting('app.role', true), '') = 'internal_admin'
                OR tenant_id = COALESCE(NULLIF(current_setting('app.current_tenant_id', true), '')::uuid, '00000000-0000-0000-0000-000000000000'::uuid)
            )
            WITH CHECK (
                COALESCE(current_setting('app.role', true), '') = 'internal_admin'
                OR tenant_id = COALESCE(NULLIF(current_setting('app.current_tenant_id', true), '')::uuid, '00000000-0000-0000-0000-000000000000'::uuid)
            );

        RAISE NOTICE 'Fixed RLS on emergency_cutoffs';
    END IF;
END $$;

-- =============================================================================
-- STEP 3: Fix ADMIN_ACTIONS table
-- =============================================================================

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'admin_actions') THEN
        DROP POLICY IF EXISTS tenant_isolation_admin_actions ON admin_actions;
        DROP POLICY IF EXISTS service_bypass_admin_actions ON admin_actions;
        DROP POLICY IF EXISTS unified_admin_actions ON admin_actions;

        CREATE POLICY unified_admin_actions ON admin_actions
            FOR ALL
            USING (
                COALESCE(current_setting('app.role', true), '') = 'internal_admin'
                OR tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid
            )
            WITH CHECK (
                COALESCE(current_setting('app.role', true), '') = 'internal_admin'
                OR tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid
            );

        RAISE NOTICE 'Fixed RLS on admin_actions';
    END IF;
END $$;

-- =============================================================================
-- STEP 4: Fix PRIVACY_AUDIT_ENTRIES table (Privacy & Integrity)
-- =============================================================================

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'privacy_audit_entries') THEN
        DROP POLICY IF EXISTS tenant_isolation_privacy_audit ON privacy_audit_entries;
        DROP POLICY IF EXISTS unified_privacy_audit_entries ON privacy_audit_entries;

        CREATE POLICY unified_privacy_audit_entries ON privacy_audit_entries
            FOR ALL
            USING (
                COALESCE(current_setting('app.role', true), '') = 'internal_admin'
                OR tenant_id = current_setting('app.current_tenant_id', true)
            )
            WITH CHECK (
                COALESCE(current_setting('app.role', true), '') = 'internal_admin'
                OR tenant_id = current_setting('app.current_tenant_id', true)
            );

        RAISE NOTICE 'Fixed RLS on privacy_audit_entries';
    END IF;
END $$;

-- =============================================================================
-- STEP 5: Fix MIND HEALTH tables
-- =============================================================================

DO $$
BEGIN
    -- mind_confidence_observations
    IF EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'mind_confidence_observations') THEN
        DROP POLICY IF EXISTS tenant_isolation_mind_confidence ON mind_confidence_observations;
        DROP POLICY IF EXISTS unified_mind_confidence ON mind_confidence_observations;

        CREATE POLICY unified_mind_confidence ON mind_confidence_observations
            FOR ALL
            USING (
                COALESCE(current_setting('app.role', true), '') = 'internal_admin'
                OR tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid
            )
            WITH CHECK (
                COALESCE(current_setting('app.role', true), '') = 'internal_admin'
                OR tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid
            );
        RAISE NOTICE 'Fixed RLS on mind_confidence_observations';
    END IF;

    -- mind_hallucination_events
    IF EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'mind_hallucination_events') THEN
        DROP POLICY IF EXISTS tenant_isolation_mind_hallucination ON mind_hallucination_events;
        DROP POLICY IF EXISTS unified_mind_hallucination ON mind_hallucination_events;

        CREATE POLICY unified_mind_hallucination ON mind_hallucination_events
            FOR ALL
            USING (
                COALESCE(current_setting('app.role', true), '') = 'internal_admin'
                OR tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid
            )
            WITH CHECK (
                COALESCE(current_setting('app.role', true), '') = 'internal_admin'
                OR tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid
            );
        RAISE NOTICE 'Fixed RLS on mind_hallucination_events';
    END IF;

    -- mind_contradiction_events
    IF EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'mind_contradiction_events') THEN
        DROP POLICY IF EXISTS tenant_isolation_mind_contradiction ON mind_contradiction_events;
        DROP POLICY IF EXISTS unified_mind_contradiction ON mind_contradiction_events;

        CREATE POLICY unified_mind_contradiction ON mind_contradiction_events
            FOR ALL
            USING (
                COALESCE(current_setting('app.role', true), '') = 'internal_admin'
                OR tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid
            )
            WITH CHECK (
                COALESCE(current_setting('app.role', true), '') = 'internal_admin'
                OR tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid
            );
        RAISE NOTICE 'Fixed RLS on mind_contradiction_events';
    END IF;

    -- mind_daily_scores
    IF EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'mind_daily_scores') THEN
        DROP POLICY IF EXISTS tenant_isolation_mind_daily_scores ON mind_daily_scores;
        DROP POLICY IF EXISTS unified_mind_daily_scores ON mind_daily_scores;

        CREATE POLICY unified_mind_daily_scores ON mind_daily_scores
            FOR ALL
            USING (
                COALESCE(current_setting('app.role', true), '') = 'internal_admin'
                OR tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid
            )
            WITH CHECK (
                COALESCE(current_setting('app.role', true), '') = 'internal_admin'
                OR tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid
            );
        RAISE NOTICE 'Fixed RLS on mind_daily_scores';
    END IF;
END $$;

-- =============================================================================
-- STEP 6: Fix USAGE/BILLING tables
-- =============================================================================

DO $$
BEGIN
    -- usage_events
    IF EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'usage_events') THEN
        DROP POLICY IF EXISTS tenant_isolation_usage_events ON usage_events;
        DROP POLICY IF EXISTS unified_usage_events ON usage_events;

        CREATE POLICY unified_usage_events ON usage_events
            FOR ALL
            USING (
                COALESCE(current_setting('app.role', true), '') = 'internal_admin'
                OR tenant_id = current_setting('app.current_tenant_id', true)
            )
            WITH CHECK (
                COALESCE(current_setting('app.role', true), '') = 'internal_admin'
                OR tenant_id = current_setting('app.current_tenant_id', true)
            );
        RAISE NOTICE 'Fixed RLS on usage_events';
    END IF;

    -- billing_cycles
    IF EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'billing_cycles') THEN
        DROP POLICY IF EXISTS tenant_isolation_billing_cycles ON billing_cycles;
        DROP POLICY IF EXISTS unified_billing_cycles ON billing_cycles;

        CREATE POLICY unified_billing_cycles ON billing_cycles
            FOR ALL
            USING (
                COALESCE(current_setting('app.role', true), '') = 'internal_admin'
                OR tenant_id = current_setting('app.current_tenant_id', true)
            )
            WITH CHECK (
                COALESCE(current_setting('app.role', true), '') = 'internal_admin'
                OR tenant_id = current_setting('app.current_tenant_id', true)
            );
        RAISE NOTICE 'Fixed RLS on billing_cycles';
    END IF;

    -- usage_daily_rollups
    IF EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'usage_daily_rollups') THEN
        DROP POLICY IF EXISTS tenant_isolation_usage_daily_rollups ON usage_daily_rollups;
        DROP POLICY IF EXISTS unified_usage_daily_rollups ON usage_daily_rollups;

        CREATE POLICY unified_usage_daily_rollups ON usage_daily_rollups
            FOR ALL
            USING (
                COALESCE(current_setting('app.role', true), '') = 'internal_admin'
                OR tenant_id = current_setting('app.current_tenant_id', true)
            )
            WITH CHECK (
                COALESCE(current_setting('app.role', true), '') = 'internal_admin'
                OR tenant_id = current_setting('app.current_tenant_id', true)
            );
        RAISE NOTICE 'Fixed RLS on usage_daily_rollups';
    END IF;
END $$;

-- =============================================================================
-- STEP 7: Fix CORE TENANT tables
-- =============================================================================

DO $$
BEGIN
    -- memories
    IF EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'memories') THEN
        DROP POLICY IF EXISTS tenant_isolation_memories ON memories;
        DROP POLICY IF EXISTS service_bypass_memories ON memories;
        DROP POLICY IF EXISTS unified_memories ON memories;

        CREATE POLICY unified_memories ON memories
            FOR ALL
            USING (
                COALESCE(current_setting('app.role', true), '') = 'internal_admin'
                OR tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid
            )
            WITH CHECK (
                COALESCE(current_setting('app.role', true), '') = 'internal_admin'
                OR tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid
            );
        RAISE NOTICE 'Fixed RLS on memories';
    END IF;

    -- entities
    IF EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'entities') THEN
        DROP POLICY IF EXISTS tenant_isolation_entities ON entities;
        DROP POLICY IF EXISTS unified_entities ON entities;

        CREATE POLICY unified_entities ON entities
            FOR ALL
            USING (
                COALESCE(current_setting('app.role', true), '') = 'internal_admin'
                OR tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid
            )
            WITH CHECK (
                COALESCE(current_setting('app.role', true), '') = 'internal_admin'
                OR tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid
            );
        RAISE NOTICE 'Fixed RLS on entities';
    END IF;

    -- entity_relationships
    IF EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'entity_relationships') THEN
        DROP POLICY IF EXISTS tenant_isolation_entity_relationships ON entity_relationships;
        DROP POLICY IF EXISTS unified_entity_relationships ON entity_relationships;

        CREATE POLICY unified_entity_relationships ON entity_relationships
            FOR ALL
            USING (
                COALESCE(current_setting('app.role', true), '') = 'internal_admin'
                OR tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid
            )
            WITH CHECK (
                COALESCE(current_setting('app.role', true), '') = 'internal_admin'
                OR tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid
            );
        RAISE NOTICE 'Fixed RLS on entity_relationships';
    END IF;
END $$;

-- =============================================================================
-- STEP 8: Fix SHADOW MEMORY tables
-- =============================================================================

DO $$
BEGIN
    -- shadow_branches
    IF EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'shadow_branches') THEN
        DROP POLICY IF EXISTS tenant_isolation_shadow_branches ON shadow_branches;
        DROP POLICY IF EXISTS unified_shadow_branches ON shadow_branches;

        CREATE POLICY unified_shadow_branches ON shadow_branches
            FOR ALL
            USING (
                COALESCE(current_setting('app.role', true), '') = 'internal_admin'
                OR tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid
            )
            WITH CHECK (
                COALESCE(current_setting('app.role', true), '') = 'internal_admin'
                OR tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid
            );
        RAISE NOTICE 'Fixed RLS on shadow_branches';
    END IF;

    -- shadow_memories
    IF EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'shadow_memories') THEN
        DROP POLICY IF EXISTS tenant_isolation_shadow_memories ON shadow_memories;
        DROP POLICY IF EXISTS unified_shadow_memories ON shadow_memories;

        CREATE POLICY unified_shadow_memories ON shadow_memories
            FOR ALL
            USING (
                COALESCE(current_setting('app.role', true), '') = 'internal_admin'
                OR tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid
            )
            WITH CHECK (
                COALESCE(current_setting('app.role', true), '') = 'internal_admin'
                OR tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid
            );
        RAISE NOTICE 'Fixed RLS on shadow_memories';
    END IF;
END $$;

-- =============================================================================
-- STEP 9: Fix INTEGRITY tables
-- =============================================================================

DO $$
BEGIN
    -- integrity_verification_runs
    IF EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'integrity_verification_runs') THEN
        DROP POLICY IF EXISTS tenant_isolation_integrity_runs ON integrity_verification_runs;
        DROP POLICY IF EXISTS unified_integrity_runs ON integrity_verification_runs;

        CREATE POLICY unified_integrity_runs ON integrity_verification_runs
            FOR ALL
            USING (
                COALESCE(current_setting('app.role', true), '') = 'internal_admin'
                OR tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid
            )
            WITH CHECK (
                COALESCE(current_setting('app.role', true), '') = 'internal_admin'
                OR tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid
            );
        RAISE NOTICE 'Fixed RLS on integrity_verification_runs';
    END IF;

    -- integrity_chain_anchors
    IF EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'integrity_chain_anchors') THEN
        DROP POLICY IF EXISTS tenant_isolation_integrity_anchors ON integrity_chain_anchors;
        DROP POLICY IF EXISTS unified_integrity_anchors ON integrity_chain_anchors;

        CREATE POLICY unified_integrity_anchors ON integrity_chain_anchors
            FOR ALL
            USING (
                COALESCE(current_setting('app.role', true), '') = 'internal_admin'
                OR tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid
            )
            WITH CHECK (
                COALESCE(current_setting('app.role', true), '') = 'internal_admin'
                OR tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid
            );
        RAISE NOTICE 'Fixed RLS on integrity_chain_anchors';
    END IF;
END $$;

-- =============================================================================
-- STEP 10: VERIFICATION TEST
-- =============================================================================

DO $$
DECLARE
    v_self_hosted_tenant UUID := '00000000-0000-0000-0000-000000000000';
    v_test_id UUID;
BEGIN
    -- Set internal admin role
    PERFORM set_config('app.role', 'internal_admin', true);
    PERFORM set_config('app.current_tenant_id', v_self_hosted_tenant::text, true);

    -- Try to insert into emergency_cutoffs
    INSERT INTO emergency_cutoffs (id, tenant_id, reason, triggered_at, triggered_by, is_active, metadata)
    VALUES (gen_random_uuid(), v_self_hosted_tenant, 'RLS fix verification test', NOW(), 'system', FALSE, '{"test": true}'::jsonb)
    RETURNING id INTO v_test_id;

    -- Clean up
    DELETE FROM emergency_cutoffs WHERE id = v_test_id;

    -- Clear role
    PERFORM set_config('app.role', '', true);

    RAISE NOTICE '✓ SUCCESS: Internal admin bypass verified for emergency_cutoffs';
EXCEPTION WHEN OTHERS THEN
    RAISE NOTICE '✗ FAILED: Internal admin bypass test failed: %', SQLERRM;
END $$;

SELECT 'RLS fix migration completed!' AS result;
