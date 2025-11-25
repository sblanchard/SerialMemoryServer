-- Event-Sourced Memory Engine Schema
-- PostgreSQL 16 + pgvector
-- Append-only event store with CQRS projections

-- Enable required extensions
CREATE EXTENSION IF NOT EXISTS "uuid-ossp";
CREATE EXTENSION IF NOT EXISTS "pgcrypto";
CREATE EXTENSION IF NOT EXISTS "vector";

-- ============================================================================
-- ENUMS
-- ============================================================================

CREATE TYPE memory_event_type AS ENUM (
    'MemoryCreated',
    'MemoryUpdated',
    'MemoryMerged',
    'MemoryInvalidated',
    'MemoryDecayed',
    'MemoryReinforced',
    'MemoryLayerTransitioned'
);

CREATE TYPE memory_layer AS ENUM (
    'L0_RAW',
    'L1_CONTEXT',
    'L2_SUMMARY',
    'L3_KNOWLEDGE',
    'L4_HEURISTIC'
);

CREATE TYPE cognitive_stage AS ENUM (
    'Plan',
    'Retrieve',
    'Reason',
    'Reflect',
    'Summarise',
    'Consolidate'
);

CREATE TYPE maintenance_task_type AS ENUM (
    'MergeDuplicates',
    'DetectContradictions',
    'ApplyDecay',
    'ReinforceStable',
    'ArchiveCold'
);

-- ============================================================================
-- EVENT STORE (Append-Only)
-- ============================================================================

-- Core event store - NEVER modified after insert
CREATE TABLE memory_events (
    event_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    stream_id UUID NOT NULL,                          -- Memory aggregate ID
    event_type memory_event_type NOT NULL,
    event_version BIGINT NOT NULL,                    -- Per-stream version for optimistic concurrency
    global_sequence BIGSERIAL,                        -- Global ordering
    event_data JSONB NOT NULL,                        -- Event payload
    metadata JSONB DEFAULT '{}',                      -- Correlation IDs, causation, etc.
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    created_by VARCHAR(255),                          -- Actor/source
    content_hash CHAR(64) NOT NULL,                   -- SHA-256 of event_data

    CONSTRAINT unique_stream_version UNIQUE (stream_id, event_version)
);

-- Indexes for event store
CREATE INDEX idx_memory_events_stream ON memory_events (stream_id, event_version);
CREATE INDEX idx_memory_events_global ON memory_events (global_sequence);
CREATE INDEX idx_memory_events_type ON memory_events (event_type);
CREATE INDEX idx_memory_events_created ON memory_events (created_at);

-- ============================================================================
-- READ MODEL PROJECTIONS (Materialized Views)
-- ============================================================================

-- Current memory state projection
CREATE TABLE memory_projections (
    memory_id UUID PRIMARY KEY,
    content TEXT NOT NULL,
    content_hash CHAR(64) NOT NULL,                   -- SHA-256 for integrity verification
    embedding vector(384),                            -- Semantic embedding
    layer memory_layer NOT NULL DEFAULT 'L0_RAW',

    -- Confidence & Decay
    confidence_score DECIMAL(4,3) NOT NULL DEFAULT 1.000 CHECK (confidence_score >= 0 AND confidence_score <= 1),
    half_life_days INTEGER NOT NULL DEFAULT 30,
    last_reinforced_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    decay_rate DECIMAL(6,5) NOT NULL DEFAULT 0.02310, -- ln(2)/30 for 30-day half-life

    -- Integrity
    causal_parents UUID[] DEFAULT '{}',               -- Parent memory IDs
    validated_by UUID[] DEFAULT '{}',                 -- Validator memory IDs

    -- Metadata
    source VARCHAR(255),
    session_id UUID,
    user_id VARCHAR(255) DEFAULT 'default_user',
    tags TEXT[] DEFAULT '{}',

    -- Retrieval scoring factors
    access_count INTEGER DEFAULT 0,
    last_accessed_at TIMESTAMPTZ,
    user_affinity_score DECIMAL(4,3) DEFAULT 0.500,
    directive_relevance DECIMAL(4,3) DEFAULT 0.500,

    -- State tracking
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    is_archived BOOLEAN NOT NULL DEFAULT FALSE,
    merged_into_id UUID REFERENCES memory_projections(memory_id),
    contradiction_ids UUID[] DEFAULT '{}',

    -- Version tracking
    current_version BIGINT NOT NULL DEFAULT 1,
    last_event_id UUID REFERENCES memory_events(event_id),

    -- Timestamps
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    -- Full-text search
    content_tsvector TSVECTOR GENERATED ALWAYS AS (to_tsvector('english', content)) STORED
);

