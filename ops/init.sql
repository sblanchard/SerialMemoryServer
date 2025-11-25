-- Enable pgvector extension for semantic search
CREATE EXTENSION IF NOT EXISTS vector;

-- Core memories/episodes table
CREATE TABLE IF NOT EXISTS memories (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
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
    name TEXT NOT NULL,
    entity_type TEXT NOT NULL, -- 'PERSON', 'ORG', 'GPE', 'DATE', 'EVENT', 'PRODUCT', etc.
    canonical_name TEXT, -- Normalized/canonical form
    created_at TIMESTAMP NOT NULL DEFAULT NOW(),
    first_seen_memory_id UUID, -- First memory this entity appeared in
    metadata JSONB, -- Additional attributes

    UNIQUE(name, entity_type)
);

-- Relationships between entities (knowledge graph edges)
CREATE TABLE IF NOT EXISTS entity_relationships (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    source_entity_id UUID NOT NULL REFERENCES entities(id) ON DELETE CASCADE,
    target_entity_id UUID NOT NULL REFERENCES entities(id) ON DELETE CASCADE,
    relationship_type TEXT NOT NULL, -- 'WORKED_WITH', 'LIVES_IN', 'CREATED', 'HAPPENED_ON', etc.
    confidence REAL DEFAULT 1.0, -- Confidence score 0.0-1.0
    created_at TIMESTAMP NOT NULL DEFAULT NOW(),
    first_seen_memory_id UUID, -- Memory where this relationship was first observed
    metadata JSONB,

    UNIQUE(source_entity_id, target_entity_id, relationship_type)
);

-- Many-to-many linking memories to entities
CREATE TABLE IF NOT EXISTS memory_entities (
    memory_id UUID NOT NULL REFERENCES memories(id) ON DELETE CASCADE,
    entity_id UUID NOT NULL REFERENCES entities(id) ON DELETE CASCADE,
    relevance REAL DEFAULT 1.0, -- How relevant this entity is to the memory

    PRIMARY KEY (memory_id, entity_id)
);

-- User personas (information about the user)
CREATE TABLE IF NOT EXISTS user_personas (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id TEXT NOT NULL DEFAULT 'default_user', -- Support multiple users
    attribute_type TEXT NOT NULL, -- 'preference', 'skill', 'goal', 'background', etc.
    attribute_key TEXT NOT NULL, -- e.g., 'programming_language', 'favorite_color'
    attribute_value TEXT NOT NULL,
    confidence REAL DEFAULT 1.0,
    created_at TIMESTAMP NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMP NOT NULL DEFAULT NOW(),
    source_memory_id UUID, -- Memory this attribute was learned from

    UNIQUE(user_id, attribute_type, attribute_key)
);

-- Conversation sessions for tracking context
CREATE TABLE IF NOT EXISTS conversation_sessions (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
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
CREATE INDEX IF NOT EXISTS idx_memories_created_at ON memories(created_at DESC);
CREATE INDEX IF NOT EXISTS idx_memories_session ON memories(conversation_session_id);
CREATE INDEX IF NOT EXISTS idx_memories_content_tsvector ON memories USING gin(content_tsvector);
CREATE INDEX IF NOT EXISTS idx_memories_embedding ON memories USING ivfflat (embedding vector_cosine_ops) WITH (lists = 100);

CREATE INDEX IF NOT EXISTS idx_entities_type ON entities(entity_type);
CREATE INDEX IF NOT EXISTS idx_entities_name ON entities(name);

CREATE INDEX IF NOT EXISTS idx_entity_relationships_source ON entity_relationships(source_entity_id);
CREATE INDEX IF NOT EXISTS idx_entity_relationships_target ON entity_relationships(target_entity_id);
CREATE INDEX IF NOT EXISTS idx_entity_relationships_type ON entity_relationships(relationship_type);

CREATE INDEX IF NOT EXISTS idx_memory_entities_memory ON memory_entities(memory_id);
CREATE INDEX IF NOT EXISTS idx_memory_entities_entity ON memory_entities(entity_id);

CREATE INDEX IF NOT EXISTS idx_user_personas_user ON user_personas(user_id);
CREATE INDEX IF NOT EXISTS idx_user_personas_type ON user_personas(attribute_type);

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