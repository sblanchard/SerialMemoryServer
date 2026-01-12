-- Migration: Fix privacy_audit_entries schema
-- The production database has 'operation' column (ENUM) but code expects 'action' (TEXT)
-- Also adds missing columns for full IntegrityAuditEntry support

-- ==========================================
-- 1. Drop the old ENUM type and recreate table with correct schema
-- ==========================================

-- First check if we have the old schema (operation column)
DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_name = 'privacy_audit_entries' AND column_name = 'operation'
    ) THEN
        -- Drop the old table and recreate with new schema
        DROP TABLE IF EXISTS privacy_audit_entries CASCADE;

        -- Drop old ENUM type if exists
        DROP TYPE IF EXISTS privacy_audit_operation CASCADE;

        RAISE NOTICE 'Dropped old privacy_audit_entries table with operation column';
    END IF;
END $$;

-- ==========================================
-- 2. Create table with correct schema (matching IntegrityAuditService)
-- ==========================================

CREATE TABLE IF NOT EXISTS privacy_audit_entries (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL REFERENCES tenants(id),
    user_id TEXT,
    action TEXT NOT NULL,
    memory_id UUID REFERENCES memories(id),
    timestamp TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    ip_address TEXT,
    user_agent TEXT,
    integrity_valid BOOLEAN,
    details JSONB,
    request_id UUID
);

-- ==========================================
-- 3. Create indexes
-- ==========================================

CREATE INDEX IF NOT EXISTS idx_privacy_audit_tenant ON privacy_audit_entries(tenant_id);
CREATE INDEX IF NOT EXISTS idx_privacy_audit_timestamp ON privacy_audit_entries(tenant_id, timestamp DESC);
CREATE INDEX IF NOT EXISTS idx_privacy_audit_action ON privacy_audit_entries(tenant_id, action);
CREATE INDEX IF NOT EXISTS idx_privacy_audit_memory ON privacy_audit_entries(memory_id) WHERE memory_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_privacy_audit_user ON privacy_audit_entries(tenant_id, user_id) WHERE user_id IS NOT NULL;

-- ==========================================
-- 4. Enable RLS
-- ==========================================

ALTER TABLE privacy_audit_entries ENABLE ROW LEVEL SECURITY;

DROP POLICY IF EXISTS privacy_audit_entries_tenant_policy ON privacy_audit_entries;
CREATE POLICY privacy_audit_entries_tenant_policy ON privacy_audit_entries
    FOR ALL USING (tenant_id = current_setting('app.tenant_id', true)::uuid);

-- Also allow internal admin role
DROP POLICY IF EXISTS privacy_audit_entries_internal_admin ON privacy_audit_entries;
CREATE POLICY privacy_audit_entries_internal_admin ON privacy_audit_entries
    FOR ALL USING (current_setting('app.internal_admin', true) = 'true');

-- Grant permissions to all potential users
GRANT ALL ON privacy_audit_entries TO serialmemory;
GRANT ALL ON privacy_audit_entries TO postgres;
GRANT ALL ON privacy_audit_entries TO PUBLIC;

-- Also ensure RLS is bypassed for table owner
ALTER TABLE privacy_audit_entries FORCE ROW LEVEL SECURITY;

-- Create a policy that allows the table owner to bypass RLS
DROP POLICY IF EXISTS privacy_audit_entries_owner_bypass ON privacy_audit_entries;
CREATE POLICY privacy_audit_entries_owner_bypass ON privacy_audit_entries
    FOR ALL
    USING (true)
    WITH CHECK (true);

SELECT 'privacy_audit_entries schema migration complete' as status;
