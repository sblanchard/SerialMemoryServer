-- ============================================================================
-- Billing Pack v2 & Memory Layer Worker Schema
-- Migration: billing_v2_memory_layer_schema.sql
-- ============================================================================

-- ============================================================================
-- PART 1: BILLING PACK V2 - FLEXIBLE BILLING CYCLES
-- ============================================================================

-- Add billing interval support to tenant_plans (the plan definition table)
DO $$
BEGIN
    -- Add billing_interval column (monthly, quarterly, annual)
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_name = 'tenant_plans' AND column_name = 'billing_interval'
    ) THEN
        ALTER TABLE tenant_plans ADD COLUMN billing_interval VARCHAR(20) DEFAULT 'monthly';
    END IF;

    -- Add annual discount percentage (default 20%)
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_name = 'tenant_plans' AND column_name = 'annual_discount_percent'
    ) THEN
        ALTER TABLE tenant_plans ADD COLUMN annual_discount_percent DECIMAL(5,2) DEFAULT 20.00;
    END IF;

    -- Add quarterly discount percentage (default 10%)
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_name = 'tenant_plans' AND column_name = 'quarterly_discount_percent'
    ) THEN
        ALTER TABLE tenant_plans ADD COLUMN quarterly_discount_percent DECIMAL(5,2) DEFAULT 10.00;
    END IF;
END $$;

-- Add billing interval support to tenant_subscriptions (the subscription instance table)
DO $$
BEGIN
    -- Add billing_interval to track what interval this subscription uses
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_name = 'tenant_subscriptions' AND column_name = 'billing_interval'
    ) THEN
        ALTER TABLE tenant_subscriptions ADD COLUMN billing_interval VARCHAR(20) DEFAULT 'monthly';
    END IF;

    -- Add next_billing_date for tracking when next payment is due
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_name = 'tenant_subscriptions' AND column_name = 'next_billing_date'
    ) THEN
        ALTER TABLE tenant_subscriptions ADD COLUMN next_billing_date TIMESTAMPTZ;
    END IF;
END $$;

-- Add constraint to validate billing_interval values
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.constraint_column_usage
        WHERE constraint_name = 'valid_billing_interval_plans'
    ) THEN
        ALTER TABLE tenant_plans ADD CONSTRAINT valid_billing_interval_plans
            CHECK (billing_interval IN ('monthly', 'quarterly', 'annual'));
    END IF;
EXCEPTION WHEN duplicate_object THEN NULL;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.constraint_column_usage
        WHERE constraint_name = 'valid_billing_interval_subscriptions'
    ) THEN
        ALTER TABLE tenant_subscriptions ADD CONSTRAINT valid_billing_interval_subscriptions
            CHECK (billing_interval IN ('monthly', 'quarterly', 'annual'));
    END IF;
EXCEPTION WHEN duplicate_object THEN NULL;
END $$;

-- ============================================================================
-- PART 2: BILLING PACK V2 - USAGE FORECASTING
-- ============================================================================

-- Usage forecasts table for predictive analytics
CREATE TABLE IF NOT EXISTS usage_forecasts (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    workspace_id TEXT NOT NULL DEFAULT 'default',
    forecast_date DATE NOT NULL,
    -- Predicted values
    predicted_credits DECIMAL(15,4) NOT NULL,
    predicted_operations BIGINT,
    -- Confidence intervals
    confidence_interval_low DECIMAL(15,4),
    confidence_interval_high DECIMAL(15,4),
    confidence_level DECIMAL(3,2) DEFAULT 0.95, -- 95% confidence by default
    -- Model metadata
    model_version VARCHAR(50) NOT NULL DEFAULT 'v1.0',
    model_type VARCHAR(50) DEFAULT 'linear_regression', -- linear_regression, exponential_smoothing, arima
    training_window_days INTEGER DEFAULT 30,
    -- Accuracy tracking (updated after actual values are known)
    actual_credits DECIMAL(15,4),
    prediction_error DECIMAL(15,4),
    mape DECIMAL(8,4), -- Mean Absolute Percentage Error
    -- Timestamps
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    CONSTRAINT usage_forecasts_unique UNIQUE (tenant_id, workspace_id, forecast_date)
);

