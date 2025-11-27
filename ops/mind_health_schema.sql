-- Mind Health Schema
-- Self-awareness tracking: confidence drift, hallucinations, contradictions

-- ==========================================
-- CONFIDENCE OBSERVATIONS
-- ==========================================

CREATE TABLE IF NOT EXISTS mind_confidence_observations (
    id UUID PRIMARY KEY,
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

CREATE INDEX idx_confidence_obs_timestamp ON mind_confidence_observations(timestamp DESC);
CREATE INDEX idx_confidence_obs_operation ON mind_confidence_observations(operation_type, timestamp DESC);
CREATE INDEX idx_confidence_obs_memory ON mind_confidence_observations(memory_id) WHERE memory_id IS NOT NULL;

-- ==========================================
-- HALLUCINATION EVENTS
-- ==========================================

CREATE TABLE IF NOT EXISTS mind_hallucination_events (
    id UUID PRIMARY KEY,
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
    resolved_at TIMESTAMPTZ,

    CONSTRAINT valid_type CHECK (type IN (
        'FactualError', 'EntityConfusion', 'TemporalConfusion',
        'RelationshipError', 'AttributionError', 'Confabulation', 'SourceInvention'
    )),
    CONSTRAINT valid_source CHECK (source IN (
        'AutoDetected', 'UserReported', 'ValidationCheck', 'ContradictionResolution'
    ))
);

CREATE INDEX idx_hallucination_detected ON mind_hallucination_events(detected_at DESC);
CREATE INDEX idx_hallucination_memory ON mind_hallucination_events(memory_id) WHERE memory_id IS NOT NULL;
CREATE INDEX idx_hallucination_unresolved ON mind_hallucination_events(severity DESC) WHERE NOT resolved;
CREATE INDEX idx_hallucination_type ON mind_hallucination_events(type, detected_at DESC);

-- ==========================================
-- CONTRADICTION EVENTS
-- ==========================================

CREATE TABLE IF NOT EXISTS mind_contradiction_events (
    id UUID PRIMARY KEY,
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
    resolved_at TIMESTAMPTZ,

    CONSTRAINT valid_contradiction_type CHECK (type IN (
        'DirectConflict', 'TemporalInconsistency', 'AttributeConflict',
        'RelationshipConflict', 'SourceDisagreement', 'LogicalInconsistency'
    )),
    CONSTRAINT valid_status CHECK (status IN ('Unresolved', 'Investigating', 'Resolved', 'Accepted')),
    CONSTRAINT different_memories CHECK (memory_a != memory_b)
);

CREATE INDEX idx_contradiction_detected ON mind_contradiction_events(detected_at DESC);
CREATE INDEX idx_contradiction_memory_a ON mind_contradiction_events(memory_a);
CREATE INDEX idx_contradiction_memory_b ON mind_contradiction_events(memory_b);
CREATE INDEX idx_contradiction_unresolved ON mind_contradiction_events(severity DESC) WHERE status != 'Resolved';
CREATE INDEX idx_contradiction_type ON mind_contradiction_events(type, detected_at DESC);

-- Unique constraint to prevent duplicate contradictions
CREATE UNIQUE INDEX idx_contradiction_pair ON mind_contradiction_events(
    LEAST(memory_a, memory_b),
    GREATEST(memory_a, memory_b)
) WHERE status != 'Resolved';

-- ==========================================
-- DAILY HEALTH SCORES (Materialized)
-- ==========================================

CREATE TABLE IF NOT EXISTS mind_daily_scores (
    date DATE PRIMARY KEY,
    overall_score REAL NOT NULL CHECK (overall_score >= 0 AND overall_score <= 1),
    confidence_score REAL NOT NULL CHECK (confidence_score >= 0 AND confidence_score <= 1),
    hallucination_score REAL NOT NULL CHECK (hallucination_score >= 0 AND hallucination_score <= 1),
    contradiction_score REAL NOT NULL CHECK (contradiction_score >= 0 AND contradiction_score <= 1),
    observations INT NOT NULL DEFAULT 0,
    computed_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_daily_scores_date ON mind_daily_scores(date DESC);

-- ==========================================
-- FUNCTIONS FOR DAILY SCORE COMPUTATION
-- ==========================================

CREATE OR REPLACE FUNCTION compute_daily_mind_health(target_date DATE)
RETURNS void AS $$
DECLARE
    conf_score REAL;
    hall_score REAL;
    cont_score REAL;
    obs_count INT;
    total_memories INT;
    hall_count INT;
    cont_count INT;
BEGIN
    -- Get observation count
    SELECT COUNT(*) INTO obs_count
    FROM mind_confidence_observations
    WHERE DATE(timestamp) = target_date;

    -- Confidence calibration score
    SELECT COALESCE(1.0 - AVG(ABS(drift)), 1.0) INTO conf_score
    FROM mind_confidence_observations
    WHERE DATE(timestamp) = target_date AND actual_accuracy IS NOT NULL;

    -- Hallucination score (inverse of rate)
    SELECT COUNT(*) INTO total_memories
    FROM memories
    WHERE DATE(created_at) = target_date;

    SELECT COUNT(*) INTO hall_count
    FROM mind_hallucination_events
    WHERE DATE(detected_at) = target_date;

    hall_score := CASE
        WHEN total_memories > 0 THEN 1.0 - LEAST(hall_count::REAL / total_memories * 10, 1.0)
        ELSE 1.0
    END;

    -- Contradiction score (inverse of rate)
    SELECT COUNT(*) INTO cont_count
    FROM mind_contradiction_events
    WHERE DATE(detected_at) = target_date;

    cont_score := CASE
        WHEN total_memories > 0 THEN 1.0 - LEAST(cont_count::REAL / total_memories * 10, 1.0)
        ELSE 1.0
    END;

    -- Insert or update daily score
    INSERT INTO mind_daily_scores (date, overall_score, confidence_score, hallucination_score, contradiction_score, observations)
    VALUES (
        target_date,
        conf_score * 0.4 + hall_score * 0.35 + cont_score * 0.25,
        COALESCE(conf_score, 1.0),
        hall_score,
        cont_score,
        obs_count
    )
    ON CONFLICT (date) DO UPDATE SET
        overall_score = EXCLUDED.overall_score,
        confidence_score = EXCLUDED.confidence_score,
        hallucination_score = EXCLUDED.hallucination_score,
        contradiction_score = EXCLUDED.contradiction_score,
        observations = EXCLUDED.observations,
        computed_at = NOW();
END;
$$ LANGUAGE plpgsql;

-- ==========================================
-- VIEWS FOR QUICK ACCESS
-- ==========================================

CREATE OR REPLACE VIEW mind_health_summary AS
SELECT
    (SELECT COUNT(*) FROM mind_confidence_observations WHERE timestamp > NOW() - INTERVAL '7 days') as confidence_observations_7d,
    (SELECT AVG(ABS(drift)) FROM mind_confidence_observations WHERE timestamp > NOW() - INTERVAL '7 days' AND actual_accuracy IS NOT NULL) as avg_drift_7d,
    (SELECT COUNT(*) FROM mind_hallucination_events WHERE detected_at > NOW() - INTERVAL '7 days') as hallucinations_7d,
    (SELECT COUNT(*) FROM mind_hallucination_events WHERE detected_at > NOW() - INTERVAL '7 days' AND NOT resolved) as unresolved_hallucinations,
    (SELECT COUNT(*) FROM mind_contradiction_events WHERE detected_at > NOW() - INTERVAL '7 days') as contradictions_7d,
    (SELECT COUNT(*) FROM mind_contradiction_events WHERE status != 'Resolved') as unresolved_contradictions,
    (SELECT AVG(overall_score) FROM mind_daily_scores WHERE date > CURRENT_DATE - 7) as avg_health_score_7d;

CREATE OR REPLACE VIEW mind_health_alerts AS
SELECT
    'HIGH_DRIFT' as alert_type,
    'Confidence' as category,
    'High confidence drift detected' as message,
    AVG(ABS(drift)) as metric
FROM mind_confidence_observations
WHERE timestamp > NOW() - INTERVAL '24 hours' AND actual_accuracy IS NOT NULL
HAVING AVG(ABS(drift)) > 0.2

UNION ALL

SELECT
    'HIGH_HALLUCINATION_RATE' as alert_type,
    'Hallucination' as category,
    'Elevated hallucination rate' as message,
    COUNT(*)::REAL / NULLIF((SELECT COUNT(*) FROM memories WHERE created_at > NOW() - INTERVAL '24 hours'), 0) as metric
FROM mind_hallucination_events
WHERE detected_at > NOW() - INTERVAL '24 hours'
HAVING COUNT(*) > 0

UNION ALL

SELECT
    'UNRESOLVED_CONTRADICTIONS' as alert_type,
    'Contradiction' as category,
    'Multiple unresolved contradictions' as message,
    COUNT(*)::REAL as metric
FROM mind_contradiction_events
WHERE status != 'Resolved'
HAVING COUNT(*) > 5;

-- ==========================================
-- TRIGGER FOR AUTO-COMPUTING DAILY SCORES
-- ==========================================

CREATE OR REPLACE FUNCTION trigger_compute_daily_score()
RETURNS TRIGGER AS $$
BEGIN
    PERFORM compute_daily_mind_health(CURRENT_DATE);
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

-- Trigger on confidence observations
DROP TRIGGER IF EXISTS trg_confidence_daily_score ON mind_confidence_observations;
CREATE TRIGGER trg_confidence_daily_score
AFTER INSERT ON mind_confidence_observations
FOR EACH STATEMENT
EXECUTE FUNCTION trigger_compute_daily_score();

-- Trigger on hallucination events
DROP TRIGGER IF EXISTS trg_hallucination_daily_score ON mind_hallucination_events;
CREATE TRIGGER trg_hallucination_daily_score
AFTER INSERT ON mind_hallucination_events
FOR EACH STATEMENT
EXECUTE FUNCTION trigger_compute_daily_score();

-- Trigger on contradiction events
DROP TRIGGER IF EXISTS trg_contradiction_daily_score ON mind_contradiction_events;
CREATE TRIGGER trg_contradiction_daily_score
AFTER INSERT ON mind_contradiction_events
FOR EACH STATEMENT
EXECUTE FUNCTION trigger_compute_daily_score();
