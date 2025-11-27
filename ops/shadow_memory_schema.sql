-- Shadow Memory Schema
-- Isolated memory branches for speculative reasoning, sandboxing, and branching paths
-- NEVER pollutes main memory until explicitly promoted

-- Branch type enum
CREATE TYPE shadow_branch_type AS ENUM (
    'speculative',  -- Speculative reasoning - may or may not be correct
    'sandbox',      -- Sandbox for testing without risk
    'branch',       -- Alternative reasoning path
    'draft'         -- Draft awaiting validation
);

-- Branch status enum
CREATE TYPE shadow_branch_status AS ENUM (
    'active',       -- Accepting new memories
    'frozen',       -- No new memories allowed
    'promoted',     -- Memories promoted to main
    'discarded',    -- Branch discarded
    'merged'        -- Merged with another branch
);

-- Shadow branches table
CREATE TABLE shadow_branches (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id VARCHAR(100) NOT NULL DEFAULT 'self',
    name VARCHAR(255) NOT NULL,
    description TEXT,
    branch_type shadow_branch_type NOT NULL,
    status shadow_branch_status NOT NULL DEFAULT 'active',

    -- Hierarchy
    parent_branch_id UUID REFERENCES shadow_branches(id) ON DELETE CASCADE,
    source_memory_id UUID,  -- Main memory that spawned this branch

    -- Confidence and reasoning
    confidence REAL NOT NULL DEFAULT 0.5 CHECK (confidence >= 0 AND confidence <= 1),
    hypothesis TEXT,
    reasoning_context TEXT,

    -- Lifecycle
    ttl_hours INTEGER,
    auto_discard_threshold REAL CHECK (auto_discard_threshold IS NULL OR (auto_discard_threshold >= 0 AND auto_discard_threshold <= 1)),
    expires_at TIMESTAMPTZ,

    -- Timestamps
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    frozen_at TIMESTAMPTZ,
    promoted_at TIMESTAMPTZ,
    discarded_at TIMESTAMPTZ,

    -- Metadata
    metadata JSONB
);

-- Shadow memories table (isolated from main memories)
CREATE TABLE shadow_memories (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    branch_id UUID NOT NULL REFERENCES shadow_branches(id) ON DELETE CASCADE,
    tenant_id VARCHAR(100) NOT NULL DEFAULT 'self',
    content TEXT NOT NULL,
    embedding vector(768),  -- Same dimension as main memories

    -- Confidence
    confidence REAL NOT NULL DEFAULT 0.5 CHECK (confidence >= 0 AND confidence <= 1),

    -- References
    shadows_memory_id UUID,  -- Main memory this shadows (if any)
    reasoning_step INTEGER,
    supporting_memory_ids UUID[],
    contradicting_memory_ids UUID[],

    -- Metadata
    source VARCHAR(100),
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    metadata JSONB
);