-- Indices for usage_forecasts
CREATE INDEX IF NOT EXISTS idx_usage_forecasts_tenant ON usage_forecasts(tenant_id, workspace_id);
CREATE INDEX IF NOT EXISTS idx_usage_forecasts_date ON usage_forecasts(forecast_date);
CREATE INDEX IF NOT EXISTS idx_usage_forecasts_pending_actual ON usage_forecasts(tenant_id, forecast_date)
    WHERE actual_credits IS NULL;

-- ============================================================================
-- PART 3: BILLING PACK V2 - COST RECOMMENDATIONS
-- ============================================================================

-- Cost optimization recommendations table
CREATE TABLE IF NOT EXISTS cost_recommendations (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    workspace_id TEXT NOT NULL DEFAULT 'default',
    -- Recommendation details
    recommendation_type VARCHAR(50) NOT NULL, -- plan_change, caching, batching, off_peak, feature_reduction
    category VARCHAR(50) NOT NULL DEFAULT 'general', -- cost_reduction, performance, efficiency
    title VARCHAR(200) NOT NULL,
    description TEXT NOT NULL,
    -- Impact analysis
    estimated_savings_credits DECIMAL(15,4),
    estimated_savings_percent DECIMAL(5,2),
    confidence DECIMAL(3,2) DEFAULT 0.8,
    -- Priority and status
    priority INTEGER NOT NULL DEFAULT 3 CHECK (priority BETWEEN 1 AND 5), -- 1=critical, 5=nice-to-have
    status VARCHAR(20) NOT NULL DEFAULT 'active' CHECK (status IN ('active', 'dismissed', 'implemented', 'expired')),
    -- User interaction
    is_dismissed BOOLEAN NOT NULL DEFAULT FALSE,
    dismissed_at TIMESTAMPTZ,
    dismissed_reason TEXT,
    implemented_at TIMESTAMPTZ,
    -- Supporting data
    supporting_data JSONB DEFAULT '{}', -- Usage patterns, metrics that led to this recommendation
    action_items JSONB DEFAULT '[]', -- Specific steps to implement
    -- Expiry (some recommendations become stale)
    expires_at TIMESTAMPTZ,
    -- Timestamps
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Indices for cost_recommendations
CREATE INDEX IF NOT EXISTS idx_cost_recommendations_tenant ON cost_recommendations(tenant_id, workspace_id);
CREATE INDEX IF NOT EXISTS idx_cost_recommendations_active ON cost_recommendations(tenant_id, status, priority)
    WHERE status = 'active' AND is_dismissed = FALSE;
CREATE INDEX IF NOT EXISTS idx_cost_recommendations_type ON cost_recommendations(recommendation_type, status);

-- ============================================================================
-- PART 4: MEMORY LAYER WORKER - TRANSITION QUEUE
-- ============================================================================

-- Layer transition queue for processing memory promotions
CREATE TABLE IF NOT EXISTS layer_transition_queue (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    memory_id UUID NOT NULL REFERENCES memories(id) ON DELETE CASCADE,
    -- Transition details
    from_layer VARCHAR(20) NOT NULL CHECK (from_layer IN ('L0_RAW', 'L1_CONTEXT', 'L2_SUMMARY', 'L3_KNOWLEDGE', 'L4_HEURISTIC')),
    to_layer VARCHAR(20) NOT NULL CHECK (to_layer IN ('L0_RAW', 'L1_CONTEXT', 'L2_SUMMARY', 'L3_KNOWLEDGE', 'L4_HEURISTIC')),
    -- Processing status
    status VARCHAR(20) NOT NULL DEFAULT 'pending' CHECK (status IN ('pending', 'processing', 'completed', 'failed', 'cancelled')),
    priority INTEGER NOT NULL DEFAULT 5 CHECK (priority BETWEEN 1 AND 10), -- Lower = higher priority
    retry_count INTEGER NOT NULL DEFAULT 0,
    max_retries INTEGER NOT NULL DEFAULT 3,
    -- Scheduling
    scheduled_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    started_at TIMESTAMPTZ,
    completed_at TIMESTAMPTZ,
    -- Error tracking
    error_message TEXT,
    error_details JSONB,
    -- Processing metadata
    processor_id VARCHAR(100), -- Which worker picked this up
    processing_duration_ms INTEGER,
    -- Result data (stores generated content like summaries, extracted facts)
    result_data JSONB,
    result_memory_ids UUID[], -- IDs of any new memories created during transition
    -- Timestamps
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    -- Prevent duplicate pending transitions for same memory
    CONSTRAINT layer_transition_unique_pending UNIQUE (memory_id, to_layer)
        -- Note: Partial unique constraints need to be created separately
);

-- Create partial unique index for pending transitions
DROP INDEX IF EXISTS idx_layer_transition_unique_pending;
CREATE UNIQUE INDEX idx_layer_transition_unique_pending ON layer_transition_queue(memory_id, to_layer)
    WHERE status IN ('pending', 'processing');

-- Additional indices for layer_transition_queue
CREATE INDEX IF NOT EXISTS idx_layer_transition_pending ON layer_transition_queue(status, priority, scheduled_at)
    WHERE status = 'pending';
CREATE INDEX IF NOT EXISTS idx_layer_transition_tenant ON layer_transition_queue(tenant_id, status);
CREATE INDEX IF NOT EXISTS idx_layer_transition_memory ON layer_transition_queue(memory_id);
CREATE INDEX IF NOT EXISTS idx_layer_transition_processing ON layer_transition_queue(processor_id, status)
    WHERE status = 'processing';

-- ============================================================================
-- PART 5: MEMORY LAYER WORKER - LEARNED HEURISTICS
-- ============================================================================

-- Learned heuristics table (L4 layer content)
CREATE TABLE IF NOT EXISTS learned_heuristics (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    memory_id UUID NOT NULL REFERENCES memories(id) ON DELETE CASCADE,
    -- Pattern identification
    pattern_type VARCHAR(50) NOT NULL CHECK (pattern_type IN ('entity', 'temporal', 'causal', 'categorical', 'behavioral', 'preference')),
    pattern_subtype VARCHAR(100),
    -- Rule definition
    rule_statement TEXT NOT NULL, -- Human-readable rule
    rule_structured JSONB, -- Machine-readable rule structure
    -- Supporting evidence
    supporting_fact_ids UUID[] NOT NULL DEFAULT '{}',
    supporting_memory_ids UUID[] NOT NULL DEFAULT '{}',
    evidence_count INTEGER NOT NULL DEFAULT 0,
    -- Confidence and validation
    confidence REAL NOT NULL CHECK (confidence >= 0 AND confidence <= 1),
    validation_count INTEGER NOT NULL DEFAULT 0,
    last_validated_at TIMESTAMPTZ,
    contradiction_count INTEGER NOT NULL DEFAULT 0,
    -- Exceptions and caveats
    exceptions JSONB DEFAULT '[]',
    caveats TEXT[],
    -- Usage tracking
    application_count INTEGER NOT NULL DEFAULT 0, -- How many times this heuristic was applied
    last_applied_at TIMESTAMPTZ,
    usefulness_score REAL, -- Computed based on application success
    -- Status
    status VARCHAR(20) NOT NULL DEFAULT 'active' CHECK (status IN ('active', 'deprecated', 'invalidated', 'merged')),
    superseded_by_id UUID REFERENCES learned_heuristics(id),
    -- Timestamps
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Indices for learned_heuristics
CREATE INDEX IF NOT EXISTS idx_learned_heuristics_tenant ON learned_heuristics(tenant_id);
CREATE INDEX IF NOT EXISTS idx_learned_heuristics_memory ON learned_heuristics(memory_id);
CREATE INDEX IF NOT EXISTS idx_learned_heuristics_pattern ON learned_heuristics(pattern_type, confidence DESC);
CREATE INDEX IF NOT EXISTS idx_learned_heuristics_active ON learned_heuristics(tenant_id, status, confidence DESC)
    WHERE status = 'active';
CREATE INDEX IF NOT EXISTS idx_learned_heuristics_supporting ON learned_heuristics USING GIN (supporting_fact_ids);

-- ============================================================================
-- PART 6: MEMORY LAYER - ADD LAYER COLUMN TO MEMORIES
-- ============================================================================

-- Add layer column to memories table if not exists
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_name = 'memories' AND column_name = 'layer'
    ) THEN
        ALTER TABLE memories ADD COLUMN layer VARCHAR(20) DEFAULT 'L0_RAW'
            CHECK (layer IN ('L0_RAW', 'L1_CONTEXT', 'L2_SUMMARY', 'L3_KNOWLEDGE', 'L4_HEURISTIC'));
    END IF;

    -- Add access_count for promotion criteria
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_name = 'memories' AND column_name = 'access_count'
    ) THEN
        ALTER TABLE memories ADD COLUMN access_count INTEGER NOT NULL DEFAULT 0;
    END IF;

    -- Add last_accessed_at for promotion criteria
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_name = 'memories' AND column_name = 'last_accessed_at'
    ) THEN
        ALTER TABLE memories ADD COLUMN last_accessed_at TIMESTAMPTZ;
    END IF;

    -- Add parent_memory_id for layer hierarchy (summaries link to source memories)
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_name = 'memories' AND column_name = 'parent_memory_ids'
    ) THEN
        ALTER TABLE memories ADD COLUMN parent_memory_ids UUID[] DEFAULT '{}';
    END IF;
