-- ============================================================================
-- Fix graph_events table schema mismatch
-- The table was created with 'id' and 'created_utc' but code expects 'event_id' and 'occurred_at'
-- Also adds missing columns needed by PostgresGraphEventStore.LogEventAsync
-- ============================================================================

BEGIN;

-- Check if the table exists but has wrong column names
DO $$
BEGIN
    -- If table has 'id' column but not 'event_id', rename it
    IF EXISTS (SELECT 1 FROM information_schema.columns
               WHERE table_name = 'graph_events' AND column_name = 'id')
       AND NOT EXISTS (SELECT 1 FROM information_schema.columns
                       WHERE table_name = 'graph_events' AND column_name = 'event_id') THEN
        ALTER TABLE graph_events RENAME COLUMN id TO event_id;
        RAISE NOTICE 'Renamed id to event_id';
    END IF;

    -- If table has 'created_utc' column but not 'occurred_at', rename it
    IF EXISTS (SELECT 1 FROM information_schema.columns
               WHERE table_name = 'graph_events' AND column_name = 'created_utc')
       AND NOT EXISTS (SELECT 1 FROM information_schema.columns
                       WHERE table_name = 'graph_events' AND column_name = 'occurred_at') THEN
        ALTER TABLE graph_events RENAME COLUMN created_utc TO occurred_at;
        RAISE NOTICE 'Renamed created_utc to occurred_at';
    END IF;
END $$;

-- Add missing columns if they don't exist
DO $$
BEGIN
    -- node_name
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                   WHERE table_name = 'graph_events' AND column_name = 'node_name') THEN
        ALTER TABLE graph_events ADD COLUMN node_name VARCHAR(500);
        RAISE NOTICE 'Added node_name column';
    END IF;

    -- source_node_id
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                   WHERE table_name = 'graph_events' AND column_name = 'source_node_id') THEN
        ALTER TABLE graph_events ADD COLUMN source_node_id UUID;
        RAISE NOTICE 'Added source_node_id column';
    END IF;

    -- target_node_id
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                   WHERE table_name = 'graph_events' AND column_name = 'target_node_id') THEN
        ALTER TABLE graph_events ADD COLUMN target_node_id UUID;
        RAISE NOTICE 'Added target_node_id column';
    END IF;

    -- source_node_name
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                   WHERE table_name = 'graph_events' AND column_name = 'source_node_name') THEN
        ALTER TABLE graph_events ADD COLUMN source_node_name VARCHAR(500);
        RAISE NOTICE 'Added source_node_name column';
    END IF;

    -- target_node_name
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                   WHERE table_name = 'graph_events' AND column_name = 'target_node_name') THEN
        ALTER TABLE graph_events ADD COLUMN target_node_name VARCHAR(500);
        RAISE NOTICE 'Added target_node_name column';
    END IF;

    -- previous_state
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                   WHERE table_name = 'graph_events' AND column_name = 'previous_state') THEN
        ALTER TABLE graph_events ADD COLUMN previous_state JSONB;
        RAISE NOTICE 'Added previous_state column';
    END IF;

    -- new_state
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                   WHERE table_name = 'graph_events' AND column_name = 'new_state') THEN
        ALTER TABLE graph_events ADD COLUMN new_state JSONB;
        RAISE NOTICE 'Added new_state column';
    END IF;

    -- confidence
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                   WHERE table_name = 'graph_events' AND column_name = 'confidence') THEN
        ALTER TABLE graph_events ADD COLUMN confidence DECIMAL(4,3);
        RAISE NOTICE 'Added confidence column';
    END IF;

    -- triggered_by
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                   WHERE table_name = 'graph_events' AND column_name = 'triggered_by') THEN
        ALTER TABLE graph_events ADD COLUMN triggered_by VARCHAR(255) DEFAULT 'system';
        RAISE NOTICE 'Added triggered_by column';
    END IF;

    -- memory_id
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                   WHERE table_name = 'graph_events' AND column_name = 'memory_id') THEN
        ALTER TABLE graph_events ADD COLUMN memory_id UUID;
        RAISE NOTICE 'Added memory_id column';
    END IF;

    -- session_id
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                   WHERE table_name = 'graph_events' AND column_name = 'session_id') THEN
        ALTER TABLE graph_events ADD COLUMN session_id UUID;
        RAISE NOTICE 'Added session_id column';
    END IF;

    -- occurred_at (in case table was created without it and we didn't rename)
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                   WHERE table_name = 'graph_events' AND column_name = 'occurred_at') THEN
        ALTER TABLE graph_events ADD COLUMN occurred_at TIMESTAMPTZ NOT NULL DEFAULT NOW();
        RAISE NOTICE 'Added occurred_at column';
    END IF;
END $$;

-- Create the graph_event_type enum if it doesn't exist
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_type WHERE typname = 'graph_event_type') THEN
        CREATE TYPE graph_event_type AS ENUM (
            'NodeCreated',
            'NodeUpdated',
            'NodeDeleted',
            'EdgeCreated',
            'EdgeUpdated',
            'EdgeDeleted',
            'NodeMerged',
            'EdgeStrengthened'
        );
        RAISE NOTICE 'Created graph_event_type enum';
    END IF;
END $$;

-- Update event_type column to use enum if it's currently text
-- First check what type it is
DO $$
DECLARE
    col_type TEXT;
BEGIN
    SELECT data_type INTO col_type
    FROM information_schema.columns
    WHERE table_name = 'graph_events' AND column_name = 'event_type';

    IF col_type = 'character varying' OR col_type = 'text' THEN
        -- Need to convert - but be careful with existing data
        RAISE NOTICE 'event_type is currently %, may need manual conversion to enum', col_type;
    ELSIF col_type = 'USER-DEFINED' THEN
        RAISE NOTICE 'event_type is already an enum type';
    END IF;
END $$;

COMMIT;

-- Verification
SELECT
    column_name,
    data_type,
    is_nullable
FROM information_schema.columns
WHERE table_name = 'graph_events'
ORDER BY ordinal_position;
