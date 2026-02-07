-- Migration: Fix usage_events schema to match ToolAnalyticsEndpoints expectations
-- This migration adds/renames columns to match the newer schema from migrate_credits.sql

-- Step 1: Add tool_name column if it doesn't exist
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_name = 'usage_events' AND column_name = 'tool_name'
    ) THEN
        -- If event_type exists, copy it to tool_name, otherwise create empty column
        IF EXISTS (
            SELECT 1 FROM information_schema.columns
            WHERE table_name = 'usage_events' AND column_name = 'event_type'
        ) THEN
            ALTER TABLE usage_events ADD COLUMN tool_name TEXT;
            UPDATE usage_events SET tool_name = event_type::TEXT;
            ALTER TABLE usage_events ALTER COLUMN tool_name SET NOT NULL;
        ELSE
            ALTER TABLE usage_events ADD COLUMN tool_name TEXT NOT NULL DEFAULT '';
        END IF;
    END IF;
END $$;

-- Step 2: Add credits_used column if it doesn't exist
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_name = 'usage_events' AND column_name = 'credits_used'
    ) THEN
        -- If credits_consumed exists, copy it to credits_used, otherwise create with default
        IF EXISTS (
            SELECT 1 FROM information_schema.columns
            WHERE table_name = 'usage_events' AND column_name = 'credits_consumed'
        ) THEN
            ALTER TABLE usage_events ADD COLUMN credits_used NUMERIC(8,4);
            UPDATE usage_events SET credits_used = credits_consumed;
            ALTER TABLE usage_events ALTER COLUMN credits_used SET NOT NULL;
        ELSE
            ALTER TABLE usage_events ADD COLUMN credits_used NUMERIC(8,4) NOT NULL DEFAULT 0;
        END IF;
    END IF;
END $$;

-- Step 3: Add created_at column if it doesn't exist
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_name = 'usage_events' AND column_name = 'created_at'
    ) THEN
        -- If event_timestamp exists, copy it to created_at, otherwise use NOW()
        IF EXISTS (
            SELECT 1 FROM information_schema.columns
            WHERE table_name = 'usage_events' AND column_name = 'event_timestamp'
        ) THEN
            ALTER TABLE usage_events ADD COLUMN created_at TIMESTAMPTZ;
            UPDATE usage_events SET created_at = event_timestamp;
            ALTER TABLE usage_events ALTER COLUMN created_at SET NOT NULL;
            ALTER TABLE usage_events ALTER COLUMN created_at SET DEFAULT NOW();
        ELSE
            ALTER TABLE usage_events ADD COLUMN created_at TIMESTAMPTZ NOT NULL DEFAULT NOW();
        END IF;
    END IF;
END $$;

-- Step 4: Add missing columns that the API expects
-- Add 'success' column if missing
ALTER TABLE usage_events ADD COLUMN IF NOT EXISTS success BOOLEAN NOT NULL DEFAULT TRUE;

-- Add 'metadata' column if missing (should already exist in both schemas)
ALTER TABLE usage_events ADD COLUMN IF NOT EXISTS metadata JSONB;

-- Step 5: Create indexes for tool_name and created_at if they don't exist
CREATE INDEX IF NOT EXISTS idx_usage_events_tool_name ON usage_events(tool_name, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_usage_events_created_at ON usage_events(created_at DESC);

-- Step 6: Fix tenant_id type mismatch (TEXT vs UUID)
-- The API passes Guid which PostgreSQL sees as UUID, but column is TEXT
DO $$
BEGIN
    -- Check if tenant_id is TEXT (old schema)
    IF EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_name = 'usage_events'
        AND column_name = 'tenant_id'
        AND data_type = 'text'
    ) THEN
        -- Recreate RLS policy to handle TEXT comparison
        DROP POLICY IF EXISTS tenant_isolation_usage_events ON usage_events;

        CREATE POLICY tenant_isolation_usage_events ON usage_events
            FOR ALL
            USING (tenant_id = current_setting('app.tenant_id', true))
            WITH CHECK (tenant_id = current_setting('app.tenant_id', true));
    END IF;
END $$;

-- Step 7: Ensure workspace_id exists (used by some queries)
ALTER TABLE usage_events ADD COLUMN IF NOT EXISTS workspace_id TEXT NOT NULL DEFAULT 'default';

-- Step 8: Ensure billing_cycle_id exists (for future compatibility)
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_name = 'usage_events' AND column_name = 'billing_cycle_id'
    ) THEN
        ALTER TABLE usage_events ADD COLUMN billing_cycle_id UUID;
    END IF;
END $$;

COMMENT ON TABLE usage_events IS 'MCP tool usage events for analytics and metering (migrated schema)';
COMMENT ON COLUMN usage_events.tool_name IS 'Name of the MCP tool that was called';
COMMENT ON COLUMN usage_events.credits_used IS 'Credits consumed by this operation';
COMMENT ON COLUMN usage_events.created_at IS 'Timestamp when the event was recorded';
COMMENT ON COLUMN usage_events.tenant_id IS 'Tenant identifier (TEXT format)';