END $$;

-- Index for layer-based queries
CREATE INDEX IF NOT EXISTS idx_memories_layer ON memories(tenant_id, layer);
CREATE INDEX IF NOT EXISTS idx_memories_access ON memories(tenant_id, layer, access_count DESC, last_accessed_at DESC);
CREATE INDEX IF NOT EXISTS idx_memories_promotion_candidates ON memories(tenant_id, layer, access_count, created_at)
    WHERE layer IN ('L0_RAW', 'L1_CONTEXT', 'L2_SUMMARY', 'L3_KNOWLEDGE');

-- ============================================================================
-- PART 7: HELPER FUNCTIONS
-- ============================================================================

-- Function to calculate billing interval end date
CREATE OR REPLACE FUNCTION calculate_billing_end_date(
    start_date TIMESTAMPTZ,
    billing_interval VARCHAR(20)
) RETURNS TIMESTAMPTZ AS $$
BEGIN
    RETURN CASE billing_interval
        WHEN 'monthly' THEN start_date + INTERVAL '1 month'
        WHEN 'quarterly' THEN start_date + INTERVAL '3 months'
        WHEN 'annual' THEN start_date + INTERVAL '1 year'
        ELSE start_date + INTERVAL '1 month'
    END;
END;
$$ LANGUAGE plpgsql IMMUTABLE;

