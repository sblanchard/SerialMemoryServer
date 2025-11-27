-- Migration: Add tenant_id to entities and entity_relationships tables
-- This enables multi-tenant isolation via RLS

BEGIN;

-- Add tenant_id column to entities table
ALTER TABLE entities ADD COLUMN IF NOT EXISTS tenant_id UUID;

-- Add tenant_id column to entity_relationships table
ALTER TABLE entity_relationships ADD COLUMN IF NOT EXISTS tenant_id UUID;

-- Set default tenant_id for existing rows (Self-Hosted tenant)
UPDATE entities SET tenant_id = '00000000-0000-0000-0000-000000000000' WHERE tenant_id IS NULL;
UPDATE entity_relationships SET tenant_id = '00000000-0000-0000-0000-000000000000' WHERE tenant_id IS NULL;

-- Make tenant_id NOT NULL after populating existing rows
ALTER TABLE entities ALTER COLUMN tenant_id SET NOT NULL;
ALTER TABLE entity_relationships ALTER COLUMN tenant_id SET NOT NULL;

-- Add default for new rows
ALTER TABLE entities ALTER COLUMN tenant_id SET DEFAULT '00000000-0000-0000-0000-000000000000';
ALTER TABLE entity_relationships ALTER COLUMN tenant_id SET DEFAULT '00000000-0000-0000-0000-000000000000';

-- Create indexes for tenant_id
CREATE INDEX IF NOT EXISTS idx_entities_tenant_id ON entities(tenant_id);
CREATE INDEX IF NOT EXISTS idx_entity_relationships_tenant_id ON entity_relationships(tenant_id);

-- Enable RLS on entities table
ALTER TABLE entities ENABLE ROW LEVEL SECURITY;

-- Create RLS policy for entities
DROP POLICY IF EXISTS entities_tenant_isolation ON entities;
CREATE POLICY entities_tenant_isolation ON entities
    USING (tenant_id = current_setting('app.tenant_id', true)::uuid);

-- Enable RLS on entity_relationships table
ALTER TABLE entity_relationships ENABLE ROW LEVEL SECURITY;

-- Create RLS policy for entity_relationships
DROP POLICY IF EXISTS entity_relationships_tenant_isolation ON entity_relationships;
CREATE POLICY entity_relationships_tenant_isolation ON entity_relationships
    USING (tenant_id = current_setting('app.tenant_id', true)::uuid);

COMMIT;

-- Verify migration
SELECT 'entities' as table_name, count(*) as row_count FROM entities
UNION ALL
SELECT 'entity_relationships', count(*) FROM entity_relationships;
