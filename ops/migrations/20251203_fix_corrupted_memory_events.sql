-- =============================================================================
-- CORRUPTED MEMORY EVENTS DIAGNOSTIC
-- Analyzes events that are missing required fields (content, streamId, newContent)
--
-- NOTE: Events are immutable and cannot be modified. The C# code has been updated
-- to handle these legacy events gracefully:
--   - MemoryEvents.cs: Removed 'required' keyword, added default values
--   - MemoryProjection.cs: Skips events with empty content during replay
-- =============================================================================

DO $$
DECLARE
    v_table_exists BOOLEAN;
    v_total_events INT;
    v_corrupted_created INT;
    v_corrupted_updated INT;
    v_missing_streamid INT;
    v_missing_layer INT;
BEGIN
    -- Check if memory_events table exists
    SELECT EXISTS (
        SELECT FROM information_schema.tables
        WHERE table_schema = 'public'
        AND table_name = 'memory_events'
    ) INTO v_table_exists;

    IF NOT v_table_exists THEN
        RAISE NOTICE '========================================';
        RAISE NOTICE 'SKIPPED: memory_events table does not exist.';
        RAISE NOTICE 'Event sourcing schema has not been applied.';
        RAISE NOTICE 'This diagnostic is not needed for this database.';
        RAISE NOTICE '========================================';
        RETURN;
    END IF;

    -- ==========================================================================
    -- Analyze corrupted events
    -- ==========================================================================
    SELECT COUNT(*) INTO v_total_events FROM memory_events;

    SELECT COUNT(*) INTO v_corrupted_created
    FROM memory_events
    WHERE event_type = 'MemoryCreated'
      AND (event_data->>'content' IS NULL OR event_data->>'content' = '');

    SELECT COUNT(*) INTO v_corrupted_updated
    FROM memory_events
    WHERE event_type = 'MemoryUpdated'
      AND (event_data->>'newContent' IS NULL OR event_data->>'newContent' = '');

    SELECT COUNT(*) INTO v_missing_streamid
    FROM memory_events
    WHERE event_data->>'streamId' IS NULL;

    SELECT COUNT(*) INTO v_missing_layer
    FROM memory_events
    WHERE event_type = 'MemoryCreated'
      AND event_data->>'layer' IS NULL;

    RAISE NOTICE '========================================';
    RAISE NOTICE 'Memory Events Diagnostic Report';
    RAISE NOTICE '========================================';
    RAISE NOTICE 'Total events: %', v_total_events;
    RAISE NOTICE '';
    RAISE NOTICE 'Issues Found:';
    RAISE NOTICE '  - MemoryCreated missing content: %', v_corrupted_created;
    RAISE NOTICE '  - MemoryUpdated missing newContent: %', v_corrupted_updated;
    RAISE NOTICE '  - Events missing streamId: %', v_missing_streamid;
    RAISE NOTICE '  - MemoryCreated missing layer: %', v_missing_layer;
    RAISE NOTICE '';
    RAISE NOTICE 'Resolution:';
    RAISE NOTICE '  Events are immutable and cannot be modified.';
    RAISE NOTICE '  The C# code has been updated to handle these gracefully:';
    RAISE NOTICE '  - MemoryEvents.cs: Default values for missing fields';
    RAISE NOTICE '  - MemoryProjection.cs: Skips events with empty content';
    RAISE NOTICE '========================================';
END $$;
