-- ============================================================================
-- FIX EVENT SOURCING COMPLETE - Multi-Tenant Event Pipeline
-- ============================================================================
-- This migration fixes ALL event sourcing issues:
-- 1. Adds tenant_id to memory_events table
-- 2. Creates event_log table for cross-cutting events
-- 3. Fixes RLS policies with internal_admin bypass
-- 4. Adds memory_timeline for time-travel replay
-- 5. Adds mind_health table for dashboard metrics
-- 6. Creates helper functions for event emission
-- ============================================================================

BEGIN;

-- ============================================================================
-- STEP 1: Extend memory_event_type enum with missing types
-- ============================================================================

-- Add new event types if they don't exist
DO $$ BEGIN
    ALTER TYPE memory_event_type ADD VALUE IF NOT EXISTS 'LayerGenerated';
EXCEPTION WHEN duplicate_object THEN NULL;
END $$;

DO $$ BEGIN
    ALTER TYPE memory_event_type ADD VALUE IF NOT EXISTS 'KnowledgeNodeAdded';
EXCEPTION WHEN duplicate_object THEN NULL;
END $$;

DO $$ BEGIN
    ALTER TYPE memory_event_type ADD VALUE IF NOT EXISTS 'ConflictDetected';
EXCEPTION WHEN duplicate_object THEN NULL;
END $$;

DO $$ BEGIN
    ALTER TYPE memory_event_type ADD VALUE IF NOT EXISTS 'TimelineSnapshotCreated';
EXCEPTION WHEN duplicate_object THEN NULL;
END $$;

DO $$ BEGIN
    ALTER TYPE memory_event_type ADD VALUE IF NOT EXISTS 'ReasoningExecuted';
EXCEPTION WHEN duplicate_object THEN NULL;
END $$;

DO $$ BEGIN
    ALTER TYPE memory_event_type ADD VALUE IF NOT EXISTS 'MutationApplied';
EXCEPTION WHEN duplicate_object THEN NULL;
END $$;

DO $$ BEGIN
    ALTER TYPE memory_event_type ADD VALUE IF NOT EXISTS 'BranchCreated';
EXCEPTION WHEN duplicate_object THEN NULL;
END $$;

DO $$ BEGIN
    ALTER TYPE memory_event_type ADD VALUE IF NOT EXISTS 'BranchMerged';
EXCEPTION WHEN duplicate_object THEN NULL;
END $$;

DO $$ BEGIN
    ALTER TYPE memory_event_type ADD VALUE IF NOT EXISTS 'MemorySplit';
EXCEPTION WHEN duplicate_object THEN NULL;
END $$;

DO $$ BEGIN
    ALTER TYPE memory_event_type ADD VALUE IF NOT EXISTS 'MemoryExpired';
EXCEPTION WHEN duplicate_object THEN NULL;
END $$;

DO $$ BEGIN
    ALTER TYPE memory_event_type ADD VALUE IF NOT EXISTS 'HallucinationDetected';
EXCEPTION WHEN duplicate_object THEN NULL;
END $$;

-- ============================================================================
-- STEP 2: Add tenant_id column to memory_events
-- ============================================================================

ALTER TABLE memory_events ADD COLUMN IF NOT EXISTS tenant_id UUID;

-- Create index on tenant_id for efficient queries
CREATE INDEX IF NOT EXISTS idx_memory_events_tenant ON memory_events (tenant_id, created_at DESC);

-- Create index for Recent Events query pattern
CREATE INDEX IF NOT EXISTS idx_memory_events_tenant_global ON memory_events (tenant_id, global_sequence DESC);

-- ============================================================================
-- STEP 3: Create event_log table for cross-cutting system events
-- ============================================================================

CREATE TABLE IF NOT EXISTS event_log (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL,
    event_type TEXT NOT NULL,
    memory_id UUID,                           -- Optional: linked memory
    actor TEXT NOT NULL DEFAULT 'system',     -- Who/what triggered the event
    payload JSONB NOT NULL DEFAULT '{}',      -- Event data
    seq BIGSERIAL,                            -- Per-tenant sequence number
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    -- Searchable fields
    severity TEXT DEFAULT 'info',             -- info, warning, error, critical
    category TEXT DEFAULT 'memory',           -- memory, reasoning, graph, security, billing
    correlation_id UUID                       -- For tracing related events
);

