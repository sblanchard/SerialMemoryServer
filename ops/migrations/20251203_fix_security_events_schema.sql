-- =============================================================================
-- FIX SECURITY EVENTS SCHEMA MIGRATION
-- Addresses missing 'category' column and schema inconsistencies
-- =============================================================================

BEGIN;

-- =============================================================================
-- STEP 0: Create security_events table if it doesn't exist
-- =============================================================================

CREATE TABLE IF NOT EXISTS security_events (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    event_type TEXT NOT NULL DEFAULT 'general',
    category TEXT NOT NULL DEFAULT 'general',
    severity TEXT NOT NULL DEFAULT 'info',
    message TEXT NOT NULL DEFAULT '',
    metadata JSONB DEFAULT '{}',
    details JSONB DEFAULT '{}',
    tenant_id UUID,
    user_id UUID,
    ip_address INET,
    memory_id UUID,
    entity_id UUID,
    target_id UUID,
    target_type TEXT,
    status TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_security_events_type ON security_events(event_type);
CREATE INDEX IF NOT EXISTS idx_security_events_category ON security_events(category);
CREATE INDEX IF NOT EXISTS idx_security_events_severity ON security_events(severity);
CREATE INDEX IF NOT EXISTS idx_security_events_created_at ON security_events(created_at DESC);
CREATE INDEX IF NOT EXISTS idx_security_events_tenant ON security_events(tenant_id);

-- =============================================================================
-- STEP 1: Add missing columns to security_events if they don't exist
-- The self_healing_schema.sql expected: id, event_type, category, severity, message, metadata
-- The security_events_schema.sql has: event_id (no category, different PK)
-- =============================================================================

DO $$
BEGIN
    -- Add 'id' column if only 'event_id' exists (alias for compatibility)
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'security_events' AND column_name = 'id') THEN
        IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'security_events' AND column_name = 'event_id') THEN
            -- Add id as a generated column pointing to event_id
            ALTER TABLE security_events ADD COLUMN id UUID GENERATED ALWAYS AS (event_id) STORED;
        ELSE
            -- Create id column as primary key
            ALTER TABLE security_events ADD COLUMN id UUID DEFAULT gen_random_uuid();
        END IF;
        RAISE NOTICE 'Added id column to security_events';
    END IF;

    -- Add 'category' column if missing
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'security_events' AND column_name = 'category') THEN
        ALTER TABLE security_events ADD COLUMN category TEXT DEFAULT 'general';
        RAISE NOTICE 'Added category column to security_events';
    END IF;

    -- Add 'metadata' column if missing (some schemas use 'details' instead)
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'security_events' AND column_name = 'metadata') THEN
        IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'security_events' AND column_name = 'details') THEN
            -- Create metadata as alias/copy of details
            ALTER TABLE security_events ADD COLUMN metadata JSONB DEFAULT '{}';
            UPDATE security_events SET metadata = COALESCE(details, '{}'::jsonb) WHERE metadata IS NULL OR metadata = '{}'::jsonb;
            RAISE NOTICE 'Added metadata column, copied from details';
        ELSE
            ALTER TABLE security_events ADD COLUMN metadata JSONB DEFAULT '{}';
            RAISE NOTICE 'Added metadata column to security_events';
        END IF;
    END IF;

    -- Add 'message' column if missing
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'security_events' AND column_name = 'message') THEN
        ALTER TABLE security_events ADD COLUMN message TEXT DEFAULT '';
        RAISE NOTICE 'Added message column to security_events';
    END IF;

    -- Add 'created_at' column if missing
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'security_events' AND column_name = 'created_at') THEN
        IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'security_events' AND column_name = 'created_utc') THEN
            -- Rename created_utc to created_at
            ALTER TABLE security_events RENAME COLUMN created_utc TO created_at;
            RAISE NOTICE 'Renamed created_utc to created_at';
        ELSIF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'security_events' AND column_name = 'occurred_at') THEN
            -- Add created_at copying from occurred_at
            ALTER TABLE security_events ADD COLUMN created_at TIMESTAMPTZ DEFAULT NOW();
            UPDATE security_events SET created_at = occurred_at WHERE created_at IS NULL;
            RAISE NOTICE 'Added created_at column, copied from occurred_at';
        ELSE
            ALTER TABLE security_events ADD COLUMN created_at TIMESTAMPTZ DEFAULT NOW();
            RAISE NOTICE 'Added created_at column to security_events';
        END IF;
    END IF;

    -- Ensure severity is TEXT (not enum) for compatibility with code
    -- Some schemas use security_severity enum, code expects TEXT
    BEGIN
        ALTER TABLE security_events ALTER COLUMN severity TYPE TEXT USING severity::TEXT;
        RAISE NOTICE 'Converted severity to TEXT type';
    EXCEPTION WHEN OTHERS THEN
        -- Already TEXT or conversion not needed
        NULL;
    END;

    -- Ensure event_type is TEXT (not enum) for compatibility with code
    BEGIN
        ALTER TABLE security_events ALTER COLUMN event_type TYPE TEXT USING event_type::TEXT;
        RAISE NOTICE 'Converted event_type to TEXT type';
    EXCEPTION WHEN OTHERS THEN
        -- Already TEXT or conversion not needed
        NULL;
    END;
