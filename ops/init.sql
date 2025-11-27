-- Enable pgvector extension for semantic search
CREATE EXTENSION IF NOT EXISTS vector;

-- Core memories/episodes table
CREATE TABLE IF NOT EXISTS memories (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL, -- Multi-tenant isolation
    content TEXT NOT NULL,
    embedding vector(384), -- sentence-transformers all-MiniLM-L6-v2 produces 384-dim vectors
    created_at TIMESTAMP NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMP NOT NULL DEFAULT NOW(),
    source TEXT, -- Where this memory came from (e.g., 'claude-desktop', 'cursor', 'api')
    conversation_session_id UUID, -- Link to conversation session
    metadata JSONB, -- Flexible metadata (tags, importance, etc.)

    -- Full-text search
    content_tsvector tsvector GENERATED ALWAYS AS (to_tsvector('english', content)) STORED
);

-- Entities extracted from memories (people, places, things, concepts)
CREATE TABLE IF NOT EXISTS entities (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL, -- Multi-tenant isolation
    name TEXT NOT NULL,
    entity_type TEXT NOT NULL, -- 'PERSON', 'ORG', 'GPE', 'DATE', 'EVENT', 'PRODUCT', etc.
    canonical_name TEXT, -- Normalized/canonical form
    created_at TIMESTAMP NOT NULL DEFAULT NOW(),
    first_seen_memory_id UUID, -- First memory this entity appeared in
    metadata JSONB, -- Additional attributes

    UNIQUE(tenant_id, name, entity_type) -- Unique per tenant
);

-- Relationships between entities (knowledge graph edges)
CREATE TABLE IF NOT EXISTS entity_relationships (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL, -- Multi-tenant isolation
    source_entity_id UUID NOT NULL REFERENCES entities(id) ON DELETE CASCADE,
    target_entity_id UUID NOT NULL REFERENCES entities(id) ON DELETE CASCADE,
    relationship_type TEXT NOT NULL, -- 'WORKED_WITH', 'LIVES_IN', 'CREATED', 'HAPPENED_ON', etc.
    confidence REAL DEFAULT 1.0, -- Confidence score 0.0-1.0
    created_at TIMESTAMP NOT NULL DEFAULT NOW(),
    first_seen_memory_id UUID, -- Memory where this relationship was first observed
    metadata JSONB,

    UNIQUE(tenant_id, source_entity_id, target_entity_id, relationship_type) -- Unique per tenant
);

-- Many-to-many linking memories to entities
CREATE TABLE IF NOT EXISTS memory_entities (
    tenant_id UUID NOT NULL, -- Multi-tenant isolation
    memory_id UUID NOT NULL REFERENCES memories(id) ON DELETE CASCADE,
    entity_id UUID NOT NULL REFERENCES entities(id) ON DELETE CASCADE,
    relevance REAL DEFAULT 1.0, -- How relevant this entity is to the memory

    PRIMARY KEY (memory_id, entity_id)
);

-- User personas (information about the user)
CREATE TABLE IF NOT EXISTS user_personas (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL, -- Multi-tenant isolation
    user_id TEXT NOT NULL DEFAULT 'default_user', -- Support multiple users
    attribute_type TEXT NOT NULL, -- 'preference', 'skill', 'goal', 'background', etc.
    attribute_key TEXT NOT NULL, -- e.g., 'programming_language', 'favorite_color'
    attribute_value TEXT NOT NULL,
    confidence REAL DEFAULT 1.0,
    created_at TIMESTAMP NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMP NOT NULL DEFAULT NOW(),
    source_memory_id UUID, -- Memory this attribute was learned from

    UNIQUE(tenant_id, user_id, attribute_type, attribute_key) -- Unique per tenant
);

-- Conversation sessions for tracking context
CREATE TABLE IF NOT EXISTS conversation_sessions (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL, -- Multi-tenant isolation
    session_name TEXT,
    started_at TIMESTAMP NOT NULL DEFAULT NOW(),
    ended_at TIMESTAMP,
    client_type TEXT, -- 'claude-desktop', 'cursor', 'windsurf', etc.
    metadata JSONB
);

-- Integrations registry (external tools/APIs)
CREATE TABLE IF NOT EXISTS integrations (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    name TEXT NOT NULL UNIQUE,
    description TEXT,
    integration_type TEXT NOT NULL, -- 'api', 'cli', 'mcp_server', etc.
    enabled BOOLEAN DEFAULT true,
    config JSONB, -- Integration-specific configuration
    created_at TIMESTAMP NOT NULL DEFAULT NOW()
);

-- Integration actions (available operations per integration)
CREATE TABLE IF NOT EXISTS integration_actions (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    integration_id UUID NOT NULL REFERENCES integrations(id) ON DELETE CASCADE,
    action_name TEXT NOT NULL,
    description TEXT,
    parameters_schema JSONB, -- JSON schema for action parameters

    UNIQUE(integration_id, action_name)
);

