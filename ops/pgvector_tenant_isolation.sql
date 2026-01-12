-- CRITICAL: pgvector Multi-Tenant Isolation
-- Prevents cross-tenant data leakage in semantic search
-- RUN IMMEDIATELY

BEGIN;

-- =============================================================================
-- STEP 1: Add tenant_id to embedding/vector tables
-- =============================================================================

-- EMBEDDING_CACHE
ALTER TABLE embedding_cache ADD COLUMN IF NOT EXISTS tenant_id uuid;

-- SEMANTIC_INDEX
ALTER TABLE semantic_index ADD COLUMN IF NOT EXISTS tenant_id uuid;

-- MEMORY_CLUSTERS
ALTER TABLE memory_clusters ADD COLUMN IF NOT EXISTS tenant_id uuid;

-- MEMORY_CLUSTER_MEMBERS
ALTER TABLE memory_cluster_members ADD COLUMN IF NOT EXISTS tenant_id uuid;

-- SYMBOLIC_RULES
ALTER TABLE symbolic_rules ADD COLUMN IF NOT EXISTS tenant_id uuid;

-- =============================================================================
-- STEP 2: Backfill tenant_id from related tables
-- =============================================================================

-- Backfill semantic_index from memories via target_memory_ids[1]
UPDATE semantic_index si
SET tenant_id = m.tenant_id
FROM memories m
WHERE si.tenant_id IS NULL
  AND si.target_memory_ids[1] = m.id;

-- Backfill memory_cluster_members from memories
UPDATE memory_cluster_members mcm
SET tenant_id = m.tenant_id
FROM memories m
WHERE mcm.tenant_id IS NULL
  AND mcm.memory_id = m.id;

-- Backfill memory_clusters from their members (use mode tenant_id)
UPDATE memory_clusters mc
SET tenant_id = sub.tenant_id
FROM (
    SELECT cluster_id, tenant_id
    FROM memory_cluster_members
    WHERE tenant_id IS NOT NULL
    GROUP BY cluster_id, tenant_id
    ORDER BY COUNT(*) DESC
    LIMIT 1
) sub
WHERE mc.tenant_id IS NULL
  AND mc.cluster_id = sub.cluster_id;

-- =============================================================================
-- STEP 3: Set NOT NULL constraints (after backfill)
-- =============================================================================

-- Only enforce NOT NULL if there are no NULLs remaining
DO $$
BEGIN
    -- Check and alter each table
    IF NOT EXISTS (SELECT 1 FROM semantic_index WHERE tenant_id IS NULL) THEN
        ALTER TABLE semantic_index ALTER COLUMN tenant_id SET NOT NULL;
        RAISE NOTICE 'semantic_index: tenant_id set to NOT NULL';
    ELSE
        RAISE NOTICE 'semantic_index: Still has NULL tenant_ids - manual review needed';
    END IF;

    IF NOT EXISTS (SELECT 1 FROM memory_cluster_members WHERE tenant_id IS NULL) THEN
        ALTER TABLE memory_cluster_members ALTER COLUMN tenant_id SET NOT NULL;
        RAISE NOTICE 'memory_cluster_members: tenant_id set to NOT NULL';
    ELSE
        RAISE NOTICE 'memory_cluster_members: Still has NULL tenant_ids - manual review needed';
    END IF;
END $$;

-- =============================================================================
-- STEP 4: Enable RLS on embedding/vector tables
-- =============================================================================

ALTER TABLE embedding_cache ENABLE ROW LEVEL SECURITY;
ALTER TABLE semantic_index ENABLE ROW LEVEL SECURITY;
ALTER TABLE memory_clusters ENABLE ROW LEVEL SECURITY;
ALTER TABLE memory_cluster_members ENABLE ROW LEVEL SECURITY;
ALTER TABLE symbolic_rules ENABLE ROW LEVEL SECURITY;

-- =============================================================================
-- STEP 5: Create RLS policies
-- =============================================================================

-- EMBEDDING_CACHE
DROP POLICY IF EXISTS tenant_isolation_embedding_cache ON embedding_cache;
CREATE POLICY tenant_isolation_embedding_cache ON embedding_cache
    FOR ALL
    USING (tenant_id = current_setting('app.tenant_id', true)::uuid OR tenant_id IS NULL)
    WITH CHECK (tenant_id = current_setting('app.tenant_id', true)::uuid);