-- Indexes for event_log
CREATE INDEX IF NOT EXISTS idx_event_log_tenant_seq ON event_log (tenant_id, seq DESC);
CREATE INDEX IF NOT EXISTS idx_event_log_tenant_created ON event_log (tenant_id, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_event_log_tenant_type ON event_log (tenant_id, event_type);
CREATE INDEX IF NOT EXISTS idx_event_log_memory ON event_log (memory_id) WHERE memory_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_event_log_category ON event_log (tenant_id, category, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_event_log_correlation ON event_log (correlation_id) WHERE correlation_id IS NOT NULL;

-- ============================================================================
-- STEP 4: Create memory_timeline table for time-travel snapshots
-- ============================================================================

CREATE TABLE IF NOT EXISTS memory_timeline (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL,
    memory_id UUID NOT NULL,
    snapshot_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    event_id UUID,                            -- The event that triggered this snapshot
    event_type TEXT NOT NULL,

    -- Memory state at this point in time
    content TEXT NOT NULL,
    layer TEXT NOT NULL DEFAULT 'L0_RAW',
    confidence_score DECIMAL(4,3) NOT NULL DEFAULT 1.000,
    metadata JSONB DEFAULT '{}',

    -- Delta from previous snapshot (for efficient replay)
    delta_type TEXT DEFAULT 'full',           -- full, patch, reference
    delta_data JSONB,

    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Indexes for timeline
CREATE INDEX IF NOT EXISTS idx_memory_timeline_tenant_memory ON memory_timeline (tenant_id, memory_id, snapshot_at DESC);
CREATE INDEX IF NOT EXISTS idx_memory_timeline_event ON memory_timeline (event_id) WHERE event_id IS NOT NULL;

-- ============================================================================
-- STEP 5: Create/update mind_health table
-- ============================================================================

CREATE TABLE IF NOT EXISTS mind_health (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL,
    computed_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    -- Health metrics
    total_memories INT NOT NULL DEFAULT 0,
    active_memories INT NOT NULL DEFAULT 0,
    decayed_memories INT NOT NULL DEFAULT 0,
    contradictions_detected INT NOT NULL DEFAULT 0,
    hallucinations_flagged INT NOT NULL DEFAULT 0,

    -- Layer distribution
    l0_count INT NOT NULL DEFAULT 0,
    l1_count INT NOT NULL DEFAULT 0,
    l2_count INT NOT NULL DEFAULT 0,
    l3_count INT NOT NULL DEFAULT 0,
    l4_count INT NOT NULL DEFAULT 0,

    -- Confidence metrics
    avg_confidence DECIMAL(4,3) NOT NULL DEFAULT 1.000,
    min_confidence DECIMAL(4,3) NOT NULL DEFAULT 1.000,

    -- Graph health
    entity_count INT NOT NULL DEFAULT 0,
    relationship_count INT NOT NULL DEFAULT 0,
    orphan_entity_count INT NOT NULL DEFAULT 0,

    -- Event metrics
    events_last_hour INT NOT NULL DEFAULT 0,
    events_last_day INT NOT NULL DEFAULT 0,

    -- Overall health score (0-100)
    health_score INT NOT NULL DEFAULT 100,
    health_status TEXT NOT NULL DEFAULT 'healthy',  -- healthy, warning, critical

    metadata JSONB DEFAULT '{}'
);

-- Only keep recent health snapshots per tenant
CREATE INDEX IF NOT EXISTS idx_mind_health_tenant ON mind_health (tenant_id, computed_at DESC);

-- ============================================================================
-- STEP 6: Create conflict_results table
-- ============================================================================

CREATE TABLE IF NOT EXISTS conflict_results (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL,
    scan_id UUID NOT NULL,                    -- Batch scan identifier
    memory_a_id UUID NOT NULL,
    memory_b_id UUID NOT NULL,
    similarity_score DECIMAL(4,3) NOT NULL,
    contradiction_type TEXT NOT NULL,         -- semantic, temporal, factual
    explanation TEXT,
    resolved BOOLEAN NOT NULL DEFAULT FALSE,
    resolved_at TIMESTAMPTZ,
    resolved_by TEXT,
    resolution_action TEXT,                   -- merged, invalidated, kept_both, manual
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    CONSTRAINT unique_conflict_pair UNIQUE (tenant_id, memory_a_id, memory_b_id)
);

CREATE INDEX IF NOT EXISTS idx_conflict_results_tenant ON conflict_results (tenant_id, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_conflict_results_unresolved ON conflict_results (tenant_id, resolved) WHERE resolved = FALSE;

-- ============================================================================
-- STEP 7: Create branch_metadata table for shadow branches
-- ============================================================================

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
    status TEXT NOT NULL DEFAULT 'active',    -- active, merged, abandoned
    memory_count INT NOT NULL DEFAULT 0,
    entity_count INT NOT NULL DEFAULT 0,

    CONSTRAINT unique_branch_name UNIQUE (tenant_id, branch_name)
);

CREATE INDEX IF NOT EXISTS idx_branch_metadata_tenant ON branch_metadata (tenant_id, status, created_at DESC);

-- ============================================================================
-- STEP 8: Enable RLS on all event-related tables
-- ============================================================================

ALTER TABLE memory_events ENABLE ROW LEVEL SECURITY;
ALTER TABLE event_log ENABLE ROW LEVEL SECURITY;
ALTER TABLE memory_timeline ENABLE ROW LEVEL SECURITY;
ALTER TABLE mind_health ENABLE ROW LEVEL SECURITY;
ALTER TABLE conflict_results ENABLE ROW LEVEL SECURITY;
ALTER TABLE branch_metadata ENABLE ROW LEVEL SECURITY;

-- Force RLS for table owners (defense in depth)
ALTER TABLE memory_events FORCE ROW LEVEL SECURITY;
ALTER TABLE event_log FORCE ROW LEVEL SECURITY;
ALTER TABLE memory_timeline FORCE ROW LEVEL SECURITY;
ALTER TABLE mind_health FORCE ROW LEVEL SECURITY;
ALTER TABLE conflict_results FORCE ROW LEVEL SECURITY;
ALTER TABLE branch_metadata FORCE ROW LEVEL SECURITY;

-- ============================================================================
-- STEP 9: Create is_internal_admin() check function
-- ============================================================================

CREATE OR REPLACE FUNCTION is_internal_admin()
RETURNS BOOLEAN AS $$
BEGIN
    RETURN COALESCE(current_setting('app.role', true), '') = 'internal_admin';
END;
$$ LANGUAGE plpgsql STABLE;

-- ============================================================================
-- STEP 10: Create RLS policies with internal_admin bypass
-- ============================================================================

-- MEMORY_EVENTS policies
DROP POLICY IF EXISTS tenant_read_memory_events ON memory_events;
DROP POLICY IF EXISTS internal_admin_memory_events ON memory_events;

CREATE POLICY tenant_read_memory_events ON memory_events
    FOR SELECT
    USING (
        tenant_id = current_setting('app.tenant_id', true)::uuid
        OR tenant_id IS NULL  -- Legacy events without tenant_id
    );

CREATE POLICY internal_admin_memory_events ON memory_events
    FOR ALL
    USING (is_internal_admin())
    WITH CHECK (is_internal_admin());

-- EVENT_LOG policies
DROP POLICY IF EXISTS tenant_read_event_log ON event_log;
DROP POLICY IF EXISTS internal_admin_event_log ON event_log;

CREATE POLICY tenant_read_event_log ON event_log
    FOR SELECT
    USING (tenant_id = current_setting('app.tenant_id', true)::uuid);

CREATE POLICY internal_admin_event_log ON event_log
    FOR ALL
    USING (is_internal_admin())
    WITH CHECK (is_internal_admin());

-- MEMORY_TIMELINE policies
DROP POLICY IF EXISTS tenant_read_memory_timeline ON memory_timeline;
DROP POLICY IF EXISTS internal_admin_memory_timeline ON memory_timeline;

CREATE POLICY tenant_read_memory_timeline ON memory_timeline
    FOR SELECT
    USING (tenant_id = current_setting('app.tenant_id', true)::uuid);

CREATE POLICY internal_admin_memory_timeline ON memory_timeline
    FOR ALL
    USING (is_internal_admin())
    WITH CHECK (is_internal_admin());

-- MIND_HEALTH policies
DROP POLICY IF EXISTS tenant_read_mind_health ON mind_health;
DROP POLICY IF EXISTS internal_admin_mind_health ON mind_health;

CREATE POLICY tenant_read_mind_health ON mind_health
    FOR SELECT
    USING (tenant_id = current_setting('app.tenant_id', true)::uuid);

CREATE POLICY internal_admin_mind_health ON mind_health
    FOR ALL
    USING (is_internal_admin())
    WITH CHECK (is_internal_admin());

-- CONFLICT_RESULTS policies
DROP POLICY IF EXISTS tenant_read_conflict_results ON conflict_results;
DROP POLICY IF EXISTS internal_admin_conflict_results ON conflict_results;

CREATE POLICY tenant_read_conflict_results ON conflict_results
    FOR SELECT
    USING (tenant_id = current_setting('app.tenant_id', true)::uuid);

CREATE POLICY internal_admin_conflict_results ON conflict_results
    FOR ALL
    USING (is_internal_admin())
    WITH CHECK (is_internal_admin());

-- BRANCH_METADATA policies
DROP POLICY IF EXISTS tenant_isolation_branch_metadata ON branch_metadata;
DROP POLICY IF EXISTS internal_admin_branch_metadata ON branch_metadata;

CREATE POLICY tenant_isolation_branch_metadata ON branch_metadata
    FOR ALL
    USING (tenant_id = current_setting('app.tenant_id', true)::uuid)
    WITH CHECK (tenant_id = current_setting('app.tenant_id', true)::uuid);

CREATE POLICY internal_admin_branch_metadata ON branch_metadata
    FOR ALL
    USING (is_internal_admin())
    WITH CHECK (is_internal_admin());

-- ============================================================================
-- STEP 11: Create emit_memory_event() function for consistent event emission
-- ============================================================================

CREATE OR REPLACE FUNCTION emit_memory_event(
    p_tenant_id UUID,
    p_memory_id UUID,
    p_event_type TEXT,
    p_event_data JSONB,
    p_actor TEXT DEFAULT 'system',
    p_metadata JSONB DEFAULT '{}'
) RETURNS UUID AS $$
DECLARE
    v_event_id UUID;
    v_content_hash TEXT;
    v_event_version BIGINT;
BEGIN
    -- Generate content hash
    v_content_hash := encode(sha256(p_event_data::text::bytea), 'hex');

    -- Get next version for this stream
    SELECT COALESCE(MAX(event_version), 0) + 1 INTO v_event_version
    FROM memory_events
    WHERE stream_id = p_memory_id;

    -- Insert into memory_events
    INSERT INTO memory_events (
        event_id,
        stream_id,
        tenant_id,
        event_type,
        event_version,
        event_data,
        metadata,
        created_by,
        content_hash
    ) VALUES (
        gen_random_uuid(),
        p_memory_id,
        p_tenant_id,
        p_event_type::memory_event_type,
        v_event_version,
        p_event_data,
        p_metadata,
        p_actor,
        v_content_hash
    )
    RETURNING event_id INTO v_event_id;

    -- Also insert into event_log for cross-cutting queries
    INSERT INTO event_log (
        tenant_id,
        event_type,
        memory_id,
        actor,
        payload,
        category
    ) VALUES (
        p_tenant_id,
        p_event_type,
        p_memory_id,
        p_actor,
        p_event_data,
        'memory'
    );

    RETURN v_event_id;
END;
$$ LANGUAGE plpgsql;

-- ============================================================================
-- STEP 12: Create emit_system_event() function for non-memory events
-- ============================================================================

CREATE OR REPLACE FUNCTION emit_system_event(
    p_tenant_id UUID,
    p_event_type TEXT,
    p_payload JSONB,
    p_actor TEXT DEFAULT 'system',
    p_category TEXT DEFAULT 'system',
    p_severity TEXT DEFAULT 'info',
    p_memory_id UUID DEFAULT NULL,
    p_correlation_id UUID DEFAULT NULL
) RETURNS UUID AS $$
DECLARE
    v_event_id UUID;
BEGIN
    INSERT INTO event_log (
        tenant_id,
        event_type,
        memory_id,
        actor,
        payload,
        category,
        severity,
        correlation_id
    ) VALUES (
        p_tenant_id,
        p_event_type,
        p_memory_id,
        p_actor,
        p_payload,
        p_category,
        p_severity,
        p_correlation_id
    )
    RETURNING id INTO v_event_id;

    RETURN v_event_id;
END;
$$ LANGUAGE plpgsql;

-- ============================================================================
-- STEP 13: Create create_timeline_snapshot() function
-- ============================================================================

CREATE OR REPLACE FUNCTION create_timeline_snapshot(
    p_tenant_id UUID,
    p_memory_id UUID,
    p_event_id UUID,
    p_event_type TEXT,
    p_content TEXT,
    p_layer TEXT,
    p_confidence DECIMAL,
    p_metadata JSONB DEFAULT '{}'
) RETURNS UUID AS $$
DECLARE
    v_snapshot_id UUID;
BEGIN
    INSERT INTO memory_timeline (
        tenant_id,
        memory_id,
        event_id,
        event_type,
        content,
        layer,
        confidence_score,
        metadata,
        delta_type
    ) VALUES (
        p_tenant_id,
        p_memory_id,
        p_event_id,
        p_event_type,
        p_content,
        p_layer,
        p_confidence,
        p_metadata,
        'full'
    )
    RETURNING id INTO v_snapshot_id;

    RETURN v_snapshot_id;
END;
$$ LANGUAGE plpgsql;

-- ============================================================================
-- STEP 14: Create compute_mind_health() function
-- ============================================================================

CREATE OR REPLACE FUNCTION compute_mind_health(p_tenant_id UUID)
RETURNS UUID AS $$
DECLARE
    v_health_id UUID;
    v_total_memories INT;
    v_active_memories INT;
    v_decayed_memories INT;
    v_contradictions INT;
    v_l0 INT; v_l1 INT; v_l2 INT; v_l3 INT; v_l4 INT;
    v_avg_conf DECIMAL;
    v_min_conf DECIMAL;
    v_entities INT;
    v_relationships INT;
    v_events_hour INT;
    v_events_day INT;
    v_health_score INT;
    v_health_status TEXT;
BEGIN
    -- Count memories by status
    SELECT
        COUNT(*),
        COUNT(*) FILTER (WHERE is_active = TRUE),
        COUNT(*) FILTER (WHERE confidence_score < 0.3)
    INTO v_total_memories, v_active_memories, v_decayed_memories
    FROM memories WHERE tenant_id = p_tenant_id;

    -- Count contradictions
    SELECT COUNT(*) INTO v_contradictions
    FROM conflict_results WHERE tenant_id = p_tenant_id AND resolved = FALSE;

    -- Layer distribution
    SELECT
        COUNT(*) FILTER (WHERE layer = 'L0_RAW'),
        COUNT(*) FILTER (WHERE layer = 'L1_CONTEXT'),
        COUNT(*) FILTER (WHERE layer = 'L2_SUMMARY'),
        COUNT(*) FILTER (WHERE layer = 'L3_KNOWLEDGE'),
        COUNT(*) FILTER (WHERE layer = 'L4_HEURISTIC')
    INTO v_l0, v_l1, v_l2, v_l3, v_l4
    FROM memories WHERE tenant_id = p_tenant_id AND is_active = TRUE;

    -- Confidence metrics
    SELECT
        COALESCE(AVG(confidence_score), 1.0),
        COALESCE(MIN(confidence_score), 1.0)
    INTO v_avg_conf, v_min_conf
    FROM memories WHERE tenant_id = p_tenant_id AND is_active = TRUE;

    -- Entity and relationship counts
    SELECT COUNT(*) INTO v_entities FROM entities WHERE tenant_id = p_tenant_id;
    SELECT COUNT(*) INTO v_relationships FROM entity_relationships WHERE tenant_id = p_tenant_id;

    -- Event counts
    SELECT COUNT(*) INTO v_events_hour
    FROM event_log
    WHERE tenant_id = p_tenant_id AND created_at > NOW() - INTERVAL '1 hour';

    SELECT COUNT(*) INTO v_events_day
    FROM event_log
    WHERE tenant_id = p_tenant_id AND created_at > NOW() - INTERVAL '1 day';

    -- Compute health score (simple heuristic)
    v_health_score := 100;

    -- Penalty for low confidence
    IF v_avg_conf < 0.5 THEN v_health_score := v_health_score - 20; END IF;
    IF v_avg_conf < 0.3 THEN v_health_score := v_health_score - 20; END IF;

    -- Penalty for unresolved contradictions
    v_health_score := v_health_score - LEAST(v_contradictions * 2, 30);

    -- Penalty for high decay
    IF v_total_memories > 0 AND (v_decayed_memories::DECIMAL / v_total_memories) > 0.3 THEN
        v_health_score := v_health_score - 15;
    END IF;

    v_health_score := GREATEST(0, v_health_score);

    v_health_status := CASE
        WHEN v_health_score >= 80 THEN 'healthy'
        WHEN v_health_score >= 50 THEN 'warning'
        ELSE 'critical'
    END;

    -- Insert health record
    INSERT INTO mind_health (
        tenant_id,
        total_memories, active_memories, decayed_memories, contradictions_detected,
        l0_count, l1_count, l2_count, l3_count, l4_count,
        avg_confidence, min_confidence,
        entity_count, relationship_count,
        events_last_hour, events_last_day,
        health_score, health_status
    ) VALUES (
        p_tenant_id,
        v_total_memories, v_active_memories, v_decayed_memories, v_contradictions,
        v_l0, v_l1, v_l2, v_l3, v_l4,
        v_avg_conf, v_min_conf,
        v_entities, v_relationships,
        v_events_hour, v_events_day,
        v_health_score, v_health_status
    )
    RETURNING id INTO v_health_id;

    -- Emit system event
    PERFORM emit_system_event(
        p_tenant_id,
        'MindHealthComputed',
        jsonb_build_object(
            'health_score', v_health_score,
            'health_status', v_health_status,
            'total_memories', v_total_memories
        ),
        'mind_health_worker',
        'health'
    );

    RETURN v_health_id;
END;
$$ LANGUAGE plpgsql;

-- ============================================================================
-- STEP 15: Create get_recent_events() function for dashboard
-- ============================================================================

CREATE OR REPLACE FUNCTION get_recent_events(
    p_tenant_id UUID,
    p_limit INT DEFAULT 50,
    p_offset INT DEFAULT 0,
    p_category TEXT DEFAULT NULL,
    p_event_type TEXT DEFAULT NULL
)
RETURNS TABLE (
    id UUID,
    seq BIGINT,
    event_type TEXT,
    memory_id UUID,
    actor TEXT,
    payload JSONB,
    category TEXT,
    severity TEXT,
    created_at TIMESTAMPTZ
) AS $$
BEGIN
    RETURN QUERY
    SELECT
        el.id,
        el.seq,
        el.event_type,
        el.memory_id,
        el.actor,
        el.payload,
        el.category,
        el.severity,
        el.created_at
    FROM event_log el
    WHERE el.tenant_id = p_tenant_id
      AND (p_category IS NULL OR el.category = p_category)
      AND (p_event_type IS NULL OR el.event_type = p_event_type)
    ORDER BY el.seq DESC
    LIMIT p_limit
    OFFSET p_offset;
END;
$$ LANGUAGE plpgsql STABLE;

-- ============================================================================
-- STEP 16: Create get_memory_timeline() function for replay
-- ============================================================================

CREATE OR REPLACE FUNCTION get_memory_timeline(
    p_tenant_id UUID,
    p_memory_id UUID,
    p_from_time TIMESTAMPTZ DEFAULT NULL,
    p_to_time TIMESTAMPTZ DEFAULT NULL
)
RETURNS TABLE (
    id UUID,
    snapshot_at TIMESTAMPTZ,
    event_type TEXT,
    content TEXT,
    layer TEXT,
    confidence_score DECIMAL,
    metadata JSONB
) AS $$
BEGIN
    RETURN QUERY
    SELECT
        mt.id,
        mt.snapshot_at,
        mt.event_type,
        mt.content,
        mt.layer,
        mt.confidence_score,
        mt.metadata
    FROM memory_timeline mt
    WHERE mt.tenant_id = p_tenant_id
      AND mt.memory_id = p_memory_id
      AND (p_from_time IS NULL OR mt.snapshot_at >= p_from_time)
      AND (p_to_time IS NULL OR mt.snapshot_at <= p_to_time)
    ORDER BY mt.snapshot_at ASC;
END;
$$ LANGUAGE plpgsql STABLE;

-- ============================================================================
-- STEP 17: Create trigger to auto-emit events on memory changes
-- ============================================================================

CREATE OR REPLACE FUNCTION trigger_memory_event()
RETURNS TRIGGER AS $$
DECLARE
    v_event_type TEXT;
    v_event_data JSONB;
BEGIN
    -- Skip if internal admin (worker will emit its own events)
    IF is_internal_admin() THEN
        RETURN NEW;
    END IF;

    IF TG_OP = 'INSERT' THEN
        v_event_type := 'MemoryCreated';
        v_event_data := jsonb_build_object(
            'content_length', LENGTH(NEW.content),
            'source', NEW.source,
            'layer', NEW.layer
        );
    ELSIF TG_OP = 'UPDATE' THEN
        IF NEW.is_active = FALSE AND OLD.is_active = TRUE THEN
            v_event_type := 'MemoryInvalidated';
        ELSIF NEW.merged_into IS NOT NULL AND OLD.merged_into IS NULL THEN
            v_event_type := 'MemoryMerged';
        ELSIF NEW.confidence_score < OLD.confidence_score THEN
            v_event_type := 'MemoryDecayed';
        ELSIF NEW.confidence_score > OLD.confidence_score THEN
            v_event_type := 'MemoryReinforced';
        ELSE
            v_event_type := 'MemoryUpdated';
        END IF;
        v_event_data := jsonb_build_object(
            'old_confidence', OLD.confidence_score,
            'new_confidence', NEW.confidence_score,
            'old_layer', OLD.layer,
            'new_layer', NEW.layer
        );
    END IF;

    -- Emit event (only if tenant context is set)
    IF NEW.tenant_id IS NOT NULL THEN
        PERFORM emit_memory_event(
            NEW.tenant_id,
            NEW.id,
            v_event_type,
            v_event_data,
            COALESCE(current_setting('app.user_id', true), 'system')
        );
    END IF;

    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

-- Drop existing trigger if any
DROP TRIGGER IF EXISTS tr_memory_events ON memories;

-- Create trigger (runs AFTER to ensure transaction succeeds first)
CREATE TRIGGER tr_memory_events
    AFTER INSERT OR UPDATE ON memories
    FOR EACH ROW
    EXECUTE FUNCTION trigger_memory_event();

-- ============================================================================
-- STEP 18: Backfill tenant_id for existing memory_events from stream_id
-- ============================================================================

UPDATE memory_events me
SET tenant_id = m.tenant_id
FROM memories m
WHERE me.stream_id = m.id
  AND me.tenant_id IS NULL;

-- ============================================================================
-- STEP 19: Create view for easy Recent Events access
-- ============================================================================

CREATE OR REPLACE VIEW v_recent_events AS
SELECT
    el.id,
    el.tenant_id,
    el.seq,
    el.event_type,
    el.memory_id,
    el.actor,
    el.payload,
    el.category,
    el.severity,
    el.created_at,
    m.content AS memory_content_preview
FROM event_log el
LEFT JOIN memories m ON el.memory_id = m.id
ORDER BY el.created_at DESC;

COMMENT ON VIEW v_recent_events IS 'Recent events with memory content preview for dashboard';

-- ============================================================================
-- STEP 20: Grant permissions
-- ============================================================================

-- Grant execute on functions to service role
DO $$ BEGIN
    GRANT EXECUTE ON FUNCTION emit_memory_event TO serialmemory_service;
    GRANT EXECUTE ON FUNCTION emit_system_event TO serialmemory_service;
    GRANT EXECUTE ON FUNCTION create_timeline_snapshot TO serialmemory_service;
    GRANT EXECUTE ON FUNCTION compute_mind_health TO serialmemory_service;
    GRANT EXECUTE ON FUNCTION get_recent_events TO serialmemory_service;
    GRANT EXECUTE ON FUNCTION get_memory_timeline TO serialmemory_service;
EXCEPTION WHEN undefined_object THEN NULL;
END $$;

COMMIT;

-- ============================================================================
-- VERIFICATION QUERIES (run manually to test)
-- ============================================================================

-- Test event emission:
-- SELECT set_config('app.role', 'internal_admin', false);
-- SELECT set_config('app.tenant_id', 'your-tenant-id-here', false);
-- SELECT emit_system_event('your-tenant-id'::uuid, 'TestEvent', '{"test": true}'::jsonb);
-- SELECT * FROM event_log WHERE event_type = 'TestEvent';

-- Test Recent Events:
-- SELECT * FROM get_recent_events('your-tenant-id'::uuid, 10);

-- Test Mind Health:
-- SELECT compute_mind_health('your-tenant-id'::uuid);
-- SELECT * FROM mind_health WHERE tenant_id = 'your-tenant-id'::uuid ORDER BY computed_at DESC LIMIT 1;
