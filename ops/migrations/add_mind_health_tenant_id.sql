-- Migration: Add tenant_id to mind health tables
-- This migration adds tenant isolation to mind health tracking tables

-- 1. Add tenant_id to mind_confidence_observations
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_name = 'mind_confidence_observations' AND column_name = 'tenant_id'
    ) AND EXISTS (
        SELECT 1 FROM information_schema.tables
        WHERE table_name = 'mind_confidence_observations'
    ) THEN
        ALTER TABLE mind_confidence_observations
        ADD COLUMN tenant_id UUID REFERENCES tenants(id);

        -- Set a default tenant for existing data (if any)
        UPDATE mind_confidence_observations
        SET tenant_id = (SELECT id FROM tenants LIMIT 1)
        WHERE tenant_id IS NULL;

        -- Make it NOT NULL
        ALTER TABLE mind_confidence_observations
        ALTER COLUMN tenant_id SET NOT NULL;

        -- Add index
        CREATE INDEX IF NOT EXISTS idx_confidence_obs_tenant
        ON mind_confidence_observations(tenant_id);

        RAISE NOTICE 'Added tenant_id to mind_confidence_observations';
    ELSIF NOT EXISTS (
        SELECT 1 FROM information_schema.tables
        WHERE table_name = 'mind_confidence_observations'
    ) THEN
        -- Create the table if it doesn't exist
        CREATE TABLE mind_confidence_observations (
            id UUID PRIMARY KEY,
            tenant_id UUID NOT NULL REFERENCES tenants(id),
            operation_type VARCHAR(100) NOT NULL,
            predicted_confidence REAL NOT NULL CHECK (predicted_confidence >= 0 AND predicted_confidence <= 1),
            actual_accuracy REAL CHECK (actual_accuracy >= 0 AND actual_accuracy <= 1),
            drift REAL GENERATED ALWAYS AS (
                CASE WHEN actual_accuracy IS NOT NULL
                     THEN predicted_confidence - actual_accuracy
                     ELSE NULL
                END
            ) STORED,
            timestamp TIMESTAMPTZ NOT NULL DEFAULT NOW(),
            memory_id UUID REFERENCES memories(id),
            context TEXT,
            CONSTRAINT valid_drift CHECK (drift IS NULL OR (drift >= -1 AND drift <= 1))
        );
        CREATE INDEX idx_confidence_obs_tenant ON mind_confidence_observations(tenant_id);
        CREATE INDEX idx_confidence_obs_timestamp ON mind_confidence_observations(tenant_id, timestamp DESC);
        RAISE NOTICE 'Created mind_confidence_observations table';
    END IF;
END $$;

-- 2. Add tenant_id to mind_hallucination_events
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_name = 'mind_hallucination_events' AND column_name = 'tenant_id'
    ) AND EXISTS (
        SELECT 1 FROM information_schema.tables
        WHERE table_name = 'mind_hallucination_events'
    ) THEN
        ALTER TABLE mind_hallucination_events
        ADD COLUMN tenant_id UUID REFERENCES tenants(id);

        UPDATE mind_hallucination_events
        SET tenant_id = (SELECT id FROM tenants LIMIT 1)
        WHERE tenant_id IS NULL;

        ALTER TABLE mind_hallucination_events
        ALTER COLUMN tenant_id SET NOT NULL;

        CREATE INDEX IF NOT EXISTS idx_hallucination_tenant
        ON mind_hallucination_events(tenant_id);

        RAISE NOTICE 'Added tenant_id to mind_hallucination_events';
    ELSIF NOT EXISTS (
        SELECT 1 FROM information_schema.tables
        WHERE table_name = 'mind_hallucination_events'
    ) THEN
        CREATE TABLE mind_hallucination_events (
            id UUID PRIMARY KEY,
            tenant_id UUID NOT NULL REFERENCES tenants(id),
            memory_id UUID REFERENCES memories(id),
            type VARCHAR(50) NOT NULL,
            severity REAL NOT NULL CHECK (severity >= 0 AND severity <= 1),
            description TEXT NOT NULL,
            evidence TEXT,
            ground_truth TEXT,
            detected_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
            source VARCHAR(50) NOT NULL DEFAULT 'AutoDetected',
            resolved BOOLEAN NOT NULL DEFAULT FALSE,
            resolution TEXT,
            resolved_at TIMESTAMPTZ
        );
        CREATE INDEX idx_hallucination_tenant ON mind_hallucination_events(tenant_id);
        CREATE INDEX idx_hallucination_detected ON mind_hallucination_events(tenant_id, detected_at DESC);
        RAISE NOTICE 'Created mind_hallucination_events table';
    END IF;
END $$;

