-- Reasoning Analysis Schema
-- Tables for storing code analysis results and findings

-- Drop existing tables if they exist (for development)
DROP TABLE IF EXISTS reasoning_findings CASCADE;
DROP TABLE IF EXISTS reasoning_results CASCADE;

-- Main analysis results table
CREATE TABLE reasoning_results (
    id UUID PRIMARY KEY,
    started_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    completed_at TIMESTAMPTZ,
    duration_ms INTEGER,
    directory TEXT NOT NULL,
    files_analyzed INTEGER NOT NULL DEFAULT 0,
    findings_count INTEGER NOT NULL DEFAULT 0,
    status TEXT NOT NULL DEFAULT 'running',
    tenant_id TEXT NOT NULL DEFAULT 'self',
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Index for recent traces lookup
CREATE INDEX idx_reasoning_results_started_at ON reasoning_results(started_at DESC);
CREATE INDEX idx_reasoning_results_tenant ON reasoning_results(tenant_id);

-- Individual findings from analysis
CREATE TABLE reasoning_findings (
    id UUID PRIMARY KEY,
    trace_id UUID NOT NULL REFERENCES reasoning_results(id) ON DELETE CASCADE,
    type TEXT NOT NULL,
    severity TEXT NOT NULL,
    title TEXT NOT NULL,
    description TEXT NOT NULL,
    file_path TEXT NOT NULL,
    line_number INTEGER NOT NULL,
    code_snippet TEXT,
    confidence REAL NOT NULL DEFAULT 0.0,
    recommendation TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Indexes for findings queries
CREATE INDEX idx_reasoning_findings_trace ON reasoning_findings(trace_id);
CREATE INDEX idx_reasoning_findings_severity ON reasoning_findings(severity);
CREATE INDEX idx_reasoning_findings_type ON reasoning_findings(type);

-- View for finding summary by type
CREATE OR REPLACE VIEW reasoning_findings_summary AS
SELECT
    type,
    severity,
    COUNT(*) AS count,
    AVG(confidence) AS avg_confidence
FROM reasoning_findings
GROUP BY type, severity
ORDER BY
    CASE severity
        WHEN 'Critical' THEN 1
        WHEN 'High' THEN 2
        WHEN 'Medium' THEN 3
        ELSE 4
    END,
    type;

-- View for recent traces with findings breakdown
CREATE OR REPLACE VIEW reasoning_traces_view AS
SELECT
    r.id,
    r.started_at,
    r.completed_at,
    r.duration_ms,
    r.directory,
    r.files_analyzed,
    r.findings_count,
    r.status,
    COUNT(CASE WHEN f.severity = 'Critical' THEN 1 END) AS critical_count,
    COUNT(CASE WHEN f.severity = 'High' THEN 1 END) AS high_count,
    COUNT(CASE WHEN f.severity = 'Medium' THEN 1 END) AS medium_count,
    COUNT(CASE WHEN f.severity = 'Low' THEN 1 END) AS low_count
FROM reasoning_results r
LEFT JOIN reasoning_findings f ON f.trace_id = r.id
GROUP BY r.id, r.started_at, r.completed_at, r.duration_ms, r.directory, r.files_analyzed, r.findings_count, r.status
ORDER BY r.started_at DESC;

-- Grant permissions (adjust as needed)
-- GRANT SELECT, INSERT, UPDATE ON reasoning_results TO app_user;
-- GRANT SELECT, INSERT, UPDATE ON reasoning_findings TO app_user;
