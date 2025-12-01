-- ============================================================================
-- Reasoning Execution Schema
-- Persists multi-model reasoning results with full trace history (DAG structure)
-- ============================================================================

-- Drop old tables if they exist (legacy schema)
DROP TABLE IF EXISTS reasoning_findings CASCADE;
DROP TABLE IF EXISTS reasoning_results CASCADE;
DROP VIEW IF EXISTS reasoning_findings_summary CASCADE;
DROP VIEW IF EXISTS reasoning_traces_view CASCADE;

-- Reasoning execution status enum
DO $$ BEGIN
    CREATE TYPE reasoning_status AS ENUM ('pending', 'running', 'completed', 'failed', 'cancelled');
EXCEPTION
    WHEN duplicate_object THEN NULL;
END $$;

-- Reasoning role enum (matches ReasoningRole in C#)
DO $$ BEGIN
    CREATE TYPE reasoning_role AS ENUM ('structural', 'risk', 'optimization', 'contradiction', 'summary');
EXCEPTION
    WHEN duplicate_object THEN NULL;
END $$;

-- ============================================================================
-- reasoning_executions: Parent table for reasoning runs
-- ============================================================================
CREATE TABLE IF NOT EXISTS reasoning_executions (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id       UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    user_id         TEXT NOT NULL,

    -- Input specification
    input_type      TEXT NOT NULL CHECK (input_type IN ('memory', 'query', 'project', 'graph')),
    input_reference TEXT,                    -- memory_id, query text, project name, etc.
    input_hash      TEXT,                    -- SHA-256 of input for deduplication

    -- Execution state
    status          reasoning_status NOT NULL DEFAULT 'pending',
    started_at      TIMESTAMPTZ,
    completed_at    TIMESTAMPTZ,

    -- Results summary (denormalized for quick access)
    models_used     INT DEFAULT 0,
    successful_models INT DEFAULT 0,
    total_duration_ms INT DEFAULT 0,
    overall_confidence REAL DEFAULT 0,
    insight_count   INT DEFAULT 0,
    disagreement_count INT DEFAULT 0,

    -- Error handling
    error_message   TEXT,
    error_details   JSONB,

    -- Audit
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Indexes for reasoning_executions
CREATE INDEX IF NOT EXISTS idx_reasoning_executions_tenant_id ON reasoning_executions(tenant_id);
CREATE INDEX IF NOT EXISTS idx_reasoning_executions_user_id ON reasoning_executions(tenant_id, user_id);
CREATE INDEX IF NOT EXISTS idx_reasoning_executions_status ON reasoning_executions(status) WHERE status IN ('pending', 'running');
CREATE INDEX IF NOT EXISTS idx_reasoning_executions_created_at ON reasoning_executions(tenant_id, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_reasoning_executions_input_hash ON reasoning_executions(tenant_id, input_hash) WHERE input_hash IS NOT NULL;

-- ============================================================================
-- reasoning_traces: Individual model execution steps (DAG nodes)
-- ============================================================================
CREATE TABLE IF NOT EXISTS reasoning_traces (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    execution_id    UUID NOT NULL REFERENCES reasoning_executions(id) ON DELETE CASCADE,
    tenant_id       UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,

    -- DAG structure
    step_id         TEXT NOT NULL,           -- Unique within execution (e.g., "structural-1", "risk-1")
    parent_step_ids TEXT[] DEFAULT '{}',     -- Parent steps this depends on (for DAG edges)
    step_order      INT NOT NULL DEFAULT 0,  -- Execution order

    -- Model information
    role            reasoning_role NOT NULL,
    model_name      TEXT NOT NULL,
    model_version   TEXT,

    -- Execution details
    started_at      TIMESTAMPTZ,
    completed_at    TIMESTAMPTZ,
    duration_ms     INT DEFAULT 0,
    success         BOOLEAN DEFAULT FALSE,

    -- Input/Output
    input_context   JSONB,                   -- What was sent to the model
    output_raw      TEXT,                    -- Raw model response
    output_parsed   JSONB,                   -- Parsed/structured output

    -- Error handling
    error_message   TEXT,
    error_code      TEXT,

    -- Audit
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Indexes for reasoning_traces
CREATE INDEX IF NOT EXISTS idx_reasoning_traces_execution_id ON reasoning_traces(execution_id);
CREATE INDEX IF NOT EXISTS idx_reasoning_traces_tenant_id ON reasoning_traces(tenant_id);
CREATE INDEX IF NOT EXISTS idx_reasoning_traces_role ON reasoning_traces(execution_id, role);
CREATE INDEX IF NOT EXISTS idx_reasoning_traces_step_order ON reasoning_traces(execution_id, step_order);

-- ============================================================================
-- reasoning_insights: Merged insights from multiple models
-- ============================================================================
CREATE TABLE IF NOT EXISTS reasoning_insights (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    execution_id    UUID NOT NULL REFERENCES reasoning_executions(id) ON DELETE CASCADE,
    tenant_id       UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,

    -- Insight content
    insight_type    TEXT NOT NULL,           -- e.g., 'risk', 'optimization', 'structural'
    content         TEXT NOT NULL,
    confidence      REAL NOT NULL DEFAULT 0,

    -- Attribution
    source_models   TEXT[] NOT NULL DEFAULT '{}',
    agreement_count INT NOT NULL DEFAULT 1,

    -- Related entities
    related_entity_ids UUID[] DEFAULT '{}',
    related_memory_ids UUID[] DEFAULT '{}',

    -- Metadata
    metadata        JSONB,

    -- Audit
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Indexes for reasoning_insights
CREATE INDEX IF NOT EXISTS idx_reasoning_insights_execution_id ON reasoning_insights(execution_id);
CREATE INDEX IF NOT EXISTS idx_reasoning_insights_tenant_id ON reasoning_insights(tenant_id);
CREATE INDEX IF NOT EXISTS idx_reasoning_insights_type ON reasoning_insights(execution_id, insight_type);
CREATE INDEX IF NOT EXISTS idx_reasoning_insights_confidence ON reasoning_insights(execution_id, confidence DESC);

-- ============================================================================
-- reasoning_disagreements: Model disagreements for analysis
-- ============================================================================
CREATE TABLE IF NOT EXISTS reasoning_disagreements (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    execution_id    UUID NOT NULL REFERENCES reasoning_executions(id) ON DELETE CASCADE,
    tenant_id       UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,

    -- Disagreement details
    topic           TEXT NOT NULL,
    model_a         TEXT NOT NULL,
    position_a      TEXT NOT NULL,
    model_b         TEXT NOT NULL,
    position_b      TEXT NOT NULL,
    severity        TEXT CHECK (severity IN ('low', 'medium', 'high')),

    -- Resolution
    resolved        BOOLEAN DEFAULT FALSE,
    resolution      TEXT,
    resolved_at     TIMESTAMPTZ,
    resolved_by     TEXT,

    -- Audit
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Indexes for reasoning_disagreements
CREATE INDEX IF NOT EXISTS idx_reasoning_disagreements_execution_id ON reasoning_disagreements(execution_id);
CREATE INDEX IF NOT EXISTS idx_reasoning_disagreements_tenant_id ON reasoning_disagreements(tenant_id);
CREATE INDEX IF NOT EXISTS idx_reasoning_disagreements_unresolved ON reasoning_disagreements(execution_id) WHERE NOT resolved;

-- ============================================================================
-- Row Level Security (RLS)
-- ============================================================================

-- Enable RLS on all reasoning tables
ALTER TABLE reasoning_executions ENABLE ROW LEVEL SECURITY;
ALTER TABLE reasoning_traces ENABLE ROW LEVEL SECURITY;
ALTER TABLE reasoning_insights ENABLE ROW LEVEL SECURITY;
ALTER TABLE reasoning_disagreements ENABLE ROW LEVEL SECURITY;

-- Drop existing policies if they exist
DROP POLICY IF EXISTS reasoning_executions_tenant_isolation ON reasoning_executions;
DROP POLICY IF EXISTS reasoning_traces_tenant_isolation ON reasoning_traces;
DROP POLICY IF EXISTS reasoning_insights_tenant_isolation ON reasoning_insights;
DROP POLICY IF EXISTS reasoning_disagreements_tenant_isolation ON reasoning_disagreements;

-- reasoning_executions policies
CREATE POLICY reasoning_executions_tenant_isolation ON reasoning_executions
    FOR ALL
    USING (
        current_setting('app.role', true) = 'internal_admin'
        OR tenant_id = current_setting('app.tenant_id', true)::uuid
    )
    WITH CHECK (
        current_setting('app.role', true) = 'internal_admin'
        OR tenant_id = current_setting('app.tenant_id', true)::uuid
    );

-- reasoning_traces policies
CREATE POLICY reasoning_traces_tenant_isolation ON reasoning_traces
    FOR ALL
    USING (
        current_setting('app.role', true) = 'internal_admin'
        OR tenant_id = current_setting('app.tenant_id', true)::uuid
    )
    WITH CHECK (
        current_setting('app.role', true) = 'internal_admin'
        OR tenant_id = current_setting('app.tenant_id', true)::uuid
    );

-- reasoning_insights policies
CREATE POLICY reasoning_insights_tenant_isolation ON reasoning_insights
    FOR ALL
    USING (
        current_setting('app.role', true) = 'internal_admin'
        OR tenant_id = current_setting('app.tenant_id', true)::uuid
    )
    WITH CHECK (
        current_setting('app.role', true) = 'internal_admin'
        OR tenant_id = current_setting('app.tenant_id', true)::uuid
    );

-- reasoning_disagreements policies
CREATE POLICY reasoning_disagreements_tenant_isolation ON reasoning_disagreements
    FOR ALL
    USING (
        current_setting('app.role', true) = 'internal_admin'
        OR tenant_id = current_setting('app.tenant_id', true)::uuid
    )
    WITH CHECK (
        current_setting('app.role', true) = 'internal_admin'
        OR tenant_id = current_setting('app.tenant_id', true)::uuid
    );

-- ============================================================================
-- Updated_at trigger
-- ============================================================================
CREATE OR REPLACE FUNCTION update_reasoning_executions_updated_at()
RETURNS TRIGGER AS $$
BEGIN
    NEW.updated_at = NOW();
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_reasoning_executions_updated_at ON reasoning_executions;
CREATE TRIGGER trg_reasoning_executions_updated_at
    BEFORE UPDATE ON reasoning_executions
    FOR EACH ROW
    EXECUTE FUNCTION update_reasoning_executions_updated_at();

-- ============================================================================
-- Helper function to get execution with full details (SECURITY DEFINER for RLS bypass)
-- ============================================================================
CREATE OR REPLACE FUNCTION get_reasoning_execution_details(p_execution_id UUID)
RETURNS TABLE (
    execution JSONB,
    traces JSONB,
    insights JSONB,
    disagreements JSONB
) AS $$
BEGIN
    RETURN QUERY
    SELECT
        to_jsonb(e.*) AS execution,
        COALESCE(
            (SELECT jsonb_agg(to_jsonb(t.*) ORDER BY t.step_order)
             FROM reasoning_traces t WHERE t.execution_id = p_execution_id),
            '[]'::jsonb
        ) AS traces,
        COALESCE(
            (SELECT jsonb_agg(to_jsonb(i.*) ORDER BY i.confidence DESC)
             FROM reasoning_insights i WHERE i.execution_id = p_execution_id),
            '[]'::jsonb
        ) AS insights,
        COALESCE(
            (SELECT jsonb_agg(to_jsonb(d.*))
             FROM reasoning_disagreements d WHERE d.execution_id = p_execution_id),
            '[]'::jsonb
        ) AS disagreements
    FROM reasoning_executions e
    WHERE e.id = p_execution_id;
END;
$$ LANGUAGE plpgsql SECURITY DEFINER;

-- Grant execute to authenticated users
GRANT EXECUTE ON FUNCTION get_reasoning_execution_details(UUID) TO PUBLIC;

-- ============================================================================
-- View for recent executions with summary
-- ============================================================================
CREATE OR REPLACE VIEW reasoning_executions_summary AS
SELECT
    e.id,
    e.tenant_id,
    e.user_id,
    e.input_type,
    e.input_reference,
    e.status,
    e.started_at,
    e.completed_at,
    e.models_used,
    e.successful_models,
    e.total_duration_ms,
    e.overall_confidence,
    e.insight_count,
    e.disagreement_count,
    e.error_message,
    e.created_at,
    COUNT(DISTINCT t.id) AS trace_count,
    COUNT(DISTINCT CASE WHEN t.success THEN t.id END) AS successful_traces
FROM reasoning_executions e
LEFT JOIN reasoning_traces t ON t.execution_id = e.id
GROUP BY e.id
ORDER BY e.created_at DESC;

-- ============================================================================
-- Comments for documentation
-- ============================================================================
COMMENT ON TABLE reasoning_executions IS 'Parent table for multi-model reasoning runs';
COMMENT ON TABLE reasoning_traces IS 'Individual model execution steps forming a DAG';
COMMENT ON TABLE reasoning_insights IS 'Merged insights from multiple models with confidence scores';
COMMENT ON TABLE reasoning_disagreements IS 'Model disagreements requiring resolution';

COMMENT ON COLUMN reasoning_traces.parent_step_ids IS 'Parent steps this depends on (forms DAG edges)';
COMMENT ON COLUMN reasoning_traces.step_id IS 'Unique within execution, e.g., structural-1, risk-1';
COMMENT ON COLUMN reasoning_insights.agreement_count IS 'Number of models that agreed on this insight';
