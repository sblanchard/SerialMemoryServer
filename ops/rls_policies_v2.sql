-- RLS Policies V2 - Complete Multi-Tenant Isolation
-- URGENT: Run this to enable full tenant isolation
-- Covers ALL tenant-scoped tables

BEGIN;

-- =============================================================================
-- STEP 1: Add tenant_id columns where missing
-- =============================================================================

-- Shadow tables
ALTER TABLE shadow_branches ADD COLUMN IF NOT EXISTS tenant_id_uuid uuid;
UPDATE shadow_branches SET tenant_id_uuid = tenant_id::uuid WHERE tenant_id_uuid IS NULL AND tenant_id != 'self';
ALTER TABLE shadow_branches ALTER COLUMN tenant_id_uuid SET NOT NULL;

ALTER TABLE shadow_memories ADD COLUMN IF NOT EXISTS tenant_id_uuid uuid;
UPDATE shadow_memories SET tenant_id_uuid = tenant_id::uuid WHERE tenant_id_uuid IS NULL AND tenant_id != 'self';
ALTER TABLE shadow_memories ALTER COLUMN tenant_id_uuid SET NOT NULL;

ALTER TABLE shadow_entities ADD COLUMN IF NOT EXISTS tenant_id_uuid uuid;
UPDATE shadow_entities SET tenant_id_uuid = tenant_id::uuid WHERE tenant_id_uuid IS NULL AND tenant_id != 'self';
ALTER TABLE shadow_entities ALTER COLUMN tenant_id_uuid SET NOT NULL;

ALTER TABLE shadow_relationships ADD COLUMN IF NOT EXISTS tenant_id_uuid uuid;
UPDATE shadow_relationships SET tenant_id_uuid = tenant_id::uuid WHERE tenant_id_uuid IS NULL AND tenant_id != 'self';
ALTER TABLE shadow_relationships ALTER COLUMN tenant_id_uuid SET NOT NULL;

ALTER TABLE shadow_promotion_log ADD COLUMN IF NOT EXISTS tenant_id_uuid uuid;
UPDATE shadow_promotion_log SET tenant_id_uuid = tenant_id::uuid WHERE tenant_id_uuid IS NULL AND tenant_id != 'self';
ALTER TABLE shadow_promotion_log ALTER COLUMN tenant_id_uuid SET NOT NULL;

-- Graph events
ALTER TABLE graph_events ADD COLUMN IF NOT EXISTS tenant_id uuid;
ALTER TABLE graph_topology_stats ADD COLUMN IF NOT EXISTS tenant_id uuid;
ALTER TABLE graph_metrics_hourly ADD COLUMN IF NOT EXISTS tenant_id uuid;

-- Security events
ALTER TABLE security_events ADD COLUMN IF NOT EXISTS tenant_id uuid;
ALTER TABLE security_scans ADD COLUMN IF NOT EXISTS tenant_id uuid;
ALTER TABLE security_metrics_hourly ADD COLUMN IF NOT EXISTS tenant_id uuid;

-- =============================================================================
-- STEP 2: Enable RLS on all tenant-scoped tables
-- =============================================================================

