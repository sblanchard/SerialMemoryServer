-- =============================================================================
-- Memory Classification Schema (L0-L4 Pipeline)
-- =============================================================================
-- Tables for the memory classification worker to process memories through
-- L0 (raw) -> L1 (context) -> L2 (summary) -> L3 (knowledge) -> L4 (heuristic)
--
-- Features:
--   - Full L0-L4 classification pipeline
--   - Automatic queue trigger on memory creation
--   - Knowledge graph nodes from L3/L4
--   - Timeline snapshots for replay
--   - Event emission for dashboard
--   - RLS policies with internal_admin bypass
-- =============================================================================

BEGIN;

-- =============================================================================
-- STEP 1: Memory Layer Enum
-- =============================================================================

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_type WHERE typname = 'memory_layer_type') THEN
        CREATE TYPE memory_layer_type AS ENUM (
            'L0_RAW',
            'L1_CONTEXT',
            'L2_SUMMARY',
            'L3_KNOWLEDGE',
            'L4_HEURISTIC'
        );
    END IF;
END $$;

-- =============================================================================
-- STEP 2: Memory Processing Status Enum
-- =============================================================================

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_type WHERE typname = 'memory_processing_status') THEN
        CREATE TYPE memory_processing_status AS ENUM (
            'PENDING',
            'PROCESSING',
            'COMPLETE',
            'FAILED',
            'PARTIAL'
        );
    END IF;
END $$;

-- =============================================================================
-- STEP 3: Memory Layers Table
-- =============================================================================

CREATE TABLE IF NOT EXISTS memory_layers (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    memory_id UUID NOT NULL,
    tenant_id UUID NOT NULL,
    layer memory_layer_type NOT NULL,
    content_json JSONB NOT NULL,
    content_hash CHAR(64) NOT NULL,  -- SHA-256 for integrity
    model_name VARCHAR(100),          -- Which LLM/model produced this
    model_version VARCHAR(50),
    processing_duration_ms INTEGER,
    confidence_score DECIMAL(4,3) CHECK (confidence_score >= 0 AND confidence_score <= 1),
    version INTEGER NOT NULL DEFAULT 1,
    is_current BOOLEAN NOT NULL DEFAULT TRUE,
    superseded_by UUID REFERENCES memory_layers(id),
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    created_by VARCHAR(255) DEFAULT 'system',

    -- Unique constraint: only one current layer per memory+type
    CONSTRAINT unique_current_layer UNIQUE (memory_id, layer, is_current)
        DEFERRABLE INITIALLY DEFERRED
);

-- Indexes
CREATE INDEX IF NOT EXISTS idx_memory_layers_memory ON memory_layers(memory_id);
CREATE INDEX IF NOT EXISTS idx_memory_layers_tenant ON memory_layers(tenant_id);
CREATE INDEX IF NOT EXISTS idx_memory_layers_layer ON memory_layers(layer);
CREATE INDEX IF NOT EXISTS idx_memory_layers_current ON memory_layers(memory_id, layer) WHERE is_current = TRUE;
CREATE INDEX IF NOT EXISTS idx_memory_layers_created ON memory_layers(created_at DESC);

-- =============================================================================
-- STEP 4: Memory Processing Queue Table
-- =============================================================================

CREATE TABLE IF NOT EXISTS memory_processing_queue (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    memory_id UUID NOT NULL,
    tenant_id UUID NOT NULL,
    current_stage memory_layer_type NOT NULL DEFAULT 'L0_RAW',
    target_stage memory_layer_type NOT NULL DEFAULT 'L4_HEURISTIC',
    status memory_processing_status NOT NULL DEFAULT 'PENDING',
    priority INTEGER NOT NULL DEFAULT 0,  -- Higher = more urgent
    locked_by VARCHAR(255),               -- Worker instance ID
    locked_until TIMESTAMPTZ,
    retry_count INTEGER NOT NULL DEFAULT 0,
    max_retries INTEGER NOT NULL DEFAULT 3,
    last_error TEXT,
    last_error_at TIMESTAMPTZ,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    started_at TIMESTAMPTZ,
    completed_at TIMESTAMPTZ,
    metadata JSONB,

    -- Ensure only one queue entry per memory
    CONSTRAINT unique_memory_queue UNIQUE (memory_id)
);

