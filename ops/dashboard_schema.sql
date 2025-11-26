-- Dashboard Schema - Export and Deletion Request Tables
-- Run this AFTER multi_tenant_schema.sql

-- =============================================================================
-- EXPORT REQUESTS TABLE
-- =============================================================================

CREATE TABLE IF NOT EXISTS export_requests (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    requested_by TEXT NOT NULL,
    status TEXT NOT NULL DEFAULT 'pending'
        CHECK (status IN ('pending', 'processing', 'completed', 'failed', 'expired')),
    options JSONB NOT NULL DEFAULT '{}',
    requested_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    started_at TIMESTAMPTZ,
    completed_at TIMESTAMPTZ,
    download_url TEXT,
    download_expires_at TIMESTAMPTZ,
    file_size_bytes BIGINT,
    error_message TEXT,
    metadata JSONB DEFAULT '{}'
);

CREATE INDEX IF NOT EXISTS idx_export_requests_tenant ON export_requests(tenant_id);
CREATE INDEX IF NOT EXISTS idx_export_requests_status ON export_requests(tenant_id, status);
CREATE INDEX IF NOT EXISTS idx_export_requests_requested_at ON export_requests(requested_at DESC);

-- =============================================================================
-- DELETION REQUESTS TABLE
-- =============================================================================

CREATE TABLE IF NOT EXISTS deletion_requests (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    requested_by TEXT NOT NULL,
    status TEXT NOT NULL DEFAULT 'pending'
        CHECK (status IN ('pending', 'processing', 'completed', 'cancelled')),
    requested_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    scheduled_for TIMESTAMPTZ NOT NULL,
    cancelled_at TIMESTAMPTZ,
    cancellation_reason TEXT,
    completed_at TIMESTAMPTZ,
    metadata JSONB DEFAULT '{}'
);

CREATE INDEX IF NOT EXISTS idx_deletion_requests_tenant ON deletion_requests(tenant_id);
CREATE INDEX IF NOT EXISTS idx_deletion_requests_status ON deletion_requests(status);
CREATE INDEX IF NOT EXISTS idx_deletion_requests_scheduled ON deletion_requests(scheduled_for)
    WHERE status = 'pending';

-- =============================================================================
-- TENANT PLANS TABLE
-- =============================================================================

CREATE TABLE IF NOT EXISTS tenant_plans (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    plan_name TEXT NOT NULL UNIQUE,
    display_name TEXT NOT NULL,
    credits_per_cycle NUMERIC NOT NULL DEFAULT 1000,
    cycle_days INTEGER NOT NULL DEFAULT 30,
    max_memories INTEGER,
    max_entities INTEGER,
    rate_limit_per_minute INTEGER DEFAULT 60,
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    metadata JSONB DEFAULT '{}',
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Insert default plans
INSERT INTO tenant_plans (plan_name, display_name, credits_per_cycle, cycle_days, max_memories, max_entities, rate_limit_per_minute)
VALUES
    ('free', 'Free Tier', 1000, 30, 1000, 500, 30),
    ('pro', 'Professional', 10000, 30, 50000, 25000, 120),
    ('enterprise', 'Enterprise', 100000, 30, NULL, NULL, 600),
    ('self', 'Self-Hosted', 999999999, 365, NULL, NULL, 1000)
ON CONFLICT (plan_name) DO NOTHING;

-- =============================================================================
-- RLS POLICIES
-- =============================================================================

-- Export requests
ALTER TABLE export_requests ENABLE ROW LEVEL SECURITY;

DROP POLICY IF EXISTS tenant_isolation_export_requests ON export_requests;
CREATE POLICY tenant_isolation_export_requests ON export_requests
    FOR ALL
    USING (tenant_id = current_setting('app.tenant_id', true)::uuid)
    WITH CHECK (tenant_id = current_setting('app.tenant_id', true)::uuid);

DROP POLICY IF EXISTS service_bypass_export_requests ON export_requests;
CREATE POLICY service_bypass_export_requests ON export_requests
    FOR ALL
    TO serialmemory_service
    USING (true)
    WITH CHECK (true);

-- Deletion requests
ALTER TABLE deletion_requests ENABLE ROW LEVEL SECURITY;

DROP POLICY IF EXISTS tenant_isolation_deletion_requests ON deletion_requests;
CREATE POLICY tenant_isolation_deletion_requests ON deletion_requests
    FOR ALL
    USING (tenant_id = current_setting('app.tenant_id', true)::uuid)
    WITH CHECK (tenant_id = current_setting('app.tenant_id', true)::uuid);

DROP POLICY IF EXISTS service_bypass_deletion_requests ON deletion_requests;
CREATE POLICY service_bypass_deletion_requests ON deletion_requests
    FOR ALL
    TO serialmemory_service
    USING (true)
    WITH CHECK (true);

-- =============================================================================
-- HELPER FUNCTIONS
-- =============================================================================

-- Function to get pending exports for processing
CREATE OR REPLACE FUNCTION get_pending_exports(p_limit INTEGER DEFAULT 10)
RETURNS SETOF export_requests AS $$
    SELECT * FROM export_requests
    WHERE status = 'pending'
    ORDER BY requested_at ASC
    LIMIT p_limit
    FOR UPDATE SKIP LOCKED;
$$ LANGUAGE sql;

-- Function to get due deletions for processing
CREATE OR REPLACE FUNCTION get_due_deletions()
RETURNS SETOF deletion_requests AS $$
    SELECT * FROM deletion_requests
    WHERE status = 'pending' AND scheduled_for <= NOW()
    ORDER BY scheduled_for ASC
    FOR UPDATE SKIP LOCKED;
$$ LANGUAGE sql;

-- Function to expire old exports (downloads expire after 24 hours)
CREATE OR REPLACE FUNCTION expire_old_exports()
RETURNS INTEGER AS $$
DECLARE
    v_count INTEGER;
BEGIN
    UPDATE export_requests
    SET status = 'expired'
    WHERE status = 'completed'
      AND download_expires_at < NOW();

    GET DIAGNOSTICS v_count = ROW_COUNT;
    RETURN v_count;
END;
$$ LANGUAGE plpgsql;

-- =============================================================================
-- TRIGGERS
-- =============================================================================

DROP TRIGGER IF EXISTS update_tenant_plans_updated_at ON tenant_plans;
CREATE TRIGGER update_tenant_plans_updated_at
    BEFORE UPDATE ON tenant_plans
    FOR EACH ROW EXECUTE FUNCTION update_updated_at_column();

-- =============================================================================
-- COMMENTS
-- =============================================================================

COMMENT ON TABLE export_requests IS 'Tracks data export requests for tenants';
COMMENT ON TABLE deletion_requests IS 'Tracks tenant deletion requests with 30-day grace period';
COMMENT ON TABLE tenant_plans IS 'Defines subscription plan limits and features';
COMMENT ON COLUMN deletion_requests.scheduled_for IS 'When the deletion will actually occur (30 days after request)';
COMMENT ON COLUMN export_requests.download_expires_at IS 'When the download URL expires (typically 24 hours)';
