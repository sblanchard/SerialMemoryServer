-- Migration: Expand memory_event_type enum with additional event types
-- Required for new MCP lifecycle, observability, and safety tools

-- Add new event types to enum (PostgreSQL allows adding values, not removing)
DO $$
BEGIN
    -- Add archived event type
    IF NOT EXISTS (SELECT 1 FROM pg_enum WHERE enumlabel = 'MemoryArchived' AND enumtypid = 'memory_event_type'::regtype) THEN
        ALTER TYPE memory_event_type ADD VALUE 'MemoryArchived';
    END IF;

    -- Add recalled event type
    IF NOT EXISTS (SELECT 1 FROM pg_enum WHERE enumlabel = 'MemoryRecalled' AND enumtypid = 'memory_event_type'::regtype) THEN
        ALTER TYPE memory_event_type ADD VALUE 'MemoryRecalled';
    END IF;

    -- Add ignored event type
    IF NOT EXISTS (SELECT 1 FROM pg_enum WHERE enumlabel = 'MemoryIgnored' AND enumtypid = 'memory_event_type'::regtype) THEN
        ALTER TYPE memory_event_type ADD VALUE 'MemoryIgnored';
    END IF;

    -- Add contradicted event type
    IF NOT EXISTS (SELECT 1 FROM pg_enum WHERE enumlabel = 'MemoryContradicted' AND enumtypid = 'memory_event_type'::regtype) THEN
        ALTER TYPE memory_event_type ADD VALUE 'MemoryContradicted';
    END IF;

    -- Add expired event type
    IF NOT EXISTS (SELECT 1 FROM pg_enum WHERE enumlabel = 'MemoryExpired' AND enumtypid = 'memory_event_type'::regtype) THEN
        ALTER TYPE memory_event_type ADD VALUE 'MemoryExpired';
    END IF;

    -- Add split event type
    IF NOT EXISTS (SELECT 1 FROM pg_enum WHERE enumlabel = 'MemorySplit' AND enumtypid = 'memory_event_type'::regtype) THEN
        ALTER TYPE memory_event_type ADD VALUE 'MemorySplit';
    END IF;

    -- Safety events
    IF NOT EXISTS (SELECT 1 FROM pg_enum WHERE enumlabel = 'ContradictionDetected' AND enumtypid = 'memory_event_type'::regtype) THEN
        ALTER TYPE memory_event_type ADD VALUE 'ContradictionDetected';
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_enum WHERE enumlabel = 'HallucinationFlagged' AND enumtypid = 'memory_event_type'::regtype) THEN
        ALTER TYPE memory_event_type ADD VALUE 'HallucinationFlagged';
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_enum WHERE enumlabel = 'IntegrityCheckFailed' AND enumtypid = 'memory_event_type'::regtype) THEN
        ALTER TYPE memory_event_type ADD VALUE 'IntegrityCheckFailed';
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_enum WHERE enumlabel = 'ExportCompleted' AND enumtypid = 'memory_event_type'::regtype) THEN
        ALTER TYPE memory_event_type ADD VALUE 'ExportCompleted';
    END IF;
END$$;

-- Add new columns to memory_projections for extended lifecycle
ALTER TABLE memory_projections ADD COLUMN IF NOT EXISTS is_expired BOOLEAN DEFAULT FALSE;
ALTER TABLE memory_projections ADD COLUMN IF NOT EXISTS is_split BOOLEAN DEFAULT FALSE;
ALTER TABLE memory_projections ADD COLUMN IF NOT EXISTS child_memory_ids UUID[] DEFAULT '{}';
ALTER TABLE memory_projections ADD COLUMN IF NOT EXISTS recall_count INTEGER DEFAULT 0;
ALTER TABLE memory_projections ADD COLUMN IF NOT EXISTS ignore_count INTEGER DEFAULT 0;
ALTER TABLE memory_projections ADD COLUMN IF NOT EXISTS last_recalled_at TIMESTAMPTZ;
ALTER TABLE memory_projections ADD COLUMN IF NOT EXISTS ttl_days INTEGER;
ALTER TABLE memory_projections ADD COLUMN IF NOT EXISTS expires_at TIMESTAMPTZ;

-- Create indexes for new columns
CREATE INDEX IF NOT EXISTS idx_memory_proj_expired ON memory_projections (is_expired) WHERE is_expired = TRUE;
CREATE INDEX IF NOT EXISTS idx_memory_proj_split ON memory_projections (is_split) WHERE is_split = TRUE;
CREATE INDEX IF NOT EXISTS idx_memory_proj_expires ON memory_projections (expires_at) WHERE expires_at IS NOT NULL;

-- Hallucination tracking table
CREATE TABLE IF NOT EXISTS hallucination_reports (
    report_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    memory_id UUID NOT NULL,
    detection_method VARCHAR(100) NOT NULL,
    confidence_score DECIMAL(4,3) NOT NULL,
    flagged_content TEXT,
    reason TEXT,
    resolution_status VARCHAR(20) DEFAULT 'detected',
    resolved_by VARCHAR(255),
    resolved_at TIMESTAMPTZ,
    created_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_hallucination_memory ON hallucination_reports (memory_id);
CREATE INDEX IF NOT EXISTS idx_hallucination_status ON hallucination_reports (resolution_status);

-- Integrity check results table
CREATE TABLE IF NOT EXISTS integrity_check_results (
    check_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    memory_id UUID NOT NULL,
    check_type VARCHAR(50) NOT NULL,
    passed BOOLEAN NOT NULL,
    expected_hash CHAR(64),
    actual_hash CHAR(64),
    details JSONB DEFAULT '{}',
    created_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_integrity_memory ON integrity_check_results (memory_id);
CREATE INDEX IF NOT EXISTS idx_integrity_failed ON integrity_check_results (passed) WHERE passed = FALSE;

-- Loop detection table (for causal parent cycles)
CREATE TABLE IF NOT EXISTS loop_detection_results (
    detection_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    cycle_memory_ids UUID[] NOT NULL,
    cycle_length INTEGER NOT NULL,
    detection_method VARCHAR(50) NOT NULL,
    severity VARCHAR(20) DEFAULT 'warning',
    resolved BOOLEAN DEFAULT FALSE,
    resolved_at TIMESTAMPTZ,
    created_at TIMESTAMPTZ DEFAULT NOW()
);

-- Memory trace table (for lineage tracking)
CREATE TABLE IF NOT EXISTS memory_traces (
    trace_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    memory_id UUID NOT NULL,
    event_sequence BIGINT[] NOT NULL,
    event_types TEXT[] NOT NULL,
    trace_depth INTEGER NOT NULL,
    root_memory_id UUID,
    created_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_trace_memory ON memory_traces (memory_id);
CREATE INDEX IF NOT EXISTS idx_trace_root ON memory_traces (root_memory_id);

-- Update entity_projections to add mention_count if not exists
ALTER TABLE entity_projections ADD COLUMN IF NOT EXISTS mention_count INTEGER DEFAULT 0;

-- Update relationship_projections to add strength if not exists
ALTER TABLE entity_relationship_projections ADD COLUMN IF NOT EXISTS strength DECIMAL(4,3) DEFAULT 1.000;

COMMENT ON TABLE hallucination_reports IS 'Tracks potential hallucinations detected in memories';
COMMENT ON TABLE integrity_check_results IS 'Results of content hash integrity verification';
COMMENT ON TABLE loop_detection_results IS 'Detected cycles in causal parent relationships';
COMMENT ON TABLE memory_traces IS 'Cached lineage traces for memory ancestry';
