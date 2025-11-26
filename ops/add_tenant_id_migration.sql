-- Add tenant_id to Core Tables Migration
-- Run this AFTER multi_tenant_schema.sql
-- This migration adds tenant_id column to all core data tables

-- =============================================================================
-- IMPORTANT: This migration should be run in a transaction
-- =============================================================================

BEGIN;

-- =============================================================================
-- STEP 1: Add tenant_id columns (nullable initially for backfill)
-- =============================================================================

-- Memories table
ALTER TABLE memories ADD COLUMN IF NOT EXISTS tenant_id UUID;

-- Entities table
ALTER TABLE entities ADD COLUMN IF NOT EXISTS tenant_id UUID;

-- Entity relationships table
ALTER TABLE entity_relationships ADD COLUMN IF NOT EXISTS tenant_id UUID;

-- Memory entities junction table
ALTER TABLE memory_entities ADD COLUMN IF NOT EXISTS tenant_id UUID;

-- User personas table
ALTER TABLE user_personas ADD COLUMN IF NOT EXISTS tenant_id UUID;

-- Conversation sessions table
ALTER TABLE conversation_sessions ADD COLUMN IF NOT EXISTS tenant_id UUID;

-- =============================================================================
-- STEP 2: Backfill existing data with default tenant (self-hosted)
-- =============================================================================

-- Self-hosted tenant ID (nil UUID)
DO $$
DECLARE
    v_default_tenant_id UUID := '00000000-0000-0000-0000-000000000000';
    v_count INTEGER;
BEGIN
    -- Backfill memories
    UPDATE memories SET tenant_id = v_default_tenant_id WHERE tenant_id IS NULL;
    GET DIAGNOSTICS v_count = ROW_COUNT;
    RAISE NOTICE 'Backfilled % memories with default tenant', v_count;

    -- Backfill entities
    UPDATE entities SET tenant_id = v_default_tenant_id WHERE tenant_id IS NULL;
    GET DIAGNOSTICS v_count = ROW_COUNT;
    RAISE NOTICE 'Backfilled % entities with default tenant', v_count;

    -- Backfill entity_relationships
    UPDATE entity_relationships SET tenant_id = v_default_tenant_id WHERE tenant_id IS NULL;
    GET DIAGNOSTICS v_count = ROW_COUNT;
    RAISE NOTICE 'Backfilled % entity_relationships with default tenant', v_count;

    -- Backfill memory_entities
    UPDATE memory_entities SET tenant_id = v_default_tenant_id WHERE tenant_id IS NULL;
    GET DIAGNOSTICS v_count = ROW_COUNT;
    RAISE NOTICE 'Backfilled % memory_entities with default tenant', v_count;

    -- Backfill user_personas
    UPDATE user_personas SET tenant_id = v_default_tenant_id WHERE tenant_id IS NULL;
    GET DIAGNOSTICS v_count = ROW_COUNT;
    RAISE NOTICE 'Backfilled % user_personas with default tenant', v_count;

    -- Backfill conversation_sessions
    UPDATE conversation_sessions SET tenant_id = v_default_tenant_id WHERE tenant_id IS NULL;
    GET DIAGNOSTICS v_count = ROW_COUNT;
    RAISE NOTICE 'Backfilled % conversation_sessions with default tenant', v_count;
END $$;

-- =============================================================================
-- STEP 3: Add NOT NULL constraints
-- =============================================================================

ALTER TABLE memories ALTER COLUMN tenant_id SET NOT NULL;
ALTER TABLE entities ALTER COLUMN tenant_id SET NOT NULL;
ALTER TABLE entity_relationships ALTER COLUMN tenant_id SET NOT NULL;
ALTER TABLE memory_entities ALTER COLUMN tenant_id SET NOT NULL;
ALTER TABLE user_personas ALTER COLUMN tenant_id SET NOT NULL;
ALTER TABLE conversation_sessions ALTER COLUMN tenant_id SET NOT NULL;

-- =============================================================================
-- STEP 4: Add foreign key constraints to tenants table
-- =============================================================================

-- Note: We use ON DELETE RESTRICT to prevent accidental tenant deletion with data
-- Tenant deletion should be handled by application logic (soft delete, then purge)

ALTER TABLE memories
ADD CONSTRAINT fk_memories_tenant
FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE RESTRICT;

ALTER TABLE entities
ADD CONSTRAINT fk_entities_tenant
FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE RESTRICT;

ALTER TABLE entity_relationships
ADD CONSTRAINT fk_entity_relationships_tenant
FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE RESTRICT;

ALTER TABLE memory_entities
ADD CONSTRAINT fk_memory_entities_tenant
FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE RESTRICT;

ALTER TABLE user_personas
ADD CONSTRAINT fk_user_personas_tenant
FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE RESTRICT;

ALTER TABLE conversation_sessions
ADD CONSTRAINT fk_conversation_sessions_tenant
FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE RESTRICT;

-- =============================================================================
-- STEP 5: Create indexes for tenant filtering
-- =============================================================================

