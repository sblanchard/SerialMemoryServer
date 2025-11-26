-- Row-Level Security (RLS) Policies for Multi-Tenant Isolation
-- Run this AFTER add_tenant_id_migration.sql
-- These policies enforce tenant isolation at the database level

-- =============================================================================
-- IMPORTANT NOTES
-- =============================================================================
-- 1. RLS is enforced for all users EXCEPT superusers and table owners
-- 2. The application must SET app.tenant_id before executing queries
-- 3. For self-hosted mode, set app.tenant_id = '00000000-0000-0000-0000-000000000000'
-- 4. Background workers may need BYPASSRLS or a service role

BEGIN;

-- =============================================================================
-- FUNCTION: Set Tenant Context
-- =============================================================================

-- Function to set the current tenant context for RLS
CREATE OR REPLACE FUNCTION set_tenant_context(p_tenant_id UUID)
RETURNS VOID AS $$
BEGIN
    -- Set as local (transaction-scoped) to prevent context leakage
    PERFORM set_config('app.tenant_id', p_tenant_id::text, true);
END;
$$ LANGUAGE plpgsql SECURITY DEFINER;

-- Function to get the current tenant context
CREATE OR REPLACE FUNCTION get_tenant_context()
RETURNS UUID AS $$
BEGIN
    RETURN NULLIF(current_setting('app.tenant_id', true), '')::uuid;
EXCEPTION WHEN OTHERS THEN
    RETURN NULL;
END;
$$ LANGUAGE plpgsql STABLE;

-- Function to check if RLS should be enforced
-- Returns FALSE for self-hosted mode when no tenant is set
CREATE OR REPLACE FUNCTION rls_check_tenant(p_row_tenant_id UUID)
RETURNS BOOLEAN AS $$
DECLARE
    v_current_tenant UUID;
BEGIN
    v_current_tenant := get_tenant_context();

    -- If no tenant context is set, deny access (fail secure)
    IF v_current_tenant IS NULL THEN
        RETURN FALSE;
    END IF;

    -- Check if row belongs to current tenant
    RETURN p_row_tenant_id = v_current_tenant;
END;
$$ LANGUAGE plpgsql STABLE;

-- =============================================================================
-- ENABLE RLS ON ALL TENANT-SCOPED TABLES
-- =============================================================================

-- Core data tables
ALTER TABLE memories ENABLE ROW LEVEL SECURITY;
ALTER TABLE entities ENABLE ROW LEVEL SECURITY;
ALTER TABLE entity_relationships ENABLE ROW LEVEL SECURITY;
ALTER TABLE memory_entities ENABLE ROW LEVEL SECURITY;
ALTER TABLE user_personas ENABLE ROW LEVEL SECURITY;
ALTER TABLE conversation_sessions ENABLE ROW LEVEL SECURITY;

-- Usage/billing tables
ALTER TABLE usage_events ENABLE ROW LEVEL SECURITY;
ALTER TABLE billing_cycles ENABLE ROW LEVEL SECURITY;
ALTER TABLE usage_daily_rollups ENABLE ROW LEVEL SECURITY;

-- Audit tables
ALTER TABLE audit_logs ENABLE ROW LEVEL SECURITY;

-- =============================================================================
-- CREATE RLS POLICIES: Core Data Tables
-- =============================================================================

-- MEMORIES
DROP POLICY IF EXISTS tenant_isolation_memories ON memories;
CREATE POLICY tenant_isolation_memories ON memories
    FOR ALL
    USING (tenant_id = current_setting('app.tenant_id', true)::uuid)
    WITH CHECK (tenant_id = current_setting('app.tenant_id', true)::uuid);

-- ENTITIES
DROP POLICY IF EXISTS tenant_isolation_entities ON entities;
CREATE POLICY tenant_isolation_entities ON entities
    FOR ALL
    USING (tenant_id = current_setting('app.tenant_id', true)::uuid)
    WITH CHECK (tenant_id = current_setting('app.tenant_id', true)::uuid);

-- ENTITY_RELATIONSHIPS
DROP POLICY IF EXISTS tenant_isolation_entity_relationships ON entity_relationships;
CREATE POLICY tenant_isolation_entity_relationships ON entity_relationships
    FOR ALL
    USING (tenant_id = current_setting('app.tenant_id', true)::uuid)
    WITH CHECK (tenant_id = current_setting('app.tenant_id', true)::uuid);

-- MEMORY_ENTITIES
DROP POLICY IF EXISTS tenant_isolation_memory_entities ON memory_entities;
CREATE POLICY tenant_isolation_memory_entities ON memory_entities
    FOR ALL
    USING (tenant_id = current_setting('app.tenant_id', true)::uuid)
    WITH CHECK (tenant_id = current_setting('app.tenant_id', true)::uuid);