-- 3. Add tenant_id to mind_contradiction_events
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_name = 'mind_contradiction_events' AND column_name = 'tenant_id'
    ) AND EXISTS (
        SELECT 1 FROM information_schema.tables
        WHERE table_name = 'mind_contradiction_events'
    ) THEN
        ALTER TABLE mind_contradiction_events
        ADD COLUMN tenant_id UUID REFERENCES tenants(id);

        UPDATE mind_contradiction_events
        SET tenant_id = (SELECT id FROM tenants LIMIT 1)
        WHERE tenant_id IS NULL;

        ALTER TABLE mind_contradiction_events
        ALTER COLUMN tenant_id SET NOT NULL;

        CREATE INDEX IF NOT EXISTS idx_contradiction_tenant
        ON mind_contradiction_events(tenant_id);

        RAISE NOTICE 'Added tenant_id to mind_contradiction_events';
    ELSIF NOT EXISTS (
        SELECT 1 FROM information_schema.tables
        WHERE table_name = 'mind_contradiction_events'
    ) THEN
        CREATE TABLE mind_contradiction_events (
            id UUID PRIMARY KEY,
            tenant_id UUID NOT NULL REFERENCES tenants(id),
            memory_a UUID NOT NULL REFERENCES memories(id),
            memory_b UUID NOT NULL REFERENCES memories(id),
            type VARCHAR(50) NOT NULL,
            severity REAL NOT NULL CHECK (severity >= 0 AND severity <= 1),
            description TEXT NOT NULL,
            similarity_score REAL CHECK (similarity_score >= 0 AND similarity_score <= 1),
            detected_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
            status VARCHAR(20) NOT NULL DEFAULT 'Unresolved',
            resolution_strategy VARCHAR(100),
            resolution_rationale TEXT,
            winner_id UUID REFERENCES memories(id),
            merged_into_id UUID REFERENCES memories(id),
            resolved_at TIMESTAMPTZ
        );
        CREATE INDEX idx_contradiction_tenant ON mind_contradiction_events(tenant_id);
        CREATE INDEX idx_contradiction_detected ON mind_contradiction_events(tenant_id, detected_at DESC);
        RAISE NOTICE 'Created mind_contradiction_events table';
    END IF;
END $$;

-- 4. Recreate mind_daily_scores with tenant_id as composite primary key
DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM information_schema.tables
        WHERE table_name = 'mind_daily_scores'
    ) AND NOT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_name = 'mind_daily_scores' AND column_name = 'tenant_id'
    ) THEN
        -- Drop and recreate with tenant_id
        DROP TABLE mind_daily_scores;
        RAISE NOTICE 'Dropped old mind_daily_scores table';
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM information_schema.tables
        WHERE table_name = 'mind_daily_scores'
    ) THEN
        CREATE TABLE mind_daily_scores (
            tenant_id UUID NOT NULL REFERENCES tenants(id),
            date DATE NOT NULL,
            overall_score REAL NOT NULL CHECK (overall_score >= 0 AND overall_score <= 1),
            confidence_score REAL NOT NULL CHECK (confidence_score >= 0 AND confidence_score <= 1),
            hallucination_score REAL NOT NULL CHECK (hallucination_score >= 0 AND hallucination_score <= 1),
            contradiction_score REAL NOT NULL CHECK (contradiction_score >= 0 AND contradiction_score <= 1),
            observations INT NOT NULL DEFAULT 0,
            computed_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
            PRIMARY KEY (tenant_id, date)
        );
        CREATE INDEX idx_daily_scores_tenant ON mind_daily_scores(tenant_id);
        CREATE INDEX idx_daily_scores_date ON mind_daily_scores(tenant_id, date DESC);
        RAISE NOTICE 'Created mind_daily_scores table with tenant_id';
    END IF;
END $$;

-- Enable RLS on all mind health tables
ALTER TABLE mind_confidence_observations ENABLE ROW LEVEL SECURITY;
ALTER TABLE mind_hallucination_events ENABLE ROW LEVEL SECURITY;
ALTER TABLE mind_contradiction_events ENABLE ROW LEVEL SECURITY;
ALTER TABLE mind_daily_scores ENABLE ROW LEVEL SECURITY;

-- Create RLS policies
DROP POLICY IF EXISTS mind_confidence_observations_tenant_policy ON mind_confidence_observations;
CREATE POLICY mind_confidence_observations_tenant_policy ON mind_confidence_observations
    FOR ALL USING (tenant_id = current_setting('app.tenant_id', true)::uuid);

DROP POLICY IF EXISTS mind_hallucination_events_tenant_policy ON mind_hallucination_events;
CREATE POLICY mind_hallucination_events_tenant_policy ON mind_hallucination_events
    FOR ALL USING (tenant_id = current_setting('app.tenant_id', true)::uuid);

DROP POLICY IF EXISTS mind_contradiction_events_tenant_policy ON mind_contradiction_events;
CREATE POLICY mind_contradiction_events_tenant_policy ON mind_contradiction_events
    FOR ALL USING (tenant_id = current_setting('app.tenant_id', true)::uuid);

DROP POLICY IF EXISTS mind_daily_scores_tenant_policy ON mind_daily_scores;
CREATE POLICY mind_daily_scores_tenant_policy ON mind_daily_scores
    FOR ALL USING (tenant_id = current_setting('app.tenant_id', true)::uuid);

-- Grant permissions to the app user
GRANT ALL ON mind_confidence_observations TO serialmemory;
GRANT ALL ON mind_hallucination_events TO serialmemory;
GRANT ALL ON mind_contradiction_events TO serialmemory;
GRANT ALL ON mind_daily_scores TO serialmemory;

SELECT 'Mind health tables migration complete' as status;