-- Function to calculate discounted price
CREATE OR REPLACE FUNCTION calculate_discounted_price(
    base_price DECIMAL(15,4),
    billing_interval VARCHAR(20),
    annual_discount DECIMAL(5,2) DEFAULT 20.00,
    quarterly_discount DECIMAL(5,2) DEFAULT 10.00
) RETURNS DECIMAL(15,4) AS $$
BEGIN
    RETURN CASE billing_interval
        WHEN 'annual' THEN base_price * 12 * (1 - annual_discount / 100)
        WHEN 'quarterly' THEN base_price * 3 * (1 - quarterly_discount / 100)
        ELSE base_price
    END;
END;
$$ LANGUAGE plpgsql IMMUTABLE;

-- Function to get next pending layer transition
CREATE OR REPLACE FUNCTION claim_next_layer_transition(
    processor_id_param VARCHAR(100)
) RETURNS UUID AS $$
DECLARE
    transition_id UUID;
BEGIN
    -- Atomically claim the next pending transition
    UPDATE layer_transition_queue
    SET
        status = 'processing',
        processor_id = processor_id_param,
        started_at = NOW(),
        updated_at = NOW()
    WHERE id = (
        SELECT id FROM layer_transition_queue
        WHERE status = 'pending'
          AND scheduled_at <= NOW()
          AND retry_count < max_retries
        ORDER BY priority ASC, scheduled_at ASC
        LIMIT 1
        FOR UPDATE SKIP LOCKED
    )
    RETURNING id INTO transition_id;

    RETURN transition_id;