-- Core data tables (already in rls_policies.sql, but ensure they're enabled)
ALTER TABLE memories ENABLE ROW LEVEL SECURITY;
ALTER TABLE entities ENABLE ROW LEVEL SECURITY;
ALTER TABLE entity_relationships ENABLE ROW LEVEL SECURITY;
ALTER TABLE memory_entities ENABLE ROW LEVEL SECURITY;
ALTER TABLE user_personas ENABLE ROW LEVEL SECURITY;
ALTER TABLE conversation_sessions ENABLE ROW LEVEL SECURITY;

-- Shadow tables
ALTER TABLE shadow_branches ENABLE ROW LEVEL SECURITY;
ALTER TABLE shadow_memories ENABLE ROW LEVEL SECURITY;
ALTER TABLE shadow_entities ENABLE ROW LEVEL SECURITY;
ALTER TABLE shadow_relationships ENABLE ROW LEVEL SECURITY;
ALTER TABLE shadow_promotion_log ENABLE ROW LEVEL SECURITY;

-- Graph tables
ALTER TABLE graph_events ENABLE ROW LEVEL SECURITY;
ALTER TABLE graph_topology_stats ENABLE ROW LEVEL SECURITY;
ALTER TABLE graph_metrics_hourly ENABLE ROW LEVEL SECURITY;

-- Security tables
ALTER TABLE security_events ENABLE ROW LEVEL SECURITY;
ALTER TABLE security_scans ENABLE ROW LEVEL SECURITY;
ALTER TABLE security_metrics_hourly ENABLE ROW LEVEL SECURITY;

-- Usage tables (if exist)
DO $$ BEGIN
    EXECUTE 'ALTER TABLE usage_records ENABLE ROW LEVEL SECURITY';
EXCEPTION WHEN undefined_table THEN NULL;
END $$;

DO $$ BEGIN
    EXECUTE 'ALTER TABLE api_keys ENABLE ROW LEVEL SECURITY';
EXCEPTION WHEN undefined_table THEN NULL;
END $$;

-- =============================================================================
-- STEP 3: Create RLS policies for all tables
-- =============================================================================

-- SHADOW_BRANCHES
DROP POLICY IF EXISTS tenant_isolation_shadow_branches ON shadow_branches;
CREATE POLICY tenant_isolation_shadow_branches ON shadow_branches
    FOR ALL
    USING (tenant_id_uuid = current_setting('app.tenant_id', true)::uuid)
    WITH CHECK (tenant_id_uuid = current_setting('app.tenant_id', true)::uuid);

-- SHADOW_MEMORIES
DROP POLICY IF EXISTS tenant_isolation_shadow_memories ON shadow_memories;
CREATE POLICY tenant_isolation_shadow_memories ON shadow_memories
    FOR ALL
    USING (tenant_id_uuid = current_setting('app.tenant_id', true)::uuid)
    WITH CHECK (tenant_id_uuid = current_setting('app.tenant_id', true)::uuid);

-- SHADOW_ENTITIES
DROP POLICY IF EXISTS tenant_isolation_shadow_entities ON shadow_entities;
CREATE POLICY tenant_isolation_shadow_entities ON shadow_entities
    FOR ALL
    USING (tenant_id_uuid = current_setting('app.tenant_id', true)::uuid)
    WITH CHECK (tenant_id_uuid = current_setting('app.tenant_id', true)::uuid);

-- SHADOW_RELATIONSHIPS
DROP POLICY IF EXISTS tenant_isolation_shadow_relationships ON shadow_relationships;
CREATE POLICY tenant_isolation_shadow_relationships ON shadow_relationships
    FOR ALL
    USING (tenant_id_uuid = current_setting('app.tenant_id', true)::uuid)
    WITH CHECK (tenant_id_uuid = current_setting('app.tenant_id', true)::uuid);

-- SHADOW_PROMOTION_LOG
DROP POLICY IF EXISTS tenant_isolation_shadow_promotion_log ON shadow_promotion_log;
CREATE POLICY tenant_isolation_shadow_promotion_log ON shadow_promotion_log
    FOR ALL
    USING (tenant_id_uuid = current_setting('app.tenant_id', true)::uuid)
    WITH CHECK (tenant_id_uuid = current_setting('app.tenant_id', true)::uuid);

-- GRAPH_EVENTS
DROP POLICY IF EXISTS tenant_isolation_graph_events ON graph_events;
CREATE POLICY tenant_isolation_graph_events ON graph_events
    FOR ALL
    USING (tenant_id = current_setting('app.tenant_id', true)::uuid OR tenant_id IS NULL)
    WITH CHECK (tenant_id = current_setting('app.tenant_id', true)::uuid);

-- GRAPH_TOPOLOGY_STATS
DROP POLICY IF EXISTS tenant_isolation_graph_topology_stats ON graph_topology_stats;
CREATE POLICY tenant_isolation_graph_topology_stats ON graph_topology_stats
    FOR ALL
    USING (tenant_id = current_setting('app.tenant_id', true)::uuid OR tenant_id IS NULL)
    WITH CHECK (tenant_id = current_setting('app.tenant_id', true)::uuid);

-- GRAPH_METRICS_HOURLY
DROP POLICY IF EXISTS tenant_isolation_graph_metrics ON graph_metrics_hourly;
CREATE POLICY tenant_isolation_graph_metrics ON graph_metrics_hourly
    FOR ALL
    USING (tenant_id = current_setting('app.tenant_id', true)::uuid OR tenant_id IS NULL)
    WITH CHECK (tenant_id = current_setting('app.tenant_id', true)::uuid);

-- SECURITY_EVENTS
DROP POLICY IF EXISTS tenant_isolation_security_events ON security_events;
CREATE POLICY tenant_isolation_security_events ON security_events
    FOR ALL
    USING (tenant_id = current_setting('app.tenant_id', true)::uuid OR tenant_id IS NULL)
    WITH CHECK (tenant_id = current_setting('app.tenant_id', true)::uuid);

-- SECURITY_SCANS
DROP POLICY IF EXISTS tenant_isolation_security_scans ON security_scans;
CREATE POLICY tenant_isolation_security_scans ON security_scans
    FOR ALL
    USING (tenant_id = current_setting('app.tenant_id', true)::uuid OR tenant_id IS NULL)
    WITH CHECK (tenant_id = current_setting('app.tenant_id', true)::uuid);

-- SECURITY_METRICS_HOURLY
DROP POLICY IF EXISTS tenant_isolation_security_metrics ON security_metrics_hourly;
CREATE POLICY tenant_isolation_security_metrics ON security_metrics_hourly
    FOR ALL
    USING (tenant_id = current_setting('app.tenant_id', true)::uuid OR tenant_id IS NULL)
    WITH CHECK (tenant_id = current_setting('app.tenant_id', true)::uuid);

-- API_KEYS (if exists)
DO $$ BEGIN
    DROP POLICY IF EXISTS tenant_isolation_api_keys ON api_keys;
    CREATE POLICY tenant_isolation_api_keys ON api_keys
        FOR ALL
        USING (tenant_id = current_setting('app.tenant_id', true)::uuid)
        WITH CHECK (tenant_id = current_setting('app.tenant_id', true)::uuid);
EXCEPTION WHEN undefined_table THEN NULL;
END $$;

-- USAGE_RECORDS (if exists)
DO $$ BEGIN
    DROP POLICY IF EXISTS tenant_isolation_usage_records ON usage_records;
    CREATE POLICY tenant_isolation_usage_records ON usage_records
        FOR ALL
        USING (tenant_id = current_setting('app.tenant_id', true)::uuid)
        WITH CHECK (tenant_id = current_setting('app.tenant_id', true)::uuid);
EXCEPTION WHEN undefined_table THEN NULL;
END $$;

-- =============================================================================
-- STEP 4: Service bypass policies (for background workers)
-- =============================================================================

-- Create service role if not exists
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'serialmemory_service') THEN
        CREATE ROLE serialmemory_service NOLOGIN;
    END IF;