-- USER_PERSONAS
DROP POLICY IF EXISTS tenant_isolation_user_personas ON user_personas;
CREATE POLICY tenant_isolation_user_personas ON user_personas
    FOR ALL
    USING (tenant_id = current_setting('app.tenant_id', true)::uuid)
    WITH CHECK (tenant_id = current_setting('app.tenant_id', true)::uuid);

-- CONVERSATION_SESSIONS
DROP POLICY IF EXISTS tenant_isolation_conversation_sessions ON conversation_sessions;
CREATE POLICY tenant_isolation_conversation_sessions ON conversation_sessions
    FOR ALL
    USING (tenant_id = current_setting('app.tenant_id', true)::uuid)
    WITH CHECK (tenant_id = current_setting('app.tenant_id', true)::uuid);

-- =============================================================================
-- CREATE RLS POLICIES: Usage/Billing Tables
-- =============================================================================

-- Note: These tables use tenant_id as TEXT, not UUID
-- We compare against the text representation of app.tenant_id

-- USAGE_EVENTS
DROP POLICY IF EXISTS tenant_isolation_usage_events ON usage_events;
CREATE POLICY tenant_isolation_usage_events ON usage_events
    FOR ALL
    USING (tenant_id = current_setting('app.tenant_id', true))
    WITH CHECK (tenant_id = current_setting('app.tenant_id', true));

-- BILLING_CYCLES
DROP POLICY IF EXISTS tenant_isolation_billing_cycles ON billing_cycles;
CREATE POLICY tenant_isolation_billing_cycles ON billing_cycles
    FOR ALL
    USING (tenant_id = current_setting('app.tenant_id', true))
    WITH CHECK (tenant_id = current_setting('app.tenant_id', true));

-- USAGE_DAILY_ROLLUPS
DROP POLICY IF EXISTS tenant_isolation_usage_daily_rollups ON usage_daily_rollups;
CREATE POLICY tenant_isolation_usage_daily_rollups ON usage_daily_rollups
    FOR ALL
    USING (tenant_id = current_setting('app.tenant_id', true))
    WITH CHECK (tenant_id = current_setting('app.tenant_id', true));

-- AUDIT_LOGS
DROP POLICY IF EXISTS tenant_isolation_audit_logs ON audit_logs;
CREATE POLICY tenant_isolation_audit_logs ON audit_logs
    FOR ALL
    USING (tenant_id = current_setting('app.tenant_id', true))
    WITH CHECK (tenant_id = current_setting('app.tenant_id', true));

-- =============================================================================
-- CREATE SERVICE ROLE FOR BACKGROUND WORKERS
-- =============================================================================

-- Create a service role that bypasses RLS for maintenance operations
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'serialmemory_service') THEN
        CREATE ROLE serialmemory_service NOLOGIN;
        RAISE NOTICE 'Created serialmemory_service role';
    END IF;
END $$;

-- Grant BYPASSRLS to service role (requires superuser)
-- This allows background workers to operate across all tenants
DO $$
BEGIN
    -- Only attempt if current user is superuser
    IF (SELECT usesuper FROM pg_user WHERE usename = current_user) THEN
        ALTER ROLE serialmemory_service BYPASSRLS;
        RAISE NOTICE 'Granted BYPASSRLS to serialmemory_service';
    ELSE
        RAISE NOTICE 'Cannot grant BYPASSRLS - current user is not superuser. Run as superuser if needed.';
    END IF;
EXCEPTION WHEN OTHERS THEN
    RAISE NOTICE 'Could not grant BYPASSRLS: %', SQLERRM;
END $$;

-- Create service bypass policies as alternative (if BYPASSRLS not available)
-- These allow the service role to access all rows

DROP POLICY IF EXISTS service_bypass_memories ON memories;
CREATE POLICY service_bypass_memories ON memories
    FOR ALL
    TO serialmemory_service
    USING (true)
    WITH CHECK (true);

DROP POLICY IF EXISTS service_bypass_entities ON entities;
CREATE POLICY service_bypass_entities ON entities
    FOR ALL
    TO serialmemory_service
    USING (true)
    WITH CHECK (true);

DROP POLICY IF EXISTS service_bypass_entity_relationships ON entity_relationships;
CREATE POLICY service_bypass_entity_relationships ON entity_relationships
    FOR ALL
    TO serialmemory_service
    USING (true)
    WITH CHECK (true);

DROP POLICY IF EXISTS service_bypass_memory_entities ON memory_entities;
CREATE POLICY service_bypass_memory_entities ON memory_entities
    FOR ALL
    TO serialmemory_service
    USING (true)
    WITH CHECK (true);

DROP POLICY IF EXISTS service_bypass_user_personas ON user_personas;
CREATE POLICY service_bypass_user_personas ON user_personas
    FOR ALL
    TO serialmemory_service
    USING (true)
    WITH CHECK (true);

DROP POLICY IF EXISTS service_bypass_conversation_sessions ON conversation_sessions;
CREATE POLICY service_bypass_conversation_sessions ON conversation_sessions
    FOR ALL
    TO serialmemory_service
    USING (true)
    WITH CHECK (true);

