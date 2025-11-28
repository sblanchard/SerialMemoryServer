-- EMERGENCY RLS FIX: Create non-superuser application role
-- This script MUST be run to fix the tenant isolation vulnerability
--
-- THE PROBLEM: The application connects as 'postgres' superuser which BYPASSES ALL RLS
-- THE FIX: Create a dedicated application role without superuser privileges
--
-- Run this as: psql -U postgres -d contextdb -f emergency_rls_fix.sql

BEGIN;

-- =============================================================================
-- STEP 1: Create the application role (if not exists)
-- =============================================================================

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'contextdb_app') THEN
        CREATE ROLE contextdb_app WITH LOGIN PASSWORD 'contextdb_app_secure_password_change_me';
        RAISE NOTICE 'Created role contextdb_app';
    ELSE
        RAISE NOTICE 'Role contextdb_app already exists';
    END IF;
END $$;

-- Ensure the role can connect
ALTER ROLE contextdb_app WITH LOGIN;

-- =============================================================================
-- STEP 2: Grant necessary permissions to contextdb_app
-- =============================================================================

-- Grant connect to database
GRANT CONNECT ON DATABASE contextdb TO contextdb_app;

-- Grant usage on schema
GRANT USAGE ON SCHEMA public TO contextdb_app;

-- Grant SELECT, INSERT, UPDATE, DELETE on all tenant-scoped tables (conditionally)
DO $$
DECLARE
    tbl TEXT;
    tables_to_grant TEXT[] := ARRAY[
        'memories', 'entities', 'entity_relationships', 'memory_entities',
        'user_personas', 'conversation_sessions', 'usage_events', 'billing_cycles',
        'usage_daily_rollups', 'shadow_branches', 'shadow_memories', 'shadow_entities',
        'shadow_relationships', 'shadow_promotion_log', 'graph_events', 'security_events',
        'agent_traces', 'trace_spans', 'maintenance_tasks', 'supervised_jobs',
        'api_keys', 'tenants', 'tenant_plans', 'security_scans'
    ];
BEGIN
    FOREACH tbl IN ARRAY tables_to_grant LOOP
        IF EXISTS (SELECT 1 FROM pg_tables WHERE tablename = tbl AND schemaname = 'public') THEN
            EXECUTE format('GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE %I TO contextdb_app', tbl);
            RAISE NOTICE 'Granted permissions on %', tbl;
        ELSE
            RAISE NOTICE 'Table % does not exist, skipping', tbl;
        END IF;
    END LOOP;
END $$;

-- Grant SELECT on reference tables (conditionally)
DO $$
DECLARE
    tbl TEXT;
    ref_tables TEXT[] := ARRAY['credit_costs', 'plan_limits', 'integrations', 'integration_actions'];
BEGIN
    FOREACH tbl IN ARRAY ref_tables LOOP
        IF EXISTS (SELECT 1 FROM pg_tables WHERE tablename = tbl AND schemaname = 'public') THEN
            EXECUTE format('GRANT SELECT ON TABLE %I TO contextdb_app', tbl);
            RAISE NOTICE 'Granted SELECT on %', tbl;
        ELSE
            RAISE NOTICE 'Reference table % does not exist, skipping', tbl;
        END IF;
    END LOOP;
END $$;

-- Grant EXECUTE on necessary functions (conditionally)
DO $$
DECLARE
    func TEXT;
    funcs TEXT[] := ARRAY[
        'set_tenant_context', 'get_current_tenant', 'require_tenant_context',
        'consume_credits', 'consume_credits_simple', 'refund_credits', 'get_credit_cost'
    ];
BEGIN
    FOREACH func IN ARRAY funcs LOOP
        IF EXISTS (SELECT 1 FROM pg_proc WHERE proname = func) THEN
            EXECUTE format('GRANT EXECUTE ON FUNCTION %I TO contextdb_app', func);
            RAISE NOTICE 'Granted EXECUTE on function %', func;
        ELSE
            RAISE NOTICE 'Function % does not exist, skipping', func;
        END IF;
    END LOOP;
END $$;

-- Grant USAGE on sequences
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO contextdb_app;

-- =============================================================================
-- STEP 3: FORCE ROW LEVEL SECURITY on all tenant-scoped tables
-- This ensures RLS applies even to table owners (but NOT superusers!)
-- =============================================================================

ALTER TABLE memories FORCE ROW LEVEL SECURITY;
ALTER TABLE entities FORCE ROW LEVEL SECURITY;
ALTER TABLE entity_relationships FORCE ROW LEVEL SECURITY;
ALTER TABLE memory_entities FORCE ROW LEVEL SECURITY;
ALTER TABLE user_personas FORCE ROW LEVEL SECURITY;
ALTER TABLE conversation_sessions FORCE ROW LEVEL SECURITY;

-- Additional tables that may have tenant data
DO $$
BEGIN
    -- Only force RLS if table exists
    IF EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'usage_events') THEN
        EXECUTE 'ALTER TABLE usage_events FORCE ROW LEVEL SECURITY';
    END IF;
    IF EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'billing_cycles') THEN
        EXECUTE 'ALTER TABLE billing_cycles FORCE ROW LEVEL SECURITY';
    END IF;
    IF EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'graph_events') THEN
        EXECUTE 'ALTER TABLE graph_events FORCE ROW LEVEL SECURITY';
    END IF;
    IF EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'security_events') THEN
        EXECUTE 'ALTER TABLE security_events FORCE ROW LEVEL SECURITY';
    END IF;
    IF EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'agent_traces') THEN
        EXECUTE 'ALTER TABLE agent_traces FORCE ROW LEVEL SECURITY';
    END IF;
END $$;

-- =============================================================================
-- STEP 4: Verify RLS is properly enabled
-- =============================================================================

DO $$
DECLARE
    v_table TEXT;
    v_rls_enabled BOOLEAN;
    v_rls_forced BOOLEAN;
    v_tables TEXT[] := ARRAY['memories', 'entities', 'entity_relationships', 'user_personas'];
BEGIN
    FOREACH v_table IN ARRAY v_tables LOOP
        SELECT relrowsecurity, relforcerowsecurity
        INTO v_rls_enabled, v_rls_forced
        FROM pg_class
        WHERE relname = v_table;

        IF NOT v_rls_enabled THEN
            RAISE EXCEPTION 'CRITICAL: RLS not enabled on table %', v_table;
        END IF;

        IF NOT v_rls_forced THEN
            RAISE WARNING 'RLS not forced on table % - table owner can bypass', v_table;
        ELSE
            RAISE NOTICE 'OK: Table % has RLS enabled and forced', v_table;
        END IF;
    END LOOP;
END $$;

-- =============================================================================
-- STEP 5: Test that contextdb_app respects RLS
-- =============================================================================

-- This should be run AFTER updating connection strings to use contextdb_app
-- SELECT test_rls_isolation();

COMMIT;

-- =============================================================================
-- IMPORTANT: Update your connection strings!
-- =============================================================================
--
-- OLD (VULNERABLE - bypasses RLS):
--   Host=postgres;Database=contextdb;Username=postgres;Password=postgres
--
-- NEW (SECURE - respects RLS):
--   Host=postgres;Database=contextdb;Username=contextdb_app;Password=contextdb_app_secure_password_change_me
--
-- Files to update:
--   - docker-compose.yml
--   - SerialMemory.Api/appsettings.json
--   - SerialMemory.Api/appsettings.Development.json
--   - SerialMemory.Worker/appsettings.json
--   - SerialMemory.Worker/appsettings.Development.json
--
-- REMEMBER: Change the password above to a secure value!