END;
$$ LANGUAGE plpgsql;

-- Function to complete a layer transition
CREATE OR REPLACE FUNCTION complete_layer_transition(
    transition_id_param UUID,
    success BOOLEAN,
    result_data_param JSONB DEFAULT NULL,
    result_memory_ids_param UUID[] DEFAULT NULL,
    error_message_param TEXT DEFAULT NULL,
    error_details_param JSONB DEFAULT NULL
) RETURNS VOID AS $$
BEGIN
    UPDATE layer_transition_queue
    SET
        status = CASE WHEN success THEN 'completed' ELSE 'failed' END,
        completed_at = NOW(),
        processing_duration_ms = EXTRACT(EPOCH FROM (NOW() - started_at)) * 1000,
        result_data = result_data_param,
        result_memory_ids = result_memory_ids_param,
        error_message = error_message_param,
        error_details = error_details_param,
        retry_count = CASE WHEN NOT success THEN retry_count + 1 ELSE retry_count END,
        updated_at = NOW()
    WHERE id = transition_id_param;

    -- If failed but can retry, reset to pending
    UPDATE layer_transition_queue
    SET
        status = 'pending',
        scheduled_at = NOW() + (retry_count * INTERVAL '5 minutes'), -- Exponential backoff
        processor_id = NULL,
        started_at = NULL
    WHERE id = transition_id_param
      AND status = 'failed'
      AND retry_count < max_retries;
END;
$$ LANGUAGE plpgsql;

-- Function to increment memory access count
CREATE OR REPLACE FUNCTION increment_memory_access(
    memory_id_param UUID
) RETURNS VOID AS $$
BEGIN
    UPDATE memories
    SET
        access_count = access_count + 1,
        last_accessed_at = NOW(),
        updated_at = NOW()
    WHERE id = memory_id_param;
END;
$$ LANGUAGE plpgsql;

-- ============================================================================
-- PART 8: STRIPE PRICE MAPPING FOR BILLING INTERVALS
-- ============================================================================

-- Add columns to stripe_price_mapping for interval-based pricing
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_name = 'stripe_price_mapping' AND column_name = 'billing_interval'
    ) THEN
        ALTER TABLE stripe_price_mapping ADD COLUMN billing_interval VARCHAR(20) DEFAULT 'monthly';

        -- Drop old unique constraint and create new one including interval
        ALTER TABLE stripe_price_mapping DROP CONSTRAINT IF EXISTS stripe_price_mapping_plan_name_key;
    END IF;
END $$;

-- Create new unique constraint including billing_interval
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.constraint_column_usage
        WHERE constraint_name = 'stripe_price_mapping_plan_interval_unique'
    ) THEN
        ALTER TABLE stripe_price_mapping
            ADD CONSTRAINT stripe_price_mapping_plan_interval_unique
            UNIQUE (plan_name, billing_interval);
    END IF;
EXCEPTION WHEN duplicate_object THEN NULL;
END $$;

-- ============================================================================
-- GRANT PERMISSIONS
-- ============================================================================

-- Grant permissions to the application role (adjust role name as needed)
DO $$
BEGIN
    -- Grant on new tables
    GRANT ALL ON usage_forecasts TO PUBLIC;
    GRANT ALL ON cost_recommendations TO PUBLIC;
    GRANT ALL ON layer_transition_queue TO PUBLIC;
    GRANT ALL ON learned_heuristics TO PUBLIC;
EXCEPTION WHEN undefined_object THEN NULL;
END $$;

-- ============================================================================
-- Migration complete
-- ============================================================================