-- =============================================================================
-- FORCE RLS FOR TABLE OWNER (RECOMMENDED FOR PRODUCTION)
-- =============================================================================

-- Uncomment these to force RLS even for table owners
-- This provides defense-in-depth

-- ALTER TABLE memories FORCE ROW LEVEL SECURITY;
-- ALTER TABLE entities FORCE ROW LEVEL SECURITY;
-- ALTER TABLE entity_relationships FORCE ROW LEVEL SECURITY;
-- ALTER TABLE memory_entities FORCE ROW LEVEL SECURITY;
-- ALTER TABLE user_personas FORCE ROW LEVEL SECURITY;
-- ALTER TABLE conversation_sessions FORCE ROW LEVEL SECURITY;

-- =============================================================================
-- VERIFICATION FUNCTIONS
-- =============================================================================

-- Function to test RLS is working
CREATE OR REPLACE FUNCTION test_rls_isolation()
RETURNS TABLE(test_name TEXT, passed BOOLEAN, details TEXT) AS $$
DECLARE
    v_tenant_a UUID := 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa';
    v_tenant_b UUID := 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb';
    v_memory_id UUID;
    v_count INTEGER;
BEGIN
    -- Setup: Create test tenants if they don't exist
    INSERT INTO tenants (id, name, slug, status)
    VALUES (v_tenant_a, 'Test Tenant A', 'test-a', 'active')
    ON CONFLICT (id) DO NOTHING;

    INSERT INTO tenants (id, name, slug, status)
    VALUES (v_tenant_b, 'Test Tenant B', 'test-b', 'active')
    ON CONFLICT (id) DO NOTHING;

    -- Test 1: Create memory as tenant A
    PERFORM set_tenant_context(v_tenant_a);
    INSERT INTO memories (id, tenant_id, content, created_at, updated_at)
    VALUES (gen_random_uuid(), v_tenant_a, 'Test memory for tenant A', NOW(), NOW())
    RETURNING id INTO v_memory_id;

    test_name := 'Create memory as tenant A';
    passed := v_memory_id IS NOT NULL;
    details := 'Memory ID: ' || COALESCE(v_memory_id::text, 'NULL');
    RETURN NEXT;

    -- Test 2: Count memories as tenant A (should see 1+)
    SELECT COUNT(*) INTO v_count FROM memories WHERE content LIKE 'Test memory%';
    test_name := 'Tenant A can see own memories';
    passed := v_count >= 1;
    details := 'Count: ' || v_count;
    RETURN NEXT;

    -- Test 3: Switch to tenant B and try to see tenant A's memory
    PERFORM set_tenant_context(v_tenant_b);
    SELECT COUNT(*) INTO v_count FROM memories WHERE id = v_memory_id;
    test_name := 'Tenant B cannot see tenant A memory';
    passed := v_count = 0;
    details := 'Count (should be 0): ' || v_count;
    RETURN NEXT;

    -- Test 4: Tenant B cannot update tenant A's memory (will be 0 rows affected)
    UPDATE memories SET content = 'Hacked!' WHERE id = v_memory_id;
    GET DIAGNOSTICS v_count = ROW_COUNT;
    test_name := 'Tenant B cannot update tenant A memory';
    passed := v_count = 0;
    details := 'Rows updated (should be 0): ' || v_count;
    RETURN NEXT;

    -- Test 5: Tenant B cannot delete tenant A's memory
    DELETE FROM memories WHERE id = v_memory_id;
    GET DIAGNOSTICS v_count = ROW_COUNT;
    test_name := 'Tenant B cannot delete tenant A memory';
    passed := v_count = 0;
    details := 'Rows deleted (should be 0): ' || v_count;
    RETURN NEXT;

    -- Cleanup: Delete test memory as tenant A
    PERFORM set_tenant_context(v_tenant_a);
    DELETE FROM memories WHERE id = v_memory_id;

    -- Clear tenant context
    PERFORM set_config('app.tenant_id', '', true);
END;
$$ LANGUAGE plpgsql;

COMMIT;

-- =============================================================================
-- COMMENTS
-- =============================================================================

COMMENT ON FUNCTION set_tenant_context(UUID) IS 'Sets the current tenant context for RLS policies. Must be called at the start of each request.';
COMMENT ON FUNCTION get_tenant_context() IS 'Returns the current tenant context, or NULL if not set.';
COMMENT ON FUNCTION test_rls_isolation() IS 'Tests that RLS policies properly isolate tenant data. Run to verify isolation.';

-- =============================================================================
-- USAGE EXAMPLE
-- =============================================================================

-- At the start of each request/connection:
--
--   SELECT set_tenant_context('12345678-1234-1234-1234-123456789012');
--
-- Or in application code (C#):
--
--   await connection.ExecuteAsync("SELECT set_tenant_context(@TenantId)", new { TenantId = tenantId });
--
-- For self-hosted mode:
--
--   SELECT set_tenant_context('00000000-0000-0000-0000-000000000000');
