-- Migration: demote_layers_to_l1.sql
-- Description: Demote all promoted memories back to L1_CONTEXT
-- Reason: Accidental "promote all layers" was clicked
-- Date: 2025-12-05

BEGIN;

-- =============================================================================
-- STEP 1: Update memories table current_layer to L1_CONTEXT
-- =============================================================================
UPDATE memories
SET current_layer = 'L1_CONTEXT'::memory_layer_type
WHERE current_layer IN ('L2_SUMMARY', 'L3_KNOWLEDGE', 'L4_HEURISTIC');

-- Also update the varchar layer column if it exists
UPDATE memories
SET layer = 'L1_CONTEXT'
WHERE layer IN ('L2_SUMMARY', 'L3_KNOWLEDGE', 'L4_HEURISTIC');

-- =============================================================================
-- STEP 2: Mark higher layer entries in memory_layers as not current
-- =============================================================================
UPDATE memory_layers
SET is_current = FALSE
WHERE layer IN ('L2_SUMMARY', 'L3_KNOWLEDGE', 'L4_HEURISTIC')
  AND is_current = TRUE;

-- =============================================================================
-- STEP 3: Restore L1_CONTEXT as current if it exists, otherwise keep L0
-- =============================================================================
-- First, mark L1_CONTEXT entries as current for memories that have them
UPDATE memory_layers ml
SET is_current = TRUE
WHERE ml.layer = 'L1_CONTEXT'
  AND EXISTS (
    SELECT 1 FROM memory_layers ml2
    WHERE ml2.memory_id = ml.memory_id
      AND ml2.layer IN ('L2_SUMMARY', 'L3_KNOWLEDGE', 'L4_HEURISTIC')
      AND ml2.is_current = FALSE
  )
  AND ml.id = (
    SELECT id FROM memory_layers ml3
    WHERE ml3.memory_id = ml.memory_id
      AND ml3.layer = 'L1_CONTEXT'
    ORDER BY ml3.version DESC
    LIMIT 1
  );

-- =============================================================================
-- STEP 4: Update processing queue to reset stages
-- =============================================================================
UPDATE memory_processing_queue
SET current_stage = 'L1_CONTEXT'::memory_layer_type,
    status = 'PENDING'
WHERE current_stage IN ('L2_SUMMARY', 'L3_KNOWLEDGE', 'L4_HEURISTIC');

-- =============================================================================
-- Verification queries (run these to check results)
-- =============================================================================
-- SELECT layer, COUNT(*) FROM memories GROUP BY layer;
-- SELECT layer, is_current, COUNT(*) FROM memory_layers GROUP BY layer, is_current ORDER BY layer;
-- SELECT current_stage, status, COUNT(*) FROM memory_processing_queue GROUP BY current_stage, status;

COMMIT;

-- Report results
DO $$
DECLARE
    v_memories_updated INTEGER;
    v_layers_demoted INTEGER;
BEGIN
    SELECT COUNT(*) INTO v_memories_updated FROM memories WHERE current_layer = 'L1_CONTEXT';
    SELECT COUNT(*) INTO v_layers_demoted FROM memory_layers WHERE is_current = FALSE AND layer IN ('L2_SUMMARY', 'L3_KNOWLEDGE', 'L4_HEURISTIC');

    RAISE NOTICE 'Demotion complete:';
    RAISE NOTICE '  - Memories at L1_CONTEXT: %', v_memories_updated;
    RAISE NOTICE '  - Higher layers marked inactive: %', v_layers_demoted;
END $$;