END $$;

-- Shadow tables service bypass
DROP POLICY IF EXISTS service_bypass_shadow_branches ON shadow_branches;
CREATE POLICY service_bypass_shadow_branches ON shadow_branches
    FOR ALL TO serialmemory_service USING (true) WITH CHECK (true);

DROP POLICY IF EXISTS service_bypass_shadow_memories ON shadow_memories;
CREATE POLICY service_bypass_shadow_memories ON shadow_memories
    FOR ALL TO serialmemory_service USING (true) WITH CHECK (true);

DROP POLICY IF EXISTS service_bypass_shadow_entities ON shadow_entities;
CREATE POLICY service_bypass_shadow_entities ON shadow_entities
    FOR ALL TO serialmemory_service USING (true) WITH CHECK (true);

DROP POLICY IF EXISTS service_bypass_shadow_relationships ON shadow_relationships;
CREATE POLICY service_bypass_shadow_relationships ON shadow_relationships
    FOR ALL TO serialmemory_service USING (true) WITH CHECK (true);

-- Graph tables service bypass
DROP POLICY IF EXISTS service_bypass_graph_events ON graph_events;
CREATE POLICY service_bypass_graph_events ON graph_events
    FOR ALL TO serialmemory_service USING (true) WITH CHECK (true);

DROP POLICY IF EXISTS service_bypass_graph_topology ON graph_topology_stats;
CREATE POLICY service_bypass_graph_topology ON graph_topology_stats
    FOR ALL TO serialmemory_service USING (true) WITH CHECK (true);

-- Security tables service bypass
DROP POLICY IF EXISTS service_bypass_security_events ON security_events;
CREATE POLICY service_bypass_security_events ON security_events
    FOR ALL TO serialmemory_service USING (true) WITH CHECK (true);

DROP POLICY IF EXISTS service_bypass_security_scans ON security_scans;
CREATE POLICY service_bypass_security_scans ON security_scans
    FOR ALL TO serialmemory_service USING (true) WITH CHECK (true);

-- =============================================================================
-- STEP 5: Force RLS for table owner (PRODUCTION HARDENING)
-- =============================================================================

