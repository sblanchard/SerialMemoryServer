-- Graph Versioning Migration
-- Enables versioned entity/relationship extraction for safe re-crawling
-- BACKWARD COMPATIBLE: existing data preserved at version 1

-- =============================================================================
-- CONSTANTS
-- =============================================================================
-- Current graph version for new extractions
-- Increment this when changing extraction logic/prompts
DO $$ BEGIN
    PERFORM set_config('app.current_graph_version', '2', false);
END $$;

-- =============================================================================
-- ADD GRAPH VERSION COLUMNS
-- =============================================================================

-- Add version to memories (tracks which extraction version was used)
ALTER TABLE memories
ADD COLUMN IF NOT EXISTS graph_version INTEGER DEFAULT 1,
ADD COLUMN IF NOT EXISTS graph_extracted_at TIMESTAMPTZ,
ADD COLUMN IF NOT EXISTS graph_extraction_error TEXT;

-- Add version to entities
ALTER TABLE entities
ADD COLUMN IF NOT EXISTS graph_version INTEGER DEFAULT 1;

-- Add version to entity_relationships
ALTER TABLE entity_relationships
ADD COLUMN IF NOT EXISTS graph_version INTEGER DEFAULT 1;

-- Add version to memory_entities junction
ALTER TABLE memory_entities
ADD COLUMN IF NOT EXISTS graph_version INTEGER DEFAULT 1;

-- Set existing data to version 1
UPDATE memories SET graph_version = 1 WHERE graph_version IS NULL;
UPDATE entities SET graph_version = 1 WHERE graph_version IS NULL;
UPDATE entity_relationships SET graph_version = 1 WHERE graph_version IS NULL;
UPDATE memory_entities SET graph_version = 1 WHERE graph_version IS NULL;

-- =============================================================================
-- INDEXES FOR VERSIONED QUERIES
-- =============================================================================

CREATE INDEX IF NOT EXISTS idx_memories_graph_version
    ON memories(graph_version);

CREATE INDEX IF NOT EXISTS idx_memories_needs_recrawl
    ON memories(tenant_id, graph_version)
    WHERE graph_version IS NULL OR graph_version < 2;

CREATE INDEX IF NOT EXISTS idx_entities_graph_version
    ON entities(graph_version);

CREATE INDEX IF NOT EXISTS idx_entity_relationships_graph_version
    ON entity_relationships(graph_version);

CREATE INDEX IF NOT EXISTS idx_memory_entities_graph_version
    ON memory_entities(graph_version);

-- =============================================================================
-- RECRAWL JOB TRACKING
-- =============================================================================

