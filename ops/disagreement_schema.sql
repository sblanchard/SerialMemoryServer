-- Model Disagreement Schema
-- Stores disagreement scores from multi-model reasoning runs

-- Model disagreement records
CREATE TABLE model_disagreements (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id VARCHAR(100) NOT NULL DEFAULT 'self',
    reasoning_run_id UUID NOT NULL,
    primary_model VARCHAR(100) NOT NULL,
    secondary_model VARCHAR(100) NOT NULL,
    disagreement_score REAL NOT NULL CHECK (disagreement_score >= 0 AND disagreement_score <= 1),
    primary_only_insights JSONB,
    secondary_only_insights JSONB,
    confidence_divergences JSONB,
    computed_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Reasoning run summary
CREATE TABLE reasoning_runs (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id VARCHAR(100) NOT NULL DEFAULT 'self',
    input_hash VARCHAR(64) NOT NULL,
    models_used INTEGER NOT NULL,
    successful_models INTEGER NOT NULL,
    total_duration_ms INTEGER NOT NULL,
    overall_confidence REAL NOT NULL CHECK (overall_confidence >= 0 AND overall_confidence <= 1),
    insight_count INTEGER NOT NULL DEFAULT 0,
    disagreement_count INTEGER NOT NULL DEFAULT 0,
    avg_disagreement_score REAL,
    project_filter VARCHAR(255),
    memory_id UUID,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Indexes
CREATE INDEX idx_disagreements_tenant ON model_disagreements(tenant_id);
CREATE INDEX idx_disagreements_run ON model_disagreements(reasoning_run_id);
CREATE INDEX idx_disagreements_score ON model_disagreements(disagreement_score DESC);
CREATE INDEX idx_disagreements_models ON model_disagreements(primary_model, secondary_model);

CREATE INDEX idx_reasoning_runs_tenant ON reasoning_runs(tenant_id);
CREATE INDEX idx_reasoning_runs_hash ON reasoning_runs(input_hash);
CREATE INDEX idx_reasoning_runs_confidence ON reasoning_runs(overall_confidence);
CREATE INDEX idx_reasoning_runs_created ON reasoning_runs(created_at DESC);

-- Function to get disagreement trends
CREATE OR REPLACE FUNCTION get_disagreement_trends(
    p_tenant_id VARCHAR(100) DEFAULT 'self',
    p_days INTEGER DEFAULT 7
)
RETURNS TABLE (
    model_pair TEXT,
    avg_disagreement REAL,
    max_disagreement REAL,
    occurrence_count BIGINT
) AS $$
BEGIN
    RETURN QUERY
    SELECT
        primary_model || ' vs ' || secondary_model AS model_pair,
        AVG(disagreement_score)::REAL AS avg_disagreement,
        MAX(disagreement_score)::REAL AS max_disagreement,
        COUNT(*) AS occurrence_count
    FROM model_disagreements
    WHERE tenant_id = p_tenant_id
        AND computed_at > NOW() - (p_days || ' days')::INTERVAL
    GROUP BY primary_model, secondary_model
    ORDER BY avg_disagreement DESC;
END;
$$ LANGUAGE plpgsql;

-- Function to get confidence history
CREATE OR REPLACE FUNCTION get_confidence_history(
    p_tenant_id VARCHAR(100) DEFAULT 'self',
    p_limit INTEGER DEFAULT 100
)
RETURNS TABLE (
    run_id UUID,
    overall_confidence REAL,
    models_used INTEGER,
    successful_models INTEGER,
    disagreement_count INTEGER,
    avg_disagreement REAL,
    created_at TIMESTAMPTZ
) AS $$
BEGIN
    RETURN QUERY
    SELECT
        r.id AS run_id,
        r.overall_confidence,
        r.models_used,
        r.successful_models,
        r.disagreement_count,
        r.avg_disagreement_score AS avg_disagreement,
        r.created_at
    FROM reasoning_runs r
    WHERE r.tenant_id = p_tenant_id
    ORDER BY r.created_at DESC
    LIMIT p_limit;
END;
$$ LANGUAGE plpgsql;