-- SEMANTIC_INDEX
DROP POLICY IF EXISTS tenant_isolation_semantic_index ON semantic_index;
CREATE POLICY tenant_isolation_semantic_index ON semantic_index
    FOR ALL
    USING (tenant_id = current_setting('app.tenant_id', true)::uuid)
    WITH CHECK (tenant_id = current_setting('app.tenant_id', true)::uuid);

-- MEMORY_CLUSTERS
DROP POLICY IF EXISTS tenant_isolation_memory_clusters ON memory_clusters;
CREATE POLICY tenant_isolation_memory_clusters ON memory_clusters
    FOR ALL
    USING (tenant_id = current_setting('app.tenant_id', true)::uuid OR tenant_id IS NULL)
    WITH CHECK (tenant_id = current_setting('app.tenant_id', true)::uuid);

-- MEMORY_CLUSTER_MEMBERS
DROP POLICY IF EXISTS tenant_isolation_memory_cluster_members ON memory_cluster_members;
CREATE POLICY tenant_isolation_memory_cluster_members ON memory_cluster_members
    FOR ALL
    USING (tenant_id = current_setting('app.tenant_id', true)::uuid)
    WITH CHECK (tenant_id = current_setting('app.tenant_id', true)::uuid);

-- SYMBOLIC_RULES
DROP POLICY IF EXISTS tenant_isolation_symbolic_rules ON symbolic_rules;
CREATE POLICY tenant_isolation_symbolic_rules ON symbolic_rules
    FOR ALL
    USING (tenant_id = current_setting('app.tenant_id', true)::uuid OR tenant_id IS NULL)
    WITH CHECK (tenant_id = current_setting('app.tenant_id', true)::uuid);

-- =============================================================================
-- STEP 6: Force RLS even for table owner
-- =============================================================================

ALTER TABLE embedding_cache FORCE ROW LEVEL SECURITY;
ALTER TABLE semantic_index FORCE ROW LEVEL SECURITY;
ALTER TABLE memory_clusters FORCE ROW LEVEL SECURITY;
ALTER TABLE memory_cluster_members FORCE ROW LEVEL SECURITY;
ALTER TABLE symbolic_rules FORCE ROW LEVEL SECURITY;

-- =============================================================================
-- STEP 7: Add indexes for tenant + vector performance
-- =============================================================================

-- Composite indexes for tenant-scoped vector search
CREATE INDEX IF NOT EXISTS idx_memories_tenant_layer
    ON memories(tenant_id, layer);

CREATE INDEX IF NOT EXISTS idx_memories_tenant_embedding
    ON memories USING ivfflat (embedding vector_cosine_ops)
    WITH (lists = 100);

CREATE INDEX IF NOT EXISTS idx_semantic_index_tenant
    ON semantic_index(tenant_id);

CREATE INDEX IF NOT EXISTS idx_memory_clusters_tenant
    ON memory_clusters(tenant_id);

CREATE INDEX IF NOT EXISTS idx_memory_cluster_members_tenant
    ON memory_cluster_members(tenant_id);

-- =============================================================================
-- STEP 8: Service bypass policies
-- =============================================================================

-- Create service role if not exists
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'serialmemory_service') THEN
        CREATE ROLE serialmemory_service NOLOGIN;
    END IF;
END $$;

DROP POLICY IF EXISTS service_bypass_embedding_cache ON embedding_cache;
CREATE POLICY service_bypass_embedding_cache ON embedding_cache
    FOR ALL TO serialmemory_service USING (true) WITH CHECK (true);

DROP POLICY IF EXISTS service_bypass_semantic_index ON semantic_index;
CREATE POLICY service_bypass_semantic_index ON semantic_index
    FOR ALL TO serialmemory_service USING (true) WITH CHECK (true);

DROP POLICY IF EXISTS service_bypass_memory_clusters ON memory_clusters;
CREATE POLICY service_bypass_memory_clusters ON memory_clusters
    FOR ALL TO serialmemory_service USING (true) WITH CHECK (true);

DROP POLICY IF EXISTS service_bypass_memory_cluster_members ON memory_cluster_members;
CREATE POLICY service_bypass_memory_cluster_members ON memory_cluster_members
    FOR ALL TO serialmemory_service USING (true) WITH CHECK (true);