-- Indexes for performance
-- Tenant isolation indexes (critical for RLS performance)
CREATE INDEX IF NOT EXISTS idx_memories_tenant ON memories(tenant_id);
CREATE INDEX IF NOT EXISTS idx_memories_tenant_created ON memories(tenant_id, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_memories_tenant_session ON memories(tenant_id, conversation_session_id);
CREATE INDEX IF NOT EXISTS idx_memories_created_at ON memories(created_at DESC);
CREATE INDEX IF NOT EXISTS idx_memories_session ON memories(conversation_session_id);
CREATE INDEX IF NOT EXISTS idx_memories_content_tsvector ON memories USING gin(content_tsvector);
CREATE INDEX IF NOT EXISTS idx_memories_embedding ON memories USING ivfflat (embedding vector_cosine_ops) WITH (lists = 100);

CREATE INDEX IF NOT EXISTS idx_entities_tenant ON entities(tenant_id);
CREATE INDEX IF NOT EXISTS idx_entities_tenant_type ON entities(tenant_id, entity_type);
CREATE INDEX IF NOT EXISTS idx_entities_tenant_name ON entities(tenant_id, name);
CREATE INDEX IF NOT EXISTS idx_entities_type ON entities(entity_type);
CREATE INDEX IF NOT EXISTS idx_entities_name ON entities(name);

CREATE INDEX IF NOT EXISTS idx_entity_relationships_tenant ON entity_relationships(tenant_id);
CREATE INDEX IF NOT EXISTS idx_entity_relationships_tenant_source ON entity_relationships(tenant_id, source_entity_id);
CREATE INDEX IF NOT EXISTS idx_entity_relationships_tenant_target ON entity_relationships(tenant_id, target_entity_id);
CREATE INDEX IF NOT EXISTS idx_entity_relationships_source ON entity_relationships(source_entity_id);
CREATE INDEX IF NOT EXISTS idx_entity_relationships_target ON entity_relationships(target_entity_id);
CREATE INDEX IF NOT EXISTS idx_entity_relationships_type ON entity_relationships(relationship_type);

CREATE INDEX IF NOT EXISTS idx_memory_entities_tenant ON memory_entities(tenant_id);
CREATE INDEX IF NOT EXISTS idx_memory_entities_tenant_memory ON memory_entities(tenant_id, memory_id);
CREATE INDEX IF NOT EXISTS idx_memory_entities_tenant_entity ON memory_entities(tenant_id, entity_id);
CREATE INDEX IF NOT EXISTS idx_memory_entities_memory ON memory_entities(memory_id);
CREATE INDEX IF NOT EXISTS idx_memory_entities_entity ON memory_entities(entity_id);

CREATE INDEX IF NOT EXISTS idx_user_personas_tenant ON user_personas(tenant_id);
CREATE INDEX IF NOT EXISTS idx_user_personas_tenant_user ON user_personas(tenant_id, user_id);
CREATE INDEX IF NOT EXISTS idx_user_personas_user ON user_personas(user_id);
CREATE INDEX IF NOT EXISTS idx_user_personas_type ON user_personas(attribute_type);

CREATE INDEX IF NOT EXISTS idx_conversation_sessions_tenant ON conversation_sessions(tenant_id);
CREATE INDEX IF NOT EXISTS idx_conversation_sessions_tenant_started ON conversation_sessions(tenant_id, started_at DESC);
CREATE INDEX IF NOT EXISTS idx_conversation_sessions_started ON conversation_sessions(started_at DESC);

-- Helper function to update updated_at timestamp
CREATE OR REPLACE FUNCTION update_updated_at_column()
RETURNS TRIGGER AS $$
BEGIN
    NEW.updated_at = NOW();
    RETURN NEW;
END;
$$ language 'plpgsql';

CREATE TRIGGER update_memories_updated_at BEFORE UPDATE ON memories
    FOR EACH ROW EXECUTE FUNCTION update_updated_at_column();

CREATE TRIGGER update_user_personas_updated_at BEFORE UPDATE ON user_personas
    FOR EACH ROW EXECUTE FUNCTION update_updated_at_column();

-- =============================================================================
-- ROW LEVEL SECURITY (RLS) FOR MULTI-TENANT ISOLATION
-- =============================================================================

-- Function to set tenant context (must be called per-connection)
CREATE OR REPLACE FUNCTION set_tenant_context(p_tenant_id UUID)
RETURNS VOID AS $$
BEGIN
    PERFORM set_config('app.tenant_id', p_tenant_id::text, false);
END;
$$ LANGUAGE plpgsql;

-- Function to get current tenant context
CREATE OR REPLACE FUNCTION get_tenant_context()
RETURNS UUID AS $$
BEGIN
    RETURN NULLIF(current_setting('app.tenant_id', true), '')::uuid;
EXCEPTION WHEN OTHERS THEN
    RETURN NULL;
END;
$$ LANGUAGE plpgsql STABLE;

-- GUARDRAIL: Function that FAILS if tenant context is not set
-- Used in RLS policies to enforce that queries without SET app.tenant_id fail hard
CREATE OR REPLACE FUNCTION require_tenant_context()
RETURNS BOOLEAN AS $$
DECLARE
    v_tenant_id UUID;
BEGIN
    v_tenant_id := NULLIF(current_setting('app.tenant_id', true), '')::uuid;

    IF v_tenant_id IS NULL THEN
        RAISE EXCEPTION 'SECURITY VIOLATION: tenant_id not set. Call set_tenant_context() before querying.';
    END IF;

    RETURN TRUE;
EXCEPTION
    WHEN invalid_text_representation THEN
        RAISE EXCEPTION 'SECURITY VIOLATION: Invalid tenant_id format. Call set_tenant_context() with valid UUID.';
END;
$$ LANGUAGE plpgsql STABLE;

-- Enable RLS on all tenant-scoped tables
ALTER TABLE memories ENABLE ROW LEVEL SECURITY;
ALTER TABLE entities ENABLE ROW LEVEL SECURITY;
ALTER TABLE entity_relationships ENABLE ROW LEVEL SECURITY;
ALTER TABLE memory_entities ENABLE ROW LEVEL SECURITY;
ALTER TABLE user_personas ENABLE ROW LEVEL SECURITY;
ALTER TABLE conversation_sessions ENABLE ROW LEVEL SECURITY;

-- RLS Policies: tenant_id must match app.tenant_id session variable
-- These policies enforce that ALL operations are scoped to the current tenant

-- RLS Policies with GUARDRAIL: require_tenant_context() forces failure if tenant not set
-- The guardrail runs FIRST, then the tenant_id check runs

CREATE POLICY tenant_isolation_memories ON memories
    FOR ALL
    USING (require_tenant_context() AND tenant_id = current_setting('app.tenant_id', true)::uuid)
    WITH CHECK (require_tenant_context() AND tenant_id = current_setting('app.tenant_id', true)::uuid);

CREATE POLICY tenant_isolation_entities ON entities
    FOR ALL
    USING (require_tenant_context() AND tenant_id = current_setting('app.tenant_id', true)::uuid)
    WITH CHECK (require_tenant_context() AND tenant_id = current_setting('app.tenant_id', true)::uuid);

CREATE POLICY tenant_isolation_entity_relationships ON entity_relationships
    FOR ALL
    USING (require_tenant_context() AND tenant_id = current_setting('app.tenant_id', true)::uuid)
    WITH CHECK (require_tenant_context() AND tenant_id = current_setting('app.tenant_id', true)::uuid);

CREATE POLICY tenant_isolation_memory_entities ON memory_entities
    FOR ALL
    USING (require_tenant_context() AND tenant_id = current_setting('app.tenant_id', true)::uuid)
    WITH CHECK (require_tenant_context() AND tenant_id = current_setting('app.tenant_id', true)::uuid);

CREATE POLICY tenant_isolation_user_personas ON user_personas
    FOR ALL
    USING (require_tenant_context() AND tenant_id = current_setting('app.tenant_id', true)::uuid)
    WITH CHECK (require_tenant_context() AND tenant_id = current_setting('app.tenant_id', true)::uuid);

CREATE POLICY tenant_isolation_conversation_sessions ON conversation_sessions
    FOR ALL
    USING (require_tenant_context() AND tenant_id = current_setting('app.tenant_id', true)::uuid)
    WITH CHECK (require_tenant_context() AND tenant_id = current_setting('app.tenant_id', true)::uuid);

-- =============================================================================
-- COMMENTS
-- =============================================================================

COMMENT ON COLUMN memories.tenant_id IS 'Tenant that owns this memory - enforced by RLS';
COMMENT ON COLUMN entities.tenant_id IS 'Tenant that owns this entity - enforced by RLS';
COMMENT ON COLUMN entity_relationships.tenant_id IS 'Tenant that owns this relationship - enforced by RLS';
COMMENT ON COLUMN memory_entities.tenant_id IS 'Tenant that owns this memory-entity link - enforced by RLS';
COMMENT ON COLUMN user_personas.tenant_id IS 'Tenant that owns this user persona - enforced by RLS';
COMMENT ON COLUMN conversation_sessions.tenant_id IS 'Tenant that owns this session - enforced by RLS';

COMMENT ON FUNCTION set_tenant_context(UUID) IS 'Sets app.tenant_id for RLS. Must be called immediately after opening connection.';
COMMENT ON FUNCTION get_tenant_context() IS 'Returns current tenant_id from session, or NULL if not set.';