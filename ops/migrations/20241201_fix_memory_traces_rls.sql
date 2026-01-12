-- ============================================================================
-- Migration: Fix memory_traces table with tenant_id and RLS policies
-- Date: 2024-12-01
-- Description: Adds tenant_id column and RLS policies to memory_traces table
--              for proper multi-tenant isolation
-- ============================================================================

-- Add tenant_id column if missing
ALTER TABLE memory_traces ADD COLUMN IF NOT EXISTS tenant_id uuid REFERENCES tenants(id);

-- Create index for tenant
CREATE INDEX IF NOT EXISTS idx_memory_traces_tenant ON memory_traces(tenant_id);

-- Enable RLS
ALTER TABLE memory_traces ENABLE ROW LEVEL SECURITY;
ALTER TABLE memory_traces FORCE ROW LEVEL SECURITY;

-- Create RLS policy for memory_traces
DROP POLICY IF EXISTS rls_memory_traces ON memory_traces;
CREATE POLICY rls_memory_traces ON memory_traces
    USING (rls_tenant_check(tenant_id))
    WITH CHECK (rls_tenant_check(tenant_id));

-- Internal admin bypass policy
DROP POLICY IF EXISTS internal_admin_memory_traces ON memory_traces;
CREATE POLICY internal_admin_memory_traces ON memory_traces
    USING (is_internal_admin())
    WITH CHECK (is_internal_admin());

-- ============================================================================
-- End of Migration
-- ============================================================================
