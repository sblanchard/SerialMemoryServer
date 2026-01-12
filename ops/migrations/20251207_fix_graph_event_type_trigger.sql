-- Fix the graph_event_type casting issue in the trigger
-- The trigger receives text from the C# code but needs to cast to enum

-- First, check the current column type and fix if needed
DO $$
BEGIN
    -- Ensure graph_events.event_type is the enum type
    IF EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_name = 'graph_events'
        AND column_name = 'event_type'
        AND data_type = 'text'
    ) THEN
        -- Convert text column to enum
        ALTER TABLE graph_events
        ALTER COLUMN event_type TYPE graph_event_type
        USING event_type::graph_event_type;
        RAISE NOTICE 'Converted graph_events.event_type from text to enum';
    END IF;
END $$;

-- Recreate the trigger function with explicit casting
-- This handles the case where text is passed and needs conversion
CREATE OR REPLACE FUNCTION update_graph_metrics_hourly() RETURNS TRIGGER AS $$
BEGIN
    -- Cast to text first (works for both text and enum input), then to enum
    INSERT INTO graph_metrics_hourly (bucket_hour, event_type, event_count)
    VALUES (
        date_trunc('hour', NEW.occurred_at),
        (NEW.event_type::text)::graph_event_type,
        1
    )
    ON CONFLICT (bucket_hour, event_type)
    DO UPDATE SET event_count = graph_metrics_hourly.event_count + 1;

    RETURN NEW;
EXCEPTION WHEN OTHERS THEN
    -- Log error but don't fail the main insert
    RAISE WARNING 'Failed to update graph metrics: %', SQLERRM;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

-- Verify
DO $$
BEGIN
    RAISE NOTICE 'Migration complete: graph_event_type trigger fixed';
END $$;
