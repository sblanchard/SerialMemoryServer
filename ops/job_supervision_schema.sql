-- Job Supervision Schema - Heartbeats, Retry, Dead-Letter Queue
-- Run this AFTER multi_tenant_schema.sql

-- =============================================================================
-- SUPERVISED JOBS TABLE
-- =============================================================================

CREATE TABLE IF NOT EXISTS supervised_jobs (
    job_id UUID PRIMARY KEY,
    tenant_id UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    job_type TEXT NOT NULL,
    payload TEXT NOT NULL,
    requested_by TEXT,

    -- Retry configuration
    max_retries INTEGER NOT NULL DEFAULT 3,
    retry_count INTEGER NOT NULL DEFAULT 0,
    heartbeat_timeout_seconds INTEGER NOT NULL DEFAULT 300,
    priority INTEGER NOT NULL DEFAULT 1,

    -- Status tracking
    status TEXT NOT NULL DEFAULT 'pending'
        CHECK (status IN ('pending', 'running', 'completed', 'failed', 'retrying', 'dead_letter', 'cancelled')),

    -- Timestamps
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    started_at TIMESTAMPTZ,
    completed_at TIMESTAMPTZ,
    last_heartbeat TIMESTAMPTZ,
    scheduled_retry_at TIMESTAMPTZ,
    moved_to_dlq_at TIMESTAMPTZ,

    -- Progress tracking
    progress_message TEXT,
    progress_percent INTEGER CHECK (progress_percent >= 0 AND progress_percent <= 100),

    -- Results and errors
    result_summary TEXT,
    error_message TEXT,
    error_history TEXT[],

    -- Metadata
    metadata JSONB DEFAULT '{}'
);

-- Indexes for job queries
CREATE INDEX IF NOT EXISTS idx_supervised_jobs_tenant ON supervised_jobs(tenant_id);
CREATE INDEX IF NOT EXISTS idx_supervised_jobs_status ON supervised_jobs(status);
CREATE INDEX IF NOT EXISTS idx_supervised_jobs_type ON supervised_jobs(job_type);
CREATE INDEX IF NOT EXISTS idx_supervised_jobs_pending ON supervised_jobs(created_at)
    WHERE status = 'pending';
CREATE INDEX IF NOT EXISTS idx_supervised_jobs_running ON supervised_jobs(last_heartbeat)
    WHERE status = 'running';
CREATE INDEX IF NOT EXISTS idx_supervised_jobs_retry ON supervised_jobs(scheduled_retry_at)
    WHERE status = 'retrying';
CREATE INDEX IF NOT EXISTS idx_supervised_jobs_dlq ON supervised_jobs(moved_to_dlq_at DESC)
    WHERE status = 'dead_letter';

-- =============================================================================
-- JOB SUPERVISION FUNCTIONS
-- =============================================================================

-- Get next pending jobs for processing (with locking)
CREATE OR REPLACE FUNCTION get_pending_jobs(
    p_job_type TEXT DEFAULT NULL,
    p_limit INTEGER DEFAULT 10
)
RETURNS SETOF supervised_jobs AS $$
BEGIN
    RETURN QUERY
    SELECT * FROM supervised_jobs
    WHERE status IN ('pending', 'retrying')
      AND (p_job_type IS NULL OR job_type = p_job_type)
      AND (scheduled_retry_at IS NULL OR scheduled_retry_at <= NOW())
    ORDER BY priority DESC, created_at ASC
    LIMIT p_limit
    FOR UPDATE SKIP LOCKED;
END;
$$ LANGUAGE plpgsql;

-- Get stuck jobs (no heartbeat within timeout)
CREATE OR REPLACE FUNCTION get_stuck_jobs(p_default_timeout_seconds INTEGER DEFAULT 300)
RETURNS TABLE(
    job_id UUID,
    tenant_id UUID,
    job_type TEXT,
    started_at TIMESTAMPTZ,
    last_heartbeat TIMESTAMPTZ,
    seconds_since_heartbeat DOUBLE PRECISION,
    retry_count INTEGER,
    max_retries INTEGER
) AS $$
BEGIN
    RETURN QUERY
    SELECT
        sj.job_id,
        sj.tenant_id,
        sj.job_type,
        sj.started_at,
        sj.last_heartbeat,
        EXTRACT(EPOCH FROM (NOW() - sj.last_heartbeat)) AS seconds_since_heartbeat,
        sj.retry_count,
        sj.max_retries
    FROM supervised_jobs sj
    WHERE sj.status = 'running'
      AND sj.last_heartbeat < NOW() - (sj.heartbeat_timeout_seconds || ' seconds')::INTERVAL;
END;
$$ LANGUAGE plpgsql;

-- Auto-fail stuck jobs and schedule retry
CREATE OR REPLACE FUNCTION auto_fail_stuck_jobs()
RETURNS INTEGER AS $$
DECLARE
    v_count INTEGER := 0;
    v_job RECORD;