-- Shadow entities table
CREATE TABLE shadow_entities (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    branch_id UUID NOT NULL REFERENCES shadow_branches(id) ON DELETE CASCADE,
    tenant_id VARCHAR(100) NOT NULL DEFAULT 'self',
    name VARCHAR(500) NOT NULL,
    entity_type VARCHAR(100) NOT NULL,
    canonical_name VARCHAR(500),

    -- References
    shadows_entity_id UUID,  -- Main entity this shadows
    confidence REAL NOT NULL DEFAULT 0.5,

    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Shadow relationships table
CREATE TABLE shadow_relationships (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    branch_id UUID NOT NULL REFERENCES shadow_branches(id) ON DELETE CASCADE,
    tenant_id VARCHAR(100) NOT NULL DEFAULT 'self',
    source_entity_id UUID NOT NULL,
    target_entity_id UUID NOT NULL,
    relationship_type VARCHAR(100) NOT NULL,
    confidence REAL NOT NULL DEFAULT 0.5,

    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Promotion log - tracks what was promoted from shadow to main
CREATE TABLE shadow_promotion_log (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    branch_id UUID NOT NULL,
    tenant_id VARCHAR(100) NOT NULL DEFAULT 'self',

    -- What was promoted
    shadow_memory_id UUID,
    shadow_entity_id UUID,
    shadow_relationship_id UUID,

    -- Where it went in main memory
    main_memory_id UUID,
    main_entity_id UUID,
    main_relationship_id UUID,

    promoted_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    promoted_by VARCHAR(100)
);

-- Indexes for shadow_branches
CREATE INDEX idx_shadow_branches_tenant ON shadow_branches(tenant_id);
CREATE INDEX idx_shadow_branches_status ON shadow_branches(status) WHERE status = 'active';
CREATE INDEX idx_shadow_branches_parent ON shadow_branches(parent_branch_id);
CREATE INDEX idx_shadow_branches_expires ON shadow_branches(expires_at) WHERE expires_at IS NOT NULL AND status = 'active';
CREATE INDEX idx_shadow_branches_confidence ON shadow_branches(confidence) WHERE status = 'active';

-- Indexes for shadow_memories
CREATE INDEX idx_shadow_memories_branch ON shadow_memories(branch_id);
CREATE INDEX idx_shadow_memories_tenant ON shadow_memories(tenant_id);
CREATE INDEX idx_shadow_memories_embedding ON shadow_memories USING ivfflat (embedding vector_cosine_ops) WITH (lists = 100);
CREATE INDEX idx_shadow_memories_shadows ON shadow_memories(shadows_memory_id) WHERE shadows_memory_id IS NOT NULL;

-- Indexes for shadow_entities
CREATE INDEX idx_shadow_entities_branch ON shadow_entities(branch_id);
CREATE INDEX idx_shadow_entities_name ON shadow_entities(canonical_name);

-- Indexes for shadow_relationships
CREATE INDEX idx_shadow_relationships_branch ON shadow_relationships(branch_id);
CREATE INDEX idx_shadow_relationships_source ON shadow_relationships(source_entity_id);
CREATE INDEX idx_shadow_relationships_target ON shadow_relationships(target_entity_id);

-- Indexes for promotion log
CREATE INDEX idx_shadow_promotion_branch ON shadow_promotion_log(branch_id);
CREATE INDEX idx_shadow_promotion_main_memory ON shadow_promotion_log(main_memory_id);

-- Function to auto-expire branches
CREATE OR REPLACE FUNCTION expire_shadow_branches()
RETURNS INTEGER AS $$
DECLARE
    expired_count INTEGER;
BEGIN
    UPDATE shadow_branches
    SET status = 'discarded',
        discarded_at = NOW()
    WHERE status = 'active'
        AND expires_at IS NOT NULL
        AND expires_at < NOW();

    GET DIAGNOSTICS expired_count = ROW_COUNT;
    RETURN expired_count;
END;
$$ LANGUAGE plpgsql;

-- Function to auto-discard low confidence branches
CREATE OR REPLACE FUNCTION discard_low_confidence_branches()
RETURNS INTEGER AS $$
DECLARE
    discarded_count INTEGER;
BEGIN
    UPDATE shadow_branches
    SET status = 'discarded',
        discarded_at = NOW()
    WHERE status = 'active'
        AND auto_discard_threshold IS NOT NULL
        AND confidence < auto_discard_threshold;

    GET DIAGNOSTICS discarded_count = ROW_COUNT;
    RETURN discarded_count;
END;
$$ LANGUAGE plpgsql;

-- Function to get branch statistics
CREATE OR REPLACE FUNCTION get_shadow_branch_stats(p_branch_id UUID)
RETURNS TABLE (
    branch_id UUID,
    memory_count BIGINT,
    entity_count BIGINT,
    relationship_count BIGINT,
    avg_confidence REAL,
    child_branch_count BIGINT,
    oldest_memory TIMESTAMPTZ,
    newest_memory TIMESTAMPTZ
) AS $$
BEGIN
    RETURN QUERY
    SELECT
        p_branch_id,
        (SELECT COUNT(*) FROM shadow_memories WHERE shadow_memories.branch_id = p_branch_id),
        (SELECT COUNT(*) FROM shadow_entities WHERE shadow_entities.branch_id = p_branch_id),
        (SELECT COUNT(*) FROM shadow_relationships WHERE shadow_relationships.branch_id = p_branch_id),
        (SELECT AVG(shadow_memories.confidence)::REAL FROM shadow_memories WHERE shadow_memories.branch_id = p_branch_id),
        (SELECT COUNT(*) FROM shadow_branches WHERE parent_branch_id = p_branch_id),
        (SELECT MIN(created_at) FROM shadow_memories WHERE shadow_memories.branch_id = p_branch_id),
        (SELECT MAX(created_at) FROM shadow_memories WHERE shadow_memories.branch_id = p_branch_id);
END;
$$ LANGUAGE plpgsql;
