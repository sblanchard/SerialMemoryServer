-- =============================================================================
-- HOMESERVER CATCH-UP MIGRATION
-- Idempotent script that creates ALL missing tables needed by MCP tools.
-- Safe to run multiple times on an existing database.
--
-- Creates/ensures: memory_events, memory_projections, entity_projections,
-- workspaces, workspace_snapshots, event_log, memory_timeline, mind_health,
-- conflict_results, contradiction_reports, + workspace_id columns.
-- =============================================================================

BEGIN;

-- =============================================================================
-- 0. EXTENSIONS
-- =============================================================================

CREATE EXTENSION IF NOT EXISTS "uuid-ossp";
CREATE EXTENSION IF NOT EXISTS "pgcrypto";
CREATE EXTENSION IF NOT EXISTS "vector";

-- =============================================================================
-- 1. ENUMS (idempotent via DO blocks)
-- =============================================================================

DO $$ BEGIN CREATE TYPE memory_event_type AS ENUM (
    'MemoryCreated','MemoryUpdated','MemoryMerged','MemoryInvalidated',
    'MemoryDecayed','MemoryReinforced','MemoryLayerTransitioned'
); EXCEPTION WHEN duplicate_object THEN NULL; END $$;

DO $$ BEGIN CREATE TYPE memory_layer AS ENUM (
    'L0_RAW','L1_CONTEXT','L2_SUMMARY','L3_KNOWLEDGE','L4_HEURISTIC'
); EXCEPTION WHEN duplicate_object THEN NULL; END $$;

DO $$ BEGIN CREATE TYPE cognitive_stage AS ENUM (
    'Plan','Retrieve','Reason','Reflect','Summarise','Consolidate'
); EXCEPTION WHEN duplicate_object THEN NULL; END $$;

DO $$ BEGIN CREATE TYPE maintenance_task_type AS ENUM (
    'MergeDuplicates','DetectContradictions','ApplyDecay','ReinforceStable','ArchiveCold'
); EXCEPTION WHEN duplicate_object THEN NULL; END $$;

-- Add extended event types
DO $$ BEGIN ALTER TYPE memory_event_type ADD VALUE IF NOT EXISTS 'LayerGenerated'; EXCEPTION WHEN duplicate_object THEN NULL; END $$;
DO $$ BEGIN ALTER TYPE memory_event_type ADD VALUE IF NOT EXISTS 'KnowledgeNodeAdded'; EXCEPTION WHEN duplicate_object THEN NULL; END $$;
DO $$ BEGIN ALTER TYPE memory_event_type ADD VALUE IF NOT EXISTS 'ConflictDetected'; EXCEPTION WHEN duplicate_object THEN NULL; END $$;
DO $$ BEGIN ALTER TYPE memory_event_type ADD VALUE IF NOT EXISTS 'TimelineSnapshotCreated'; EXCEPTION WHEN duplicate_object THEN NULL; END $$;
DO $$ BEGIN ALTER TYPE memory_event_type ADD VALUE IF NOT EXISTS 'ReasoningExecuted'; EXCEPTION WHEN duplicate_object THEN NULL; END $$;
DO $$ BEGIN ALTER TYPE memory_event_type ADD VALUE IF NOT EXISTS 'MutationApplied'; EXCEPTION WHEN duplicate_object THEN NULL; END $$;
DO $$ BEGIN ALTER TYPE memory_event_type ADD VALUE IF NOT EXISTS 'BranchCreated'; EXCEPTION WHEN duplicate_object THEN NULL; END $$;
DO $$ BEGIN ALTER TYPE memory_event_type ADD VALUE IF NOT EXISTS 'BranchMerged'; EXCEPTION WHEN duplicate_object THEN NULL; END $$;
DO $$ BEGIN ALTER TYPE memory_event_type ADD VALUE IF NOT EXISTS 'MemorySplit'; EXCEPTION WHEN duplicate_object THEN NULL; END $$;
DO $$ BEGIN ALTER TYPE memory_event_type ADD VALUE IF NOT EXISTS 'MemoryExpired'; EXCEPTION WHEN duplicate_object THEN NULL; END $$;
DO $$ BEGIN ALTER TYPE memory_event_type ADD VALUE IF NOT EXISTS 'HallucinationDetected'; EXCEPTION WHEN duplicate_object THEN NULL; END $$;

-- =============================================================================
-- 2. EVENT STORE: memory_events
-- =============================================================================

CREATE TABLE IF NOT EXISTS memory_events (
    event_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    stream_id UUID NOT NULL,
    event_type memory_event_type NOT NULL,
    event_version BIGINT NOT NULL,
    global_sequence BIGSERIAL,
    event_data JSONB NOT NULL,
    metadata JSONB DEFAULT '{}',
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    created_by VARCHAR(255),
    content_hash CHAR(64) NOT NULL,
    CONSTRAINT unique_stream_version UNIQUE (stream_id, event_version)
);