-- FORCE RLS even for table owners - defense in depth
ALTER TABLE memories FORCE ROW LEVEL SECURITY;
ALTER TABLE entities FORCE ROW LEVEL SECURITY;
ALTER TABLE entity_relationships FORCE ROW LEVEL SECURITY;
ALTER TABLE memory_entities FORCE ROW LEVEL SECURITY;
ALTER TABLE user_personas FORCE ROW LEVEL SECURITY;
ALTER TABLE conversation_sessions FORCE ROW LEVEL SECURITY;
ALTER TABLE shadow_branches FORCE ROW LEVEL SECURITY;
ALTER TABLE shadow_memories FORCE ROW LEVEL SECURITY;
ALTER TABLE shadow_entities FORCE ROW LEVEL SECURITY;
ALTER TABLE shadow_relationships FORCE ROW LEVEL SECURITY;
ALTER TABLE shadow_promotion_log FORCE ROW LEVEL SECURITY;
ALTER TABLE graph_events FORCE ROW LEVEL SECURITY;
ALTER TABLE security_events FORCE ROW LEVEL SECURITY;

-- =============================================================================
-- STEP 6: Enhanced isolation test
-- =============================================================================

CREATE OR REPLACE FUNCTION test_full_rls_isolation()
RETURNS TABLE(table_name TEXT, test_name TEXT, passed BOOLEAN, details TEXT) AS $$
DECLARE
    v_tenant_a UUID := 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa';
    v_tenant_b UUID := 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb';
    v_memory_id UUID;
    v_count INTEGER;
BEGIN
    -- Setup tenants
    INSERT INTO tenants (id, name, slug, status)
    VALUES (v_tenant_a, 'RLS Test Tenant A', 'rls-test-a', 'active')
    ON CONFLICT (id) DO NOTHING;
    INSERT INTO tenants (id, name, slug, status)
    VALUES (v_tenant_b, 'RLS Test Tenant B', 'rls-test-b', 'active')
    ON CONFLICT (id) DO NOTHING;

    -- Test memories table
    PERFORM set_tenant_context(v_tenant_a);
    INSERT INTO memories (id, tenant_id, content, created_at, updated_at)
    VALUES (gen_random_uuid(), v_tenant_a, 'RLS Test Memory A', NOW(), NOW())
    RETURNING id INTO v_memory_id;

    PERFORM set_tenant_context(v_tenant_b);
    SELECT COUNT(*) INTO v_count FROM memories WHERE id = v_memory_id;
    table_name := 'memories';
    test_name := 'Cross-tenant read blocked';
    passed := v_count = 0;
    details := 'Tenant B count of A memory: ' || v_count;
    RETURN NEXT;

    -- Test entities table
    PERFORM set_tenant_context(v_tenant_a);
    INSERT INTO entities (id, tenant_id, name, entity_type)
    VALUES (gen_random_uuid(), v_tenant_a, 'RLS Test Entity A', 'TEST');

    PERFORM set_tenant_context(v_tenant_b);
    SELECT COUNT(*) INTO v_count FROM entities WHERE name = 'RLS Test Entity A';
    table_name := 'entities';
    test_name := 'Cross-tenant read blocked';
    passed := v_count = 0;
    details := 'Tenant B count of A entity: ' || v_count;
    RETURN NEXT;

    -- Test user_personas table
    PERFORM set_tenant_context(v_tenant_a);
    INSERT INTO user_personas (tenant_id, user_id, attribute_type, attribute_key, attribute_value)
    VALUES (v_tenant_a, 'rls-test-user', 'preference', 'test_key', 'test_value')
    ON CONFLICT DO NOTHING;

    PERFORM set_tenant_context(v_tenant_b);
    SELECT COUNT(*) INTO v_count FROM user_personas WHERE attribute_key = 'test_key';
    table_name := 'user_personas';
    test_name := 'Cross-tenant read blocked';
    passed := v_count = 0;
    details := 'Tenant B count of A persona: ' || v_count;
    RETURN NEXT;

    -- Cleanup
    PERFORM set_tenant_context(v_tenant_a);
    DELETE FROM memories WHERE content = 'RLS Test Memory A';
    DELETE FROM entities WHERE name = 'RLS Test Entity A';
    DELETE FROM user_personas WHERE attribute_key = 'test_key';

    PERFORM set_config('app.tenant_id', '', true);
END;
$$ LANGUAGE plpgsql;

COMMIT;

-- =============================================================================
-- VERIFICATION: Run this to test RLS
-- =============================================================================
-- SELECT * FROM test_full_rls_isolation();

COMMENT ON FUNCTION test_full_rls_isolation IS 'Comprehensive RLS isolation test. All tests should pass=true';