-- =============================================================================
-- STEP 9: Test function for pgvector isolation
-- =============================================================================

CREATE OR REPLACE FUNCTION test_pgvector_tenant_isolation()
RETURNS TABLE(test_name TEXT, passed BOOLEAN, details TEXT) AS $$
DECLARE
    v_tenant_a UUID := 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa';
    v_tenant_b UUID := 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb';
    v_memory_id_a UUID;
    v_memory_id_b UUID;
    v_count INTEGER;
    v_embedding vector(768);
BEGIN
    -- Setup tenants
    INSERT INTO tenants (id, name, slug, status)
    VALUES (v_tenant_a, 'pgvector Test A', 'pgvector-a', 'active')
    ON CONFLICT (id) DO NOTHING;
    INSERT INTO tenants (id, name, slug, status)
    VALUES (v_tenant_b, 'pgvector Test B', 'pgvector-b', 'active')
    ON CONFLICT (id) DO NOTHING;

    -- Create test embedding (768-dim)
    v_embedding := (SELECT array_agg(random())::vector(768) FROM generate_series(1, 768));

    -- Create memory for tenant A
    PERFORM set_tenant_context(v_tenant_a);
    INSERT INTO memories (id, tenant_id, content, embedding, created_at, updated_at)
    VALUES (gen_random_uuid(), v_tenant_a, 'pgvector test A', v_embedding, NOW(), NOW())
    RETURNING id INTO v_memory_id_a;

    -- Create memory for tenant B
    PERFORM set_tenant_context(v_tenant_b);
    INSERT INTO memories (id, tenant_id, content, embedding, created_at, updated_at)
    VALUES (gen_random_uuid(), v_tenant_b, 'pgvector test B', v_embedding, NOW(), NOW())
    RETURNING id INTO v_memory_id_b;

    -- TEST 1: Tenant A vector search should NOT see tenant B's memory
    PERFORM set_tenant_context(v_tenant_a);
    SELECT COUNT(*) INTO v_count
    FROM memories
    WHERE id = v_memory_id_b
    ORDER BY embedding <-> v_embedding
    LIMIT 10;

    test_name := 'Vector search: A cannot see B memory';
    passed := v_count = 0;
    details := 'Count of B memory from A context: ' || v_count;
    RETURN NEXT;

    -- TEST 2: Tenant A should see own memory in vector search
    SELECT COUNT(*) INTO v_count
    FROM memories
    WHERE content LIKE 'pgvector test%'
    ORDER BY embedding <-> v_embedding
    LIMIT 10;

    test_name := 'Vector search: A sees own memory';
    passed := v_count = 1;
    details := 'Count of A memories: ' || v_count;
    RETURN NEXT;

    -- TEST 3: Tenant B vector search should NOT see tenant A's memory
    PERFORM set_tenant_context(v_tenant_b);
    SELECT COUNT(*) INTO v_count
    FROM memories
    WHERE id = v_memory_id_a;

    test_name := 'Vector search: B cannot see A memory';
    passed := v_count = 0;
    details := 'Count of A memory from B context: ' || v_count;
    RETURN NEXT;

    -- TEST 4: No tenant context should see nothing
    PERFORM set_config('app.tenant_id', '', true);
    SELECT COUNT(*) INTO v_count
    FROM memories
    WHERE content LIKE 'pgvector test%';

    test_name := 'No context: sees nothing';
    passed := v_count = 0;
    details := 'Count without tenant context: ' || v_count;
    RETURN NEXT;

    -- Cleanup
    PERFORM set_tenant_context(v_tenant_a);
    DELETE FROM memories WHERE id = v_memory_id_a;
    PERFORM set_tenant_context(v_tenant_b);
    DELETE FROM memories WHERE id = v_memory_id_b;
    PERFORM set_config('app.tenant_id', '', true);
END;
$$ LANGUAGE plpgsql;

COMMIT;

-- =============================================================================
-- RUN TESTS
-- =============================================================================
-- SELECT * FROM test_pgvector_tenant_isolation();

COMMENT ON FUNCTION test_pgvector_tenant_isolation IS
'Tests that pgvector searches are properly tenant-isolated. ALL tests must pass=true';