-- Indexes for projections
CREATE INDEX idx_memory_proj_embedding ON memory_projections USING ivfflat (embedding vector_cosine_ops) WITH (lists = 100);
CREATE INDEX idx_memory_proj_layer ON memory_projections (layer);
CREATE INDEX idx_memory_proj_confidence ON memory_projections (confidence_score DESC);
CREATE INDEX idx_memory_proj_active ON memory_projections (is_active) WHERE is_active = TRUE;
CREATE INDEX idx_memory_proj_user ON memory_projections (user_id);
CREATE INDEX idx_memory_proj_created ON memory_projections (created_at DESC);
CREATE INDEX idx_memory_proj_tsvector ON memory_projections USING GIN (content_tsvector);
CREATE INDEX idx_memory_proj_tags ON memory_projections USING GIN (tags);
CREATE INDEX idx_memory_proj_decay ON memory_projections (last_reinforced_at) WHERE is_active = TRUE;

-- ============================================================================
-- ENTITY PROJECTIONS
-- ============================================================================

CREATE TABLE entity_projections (
    entity_id UUID PRIMARY KEY,
    name VARCHAR(500) NOT NULL,
    entity_type VARCHAR(50) NOT NULL,
    canonical_name VARCHAR(500),
    confidence_score DECIMAL(4,3) DEFAULT 1.000,
    memory_count INTEGER DEFAULT 0,
    first_seen_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    last_seen_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    metadata JSONB DEFAULT '{}',
    is_active BOOLEAN DEFAULT TRUE,

    CONSTRAINT unique_entity_name_type UNIQUE (name, entity_type)
);

CREATE INDEX idx_entity_proj_type ON entity_projections (entity_type);
CREATE INDEX idx_entity_proj_name ON entity_projections (canonical_name);

-- Memory-Entity links
CREATE TABLE memory_entity_links (
    memory_id UUID NOT NULL REFERENCES memory_projections(memory_id) ON DELETE CASCADE,
    entity_id UUID NOT NULL REFERENCES entity_projections(entity_id) ON DELETE CASCADE,
    relevance DECIMAL(4,3) DEFAULT 1.000,
    created_at TIMESTAMPTZ DEFAULT NOW(),

    PRIMARY KEY (memory_id, entity_id)
);

-- Entity relationships
CREATE TABLE entity_relationship_projections (
    relationship_id UUID PRIMARY KEY,
    source_entity_id UUID NOT NULL REFERENCES entity_projections(entity_id),
    target_entity_id UUID NOT NULL REFERENCES entity_projections(entity_id),
    relationship_type VARCHAR(100) NOT NULL,
    confidence DECIMAL(4,3) DEFAULT 1.000,
    evidence_memory_ids UUID[] DEFAULT '{}',
    created_at TIMESTAMPTZ DEFAULT NOW(),

    CONSTRAINT unique_relationship UNIQUE (source_entity_id, target_entity_id, relationship_type)
);

CREATE INDEX idx_entity_rel_source ON entity_relationship_projections (source_entity_id);
CREATE INDEX idx_entity_rel_target ON entity_relationship_projections (target_entity_id);

-- ============================================================================
-- COGNITIVE STAGE INSTRUMENTATION
-- ============================================================================

CREATE TABLE cognitive_stage_logs (
    log_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    session_id UUID,
    stage cognitive_stage NOT NULL,
    started_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    completed_at TIMESTAMPTZ,
    duration_ms INTEGER,
    input_summary TEXT,
    output_summary TEXT,
    memory_ids_involved UUID[] DEFAULT '{}',
    metadata JSONB DEFAULT '{}',
    success BOOLEAN DEFAULT TRUE,
    error_message TEXT
);

CREATE INDEX idx_cognitive_logs_session ON cognitive_stage_logs (session_id);
CREATE INDEX idx_cognitive_logs_stage ON cognitive_stage_logs (stage);
CREATE INDEX idx_cognitive_logs_time ON cognitive_stage_logs (started_at DESC);

-- ============================================================================
-- MAINTENANCE TRACKING
-- ============================================================================

CREATE TABLE maintenance_tasks (
    task_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    task_type maintenance_task_type NOT NULL,
    status VARCHAR(20) NOT NULL DEFAULT 'pending',
    scheduled_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    started_at TIMESTAMPTZ,
    completed_at TIMESTAMPTZ,
    affected_memory_ids UUID[] DEFAULT '{}',
    result_summary JSONB,
    worker_id VARCHAR(255),
    idempotency_key VARCHAR(255) UNIQUE,
    retry_count INTEGER DEFAULT 0,
    last_error TEXT
);