CREATE TABLE IF NOT EXISTS memory_recrawl_jobs (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL,
    target_version INTEGER NOT NULL,
    status TEXT NOT NULL DEFAULT 'pending' CHECK (status IN ('pending', 'running', 'completed', 'failed', 'cancelled')),
    total_memories INTEGER NOT NULL DEFAULT 0,
    processed_count INTEGER NOT NULL DEFAULT 0,
    success_count INTEGER NOT NULL DEFAULT 0,
    failure_count INTEGER NOT NULL DEFAULT 0,
    batch_size INTEGER NOT NULL DEFAULT 100,
    started_at TIMESTAMPTZ,
    completed_at TIMESTAMPTZ,
    error_message TEXT,
    metadata JSONB,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_recrawl_jobs_tenant ON memory_recrawl_jobs(tenant_id);
CREATE INDEX IF NOT EXISTS idx_recrawl_jobs_status ON memory_recrawl_jobs(status);

-- Individual memory recrawl tracking (for retry/resume)
CREATE TABLE IF NOT EXISTS memory_recrawl_progress (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    job_id UUID NOT NULL REFERENCES memory_recrawl_jobs(id) ON DELETE CASCADE,
    memory_id UUID NOT NULL REFERENCES memories(id) ON DELETE CASCADE,
    status TEXT NOT NULL DEFAULT 'pending' CHECK (status IN ('pending', 'processing', 'completed', 'failed')),
    retry_count INTEGER NOT NULL DEFAULT 0,
    last_error TEXT,
    started_at TIMESTAMPTZ,
    completed_at TIMESTAMPTZ,
    entity_count INTEGER,
    relationship_count INTEGER,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    UNIQUE(job_id, memory_id)
);

CREATE INDEX IF NOT EXISTS idx_recrawl_progress_job ON memory_recrawl_progress(job_id, status);
CREATE INDEX IF NOT EXISTS idx_recrawl_progress_memory ON memory_recrawl_progress(memory_id);

-- Dead letter table for permanently failed extractions
CREATE TABLE IF NOT EXISTS recrawl_failures (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL,
    memory_id UUID NOT NULL,
    job_id UUID REFERENCES memory_recrawl_jobs(id),
    target_version INTEGER NOT NULL,
    error_message TEXT NOT NULL,
    error_stack TEXT,
    retry_count INTEGER NOT NULL DEFAULT 0,
    memory_content_preview TEXT, -- First 500 chars for debugging
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    UNIQUE(memory_id, target_version)
);

CREATE INDEX IF NOT EXISTS idx_recrawl_failures_tenant ON recrawl_failures(tenant_id);
CREATE INDEX IF NOT EXISTS idx_recrawl_failures_memory ON recrawl_failures(memory_id);

-- =============================================================================
-- FUNCTIONS FOR RECRAWL OPERATIONS
-- =============================================================================

-- Get memories that need recrawling for a tenant
CREATE OR REPLACE FUNCTION get_memories_needing_recrawl(
    p_tenant_id UUID,
    p_target_version INTEGER,
    p_batch_size INTEGER DEFAULT 100,
    p_job_id UUID DEFAULT NULL
)
RETURNS TABLE (
    memory_id UUID,
    content TEXT,
    current_version INTEGER,
    created_at TIMESTAMPTZ
) AS $$
BEGIN
    RETURN QUERY
    SELECT m.id, m.content, m.graph_version, m.created_at
    FROM memories m
    WHERE m.tenant_id = p_tenant_id
      AND m.is_active = TRUE
      AND (m.graph_version IS NULL OR m.graph_version < p_target_version)
      -- Exclude memories already being processed in this job
      AND NOT EXISTS (
          SELECT 1 FROM memory_recrawl_progress mrp
          WHERE mrp.memory_id = m.id
            AND mrp.job_id = p_job_id
            AND mrp.status IN ('processing', 'completed')
      )
      -- Exclude permanently failed memories
      AND NOT EXISTS (
          SELECT 1 FROM recrawl_failures rf
          WHERE rf.memory_id = m.id
            AND rf.target_version = p_target_version
            AND rf.retry_count >= 3
      )
    ORDER BY m.created_at ASC
    LIMIT p_batch_size;
END;
$$ LANGUAGE plpgsql;

-- Mark memory extraction as started
CREATE OR REPLACE FUNCTION start_memory_recrawl(
    p_job_id UUID,
    p_memory_id UUID
)
RETURNS VOID AS $$
BEGIN
    INSERT INTO memory_recrawl_progress (job_id, memory_id, status, started_at)
    VALUES (p_job_id, p_memory_id, 'processing', NOW())
    ON CONFLICT (job_id, memory_id) DO UPDATE SET
        status = 'processing',
        started_at = NOW(),
        retry_count = memory_recrawl_progress.retry_count + 1;
END;
$$ LANGUAGE plpgsql;

-- Mark memory extraction as completed successfully
CREATE OR REPLACE FUNCTION complete_memory_recrawl(
    p_job_id UUID,
    p_memory_id UUID,
    p_entity_count INTEGER,
    p_relationship_count INTEGER,
    p_target_version INTEGER
)
RETURNS VOID AS $$
BEGIN
    -- Update progress tracking
    UPDATE memory_recrawl_progress
    SET status = 'completed',
        completed_at = NOW(),
        entity_count = p_entity_count,
        relationship_count = p_relationship_count
    WHERE job_id = p_job_id AND memory_id = p_memory_id;

    -- Update memory with new version
    UPDATE memories
    SET graph_version = p_target_version,
        graph_extracted_at = NOW(),
        graph_extraction_error = NULL
    WHERE id = p_memory_id;

    -- Update job counters
    UPDATE memory_recrawl_jobs
    SET processed_count = processed_count + 1,
        success_count = success_count + 1,
        updated_at = NOW()
    WHERE id = p_job_id;
END;
$$ LANGUAGE plpgsql;

-- Mark memory extraction as failed
CREATE OR REPLACE FUNCTION fail_memory_recrawl(
    p_job_id UUID,
    p_memory_id UUID,
    p_error_message TEXT,
    p_target_version INTEGER,
    p_tenant_id UUID
)
RETURNS VOID AS $$
DECLARE
    v_retry_count INTEGER;
    v_content_preview TEXT;
BEGIN
    -- Get current retry count
    SELECT retry_count INTO v_retry_count
    FROM memory_recrawl_progress
    WHERE job_id = p_job_id AND memory_id = p_memory_id;

    -- Update progress tracking
    UPDATE memory_recrawl_progress
    SET status = 'failed',
        completed_at = NOW(),
        last_error = p_error_message
    WHERE job_id = p_job_id AND memory_id = p_memory_id;

    -- Update memory with error
    UPDATE memories
    SET graph_extraction_error = p_error_message
    WHERE id = p_memory_id;

    -- Update job counters
    UPDATE memory_recrawl_jobs
    SET processed_count = processed_count + 1,
        failure_count = failure_count + 1,
        updated_at = NOW()
    WHERE id = p_job_id;

    -- If exceeded retries, move to dead letter table
    IF COALESCE(v_retry_count, 0) >= 3 THEN
        SELECT LEFT(content, 500) INTO v_content_preview
        FROM memories WHERE id = p_memory_id;

        INSERT INTO recrawl_failures (tenant_id, memory_id, job_id, target_version, error_message, retry_count, memory_content_preview)
        VALUES (p_tenant_id, p_memory_id, p_job_id, p_target_version, p_error_message, v_retry_count, v_content_preview)
        ON CONFLICT (memory_id, target_version) DO UPDATE SET
            error_message = EXCLUDED.error_message,
            retry_count = EXCLUDED.retry_count,
            job_id = EXCLUDED.job_id;
    END IF;
END;
$$ LANGUAGE plpgsql;

-- Create a new recrawl job
CREATE OR REPLACE FUNCTION create_recrawl_job(
    p_tenant_id UUID,
    p_target_version INTEGER,
    p_batch_size INTEGER DEFAULT 100
)
RETURNS UUID AS $$
DECLARE
    v_job_id UUID;
    v_total_memories INTEGER;
BEGIN
    -- Count memories needing recrawl
    SELECT COUNT(*) INTO v_total_memories
    FROM memories
    WHERE tenant_id = p_tenant_id
      AND is_active = TRUE
      AND (graph_version IS NULL OR graph_version < p_target_version);

    -- Create job
    INSERT INTO memory_recrawl_jobs (tenant_id, target_version, total_memories, batch_size, status)
    VALUES (p_tenant_id, p_target_version, v_total_memories, p_batch_size, 'pending')
    RETURNING id INTO v_job_id;

    RETURN v_job_id;
END;
$$ LANGUAGE plpgsql;

-- Start a recrawl job
CREATE OR REPLACE FUNCTION start_recrawl_job(p_job_id UUID)
RETURNS VOID AS $$
BEGIN
    UPDATE memory_recrawl_jobs
    SET status = 'running',
        started_at = NOW(),
        updated_at = NOW()
    WHERE id = p_job_id;
END;
$$ LANGUAGE plpgsql;

-- Complete a recrawl job
CREATE OR REPLACE FUNCTION complete_recrawl_job(p_job_id UUID)
RETURNS VOID AS $$
BEGIN
    UPDATE memory_recrawl_jobs
    SET status = CASE
            WHEN failure_count > 0 AND success_count = 0 THEN 'failed'
            ELSE 'completed'
        END,
        completed_at = NOW(),
        updated_at = NOW()
    WHERE id = p_job_id;
END;
$$ LANGUAGE plpgsql;

-- =============================================================================
-- VERSIONED ENTITY QUERIES (prefer highest version)
-- =============================================================================

-- Get entities for a memory, preferring highest graph version
CREATE OR REPLACE FUNCTION get_memory_entities_versioned(
    p_memory_id UUID,
    p_min_version INTEGER DEFAULT NULL
)
RETURNS TABLE (
    entity_id UUID,
    entity_name TEXT,
    entity_type TEXT,
    graph_version INTEGER
) AS $$
BEGIN
    RETURN QUERY
    SELECT DISTINCT ON (e.name, e.entity_type)
        e.id,
        e.name,
        e.entity_type,
        me.graph_version
    FROM memory_entities me
    JOIN entities e ON e.id = me.entity_id
    WHERE me.memory_id = p_memory_id
      AND (p_min_version IS NULL OR me.graph_version >= p_min_version)
    ORDER BY e.name, e.entity_type, me.graph_version DESC;
END;
$$ LANGUAGE plpgsql;

-- Get relationships for an entity, preferring highest graph version
CREATE OR REPLACE FUNCTION get_entity_relationships_versioned(
    p_entity_id UUID,
    p_direction TEXT DEFAULT 'both', -- 'outgoing', 'incoming', 'both'
    p_min_version INTEGER DEFAULT NULL
)
RETURNS TABLE (
    relationship_id UUID,
    source_entity_id UUID,
    target_entity_id UUID,
    relationship_type TEXT,
    graph_version INTEGER
) AS $$
BEGIN
    RETURN QUERY
    SELECT DISTINCT ON (er.source_entity_id, er.target_entity_id, er.relationship_type)
        er.id,
        er.source_entity_id,
        er.target_entity_id,
        er.relationship_type,
        er.graph_version
    FROM entity_relationships er
    WHERE (
        (p_direction IN ('outgoing', 'both') AND er.source_entity_id = p_entity_id)
        OR (p_direction IN ('incoming', 'both') AND er.target_entity_id = p_entity_id)
    )
    AND (p_min_version IS NULL OR er.graph_version >= p_min_version)
    ORDER BY er.source_entity_id, er.target_entity_id, er.relationship_type, er.graph_version DESC;
END;
$$ LANGUAGE plpgsql;

-- =============================================================================
-- STATISTICS
-- =============================================================================

-- Get recrawl statistics for a tenant
CREATE OR REPLACE FUNCTION get_recrawl_statistics(p_tenant_id UUID)
RETURNS TABLE (
    total_memories BIGINT,
    version_1_count BIGINT,
    version_2_count BIGINT,
    needs_recrawl BIGINT,
    active_jobs INTEGER,
    failed_extractions BIGINT
) AS $$
BEGIN
    RETURN QUERY
    SELECT
        (SELECT COUNT(*) FROM memories WHERE tenant_id = p_tenant_id AND is_active = TRUE),
        (SELECT COUNT(*) FROM memories WHERE tenant_id = p_tenant_id AND is_active = TRUE AND graph_version = 1),
        (SELECT COUNT(*) FROM memories WHERE tenant_id = p_tenant_id AND is_active = TRUE AND graph_version = 2),
        (SELECT COUNT(*) FROM memories WHERE tenant_id = p_tenant_id AND is_active = TRUE AND (graph_version IS NULL OR graph_version < 2)),
        (SELECT COUNT(*)::INTEGER FROM memory_recrawl_jobs WHERE tenant_id = p_tenant_id AND status = 'running'),
        (SELECT COUNT(*) FROM recrawl_failures WHERE tenant_id = p_tenant_id);
END;
$$ LANGUAGE plpgsql;

-- =============================================================================
-- TRIGGERS
-- =============================================================================

-- Auto-update updated_at for recrawl jobs
CREATE OR REPLACE TRIGGER update_recrawl_jobs_updated_at
    BEFORE UPDATE ON memory_recrawl_jobs
    FOR EACH ROW
    EXECUTE FUNCTION update_updated_at_column();
