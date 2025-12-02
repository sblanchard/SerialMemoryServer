-- ============================================================================
-- Fix missing columns on memories table required by trigger_memory_event()
-- Run this migration to fix: record 'new' has no field 'is_active'
-- ============================================================================

BEGIN;

-- Add is_active column if it doesn't exist
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                   WHERE table_name = 'memories' AND column_name = 'is_active') THEN
        ALTER TABLE memories ADD COLUMN is_active BOOLEAN NOT NULL DEFAULT TRUE;
        CREATE INDEX IF NOT EXISTS idx_memories_active ON memories(is_active) WHERE is_active = TRUE;
        RAISE NOTICE 'Added is_active column to memories table';
    END IF;
END $$;

-- Add merged_into column if it doesn't exist
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                   WHERE table_name = 'memories' AND column_name = 'merged_into') THEN
        ALTER TABLE memories ADD COLUMN merged_into UUID REFERENCES memories(id);
        RAISE NOTICE 'Added merged_into column to memories table';
    END IF;
END $$;

-- Add confidence_score column if it doesn't exist
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                   WHERE table_name = 'memories' AND column_name = 'confidence_score') THEN
        ALTER TABLE memories ADD COLUMN confidence_score DECIMAL(4,3) NOT NULL DEFAULT 1.000
            CHECK (confidence_score >= 0 AND confidence_score <= 1);
        RAISE NOTICE 'Added confidence_score column to memories table';
    END IF;
END $$;

-- Add layer column if it doesn't exist (may already exist from billing_v2_memory_layer_schema.sql)
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                   WHERE table_name = 'memories' AND column_name = 'layer') THEN
        ALTER TABLE memories ADD COLUMN layer VARCHAR(20) DEFAULT 'L0_RAW'
            CHECK (layer IN ('L0_RAW', 'L1_CONTEXT', 'L2_SUMMARY', 'L3_KNOWLEDGE', 'L4_HEURISTIC'));
        CREATE INDEX IF NOT EXISTS idx_memories_layer ON memories(tenant_id, layer);
        RAISE NOTICE 'Added layer column to memories table';
    END IF;
END $$;

-- Verify trigger function still exists and is valid
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_proc WHERE proname = 'trigger_memory_event') THEN
        RAISE NOTICE 'trigger_memory_event() function exists';
    ELSE
        RAISE WARNING 'trigger_memory_event() function does not exist - run reasoning_schema.sql';
    END IF;
END $$;

COMMIT;

-- Verification query
SELECT
    column_name,
    data_type,
    column_default,
    is_nullable
FROM information_schema.columns
WHERE table_name = 'memories'
  AND column_name IN ('is_active', 'merged_into', 'confidence_score', 'layer')
ORDER BY column_name;