ALTER TABLE memory_events ADD COLUMN IF NOT EXISTS tenant_id UUID;

CREATE INDEX IF NOT EXISTS idx_memory_events_stream ON memory_events (stream_id, event_version);
CREATE INDEX IF NOT EXISTS idx_memory_events_global ON memory_events (global_sequence);
CREATE INDEX IF NOT EXISTS idx_memory_events_type ON memory_events (event_type);
CREATE INDEX IF NOT EXISTS idx_memory_events_created ON memory_events (created_at);
CREATE INDEX IF NOT EXISTS idx_memory_events_tenant ON memory_events (tenant_id, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_memory_events_tenant_global ON memory_events (tenant_id, global_sequence DESC);

-- =============================================================================
-- 3. READ MODEL: memory_projections
-- =============================================================================

CREATE TABLE IF NOT EXISTS memory_projections (
    memory_id UUID PRIMARY KEY,
    content TEXT NOT NULL,
    content_hash CHAR(64) NOT NULL,
    embedding vector(384),
    layer memory_layer NOT NULL DEFAULT 'L0_RAW',
    confidence_score DECIMAL(4,3) NOT NULL DEFAULT 1.000 CHECK (confidence_score >= 0 AND confidence_score <= 1),
    half_life_days INTEGER NOT NULL DEFAULT 30,
    last_reinforced_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    decay_rate DECIMAL(6,5) NOT NULL DEFAULT 0.02310,
    causal_parents UUID[] DEFAULT '{}',
    validated_by UUID[] DEFAULT '{}',
    source VARCHAR(255),
    session_id UUID,
    user_id VARCHAR(255) DEFAULT 'default_user',
    tags TEXT[] DEFAULT '{}',
    access_count INTEGER DEFAULT 0,
    last_accessed_at TIMESTAMPTZ,
    user_affinity_score DECIMAL(4,3) DEFAULT 0.500,
    directive_relevance DECIMAL(4,3) DEFAULT 0.500,
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    is_archived BOOLEAN NOT NULL DEFAULT FALSE,
    merged_into_id UUID REFERENCES memory_projections(memory_id),
    contradiction_ids UUID[] DEFAULT '{}',
    current_version BIGINT NOT NULL DEFAULT 1,
    last_event_id UUID,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    content_tsvector TSVECTOR GENERATED ALWAYS AS (to_tsvector('english', content)) STORED
);

CREATE INDEX IF NOT EXISTS idx_memory_proj_layer ON memory_projections (layer);
CREATE INDEX IF NOT EXISTS idx_memory_proj_confidence ON memory_projections (confidence_score DESC);
CREATE INDEX IF NOT EXISTS idx_memory_proj_active ON memory_projections (is_active) WHERE is_active = TRUE;
CREATE INDEX IF NOT EXISTS idx_memory_proj_user ON memory_projections (user_id);
CREATE INDEX IF NOT EXISTS idx_memory_proj_created ON memory_projections (created_at DESC);
CREATE INDEX IF NOT EXISTS idx_memory_proj_tsvector ON memory_projections USING GIN (content_tsvector);
CREATE INDEX IF NOT EXISTS idx_memory_proj_tags ON memory_projections USING GIN (tags);
CREATE INDEX IF NOT EXISTS idx_memory_proj_decay ON memory_projections (last_reinforced_at) WHERE is_active = TRUE;

-- =============================================================================
-- 4. ENTITY PROJECTIONS
-- =============================================================================

CREATE TABLE IF NOT EXISTS entity_projections (
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

CREATE TABLE IF NOT EXISTS memory_entity_links (
    memory_id UUID NOT NULL REFERENCES memory_projections(memory_id) ON DELETE CASCADE,
    entity_id UUID NOT NULL REFERENCES entity_projections(entity_id) ON DELETE CASCADE,
    relevance DECIMAL(4,3) DEFAULT 1.000,
    created_at TIMESTAMPTZ DEFAULT NOW(),
    PRIMARY KEY (memory_id, entity_id)
);

CREATE TABLE IF NOT EXISTS entity_relationship_projections (
    relationship_id UUID PRIMARY KEY,
    source_entity_id UUID NOT NULL REFERENCES entity_projections(entity_id),
    target_entity_id UUID NOT NULL REFERENCES entity_projections(entity_id),
    relationship_type VARCHAR(100) NOT NULL,
    confidence DECIMAL(4,3) DEFAULT 1.000,
    evidence_memory_ids UUID[] DEFAULT '{}',
    created_at TIMESTAMPTZ DEFAULT NOW(),
    CONSTRAINT unique_relationship UNIQUE (source_entity_id, target_entity_id, relationship_type)
);

-- =============================================================================
-- 5. SUPPORTING EVENT-SOURCING TABLES
-- =============================================================================

CREATE TABLE IF NOT EXISTS cognitive_stage_logs (
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

CREATE TABLE IF NOT EXISTS maintenance_tasks (
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

CREATE TABLE IF NOT EXISTS projection_checkpoints (
    projection_name VARCHAR(100) PRIMARY KEY,
    last_processed_sequence BIGINT NOT NULL DEFAULT 0,
    last_processed_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    is_rebuilding BOOLEAN DEFAULT FALSE
);

INSERT INTO projection_checkpoints (projection_name, last_processed_sequence)
VALUES ('memory_projections', 0), ('entity_projections', 0), ('entity_relationship_projections', 0)
ON CONFLICT DO NOTHING;

CREATE TABLE IF NOT EXISTS export_jobs (
    export_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    export_type VARCHAR(50) NOT NULL,
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

CREATE TABLE IF NOT EXISTS user_memory_interactions (
    interaction_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id VARCHAR(255) NOT NULL,
    memory_id UUID NOT NULL REFERENCES memory_projections(memory_id),
    interaction_type VARCHAR(50) NOT NULL,
    created_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS contradiction_reports (
    report_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    memory_a_id UUID NOT NULL REFERENCES memory_projections(memory_id),
    memory_b_id UUID NOT NULL REFERENCES memory_projections(memory_id),
    contradiction_score DECIMAL(4,3) NOT NULL,
    detection_method VARCHAR(50),
    resolution_status VARCHAR(20) DEFAULT 'detected',
    resolved_by VARCHAR(255),
    resolved_at TIMESTAMPTZ,
    notes TEXT,
    created_at TIMESTAMPTZ DEFAULT NOW(),
    CONSTRAINT unique_contradiction UNIQUE (memory_a_id, memory_b_id)
);

-- =============================================================================
-- 6. EVENT LOG & TIMELINE (from fix_event_sourcing_complete)
-- =============================================================================

CREATE TABLE IF NOT EXISTS event_log (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL,
    event_type TEXT NOT NULL,
    memory_id UUID,
    actor TEXT NOT NULL DEFAULT 'system',
    payload JSONB NOT NULL DEFAULT '{}',
    seq BIGSERIAL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    severity TEXT DEFAULT 'info',
    category TEXT DEFAULT 'memory',
    correlation_id UUID
);

CREATE INDEX IF NOT EXISTS idx_event_log_tenant_seq ON event_log (tenant_id, seq DESC);
CREATE INDEX IF NOT EXISTS idx_event_log_tenant_created ON event_log (tenant_id, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_event_log_tenant_type ON event_log (tenant_id, event_type);

CREATE TABLE IF NOT EXISTS memory_timeline (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL,
    memory_id UUID NOT NULL,
    snapshot_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    event_id UUID,
    event_type TEXT NOT NULL,
    content TEXT NOT NULL,
    layer TEXT NOT NULL DEFAULT 'L0_RAW',
    confidence_score DECIMAL(4,3) NOT NULL DEFAULT 1.000,
    metadata JSONB DEFAULT '{}',
    delta_type TEXT DEFAULT 'full',
    delta_data JSONB,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS mind_health (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL,
    computed_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    total_memories INT NOT NULL DEFAULT 0,
    active_memories INT NOT NULL DEFAULT 0,
    decayed_memories INT NOT NULL DEFAULT 0,
    contradictions_detected INT NOT NULL DEFAULT 0,
    hallucinations_flagged INT NOT NULL DEFAULT 0,
    l0_count INT NOT NULL DEFAULT 0,
    l1_count INT NOT NULL DEFAULT 0,
    l2_count INT NOT NULL DEFAULT 0,
    l3_count INT NOT NULL DEFAULT 0,
    l4_count INT NOT NULL DEFAULT 0,
    avg_confidence DECIMAL(4,3) NOT NULL DEFAULT 1.000,
    min_confidence DECIMAL(4,3) NOT NULL DEFAULT 1.000,
    entity_count INT NOT NULL DEFAULT 0,
    relationship_count INT NOT NULL DEFAULT 0,
    orphan_entity_count INT NOT NULL DEFAULT 0,
    events_last_hour INT NOT NULL DEFAULT 0,
    events_last_day INT NOT NULL DEFAULT 0,
    health_score INT NOT NULL DEFAULT 100,
    health_status TEXT NOT NULL DEFAULT 'healthy',
    metadata JSONB DEFAULT '{}'
);

CREATE TABLE IF NOT EXISTS conflict_results (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL,
    scan_id UUID NOT NULL,
    memory_a_id UUID NOT NULL,
    memory_b_id UUID NOT NULL,
    similarity_score DECIMAL(4,3) NOT NULL,
    contradiction_type TEXT NOT NULL,
    explanation TEXT,
    resolved BOOLEAN NOT NULL DEFAULT FALSE,
    resolved_at TIMESTAMPTZ,
    resolved_by TEXT,
    resolution_action TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT unique_conflict_pair UNIQUE (tenant_id, memory_a_id, memory_b_id)
);

CREATE TABLE IF NOT EXISTS branch_metadata (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL,
    branch_name TEXT NOT NULL,
    description TEXT,
    source_branch TEXT DEFAULT 'main',
    created_by TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    merged_at TIMESTAMPTZ,
    merged_by TEXT,
    status TEXT NOT NULL DEFAULT 'active',
    memory_count INT NOT NULL DEFAULT 0,
    entity_count INT NOT NULL DEFAULT 0,
    CONSTRAINT unique_branch_name UNIQUE (tenant_id, branch_name)
);

-- =============================================================================
-- 7. WORKSPACE TABLES
-- =============================================================================

CREATE TABLE IF NOT EXISTS workspaces (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL,
    workspace_id TEXT NOT NULL,
    display_name TEXT,
    description TEXT,
    metadata JSONB,
    created_at TIMESTAMP NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMP NOT NULL DEFAULT NOW(),
    UNIQUE(tenant_id, workspace_id)
);

CREATE INDEX IF NOT EXISTS idx_workspaces_tenant ON workspaces(tenant_id);
CREATE INDEX IF NOT EXISTS idx_workspaces_tenant_workspace ON workspaces(tenant_id, workspace_id);

CREATE TABLE IF NOT EXISTS workspace_snapshots (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL,
    workspace_id TEXT NOT NULL,
    snapshot_name TEXT NOT NULL,
    state_data JSONB NOT NULL,
    created_at TIMESTAMP NOT NULL DEFAULT NOW(),
    UNIQUE(tenant_id, workspace_id, snapshot_name)
);

CREATE INDEX IF NOT EXISTS idx_workspace_snapshots_tenant ON workspace_snapshots(tenant_id);
CREATE INDEX IF NOT EXISTS idx_workspace_snapshots_tenant_workspace ON workspace_snapshots(tenant_id, workspace_id);

-- =============================================================================
-- 8. ADD workspace_id TO WORKSPACE-SCOPED TABLES
-- =============================================================================

ALTER TABLE memories ADD COLUMN IF NOT EXISTS workspace_id TEXT NOT NULL DEFAULT 'default';
CREATE INDEX IF NOT EXISTS idx_memories_tenant_workspace ON memories(tenant_id, workspace_id);
CREATE INDEX IF NOT EXISTS idx_memories_tenant_workspace_created ON memories(tenant_id, workspace_id, created_at DESC);

ALTER TABLE memory_entities ADD COLUMN IF NOT EXISTS workspace_id TEXT NOT NULL DEFAULT 'default';
CREATE INDEX IF NOT EXISTS idx_memory_entities_tenant_workspace ON memory_entities(tenant_id, workspace_id);

ALTER TABLE user_personas ADD COLUMN IF NOT EXISTS workspace_id TEXT NOT NULL DEFAULT 'default';
CREATE INDEX IF NOT EXISTS idx_user_personas_tenant_workspace ON user_personas(tenant_id, workspace_id);

ALTER TABLE conversation_sessions ADD COLUMN IF NOT EXISTS workspace_id TEXT NOT NULL DEFAULT 'default';
CREATE INDEX IF NOT EXISTS idx_conversation_sessions_tenant_workspace ON conversation_sessions(tenant_id, workspace_id);

-- Add missing columns to memories that other schemas expect
ALTER TABLE memories ADD COLUMN IF NOT EXISTS is_active BOOLEAN NOT NULL DEFAULT TRUE;
ALTER TABLE memories ADD COLUMN IF NOT EXISTS confidence_score DECIMAL(4,3) NOT NULL DEFAULT 1.000;
ALTER TABLE memories ADD COLUMN IF NOT EXISTS layer TEXT NOT NULL DEFAULT 'L1_CONTEXT';
ALTER TABLE memories ADD COLUMN IF NOT EXISTS merged_into UUID;

-- =============================================================================
-- 9. FUNCTIONS
-- =============================================================================

CREATE OR REPLACE FUNCTION calculate_decayed_confidence(
    base_confidence DECIMAL,
    half_life_days INTEGER,
    last_reinforced TIMESTAMPTZ
) RETURNS DECIMAL AS $$
DECLARE days_elapsed DECIMAL; decay_factor DECIMAL;
BEGIN
    days_elapsed := EXTRACT(EPOCH FROM (NOW() - last_reinforced)) / 86400.0;
    decay_factor := POWER(0.5, days_elapsed / half_life_days);
    RETURN GREATEST(0.0, base_confidence * decay_factor);
END;
$$ LANGUAGE plpgsql IMMUTABLE;

CREATE OR REPLACE FUNCTION calculate_retrieval_score(
    semantic_score DECIMAL, recency_days DECIMAL, confidence DECIMAL,
    user_affinity DECIMAL, directive_match DECIMAL, contradiction_count INTEGER,
    w_semantic DECIMAL DEFAULT 0.35, w_recency DECIMAL DEFAULT 0.15,
    w_confidence DECIMAL DEFAULT 0.20, w_affinity DECIMAL DEFAULT 0.15,
    w_directive DECIMAL DEFAULT 0.15, contradiction_penalty DECIMAL DEFAULT 0.10
) RETURNS DECIMAL AS $$
DECLARE recency_score DECIMAL; base_score DECIMAL; penalty DECIMAL;
BEGIN
    recency_score := POWER(0.5, recency_days / 90.0);
    base_score := (semantic_score * w_semantic) + (recency_score * w_recency) +
                  (confidence * w_confidence) + (user_affinity * w_affinity) +
                  (directive_match * w_directive);
    penalty := contradiction_count * contradiction_penalty;
    RETURN GREATEST(0.0, base_score - penalty);
END;
$$ LANGUAGE plpgsql IMMUTABLE;

CREATE OR REPLACE FUNCTION is_internal_admin()
RETURNS BOOLEAN AS $$
BEGIN
    RETURN COALESCE(current_setting('app.role', true), '') = 'internal_admin';
END;
$$ LANGUAGE plpgsql STABLE;

CREATE OR REPLACE FUNCTION get_safe_tenant_uuid()
RETURNS UUID AS $$
BEGIN
    RETURN NULLIF(current_setting('app.tenant_id', true), '')::uuid;
EXCEPTION WHEN OTHERS THEN
    RETURN NULL;
END;
$$ LANGUAGE plpgsql STABLE;

CREATE OR REPLACE FUNCTION set_workspace_context(p_workspace_id TEXT)
RETURNS VOID AS $$
BEGIN
    PERFORM set_config('app.workspace_id', p_workspace_id, false);
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION get_workspace_context()
RETURNS TEXT AS $$
BEGIN
    RETURN COALESCE(NULLIF(current_setting('app.workspace_id', true), ''), 'default');
END;
$$ LANGUAGE plpgsql STABLE;

-- update_updated_at_column (needed by workspaces trigger)
CREATE OR REPLACE FUNCTION update_updated_at_column()
RETURNS TRIGGER AS $$
BEGIN NEW.updated_at := NOW(); RETURN NEW; END;
$$ LANGUAGE plpgsql;

-- emit_memory_event
CREATE OR REPLACE FUNCTION emit_memory_event(
    p_tenant_id UUID, p_memory_id UUID, p_event_type TEXT,
    p_event_data JSONB, p_actor TEXT DEFAULT 'system', p_metadata JSONB DEFAULT '{}'
) RETURNS UUID AS $$
DECLARE v_event_id UUID; v_content_hash TEXT; v_event_version BIGINT;
BEGIN
    v_content_hash := encode(digest(p_event_data::text, 'sha256'), 'hex');
    SELECT COALESCE(MAX(event_version), 0) + 1 INTO v_event_version
    FROM memory_events WHERE stream_id = p_memory_id;
    INSERT INTO memory_events (event_id, stream_id, tenant_id, event_type, event_version, event_data, metadata, created_by, content_hash)
    VALUES (gen_random_uuid(), p_memory_id, p_tenant_id, p_event_type::memory_event_type, v_event_version, p_event_data, p_metadata, p_actor, v_content_hash)
    RETURNING event_id INTO v_event_id;
    INSERT INTO event_log (tenant_id, event_type, memory_id, actor, payload, category)
    VALUES (p_tenant_id, p_event_type, p_memory_id, p_actor, p_event_data, 'memory');
    RETURN v_event_id;
END;
$$ LANGUAGE plpgsql SECURITY DEFINER;

-- emit_system_event
CREATE OR REPLACE FUNCTION emit_system_event(
    p_tenant_id UUID, p_event_type TEXT, p_payload JSONB,
    p_actor TEXT DEFAULT 'system', p_category TEXT DEFAULT 'system',
    p_severity TEXT DEFAULT 'info', p_memory_id UUID DEFAULT NULL,
    p_correlation_id UUID DEFAULT NULL
) RETURNS UUID AS $$
DECLARE v_event_id UUID;
BEGIN
    INSERT INTO event_log (tenant_id, event_type, memory_id, actor, payload, category, severity, correlation_id)
    VALUES (p_tenant_id, p_event_type, p_memory_id, p_actor, p_payload, p_category, p_severity, p_correlation_id)
    RETURNING id INTO v_event_id;
    RETURN v_event_id;
END;
$$ LANGUAGE plpgsql SECURITY DEFINER;

-- =============================================================================
-- 10. RLS ON NEW TABLES
-- =============================================================================

ALTER TABLE memory_events ENABLE ROW LEVEL SECURITY;
ALTER TABLE event_log ENABLE ROW LEVEL SECURITY;
ALTER TABLE memory_timeline ENABLE ROW LEVEL SECURITY;
ALTER TABLE mind_health ENABLE ROW LEVEL SECURITY;
ALTER TABLE conflict_results ENABLE ROW LEVEL SECURITY;
ALTER TABLE branch_metadata ENABLE ROW LEVEL SECURITY;
ALTER TABLE workspaces ENABLE ROW LEVEL SECURITY;
ALTER TABLE workspace_snapshots ENABLE ROW LEVEL SECURITY;

-- =============================================================================
-- 11. RLS POLICIES (drop + recreate for idempotency)
-- =============================================================================

-- memory_events
DROP POLICY IF EXISTS tenant_read_memory_events ON memory_events;
DROP POLICY IF EXISTS internal_admin_memory_events ON memory_events;
CREATE POLICY tenant_read_memory_events ON memory_events FOR SELECT
    USING (tenant_id = current_setting('app.tenant_id', true)::uuid OR tenant_id IS NULL);
CREATE POLICY internal_admin_memory_events ON memory_events FOR ALL
    USING (is_internal_admin()) WITH CHECK (is_internal_admin());

-- event_log
DROP POLICY IF EXISTS tenant_read_event_log ON event_log;
DROP POLICY IF EXISTS internal_admin_event_log ON event_log;
CREATE POLICY tenant_read_event_log ON event_log FOR SELECT
    USING (tenant_id = current_setting('app.tenant_id', true)::uuid);
CREATE POLICY internal_admin_event_log ON event_log FOR ALL
    USING (is_internal_admin()) WITH CHECK (is_internal_admin());

-- memory_timeline
DROP POLICY IF EXISTS tenant_read_memory_timeline ON memory_timeline;
DROP POLICY IF EXISTS internal_admin_memory_timeline ON memory_timeline;
CREATE POLICY tenant_read_memory_timeline ON memory_timeline FOR SELECT
    USING (tenant_id = current_setting('app.tenant_id', true)::uuid);
CREATE POLICY internal_admin_memory_timeline ON memory_timeline FOR ALL
    USING (is_internal_admin()) WITH CHECK (is_internal_admin());

-- mind_health
DROP POLICY IF EXISTS tenant_read_mind_health ON mind_health;
DROP POLICY IF EXISTS internal_admin_mind_health ON mind_health;
CREATE POLICY tenant_read_mind_health ON mind_health FOR SELECT
    USING (tenant_id = current_setting('app.tenant_id', true)::uuid);
CREATE POLICY internal_admin_mind_health ON mind_health FOR ALL
    USING (is_internal_admin()) WITH CHECK (is_internal_admin());

-- conflict_results
DROP POLICY IF EXISTS tenant_read_conflict_results ON conflict_results;
DROP POLICY IF EXISTS internal_admin_conflict_results ON conflict_results;
CREATE POLICY tenant_read_conflict_results ON conflict_results FOR SELECT
    USING (tenant_id = current_setting('app.tenant_id', true)::uuid);
CREATE POLICY internal_admin_conflict_results ON conflict_results FOR ALL
    USING (is_internal_admin()) WITH CHECK (is_internal_admin());

-- branch_metadata
DROP POLICY IF EXISTS tenant_isolation_branch_metadata ON branch_metadata;
DROP POLICY IF EXISTS internal_admin_branch_metadata ON branch_metadata;
CREATE POLICY tenant_isolation_branch_metadata ON branch_metadata FOR ALL
    USING (tenant_id = current_setting('app.tenant_id', true)::uuid)
    WITH CHECK (tenant_id = current_setting('app.tenant_id', true)::uuid);
CREATE POLICY internal_admin_branch_metadata ON branch_metadata FOR ALL
    USING (is_internal_admin()) WITH CHECK (is_internal_admin());

-- workspaces (tenant-scoped with internal_admin bypass)
DROP POLICY IF EXISTS tenant_isolation_workspaces ON workspaces;
CREATE POLICY tenant_isolation_workspaces ON workspaces FOR ALL
    USING (
        current_setting('app.role', true) = 'internal_admin'
        OR (get_safe_tenant_uuid() IS NOT NULL AND tenant_id = get_safe_tenant_uuid())
    )
    WITH CHECK (
        current_setting('app.role', true) = 'internal_admin'
        OR (get_safe_tenant_uuid() IS NOT NULL AND tenant_id = get_safe_tenant_uuid())
    );

-- workspace_snapshots
DROP POLICY IF EXISTS tenant_isolation_snapshots ON workspace_snapshots;
CREATE POLICY tenant_isolation_snapshots ON workspace_snapshots FOR ALL
    USING (
        current_setting('app.role', true) = 'internal_admin'
        OR (get_safe_tenant_uuid() IS NOT NULL AND tenant_id = get_safe_tenant_uuid())
    )
    WITH CHECK (
        current_setting('app.role', true) = 'internal_admin'
        OR (get_safe_tenant_uuid() IS NOT NULL AND tenant_id = get_safe_tenant_uuid())
    );

-- workspace-scoped tables (memories, memory_entities, user_personas, conversation_sessions)
DROP POLICY IF EXISTS workspace_isolation_memories ON memories;
DROP POLICY IF EXISTS tenant_isolation_memories ON memories;
CREATE POLICY workspace_isolation_memories ON memories FOR ALL
    USING (
        current_setting('app.role', true) = 'internal_admin'
        OR (
            get_safe_tenant_uuid() IS NOT NULL
            AND tenant_id = get_safe_tenant_uuid()
            AND workspace_id = COALESCE(NULLIF(current_setting('app.workspace_id', true), ''), 'default')
        )
    )
    WITH CHECK (
        current_setting('app.role', true) = 'internal_admin'
        OR (
            get_safe_tenant_uuid() IS NOT NULL
            AND tenant_id = get_safe_tenant_uuid()
            AND workspace_id = COALESCE(NULLIF(current_setting('app.workspace_id', true), ''), 'default')
        )
    );

DROP POLICY IF EXISTS workspace_isolation_memory_entities ON memory_entities;
DROP POLICY IF EXISTS tenant_isolation_memory_entities ON memory_entities;
CREATE POLICY workspace_isolation_memory_entities ON memory_entities FOR ALL
    USING (
        current_setting('app.role', true) = 'internal_admin'
        OR (
            get_safe_tenant_uuid() IS NOT NULL
            AND tenant_id = get_safe_tenant_uuid()
            AND workspace_id = COALESCE(NULLIF(current_setting('app.workspace_id', true), ''), 'default')
        )
    )
    WITH CHECK (
        current_setting('app.role', true) = 'internal_admin'
        OR (
            get_safe_tenant_uuid() IS NOT NULL
            AND tenant_id = get_safe_tenant_uuid()
            AND workspace_id = COALESCE(NULLIF(current_setting('app.workspace_id', true), ''), 'default')
        )
    );

DROP POLICY IF EXISTS workspace_isolation_user_personas ON user_personas;
DROP POLICY IF EXISTS tenant_isolation_user_personas ON user_personas;
CREATE POLICY workspace_isolation_user_personas ON user_personas FOR ALL
    USING (
        current_setting('app.role', true) = 'internal_admin'
        OR (
            get_safe_tenant_uuid() IS NOT NULL
            AND tenant_id = get_safe_tenant_uuid()
            AND workspace_id = COALESCE(NULLIF(current_setting('app.workspace_id', true), ''), 'default')
        )
    )
    WITH CHECK (
        current_setting('app.role', true) = 'internal_admin'
        OR (
            get_safe_tenant_uuid() IS NOT NULL
            AND tenant_id = get_safe_tenant_uuid()
            AND workspace_id = COALESCE(NULLIF(current_setting('app.workspace_id', true), ''), 'default')
        )
    );

DROP POLICY IF EXISTS workspace_isolation_conversation_sessions ON conversation_sessions;
DROP POLICY IF EXISTS tenant_isolation_conversation_sessions ON conversation_sessions;
CREATE POLICY workspace_isolation_conversation_sessions ON conversation_sessions FOR ALL
    USING (
        current_setting('app.role', true) = 'internal_admin'
        OR (
            get_safe_tenant_uuid() IS NOT NULL
            AND tenant_id = get_safe_tenant_uuid()
            AND workspace_id = COALESCE(NULLIF(current_setting('app.workspace_id', true), ''), 'default')
        )
    )
    WITH CHECK (
        current_setting('app.role', true) = 'internal_admin'
        OR (
            get_safe_tenant_uuid() IS NOT NULL
            AND tenant_id = get_safe_tenant_uuid()
            AND workspace_id = COALESCE(NULLIF(current_setting('app.workspace_id', true), ''), 'default')
        )
    );

-- entities + entity_relationships (tenant-scoped with safe UUID)
DROP POLICY IF EXISTS tenant_isolation_entities ON entities;
CREATE POLICY tenant_isolation_entities ON entities FOR ALL
    USING (
        current_setting('app.role', true) = 'internal_admin'
        OR (get_safe_tenant_uuid() IS NOT NULL AND tenant_id = get_safe_tenant_uuid())
    )
    WITH CHECK (
        current_setting('app.role', true) = 'internal_admin'
        OR (get_safe_tenant_uuid() IS NOT NULL AND tenant_id = get_safe_tenant_uuid())
    );

DROP POLICY IF EXISTS tenant_isolation_entity_relationships ON entity_relationships;
CREATE POLICY tenant_isolation_entity_relationships ON entity_relationships FOR ALL
    USING (
        current_setting('app.role', true) = 'internal_admin'
        OR (get_safe_tenant_uuid() IS NOT NULL AND tenant_id = get_safe_tenant_uuid())
    )
    WITH CHECK (
        current_setting('app.role', true) = 'internal_admin'
        OR (get_safe_tenant_uuid() IS NOT NULL AND tenant_id = get_safe_tenant_uuid())
    );

-- =============================================================================
-- 12. TRIGGERS
-- =============================================================================

DROP TRIGGER IF EXISTS update_workspaces_updated_at ON workspaces;
CREATE TRIGGER update_workspaces_updated_at BEFORE UPDATE ON workspaces
    FOR EACH ROW EXECUTE FUNCTION update_updated_at_column();

-- Prevent event modification
CREATE OR REPLACE FUNCTION prevent_event_modification()
RETURNS TRIGGER AS $$
BEGIN RAISE EXCEPTION 'Events are immutable and cannot be modified or deleted'; END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS tr_prevent_event_update ON memory_events;
CREATE TRIGGER tr_prevent_event_update BEFORE UPDATE ON memory_events
    FOR EACH ROW EXECUTE FUNCTION prevent_event_modification();

DROP TRIGGER IF EXISTS tr_prevent_event_delete ON memory_events;
CREATE TRIGGER tr_prevent_event_delete BEFORE DELETE ON memory_events
    FOR EACH ROW EXECUTE FUNCTION prevent_event_modification();

-- =============================================================================
-- 13. SEED DEFAULT WORKSPACE
-- =============================================================================

INSERT INTO workspaces (id, tenant_id, workspace_id, display_name, description)
SELECT gen_random_uuid(), dt.tenant_id, 'default', 'Default Workspace', 'Auto-created default workspace'
FROM (
    SELECT DISTINCT tenant_id FROM memories
    UNION
    SELECT DISTINCT tenant_id FROM conversation_sessions
) AS dt
ON CONFLICT (tenant_id, workspace_id) DO NOTHING;

-- =============================================================================
-- 14. GRANTS (safe - skips if roles don't exist)
-- =============================================================================

DO $$ BEGIN
    GRANT EXECUTE ON FUNCTION emit_memory_event TO contextdb_app;
    GRANT EXECUTE ON FUNCTION emit_system_event TO contextdb_app;
    GRANT EXECUTE ON FUNCTION get_safe_tenant_uuid() TO contextdb_app;
    GRANT EXECUTE ON FUNCTION is_internal_admin() TO contextdb_app;
    GRANT EXECUTE ON FUNCTION set_workspace_context(TEXT) TO contextdb_app;
    GRANT EXECUTE ON FUNCTION get_workspace_context() TO contextdb_app;
EXCEPTION WHEN undefined_object THEN NULL;
END $$;

DO $$ BEGIN
    -- Grant table access to contextdb_app
    GRANT ALL ON memory_events TO contextdb_app;
    GRANT ALL ON memory_projections TO contextdb_app;
    GRANT ALL ON entity_projections TO contextdb_app;
    GRANT ALL ON memory_entity_links TO contextdb_app;
    GRANT ALL ON entity_relationship_projections TO contextdb_app;
    GRANT ALL ON cognitive_stage_logs TO contextdb_app;
    GRANT ALL ON maintenance_tasks TO contextdb_app;
    GRANT ALL ON projection_checkpoints TO contextdb_app;
    GRANT ALL ON export_jobs TO contextdb_app;
    GRANT ALL ON user_memory_interactions TO contextdb_app;
    GRANT ALL ON contradiction_reports TO contextdb_app;
    GRANT ALL ON event_log TO contextdb_app;
    GRANT ALL ON memory_timeline TO contextdb_app;
    GRANT ALL ON mind_health TO contextdb_app;
    GRANT ALL ON conflict_results TO contextdb_app;
    GRANT ALL ON branch_metadata TO contextdb_app;
    GRANT ALL ON workspaces TO contextdb_app;
    GRANT ALL ON workspace_snapshots TO contextdb_app;
    -- Grant sequence access
    GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO contextdb_app;
EXCEPTION WHEN undefined_object THEN NULL;
END $$;

COMMIT;

-- =============================================================================
-- VERIFICATION (run after migration to confirm)
-- =============================================================================
-- SELECT tablename FROM pg_tables WHERE schemaname='public' AND tablename IN (
--   'memory_events','memory_projections','workspaces','workspace_snapshots',
--   'event_log','memory_timeline','mind_health','conflict_results'
-- );