-- Memories - composite index for tenant + common queries
CREATE INDEX IF NOT EXISTS idx_memories_tenant ON memories(tenant_id);
CREATE INDEX IF NOT EXISTS idx_memories_tenant_created ON memories(tenant_id, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_memories_tenant_session ON memories(tenant_id, conversation_session_id);

-- Entities - composite index for tenant + type/name lookups
CREATE INDEX IF NOT EXISTS idx_entities_tenant ON entities(tenant_id);
CREATE INDEX IF NOT EXISTS idx_entities_tenant_type ON entities(tenant_id, entity_type);
CREATE INDEX IF NOT EXISTS idx_entities_tenant_name ON entities(tenant_id, name);

-- Entity relationships
CREATE INDEX IF NOT EXISTS idx_entity_relationships_tenant ON entity_relationships(tenant_id);
CREATE INDEX IF NOT EXISTS idx_entity_relationships_tenant_source ON entity_relationships(tenant_id, source_entity_id);
CREATE INDEX IF NOT EXISTS idx_entity_relationships_tenant_target ON entity_relationships(tenant_id, target_entity_id);

-- Memory entities junction
CREATE INDEX IF NOT EXISTS idx_memory_entities_tenant ON memory_entities(tenant_id);
CREATE INDEX IF NOT EXISTS idx_memory_entities_tenant_memory ON memory_entities(tenant_id, memory_id);
CREATE INDEX IF NOT EXISTS idx_memory_entities_tenant_entity ON memory_entities(tenant_id, entity_id);

-- User personas
CREATE INDEX IF NOT EXISTS idx_user_personas_tenant ON user_personas(tenant_id);
CREATE INDEX IF NOT EXISTS idx_user_personas_tenant_user ON user_personas(tenant_id, user_id);

-- Conversation sessions
CREATE INDEX IF NOT EXISTS idx_conversation_sessions_tenant ON conversation_sessions(tenant_id);
CREATE INDEX IF NOT EXISTS idx_conversation_sessions_tenant_started ON conversation_sessions(tenant_id, started_at DESC);

-- =============================================================================
-- STEP 6: Update unique constraints to include tenant_id
-- =============================================================================

-- Drop old unique constraints and recreate with tenant_id
-- This ensures uniqueness is scoped to tenant

-- Entities: (name, entity_type) should be unique per tenant
ALTER TABLE entities DROP CONSTRAINT IF EXISTS entities_name_entity_type_key;
ALTER TABLE entities ADD CONSTRAINT entities_tenant_name_type_unique
    UNIQUE (tenant_id, name, entity_type);

-- Entity relationships: (source, target, type) should be unique per tenant
ALTER TABLE entity_relationships DROP CONSTRAINT IF EXISTS entity_relationships_source_entity_id_target_entity_id_relat_key;
ALTER TABLE entity_relationships ADD CONSTRAINT entity_relationships_tenant_unique
    UNIQUE (tenant_id, source_entity_id, target_entity_id, relationship_type);

-- User personas: (user_id, attribute_type, attribute_key) should be unique per tenant
ALTER TABLE user_personas DROP CONSTRAINT IF EXISTS user_personas_user_id_attribute_type_attribute_key_key;
ALTER TABLE user_personas ADD CONSTRAINT user_personas_tenant_unique
    UNIQUE (tenant_id, user_id, attribute_type, attribute_key);

-- =============================================================================
-- STEP 7: Add tenant_id to event sourcing tables if they exist
-- =============================================================================

DO $$
BEGIN
    -- Check if events table exists (from event sourcing module)
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'events') THEN
        ALTER TABLE events ADD COLUMN IF NOT EXISTS tenant_id UUID;
        UPDATE events SET tenant_id = '00000000-0000-0000-0000-000000000000' WHERE tenant_id IS NULL;
        ALTER TABLE events ALTER COLUMN tenant_id SET NOT NULL;
        CREATE INDEX IF NOT EXISTS idx_events_tenant ON events(tenant_id);
        RAISE NOTICE 'Added tenant_id to events table';
    END IF;

    -- Check if memory_events table exists
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'memory_events') THEN
        ALTER TABLE memory_events ADD COLUMN IF NOT EXISTS tenant_id UUID;
        UPDATE memory_events SET tenant_id = '00000000-0000-0000-0000-000000000000' WHERE tenant_id IS NULL;
        ALTER TABLE memory_events ALTER COLUMN tenant_id SET NOT NULL;
        CREATE INDEX IF NOT EXISTS idx_memory_events_tenant ON memory_events(tenant_id);
        RAISE NOTICE 'Added tenant_id to memory_events table';
    END IF;
END $$;

-- =============================================================================
-- STEP 8: Verify migration
-- =============================================================================

DO $$
DECLARE
    v_table TEXT;
    v_has_tenant BOOLEAN;
    v_tables TEXT[] := ARRAY['memories', 'entities', 'entity_relationships', 'memory_entities', 'user_personas', 'conversation_sessions'];
BEGIN
    FOREACH v_table IN ARRAY v_tables
    LOOP
        SELECT EXISTS (
            SELECT 1 FROM information_schema.columns
            WHERE table_name = v_table AND column_name = 'tenant_id' AND is_nullable = 'NO'
        ) INTO v_has_tenant;

        IF NOT v_has_tenant THEN
            RAISE EXCEPTION 'Migration verification failed: % does not have NOT NULL tenant_id column', v_table;
        END IF;

        RAISE NOTICE 'Verified tenant_id on table: %', v_table;
    END LOOP;

    RAISE NOTICE 'Migration completed successfully!';
END $$;

COMMIT;

-- =============================================================================
-- COMMENTS
-- =============================================================================

COMMENT ON COLUMN memories.tenant_id IS 'Tenant that owns this memory';
COMMENT ON COLUMN entities.tenant_id IS 'Tenant that owns this entity';
COMMENT ON COLUMN entity_relationships.tenant_id IS 'Tenant that owns this relationship';
COMMENT ON COLUMN memory_entities.tenant_id IS 'Tenant that owns this memory-entity link';
COMMENT ON COLUMN user_personas.tenant_id IS 'Tenant that owns this user persona attribute';
COMMENT ON COLUMN conversation_sessions.tenant_id IS 'Tenant that owns this session';