BEGIN
    FOR v_job IN
        SELECT * FROM get_stuck_jobs()
    LOOP
        IF v_job.retry_count < v_job.max_retries THEN
            -- Schedule retry
            UPDATE supervised_jobs
            SET status = 'retrying',
                retry_count = retry_count + 1,
                error_message = 'Job stuck (no heartbeat)',
                error_history = array_append(
                    COALESCE(error_history, ARRAY[]::TEXT[]),
                    '[' || NOW()::TEXT || '] Job stuck - no heartbeat received'
                ),
                scheduled_retry_at = NOW() + (POWER(2, retry_count + 1) || ' minutes')::INTERVAL
            WHERE job_id = v_job.job_id;
        ELSE
            -- Move to dead letter
            UPDATE supervised_jobs
            SET status = 'dead_letter',
                error_message = 'Job stuck after max retries',
                error_history = array_append(
                    COALESCE(error_history, ARRAY[]::TEXT[]),
                    '[' || NOW()::TEXT || '] Moved to DLQ - max retries exceeded'
                ),
                moved_to_dlq_at = NOW()
            WHERE job_id = v_job.job_id;
        END IF;

        v_count := v_count + 1;
    END LOOP;

    RETURN v_count;
END;
$$ LANGUAGE plpgsql;

-- Job statistics by tenant
CREATE OR REPLACE FUNCTION get_job_statistics(p_tenant_id UUID DEFAULT NULL)
RETURNS TABLE(
    tenant_id UUID,
    total_jobs BIGINT,
    pending_jobs BIGINT,
    running_jobs BIGINT,
    completed_jobs BIGINT,
    failed_jobs BIGINT,
    dlq_jobs BIGINT,
    avg_completion_time_seconds DOUBLE PRECISION
) AS $$
BEGIN
    RETURN QUERY
    SELECT
        sj.tenant_id,
        COUNT(*) AS total_jobs,
        COUNT(*) FILTER (WHERE sj.status = 'pending') AS pending_jobs,
        COUNT(*) FILTER (WHERE sj.status = 'running') AS running_jobs,
        COUNT(*) FILTER (WHERE sj.status = 'completed') AS completed_jobs,
        COUNT(*) FILTER (WHERE sj.status = 'failed') AS failed_jobs,
        COUNT(*) FILTER (WHERE sj.status = 'dead_letter') AS dlq_jobs,
        AVG(EXTRACT(EPOCH FROM (sj.completed_at - sj.started_at)))
            FILTER (WHERE sj.status = 'completed' AND sj.started_at IS NOT NULL) AS avg_completion_time_seconds
    FROM supervised_jobs sj
    WHERE p_tenant_id IS NULL OR sj.tenant_id = p_tenant_id
    GROUP BY sj.tenant_id;
END;
$$ LANGUAGE plpgsql;

-- =============================================================================
-- RLS POLICIES
-- =============================================================================

ALTER TABLE supervised_jobs ENABLE ROW LEVEL SECURITY;

DROP POLICY IF EXISTS tenant_isolation_supervised_jobs ON supervised_jobs;
CREATE POLICY tenant_isolation_supervised_jobs ON supervised_jobs
    FOR ALL
    USING (tenant_id = current_setting('app.tenant_id', true)::uuid)
    WITH CHECK (tenant_id = current_setting('app.tenant_id', true)::uuid);

DROP POLICY IF EXISTS service_bypass_supervised_jobs ON supervised_jobs;
CREATE POLICY service_bypass_supervised_jobs ON supervised_jobs
    FOR ALL
    TO serialmemory_service
    USING (true)
    WITH CHECK (true);

-- =============================================================================
-- CLEANUP FUNCTIONS
-- =============================================================================

-- Clean up old completed jobs (retention policy)
CREATE OR REPLACE FUNCTION cleanup_old_jobs(
    p_retention_days INTEGER DEFAULT 30
)
RETURNS INTEGER AS $$
DECLARE
    v_count INTEGER;
BEGIN
    DELETE FROM supervised_jobs
    WHERE status IN ('completed', 'cancelled')
      AND completed_at < NOW() - (p_retention_days || ' days')::INTERVAL;

    GET DIAGNOSTICS v_count = ROW_COUNT;
    RETURN v_count;
END;
$$ LANGUAGE plpgsql;

-- =============================================================================
-- COMMENTS
-- =============================================================================

COMMENT ON TABLE supervised_jobs IS 'Background job tracking with heartbeats, retry, and dead-letter queue';
COMMENT ON COLUMN supervised_jobs.heartbeat_timeout_seconds IS 'Max seconds between heartbeats before job is considered stuck';
COMMENT ON COLUMN supervised_jobs.scheduled_retry_at IS 'When to retry a failed job (exponential backoff)';
COMMENT ON COLUMN supervised_jobs.moved_to_dlq_at IS 'When job was moved to dead-letter queue';
COMMENT ON COLUMN supervised_jobs.error_history IS 'Array of timestamped error messages from all attempts';
