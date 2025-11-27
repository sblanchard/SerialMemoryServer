-- Migration Script: Add tenant_id to memories table with safe multi-tenant isolation
-- This script is IDEMPOTENT and can be run multiple times safely
-- Run order: 1. multi_tenant_schema.sql (creates tenants table)
--            2. This script (adds tenant_id to memories)
--            3. rls_policies.sql (enables RLS)

BEGIN;

-- =============================================================================
-- STEP 1: Add tenant_id column if it doesn't exist (nullable initially)
-- =============================================================================

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_name = 'memories' AND column_name = 'tenant_id'
    ) THEN
        ALTER TABLE memories ADD COLUMN tenant_id UUID NULL;
        RAISE NOTICE 'Added tenant_id column to memories table';
    ELSE
        RAISE NOTICE 'tenant_id column already exists on memories table';
    END IF;
END $$;

-- =============================================================================
-- STEP 2: Ensure default tenant exists (for backfill)
-- =============================================================================

-- Create tenants table if it doesn't exist (minimal version for migration)
CREATE TABLE IF NOT EXISTS tenants (
    id UUID PRIMARY KEY,
    name TEXT NOT NULL,
    slug TEXT UNIQUE,
    status TEXT DEFAULT 'active',
    created_at TIMESTAMPTZ DEFAULT NOW()
);

-- Insert default self-hosted tenant
INSERT INTO tenants (id, name, slug, status)
VALUES ('00000000-0000-0000-0000-000000000000', 'Self-Hosted', 'self-hosted', 'active')
ON CONFLICT (id) DO NOTHING;

-- Insert the production tenant for stephan@serialcoder.com
INSERT INTO tenants (id, name, slug, status)
VALUES ('019ac272-2239-7407-9f5e-b1d4e4232dc7', 'SerialCoder Production', 'serialcoder', 'active')
ON CONFLICT (id) DO NOTHING;

-- =============================================================================
-- STEP 3: Backfill existing memories with tenant_id
-- =============================================================================

-- For production: Assign all existing memories to the SerialCoder tenant
-- This is the primary owner: stephan@serialcoder.com
DO $$
DECLARE
    v_production_tenant_id UUID := '019ac272-2239-7407-9f5e-b1d4e4232dc7';
    v_count INTEGER;
BEGIN
    UPDATE memories
    SET tenant_id = v_production_tenant_id
    WHERE tenant_id IS NULL;

    GET DIAGNOSTICS v_count = ROW_COUNT;
    RAISE NOTICE 'Backfilled % memories with tenant_id %', v_count, v_production_tenant_id;
END $$;

-- =============================================================================
-- STEP 4: Enforce NOT NULL constraint
-- =============================================================================

DO $$
BEGIN
    -- Check if column is already NOT NULL
    IF EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_name = 'memories'
        AND column_name = 'tenant_id'
        AND is_nullable = 'YES'
    ) THEN
        -- Verify no NULLs remain before adding constraint
        IF EXISTS (SELECT 1 FROM memories WHERE tenant_id IS NULL LIMIT 1) THEN
            RAISE EXCEPTION 'Cannot set NOT NULL: memories table still has NULL tenant_id values';
        END IF;

        ALTER TABLE memories ALTER COLUMN tenant_id SET NOT NULL;
        RAISE NOTICE 'Set NOT NULL constraint on memories.tenant_id';
    ELSE
        RAISE NOTICE 'tenant_id is already NOT NULL';
    END IF;
END $$;

-- =============================================================================
-- STEP 5: Create indexes for tenant filtering (if they don't exist)
-- =============================================================================

CREATE INDEX IF NOT EXISTS idx_memories_tenant_id ON memories(tenant_id);
CREATE INDEX IF NOT EXISTS idx_memories_tenant_created ON memories(tenant_id, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_memories_tenant_session ON memories(tenant_id, conversation_session_id);

-- =============================================================================
-- STEP 6: Enable Row Level Security
-- =============================================================================

ALTER TABLE memories ENABLE ROW LEVEL SECURITY;

-- Create RLS policy (drop first if exists to make idempotent)
DROP POLICY IF EXISTS tenant_isolation_memories ON memories;
CREATE POLICY tenant_isolation_memories ON memories
    FOR ALL
    USING (tenant_id = current_setting('app.tenant_id', true)::uuid)
    WITH CHECK (tenant_id = current_setting('app.tenant_id', true)::uuid);

-- =============================================================================
-- STEP 7: Verify migration
-- =============================================================================

DO $$
DECLARE
    v_has_tenant_id BOOLEAN;
    v_is_not_null BOOLEAN;
    v_has_rls BOOLEAN;
    v_memory_count BIGINT;
    v_null_count BIGINT;
BEGIN
    -- Check column exists
    SELECT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_name = 'memories' AND column_name = 'tenant_id'
    ) INTO v_has_tenant_id;

    -- Check NOT NULL
    SELECT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_name = 'memories'
        AND column_name = 'tenant_id'
        AND is_nullable = 'NO'
    ) INTO v_is_not_null;

    -- Check RLS enabled
    SELECT relrowsecurity FROM pg_class WHERE relname = 'memories' INTO v_has_rls;

    -- Count memories and NULLs
    SELECT COUNT(*), COUNT(*) FILTER (WHERE tenant_id IS NULL)
    INTO v_memory_count, v_null_count
    FROM memories;

    RAISE NOTICE '=== Migration Verification ===';
    RAISE NOTICE 'tenant_id column exists: %', v_has_tenant_id;
    RAISE NOTICE 'tenant_id is NOT NULL: %', v_is_not_null;
    RAISE NOTICE 'RLS enabled: %', v_has_rls;
    RAISE NOTICE 'Total memories: %', v_memory_count;
    RAISE NOTICE 'Memories with NULL tenant_id: %', v_null_count;

    IF NOT v_has_tenant_id THEN
        RAISE EXCEPTION 'Migration failed: tenant_id column not found';
    END IF;

    IF NOT v_is_not_null THEN
        RAISE EXCEPTION 'Migration failed: tenant_id is not NOT NULL';
    END IF;

    IF NOT v_has_rls THEN
        RAISE EXCEPTION 'Migration failed: RLS not enabled';
    END IF;

    IF v_null_count > 0 THEN
        RAISE EXCEPTION 'Migration failed: % memories still have NULL tenant_id', v_null_count;
    END IF;

    RAISE NOTICE 'Migration completed successfully!';
END $$;

COMMIT;

-- =============================================================================
-- STEP 8: Add foreign key constraint (optional, run separately if tenants table exists)
-- =============================================================================

-- Uncomment if you want FK enforcement:
-- ALTER TABLE memories
-- ADD CONSTRAINT fk_memories_tenant
-- FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE RESTRICT;