END $$;

-- =============================================================================
-- STEP 2: Create indexes for performance
-- =============================================================================

CREATE INDEX IF NOT EXISTS idx_security_events_category ON security_events(category);
CREATE INDEX IF NOT EXISTS idx_security_events_created_at ON security_events(created_at DESC);

-- =============================================================================
-- STEP 3: Relax constraints that might prevent inserts
-- =============================================================================

DO $$
BEGIN
    -- Drop CHECK constraints on event_type if they exist (telemetry_schema has strict CHECK)
    IF EXISTS (
        SELECT 1 FROM information_schema.table_constraints
        WHERE table_name = 'security_events'
        AND constraint_type = 'CHECK'
        AND constraint_name LIKE '%event_type%'
    ) THEN
        -- Get the constraint name and drop it
        EXECUTE (
            SELECT 'ALTER TABLE security_events DROP CONSTRAINT ' || constraint_name
            FROM information_schema.table_constraints
            WHERE table_name = 'security_events'
            AND constraint_type = 'CHECK'
            AND constraint_name LIKE '%event_type%'
            LIMIT 1
        );
        RAISE NOTICE 'Dropped restrictive CHECK constraint on event_type';
    END IF;

    -- Drop CHECK constraints on status if they exist
    IF EXISTS (
        SELECT 1 FROM information_schema.table_constraints
        WHERE table_name = 'security_events'
        AND constraint_type = 'CHECK'
        AND constraint_name LIKE '%status%'
    ) THEN
        EXECUTE (
            SELECT 'ALTER TABLE security_events DROP CONSTRAINT ' || constraint_name
            FROM information_schema.table_constraints
            WHERE table_name = 'security_events'
            AND constraint_type = 'CHECK'
            AND constraint_name LIKE '%status%'
            LIMIT 1
        );
        RAISE NOTICE 'Dropped restrictive CHECK constraint on status';
    END IF;
END $$;

-- =============================================================================
-- STEP 4: Grant permissions
-- =============================================================================

GRANT SELECT, INSERT, UPDATE, DELETE ON security_events TO postgres;

-- =============================================================================
-- VERIFICATION
-- =============================================================================

DO $$
DECLARE
    v_columns TEXT[];
BEGIN
    SELECT array_agg(column_name::TEXT ORDER BY ordinal_position) INTO v_columns
    FROM information_schema.columns
    WHERE table_name = 'security_events';

    RAISE NOTICE '==========================================';
    RAISE NOTICE 'Security Events Schema Fix Complete';
    RAISE NOTICE '==========================================';
    RAISE NOTICE 'Current columns: %', v_columns;
    RAISE NOTICE '==========================================';
END $$;

COMMIT;
