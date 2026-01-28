-- =============================================================================
-- Fix: complete_layer_classification FK constraint violation
-- =============================================================================
-- The original function set superseded_by = gen_random_uuid() which violated
-- the foreign key constraint (memory_layers_superseded_by_fkey).
--
-- Fix: Set superseded_by to NULL first, then update it after the new layer
-- is inserted.
-- =============================================================================

BEGIN;

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

    -- Compute content hash (use convert_to for proper UTF-8 encoding)
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

COMMIT;

-- Verification
DO $$
BEGIN
    RAISE NOTICE 'Fixed complete_layer_classification function - FK constraint issue resolved';
END $$;