-- Indexes for efficient polling
CREATE INDEX IF NOT EXISTS idx_queue_pending ON memory_processing_queue(status, priority DESC, created_at)
    WHERE status = 'PENDING';
CREATE INDEX IF NOT EXISTS idx_queue_locked ON memory_processing_queue(locked_by, locked_until)
    WHERE locked_by IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_queue_tenant ON memory_processing_queue(tenant_id);
CREATE INDEX IF NOT EXISTS idx_queue_failed ON memory_processing_queue(status, retry_count)
    WHERE status = 'FAILED';

-- =============================================================================
-- STEP 5: Knowledge Graph Nodes from L3/L4
-- =============================================================================

CREATE TABLE IF NOT EXISTS knowledge_graph_nodes (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL,
    memory_id UUID NOT NULL,
    layer_id UUID REFERENCES memory_layers(id),
    node_type VARCHAR(50) NOT NULL,     -- 'fact', 'entity', 'relationship', 'rule', 'preference', 'habit', 'mental_model'
    subject TEXT NOT NULL,
    predicate TEXT,
    object TEXT,
    confidence DECIMAL(4,3) CHECK (confidence >= 0 AND confidence <= 1),
    source_layer memory_layer_type NOT NULL,
    evidence_text TEXT,
    embedding vector(1536),  -- OpenAI text-embedding-3-small dimension
    metadata JSONB,
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Indexes
CREATE INDEX IF NOT EXISTS idx_kg_nodes_tenant ON knowledge_graph_nodes(tenant_id);
CREATE INDEX IF NOT EXISTS idx_kg_nodes_memory ON knowledge_graph_nodes(memory_id);
CREATE INDEX IF NOT EXISTS idx_kg_nodes_type ON knowledge_graph_nodes(node_type);
CREATE INDEX IF NOT EXISTS idx_kg_nodes_subject ON knowledge_graph_nodes(tenant_id, subject);
CREATE INDEX IF NOT EXISTS idx_kg_nodes_active ON knowledge_graph_nodes(tenant_id, is_active) WHERE is_active = TRUE;

-- Vector index for semantic search on knowledge nodes
DO $$
BEGIN
    -- Only create index if we have enough rows (avoid empty index issues)
    CREATE INDEX IF NOT EXISTS idx_kg_nodes_embedding ON knowledge_graph_nodes
        USING ivfflat (embedding vector_cosine_ops) WITH (lists = 100);
EXCEPTION WHEN others THEN
    RAISE NOTICE 'Vector index creation deferred (table may be empty): %', SQLERRM;
END $$;

-- =============================================================================
-- STEP 6: Memory Classification Events (for dashboard tracing)
-- =============================================================================

CREATE TABLE IF NOT EXISTS memory_classification_events (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    memory_id UUID NOT NULL,
    tenant_id UUID NOT NULL,
    event_type VARCHAR(50) NOT NULL,    -- 'LAYER_STARTED', 'LAYER_COMPLETED', 'LAYER_FAILED', etc.
    layer memory_layer_type,
    details JSONB,
    duration_ms INTEGER,
    error_message TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Indexes for efficient querying
CREATE INDEX IF NOT EXISTS idx_class_events_memory ON memory_classification_events(memory_id, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_class_events_tenant ON memory_classification_events(tenant_id, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_class_events_type ON memory_classification_events(event_type);
CREATE INDEX IF NOT EXISTS idx_class_events_time ON memory_classification_events(created_at DESC);

-- =============================================================================
-- STEP 7: Timeline Snapshots (for replay functionality)
-- =============================================================================

CREATE TABLE IF NOT EXISTS timeline_snapshots (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL,
    memory_id UUID NOT NULL,
    event_id UUID NOT NULL,
    event_type VARCHAR(100) NOT NULL,
    content TEXT NOT NULL,
    layer VARCHAR(20) NOT NULL,
    confidence DECIMAL(4,3),
    metadata JSONB,
    snapshot_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Indexes
CREATE INDEX IF NOT EXISTS idx_timeline_tenant ON timeline_snapshots(tenant_id, snapshot_at DESC);
CREATE INDEX IF NOT EXISTS idx_timeline_memory ON timeline_snapshots(memory_id, snapshot_at DESC);
CREATE INDEX IF NOT EXISTS idx_timeline_event ON timeline_snapshots(event_id);

-- =============================================================================
-- STEP 8: Add classification columns to memories table
-- =============================================================================

DO $$
BEGIN
    -- Add classification_status if not exists
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                   WHERE table_name = 'memories' AND column_name = 'classification_status') THEN
        ALTER TABLE memories ADD COLUMN classification_status memory_processing_status DEFAULT 'PENDING';
    END IF;

    -- Add current_layer if not exists
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                   WHERE table_name = 'memories' AND column_name = 'current_layer') THEN
        ALTER TABLE memories ADD COLUMN current_layer memory_layer_type DEFAULT 'L0_RAW';
    END IF;

    -- Add classification_started_at if not exists
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                   WHERE table_name = 'memories' AND column_name = 'classification_started_at') THEN
        ALTER TABLE memories ADD COLUMN classification_started_at TIMESTAMPTZ;
    END IF;

    -- Add classification_completed_at if not exists
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                   WHERE table_name = 'memories' AND column_name = 'classification_completed_at') THEN
        ALTER TABLE memories ADD COLUMN classification_completed_at TIMESTAMPTZ;
    END IF;
END $$;

-- Index for finding unprocessed memories
CREATE INDEX IF NOT EXISTS idx_memories_classification ON memories(classification_status, created_at)
    WHERE classification_status IN ('PENDING', 'PROCESSING');

-- =============================================================================
-- STEP 9: Enable RLS on new tables
-- =============================================================================

ALTER TABLE memory_layers ENABLE ROW LEVEL SECURITY;
ALTER TABLE memory_processing_queue ENABLE ROW LEVEL SECURITY;
ALTER TABLE knowledge_graph_nodes ENABLE ROW LEVEL SECURITY;
ALTER TABLE memory_classification_events ENABLE ROW LEVEL SECURITY;
ALTER TABLE timeline_snapshots ENABLE ROW LEVEL SECURITY;

-- =============================================================================
-- STEP 10: Create RLS policies with internal admin bypass
-- =============================================================================

-- memory_layers
DROP POLICY IF EXISTS unified_memory_layers ON memory_layers;
CREATE POLICY unified_memory_layers ON memory_layers
    FOR ALL
    USING (
        COALESCE(current_setting('app.role', true), '') = 'internal_admin'
        OR tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
    )
    WITH CHECK (
        COALESCE(current_setting('app.role', true), '') = 'internal_admin'
        OR tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
    );

-- memory_processing_queue
DROP POLICY IF EXISTS unified_memory_processing_queue ON memory_processing_queue;
CREATE POLICY unified_memory_processing_queue ON memory_processing_queue
    FOR ALL
    USING (
        COALESCE(current_setting('app.role', true), '') = 'internal_admin'
        OR tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
    )
    WITH CHECK (
        COALESCE(current_setting('app.role', true), '') = 'internal_admin'
        OR tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
    );

-- knowledge_graph_nodes
DROP POLICY IF EXISTS unified_knowledge_graph_nodes ON knowledge_graph_nodes;
CREATE POLICY unified_knowledge_graph_nodes ON knowledge_graph_nodes
    FOR ALL
    USING (
        COALESCE(current_setting('app.role', true), '') = 'internal_admin'
        OR tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
    )
    WITH CHECK (
        COALESCE(current_setting('app.role', true), '') = 'internal_admin'
        OR tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
    );

-- memory_classification_events
DROP POLICY IF EXISTS unified_memory_classification_events ON memory_classification_events;
CREATE POLICY unified_memory_classification_events ON memory_classification_events
    FOR ALL
    USING (
        COALESCE(current_setting('app.role', true), '') = 'internal_admin'
        OR tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
    )
    WITH CHECK (
        COALESCE(current_setting('app.role', true), '') = 'internal_admin'
        OR tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
    );

-- timeline_snapshots
DROP POLICY IF EXISTS unified_timeline_snapshots ON timeline_snapshots;
CREATE POLICY unified_timeline_snapshots ON timeline_snapshots
    FOR ALL
    USING (
        COALESCE(current_setting('app.role', true), '') = 'internal_admin'
        OR tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
    )
    WITH CHECK (
        COALESCE(current_setting('app.role', true), '') = 'internal_admin'
        OR tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
    );

-- =============================================================================
-- STEP 11: Helper functions for classification
-- =============================================================================

-- Function to enqueue a memory for classification
CREATE OR REPLACE FUNCTION enqueue_memory_for_classification(
    p_memory_id UUID,
    p_tenant_id UUID,
    p_priority INTEGER DEFAULT 0
)
RETURNS UUID
SECURITY DEFINER
SET search_path = public
AS $$
DECLARE
    v_queue_id UUID;
BEGIN
    INSERT INTO memory_processing_queue (memory_id, tenant_id, priority)
    VALUES (p_memory_id, p_tenant_id, p_priority)
    ON CONFLICT (memory_id) DO UPDATE
    SET status = 'PENDING',
        retry_count = 0,
        last_error = NULL,
        priority = GREATEST(memory_processing_queue.priority, p_priority)
    WHERE memory_processing_queue.status IN ('FAILED', 'COMPLETE')
    RETURNING id INTO v_queue_id;

    -- Update memory status
    UPDATE memories
    SET classification_status = 'PENDING'
    WHERE id = p_memory_id;

    RETURN v_queue_id;
END;
$$ LANGUAGE plpgsql;

-- Function to acquire next batch of memories for processing
CREATE OR REPLACE FUNCTION acquire_classification_batch(
    p_worker_id VARCHAR(255),
    p_batch_size INTEGER DEFAULT 10,
    p_lock_duration INTERVAL DEFAULT '5 minutes'
)
RETURNS TABLE (
    queue_id UUID,
    memory_id UUID,
    tenant_id UUID,
    current_stage memory_layer_type,
    retry_count INTEGER
)
SECURITY DEFINER
SET search_path = public
AS $$
BEGIN
    RETURN QUERY
    WITH acquired AS (
        SELECT q.id, q.memory_id, q.tenant_id, q.current_stage, q.retry_count
        FROM memory_processing_queue q
        WHERE q.status = 'PENDING'
          AND (q.locked_by IS NULL OR q.locked_until < NOW())
        ORDER BY q.priority DESC, q.created_at
        LIMIT p_batch_size
        FOR UPDATE SKIP LOCKED
    )
    UPDATE memory_processing_queue mq
    SET locked_by = p_worker_id,
        locked_until = NOW() + p_lock_duration,
        status = 'PROCESSING',
        started_at = COALESCE(started_at, NOW())
    FROM acquired a
    WHERE mq.id = a.id
    RETURNING mq.id, mq.memory_id, mq.tenant_id, mq.current_stage, mq.retry_count;
END;
$$ LANGUAGE plpgsql;

-- Function to complete layer classification
CREATE OR REPLACE FUNCTION complete_layer_classification(
    p_memory_id UUID,
    p_layer memory_layer_type,
    p_content_json JSONB,
    p_model_name VARCHAR(100),
    p_duration_ms INTEGER,
    p_confidence DECIMAL(4,3) DEFAULT NULL
)
RETURNS UUID
SECURITY DEFINER
SET search_path = public
AS $$
DECLARE
    v_layer_id UUID;
    v_tenant_id UUID;
    v_content_hash CHAR(64);
BEGIN
    -- Get tenant_id from memory
    SELECT tenant_id INTO v_tenant_id FROM memories WHERE id = p_memory_id;

    -- Compute content hash (use convert_to for proper UTF-8 encoding, not ::bytea which fails on escape chars)
    v_content_hash := encode(sha256(convert_to(p_content_json::text, 'UTF8')), 'hex');

    -- Mark previous layer as not current (superseded_by will be set after insert)
    UPDATE memory_layers
    SET is_current = FALSE
    WHERE memory_id = p_memory_id AND layer = p_layer AND is_current = TRUE;

    -- Insert new layer
    INSERT INTO memory_layers (memory_id, tenant_id, layer, content_json, content_hash, model_name,
                                processing_duration_ms, confidence_score)
    VALUES (p_memory_id, v_tenant_id, p_layer, p_content_json, v_content_hash, p_model_name,
            p_duration_ms, p_confidence)
    RETURNING id INTO v_layer_id;

    -- Update the superseded_by to point to new layer
    UPDATE memory_layers SET superseded_by = v_layer_id
    WHERE memory_id = p_memory_id AND layer = p_layer AND id != v_layer_id AND is_current = FALSE AND superseded_by IS NULL;

    -- Update memory current_layer (cast text to enum)
    UPDATE memories
    SET current_layer = p_layer,
        classification_status = CASE WHEN p_layer = 'L4_HEURISTIC' THEN 'COMPLETE'::memory_processing_status ELSE 'PROCESSING'::memory_processing_status END,
        classification_completed_at = CASE WHEN p_layer = 'L4_HEURISTIC' THEN NOW() ELSE NULL END
    WHERE id = p_memory_id;

    -- Log event
    INSERT INTO memory_classification_events (memory_id, tenant_id, event_type, layer, duration_ms,
                                               details)
    VALUES (p_memory_id, v_tenant_id, 'LAYER_COMPLETED', p_layer, p_duration_ms,
            jsonb_build_object('model', p_model_name, 'confidence', p_confidence));

    -- Update queue (cast text to enum)
    UPDATE memory_processing_queue
    SET current_stage = p_layer,
        status = CASE WHEN p_layer = 'L4_HEURISTIC' THEN 'COMPLETE'::memory_processing_status ELSE 'PROCESSING'::memory_processing_status END,
        completed_at = CASE WHEN p_layer = 'L4_HEURISTIC' THEN NOW() ELSE NULL END
    WHERE memory_id = p_memory_id;

    RETURN v_layer_id;
END;
$$ LANGUAGE plpgsql;

-- Function to mark classification as failed
CREATE OR REPLACE FUNCTION fail_layer_classification(
    p_memory_id UUID,
    p_layer memory_layer_type,
    p_error_message TEXT
)
RETURNS VOID
SECURITY DEFINER
SET search_path = public
AS $$
DECLARE
    v_tenant_id UUID;
    v_retry_count INTEGER;
    v_max_retries INTEGER;
BEGIN
    -- Get tenant_id and retry info
    SELECT m.tenant_id, q.retry_count, q.max_retries
    INTO v_tenant_id, v_retry_count, v_max_retries
    FROM memories m
    JOIN memory_processing_queue q ON q.memory_id = m.id
    WHERE m.id = p_memory_id;

    -- Log error event
    INSERT INTO memory_classification_events (memory_id, tenant_id, event_type, layer, error_message)
    VALUES (p_memory_id, v_tenant_id, 'LAYER_FAILED', p_layer, p_error_message);

    -- Update queue
    IF v_retry_count + 1 >= v_max_retries THEN
        UPDATE memory_processing_queue
        SET status = 'FAILED',
            retry_count = retry_count + 1,
            last_error = p_error_message,
            last_error_at = NOW(),
            locked_by = NULL,
            locked_until = NULL
        WHERE memory_id = p_memory_id;

        UPDATE memories SET classification_status = 'FAILED' WHERE id = p_memory_id;
    ELSE
        UPDATE memory_processing_queue
        SET status = 'PENDING',
            retry_count = retry_count + 1,
            last_error = p_error_message,
            last_error_at = NOW(),
            locked_by = NULL,
            locked_until = NULL
        WHERE memory_id = p_memory_id;
    END IF;
END;
$$ LANGUAGE plpgsql;

-- Function to create timeline snapshot
CREATE OR REPLACE FUNCTION create_timeline_snapshot(
    p_tenant_id UUID,
    p_memory_id UUID,
    p_event_id UUID,
    p_event_type VARCHAR(100),
    p_content TEXT,
    p_layer VARCHAR(20),
    p_confidence DECIMAL(4,3),
    p_metadata JSONB DEFAULT NULL
)
RETURNS UUID
SECURITY DEFINER
SET search_path = public
AS $$
DECLARE
    v_snapshot_id UUID;
BEGIN
    INSERT INTO timeline_snapshots (tenant_id, memory_id, event_id, event_type, content, layer, confidence, metadata)
    VALUES (p_tenant_id, p_memory_id, p_event_id, p_event_type, p_content, p_layer, p_confidence, COALESCE(p_metadata, '{}'))
    RETURNING id INTO v_snapshot_id;

    RETURN v_snapshot_id;
END;
$$ LANGUAGE plpgsql;

-- =============================================================================
-- STEP 12: Auto-enqueue trigger for new memories
-- =============================================================================

-- Trigger function to automatically enqueue new memories for classification
CREATE OR REPLACE FUNCTION trigger_enqueue_memory_for_classification()
RETURNS TRIGGER
SECURITY DEFINER
SET search_path = public
AS $$
BEGIN
    -- Only enqueue if classification is enabled (status is PENDING)
    IF NEW.classification_status = 'PENDING' THEN
        INSERT INTO memory_processing_queue (memory_id, tenant_id, priority)
        VALUES (NEW.id, NEW.tenant_id, 0)
        ON CONFLICT (memory_id) DO NOTHING;
    END IF;

    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

-- Create trigger on memories table
DROP TRIGGER IF EXISTS auto_enqueue_memory_classification ON memories;
CREATE TRIGGER auto_enqueue_memory_classification
    AFTER INSERT ON memories
    FOR EACH ROW
    EXECUTE FUNCTION trigger_enqueue_memory_for_classification();

COMMIT;

-- =============================================================================
-- STEP 13: Grant permissions to application user
-- =============================================================================

-- Grant permissions on new tables to contextdb_app user
GRANT SELECT, INSERT, UPDATE, DELETE ON memory_layers TO contextdb_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON memory_processing_queue TO contextdb_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON knowledge_graph_nodes TO contextdb_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON memory_classification_events TO contextdb_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON timeline_snapshots TO contextdb_app;

-- Grant execute on functions
GRANT EXECUTE ON FUNCTION enqueue_memory_for_classification(UUID, UUID, INTEGER) TO contextdb_app;
GRANT EXECUTE ON FUNCTION acquire_classification_batch(VARCHAR, INTEGER, INTERVAL) TO contextdb_app;
GRANT EXECUTE ON FUNCTION complete_layer_classification(UUID, memory_layer_type, JSONB, VARCHAR, INTEGER, DECIMAL) TO contextdb_app;
GRANT EXECUTE ON FUNCTION fail_layer_classification(UUID, memory_layer_type, TEXT) TO contextdb_app;
GRANT EXECUTE ON FUNCTION create_timeline_snapshot(UUID, UUID, UUID, VARCHAR, TEXT, VARCHAR, DECIMAL, JSONB) TO contextdb_app;
GRANT EXECUTE ON FUNCTION trigger_enqueue_memory_for_classification() TO contextdb_app;

-- =============================================================================
-- VERIFICATION
-- =============================================================================

DO $$
BEGIN
    RAISE NOTICE '===============================================';
    RAISE NOTICE 'Memory Classification Schema Applied Successfully';
    RAISE NOTICE '===============================================';
    RAISE NOTICE 'Tables created:';
    RAISE NOTICE '  - memory_layers';
    RAISE NOTICE '  - memory_processing_queue';
    RAISE NOTICE '  - knowledge_graph_nodes';
    RAISE NOTICE '  - memory_classification_events';
    RAISE NOTICE '  - timeline_snapshots';
    RAISE NOTICE '';
    RAISE NOTICE 'Functions created:';
    RAISE NOTICE '  - enqueue_memory_for_classification()';
    RAISE NOTICE '  - acquire_classification_batch()';
    RAISE NOTICE '  - complete_layer_classification()';
    RAISE NOTICE '  - fail_layer_classification()';
    RAISE NOTICE '  - create_timeline_snapshot()';
    RAISE NOTICE '';
    RAISE NOTICE 'Triggers created:';
    RAISE NOTICE '  - auto_enqueue_memory_classification (on memories INSERT)';
    RAISE NOTICE '';
    RAISE NOTICE 'RLS policies applied with internal_admin bypass';
    RAISE NOTICE '===============================================';
END $$;
