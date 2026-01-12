-- Engine Telemetry Schema
-- Tracks metrics for optional infrastructure engines

-- ===================================================
-- 1. Embedding Cache Metrics
-- ===================================================
CREATE TABLE IF NOT EXISTS embedding_cache_metrics (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL,
    recorded_at timestamptz DEFAULT NOW(),

    -- Hit/Miss Statistics
    cache_hits bigint DEFAULT 0,
    cache_misses bigint DEFAULT 0,
    cache_hit_rate decimal(5,4) GENERATED ALWAYS AS (
        CASE WHEN cache_hits + cache_misses > 0
        THEN cache_hits::decimal / (cache_hits + cache_misses)
        ELSE 0 END
    ) STORED,

    -- Size Statistics
    total_cached_embeddings bigint DEFAULT 0,
    total_cache_size_bytes bigint DEFAULT 0,
    compiled_embeddings bigint DEFAULT 0,

    -- Performance
    avg_lookup_ms decimal(10,2),
    avg_store_ms decimal(10,2),

    -- Period
    period_start timestamptz NOT NULL,
    period_end timestamptz NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_embedding_cache_metrics_tenant
    ON embedding_cache_metrics(tenant_id, recorded_at DESC);

-- ===================================================
-- 2. Memory Compilation Metrics
-- ===================================================
CREATE TABLE IF NOT EXISTS compilation_metrics (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL,
    recorded_at timestamptz DEFAULT NOW(),

    -- Queue Statistics
    pending_compilations bigint DEFAULT 0,
    in_progress_compilations bigint DEFAULT 0,
    completed_compilations bigint DEFAULT 0,
    failed_compilations bigint DEFAULT 0,

    -- Cluster Statistics
    total_clusters bigint DEFAULT 0,
    stable_clusters bigint DEFAULT 0,
    avg_cluster_size decimal(10,2),

    -- Performance
    avg_compilation_time_ms decimal(10,2),
    total_compilation_time_ms bigint DEFAULT 0,
    memories_per_second decimal(10,2),

    -- Period
    period_start timestamptz NOT NULL,
    period_end timestamptz NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_compilation_metrics_tenant
    ON compilation_metrics(tenant_id, recorded_at DESC);

-- ===================================================
-- 3. Graph Recrawl Metrics
-- ===================================================
CREATE TABLE IF NOT EXISTS recrawl_metrics (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL,
    recorded_at timestamptz DEFAULT NOW(),

    -- Job Statistics
    total_jobs bigint DEFAULT 0,
    active_jobs bigint DEFAULT 0,
    completed_jobs bigint DEFAULT 0,
    failed_jobs bigint DEFAULT 0,

    -- Memory Processing
    memories_processed bigint DEFAULT 0,
    memories_succeeded bigint DEFAULT 0,
    memories_failed bigint DEFAULT 0,

    -- Entity/Relationship Extraction
    entities_extracted bigint DEFAULT 0,
    relationships_extracted bigint DEFAULT 0,

    -- Performance
    avg_memory_processing_ms decimal(10,2),
    total_processing_time_ms bigint DEFAULT 0,

    -- Period
    period_start timestamptz NOT NULL,
    period_end timestamptz NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_recrawl_metrics_tenant
    ON recrawl_metrics(tenant_id, recorded_at DESC);

-- ===================================================
-- 4. Dual-Pass Reasoning Metrics
-- ===================================================
CREATE TABLE IF NOT EXISTS reasoning_metrics (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL,
    recorded_at timestamptz DEFAULT NOW(),

    -- Reasoning Statistics
    total_reasoning_calls bigint DEFAULT 0,
    successful_calls bigint DEFAULT 0,
    failed_calls bigint DEFAULT 0,

    -- Confidence Improvement
    avg_confidence_before decimal(5,4),
    avg_confidence_after decimal(5,4),
    avg_confidence_improvement decimal(5,4),

    -- Critique Statistics
    total_issues_detected bigint DEFAULT 0,
    issues_by_type jsonb DEFAULT '{}',
    avg_issues_per_call decimal(10,2),

    -- Improvements
    total_improvements_made bigint DEFAULT 0,
    improvements_by_type jsonb DEFAULT '{}',

    -- Performance
    avg_pass1_duration_ms decimal(10,2),
    avg_pass2_duration_ms decimal(10,2),
    avg_total_duration_ms decimal(10,2),

    -- Period
    period_start timestamptz NOT NULL,
    period_end timestamptz NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_reasoning_metrics_tenant
    ON reasoning_metrics(tenant_id, recorded_at DESC);

-- ===================================================
-- 5. Context Optimization Metrics
-- ===================================================
CREATE TABLE IF NOT EXISTS context_optimization_metrics (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL,
    recorded_at timestamptz DEFAULT NOW(),

    -- Budget Statistics
    total_budgets_created bigint DEFAULT 0,
    total_contexts_packed bigint DEFAULT 0,

    -- Token Statistics
    avg_max_tokens bigint,
    avg_used_tokens bigint,
    avg_utilization_rate decimal(5,4),

    -- Memory Selection
    avg_candidates_per_pack bigint,
    avg_included_per_pack bigint,
    avg_excluded_per_pack bigint,
    avg_coverage_score decimal(5,4),

    -- Performance
    avg_packing_time_ms decimal(10,2),

    -- Period
    period_start timestamptz NOT NULL,
    period_end timestamptz NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_context_optimization_metrics_tenant
    ON context_optimization_metrics(tenant_id, recorded_at DESC);

-- ===================================================
-- 6. Time Travel Debugging Metrics
-- ===================================================
CREATE TABLE IF NOT EXISTS time_travel_metrics (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL,
    recorded_at timestamptz DEFAULT NOW(),

    -- Snapshot Statistics
    total_snapshots bigint DEFAULT 0,
    snapshots_by_type jsonb DEFAULT '{}',

    -- Query Statistics
    total_state_queries bigint DEFAULT 0,
    total_replay_queries bigint DEFAULT 0,

    -- Performance
    avg_snapshot_time_ms decimal(10,2),
    avg_state_query_time_ms decimal(10,2),
    avg_replay_time_ms decimal(10,2),

    -- Period
    period_start timestamptz NOT NULL,
    period_end timestamptz NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_time_travel_metrics_tenant
    ON time_travel_metrics(tenant_id, recorded_at DESC);

-- ===================================================
-- 7. Encryption Metrics
-- ===================================================
CREATE TABLE IF NOT EXISTS encryption_metrics (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL,
    recorded_at timestamptz DEFAULT NOW(),

    -- Key Statistics
    total_keys bigint DEFAULT 0,
    active_keys bigint DEFAULT 0,
    key_rotations bigint DEFAULT 0,

    -- Encryption Operations
    total_encryptions bigint DEFAULT 0,
    total_decryptions bigint DEFAULT 0,
    encrypted_memories bigint DEFAULT 0,

    -- Performance
    avg_encryption_time_ms decimal(10,2),
    avg_decryption_time_ms decimal(10,2),

    -- Period
    period_start timestamptz NOT NULL,
    period_end timestamptz NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_encryption_metrics_tenant
    ON encryption_metrics(tenant_id, recorded_at DESC);

-- ===================================================
-- 8. Inference Session Metrics
-- ===================================================
CREATE TABLE IF NOT EXISTS inference_session_metrics (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL,
    recorded_at timestamptz DEFAULT NOW(),

    -- Session Statistics
    total_sessions bigint DEFAULT 0,
    active_sessions bigint DEFAULT 0,
    completed_sessions bigint DEFAULT 0,
    replayed_sessions bigint DEFAULT 0,

    -- Determinism Statistics
    total_verifications bigint DEFAULT 0,
    deterministic_replays bigint DEFAULT 0,
    divergent_replays bigint DEFAULT 0,
    determinism_rate decimal(5,4),

    -- Step Statistics
    total_steps_executed bigint DEFAULT 0,
    avg_steps_per_session decimal(10,2),

    -- Performance
    avg_session_duration_ms decimal(10,2),
    avg_step_duration_ms decimal(10,2),

    -- Period
    period_start timestamptz NOT NULL,
    period_end timestamptz NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_inference_session_metrics_tenant
    ON inference_session_metrics(tenant_id, recorded_at DESC);

-- ===================================================
-- Aggregation Function for Dashboard
-- ===================================================
CREATE OR REPLACE FUNCTION get_engine_metrics_summary(
    p_tenant_id uuid,
    p_period_hours int DEFAULT 24
)
RETURNS jsonb
LANGUAGE plpgsql
AS $$
DECLARE
    result jsonb;
BEGIN
    SELECT jsonb_build_object(
        'embedding_cache', (
            SELECT jsonb_build_object(
                'hit_rate', COALESCE(AVG(cache_hit_rate), 0),
                'total_cached', COALESCE(MAX(total_cached_embeddings), 0),
                'compiled', COALESCE(MAX(compiled_embeddings), 0)
            )
            FROM embedding_cache_metrics
            WHERE tenant_id = p_tenant_id
              AND recorded_at > NOW() - (p_period_hours || ' hours')::interval
        ),
        'compilation', (
            SELECT jsonb_build_object(
                'pending', COALESCE(MAX(pending_compilations), 0),
                'completed', COALESCE(SUM(completed_compilations), 0),
                'clusters', COALESCE(MAX(total_clusters), 0),
                'avg_time_ms', COALESCE(AVG(avg_compilation_time_ms), 0)
            )
            FROM compilation_metrics
            WHERE tenant_id = p_tenant_id
              AND recorded_at > NOW() - (p_period_hours || ' hours')::interval
        ),
        'recrawl', (
            SELECT jsonb_build_object(
                'active_jobs', COALESCE(MAX(active_jobs), 0),
                'memories_processed', COALESCE(SUM(memories_processed), 0),
                'entities_extracted', COALESCE(SUM(entities_extracted), 0),
                'relationships_extracted', COALESCE(SUM(relationships_extracted), 0)
            )
            FROM recrawl_metrics
            WHERE tenant_id = p_tenant_id
              AND recorded_at > NOW() - (p_period_hours || ' hours')::interval
        ),
        'reasoning', (
            SELECT jsonb_build_object(
                'total_calls', COALESCE(SUM(total_reasoning_calls), 0),
                'avg_confidence_improvement', COALESCE(AVG(avg_confidence_improvement), 0),
                'total_improvements', COALESCE(SUM(total_improvements_made), 0),
                'avg_duration_ms', COALESCE(AVG(avg_total_duration_ms), 0)
            )
            FROM reasoning_metrics
            WHERE tenant_id = p_tenant_id
              AND recorded_at > NOW() - (p_period_hours || ' hours')::interval
        ),
        'context_optimization', (
            SELECT jsonb_build_object(
                'contexts_packed', COALESCE(SUM(total_contexts_packed), 0),
                'avg_utilization', COALESCE(AVG(avg_utilization_rate), 0),
                'avg_coverage', COALESCE(AVG(avg_coverage_score), 0)
            )
            FROM context_optimization_metrics
            WHERE tenant_id = p_tenant_id
              AND recorded_at > NOW() - (p_period_hours || ' hours')::interval
        ),
        'recorded_at', NOW()
    ) INTO result;

    RETURN result;
END;
$$;

-- ===================================================
-- Email Verification Tokens (for signup flow)
-- ===================================================
CREATE TABLE IF NOT EXISTS email_verification_tokens (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    email varchar(255) NOT NULL,
    token varchar(255) NOT NULL UNIQUE,
    expires_at timestamptz NOT NULL,
    verified_at timestamptz,
    created_at timestamptz DEFAULT NOW(),

    CONSTRAINT fk_verification_tenant
        FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS idx_email_verification_token
    ON email_verification_tokens(token) WHERE verified_at IS NULL;
CREATE INDEX IF NOT EXISTS idx_email_verification_user
    ON email_verification_tokens(user_id);
CREATE INDEX IF NOT EXISTS idx_email_verification_expires
    ON email_verification_tokens(expires_at) WHERE verified_at IS NULL;