CREATE INDEX idx_maintenance_status ON maintenance_tasks (status, scheduled_at);
CREATE INDEX idx_maintenance_type ON maintenance_tasks (task_type);

-- ============================================================================
-- CHECKPOINTS FOR PROJECTION REBUILDING
-- ============================================================================

CREATE TABLE projection_checkpoints (
    projection_name VARCHAR(100) PRIMARY KEY,
    last_processed_sequence BIGINT NOT NULL DEFAULT 0,
    last_processed_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    is_rebuilding BOOLEAN DEFAULT FALSE
);

-- Initialize checkpoints
INSERT INTO projection_checkpoints (projection_name, last_processed_sequence) VALUES
    ('memory_projections', 0),
    ('entity_projections', 0),
    ('entity_relationship_projections', 0);

-- ============================================================================
-- EXPORT TRACKING
-- ============================================================================

CREATE TABLE export_jobs (
    export_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    export_type VARCHAR(50) NOT NULL,               -- 'full', 'incremental', 'chunked'
    status VARCHAR(20) NOT NULL DEFAULT 'pending',
    started_at TIMESTAMPTZ,
    completed_at TIMESTAMPTZ,
    total_records INTEGER,
    exported_records INTEGER DEFAULT 0,
    chunk_size INTEGER DEFAULT 1000,
    current_chunk INTEGER DEFAULT 0,
    last_exported_sequence BIGINT,
    encryption_enabled BOOLEAN DEFAULT FALSE,
    encryption_key_id VARCHAR(255),
    output_path TEXT,
    error_message TEXT,
    created_by VARCHAR(255),
    created_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE INDEX idx_export_status ON export_jobs (status);

-- ============================================================================
-- USER AFFINITY TRACKING
-- ============================================================================

CREATE TABLE user_memory_interactions (
    interaction_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id VARCHAR(255) NOT NULL,
    memory_id UUID NOT NULL REFERENCES memory_projections(memory_id),
    interaction_type VARCHAR(50) NOT NULL,          -- 'view', 'reference', 'reinforce', 'dismiss'
    created_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE INDEX idx_user_interactions_user ON user_memory_interactions (user_id, created_at DESC);
CREATE INDEX idx_user_interactions_memory ON user_memory_interactions (memory_id);

-- ============================================================================
-- CONTRADICTION DETECTION
-- ============================================================================

CREATE TABLE contradiction_reports (
    report_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    memory_a_id UUID NOT NULL REFERENCES memory_projections(memory_id),
    memory_b_id UUID NOT NULL REFERENCES memory_projections(memory_id),
    contradiction_score DECIMAL(4,3) NOT NULL,
    detection_method VARCHAR(50),                   -- 'semantic', 'entity', 'temporal'
    resolution_status VARCHAR(20) DEFAULT 'detected',
    resolved_by VARCHAR(255),
    resolved_at TIMESTAMPTZ,
    notes TEXT,
    created_at TIMESTAMPTZ DEFAULT NOW(),

    CONSTRAINT unique_contradiction UNIQUE (memory_a_id, memory_b_id)
);

CREATE INDEX idx_contradictions_status ON contradiction_reports (resolution_status);

-- ============================================================================
-- FUNCTIONS
-- ============================================================================

-- Calculate current confidence with decay
CREATE OR REPLACE FUNCTION calculate_decayed_confidence(
    base_confidence DECIMAL,
    half_life_days INTEGER,
    last_reinforced TIMESTAMPTZ
) RETURNS DECIMAL AS $$
DECLARE
    days_elapsed DECIMAL;
    decay_factor DECIMAL;
BEGIN
    days_elapsed := EXTRACT(EPOCH FROM (NOW() - last_reinforced)) / 86400.0;
    decay_factor := POWER(0.5, days_elapsed / half_life_days);
    RETURN GREATEST(0.0, base_confidence * decay_factor);
END;
$$ LANGUAGE plpgsql IMMUTABLE;

-- Composite retrieval score
CREATE OR REPLACE FUNCTION calculate_retrieval_score(
    semantic_score DECIMAL,
    recency_days DECIMAL,
    confidence DECIMAL,
    user_affinity DECIMAL,
    directive_match DECIMAL,
    contradiction_count INTEGER,
    -- Weights
    w_semantic DECIMAL DEFAULT 0.35,
    w_recency DECIMAL DEFAULT 0.15,
    w_confidence DECIMAL DEFAULT 0.20,
    w_affinity DECIMAL DEFAULT 0.15,
    w_directive DECIMAL DEFAULT 0.15,
    contradiction_penalty DECIMAL DEFAULT 0.10
) RETURNS DECIMAL AS $$
DECLARE
    recency_score DECIMAL;
    base_score DECIMAL;
    penalty DECIMAL;
BEGIN
    -- Recency: exponential decay over 90 days
    recency_score := POWER(0.5, recency_days / 90.0);

    -- Weighted sum
    base_score := (semantic_score * w_semantic) +
                  (recency_score * w_recency) +
                  (confidence * w_confidence) +
                  (user_affinity * w_affinity) +
                  (directive_match * w_directive);

    -- Apply contradiction penalty
    penalty := contradiction_count * contradiction_penalty;

    RETURN GREATEST(0.0, base_score - penalty);
END;
$$ LANGUAGE plpgsql IMMUTABLE;

-- Verify content hash
CREATE OR REPLACE FUNCTION verify_content_hash(content TEXT, expected_hash CHAR(64))
RETURNS BOOLEAN AS $$
BEGIN
    RETURN encode(sha256(content::bytea), 'hex') = expected_hash;
END;
$$ LANGUAGE plpgsql IMMUTABLE;

-- Generate content hash
CREATE OR REPLACE FUNCTION generate_content_hash(content TEXT)
RETURNS CHAR(64) AS $$
BEGIN
    RETURN encode(sha256(content::bytea), 'hex');
END;
$$ LANGUAGE plpgsql IMMUTABLE;

-- ============================================================================
-- TRIGGERS
-- ============================================================================

-- Prevent event modification
CREATE OR REPLACE FUNCTION prevent_event_modification()
RETURNS TRIGGER AS $$
BEGIN
    RAISE EXCEPTION 'Events are immutable and cannot be modified or deleted';
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER tr_prevent_event_update
    BEFORE UPDATE ON memory_events
    FOR EACH ROW
    EXECUTE FUNCTION prevent_event_modification();

CREATE TRIGGER tr_prevent_event_delete
    BEFORE DELETE ON memory_events
    FOR EACH ROW
    EXECUTE FUNCTION prevent_event_modification();

-- Update projection timestamps
CREATE OR REPLACE FUNCTION update_projection_timestamp()
RETURNS TRIGGER AS $$
BEGIN
    NEW.updated_at := NOW();
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER tr_memory_proj_timestamp
    BEFORE UPDATE ON memory_projections
    FOR EACH ROW
    EXECUTE FUNCTION update_projection_timestamp();

-- ============================================================================
-- VIEWS
-- ============================================================================

-- Active memories with decayed confidence
CREATE VIEW v_active_memories AS
SELECT
    m.*,
    calculate_decayed_confidence(m.confidence_score, m.half_life_days, m.last_reinforced_at) AS current_confidence,
    EXTRACT(EPOCH FROM (NOW() - m.created_at)) / 86400.0 AS age_days,
    array_length(m.contradiction_ids, 1) AS contradiction_count
FROM memory_projections m
WHERE m.is_active = TRUE AND m.is_archived = FALSE;

-- Memory statistics by layer
CREATE VIEW v_layer_statistics AS
SELECT
    layer,
    COUNT(*) AS total_count,
    COUNT(*) FILTER (WHERE is_active) AS active_count,
    AVG(confidence_score) AS avg_confidence,
    AVG(access_count) AS avg_access_count,
    MIN(created_at) AS oldest_memory,
    MAX(created_at) AS newest_memory
FROM memory_projections
GROUP BY layer;

-- Event stream summary
CREATE VIEW v_event_stream_summary AS
SELECT
    stream_id,
    COUNT(*) AS event_count,
    MIN(created_at) AS first_event,
    MAX(created_at) AS last_event,
    MAX(event_version) AS current_version,
    array_agg(DISTINCT event_type) AS event_types
FROM memory_events
GROUP BY stream_id;

-- ============================================================================
-- INITIAL DATA
-- ============================================================================

-- Create default maintenance schedule
INSERT INTO maintenance_tasks (task_type, status, scheduled_at, idempotency_key) VALUES
    ('ApplyDecay', 'pending', NOW(), 'initial_decay_' || NOW()::DATE),
    ('DetectContradictions', 'pending', NOW(), 'initial_contradiction_' || NOW()::DATE),
    ('MergeDuplicates', 'pending', NOW(), 'initial_merge_' || NOW()::DATE);

COMMENT ON TABLE memory_events IS 'Append-only event store for memory mutations. NEVER modify or delete rows.';
COMMENT ON TABLE memory_projections IS 'Materialized read model rebuilt from events. Can be dropped and rebuilt.';
COMMENT ON FUNCTION calculate_decayed_confidence IS 'Computes current confidence using exponential decay based on half-life.';
COMMENT ON FUNCTION calculate_retrieval_score IS 'Multi-axis composite scoring for memory retrieval.';
