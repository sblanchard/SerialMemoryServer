-- Self-Healing Recommendation Tables
-- For ML-style anomaly detection and healing recommendations

-- Main recommendations table
CREATE TABLE IF NOT EXISTS self_healing_recommendations (
    id UUID PRIMARY KEY,
    anomaly_type TEXT NOT NULL,
    anomaly_id UUID,
    confidence DOUBLE PRECISION NOT NULL DEFAULT 0.5,
    severity DOUBLE PRECISION NOT NULL DEFAULT 0.5,
    recommended_action TEXT NOT NULL,
    target_memory_id UUID REFERENCES memories(id) ON DELETE SET NULL,
    target_entity_id UUID REFERENCES entities(id) ON DELETE SET NULL,
    tenant_id UUID REFERENCES tenants(id) ON DELETE CASCADE,
    reason TEXT NOT NULL,
    status TEXT NOT NULL DEFAULT 'Pending',
    context JSONB DEFAULT '{}',
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    applied_at TIMESTAMPTZ,
    applied_by TEXT,
    dismissed_reason TEXT,
    worker_id TEXT,
    CONSTRAINT valid_status CHECK (status IN ('Pending', 'Applied', 'Dismissed', 'Failed', 'Expired'))
);

-- Indexes for recommendations
CREATE INDEX IF NOT EXISTS idx_recommendations_status ON self_healing_recommendations(status);
CREATE INDEX IF NOT EXISTS idx_recommendations_tenant ON self_healing_recommendations(tenant_id);
CREATE INDEX IF NOT EXISTS idx_recommendations_severity ON self_healing_recommendations(severity DESC);
CREATE INDEX IF NOT EXISTS idx_recommendations_created ON self_healing_recommendations(created_at DESC);
CREATE INDEX IF NOT EXISTS idx_recommendations_target_memory ON self_healing_recommendations(target_memory_id) WHERE target_memory_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_recommendations_target_entity ON self_healing_recommendations(target_entity_id) WHERE target_entity_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_recommendations_anomaly_type ON self_healing_recommendations(anomaly_type);

-- Memory re-embed jobs queue
CREATE TABLE IF NOT EXISTS memory_reembed_jobs (
    id UUID PRIMARY KEY,
    memory_id UUID NOT NULL REFERENCES memories(id) ON DELETE CASCADE,
    status TEXT NOT NULL DEFAULT 'pending',
    error TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    started_at TIMESTAMPTZ,
    completed_at TIMESTAMPTZ,
    CONSTRAINT valid_reembed_status CHECK (status IN ('pending', 'processing', 'completed', 'failed'))
);

CREATE INDEX IF NOT EXISTS idx_reembed_jobs_status ON memory_reembed_jobs(status);
CREATE INDEX IF NOT EXISTS idx_reembed_jobs_memory ON memory_reembed_jobs(memory_id);

-- Memory recrawl jobs queue (for entity extraction)
CREATE TABLE IF NOT EXISTS memory_recrawl_jobs (
    id UUID PRIMARY KEY,
    memory_id UUID REFERENCES memories(id) ON DELETE CASCADE,
    entity_id UUID REFERENCES entities(id) ON DELETE CASCADE,
    status TEXT NOT NULL DEFAULT 'pending',
    error TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    started_at TIMESTAMPTZ,
    completed_at TIMESTAMPTZ,
    CONSTRAINT valid_recrawl_status CHECK (status IN ('pending', 'processing', 'completed', 'failed'))
);

CREATE INDEX IF NOT EXISTS idx_recrawl_jobs_status ON memory_recrawl_jobs(status);
CREATE INDEX IF NOT EXISTS idx_recrawl_jobs_memory ON memory_recrawl_jobs(memory_id) WHERE memory_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_recrawl_jobs_entity ON memory_recrawl_jobs(entity_id) WHERE entity_id IS NOT NULL;

-- Manual review items (flagged for human review)
CREATE TABLE IF NOT EXISTS manual_review_items (
    id UUID PRIMARY KEY,
    item_type TEXT NOT NULL,
    target_id UUID,
    reason TEXT NOT NULL,
    severity DOUBLE PRECISION NOT NULL DEFAULT 0.5,
    status TEXT NOT NULL DEFAULT 'pending',
    reviewed_by TEXT,
    reviewed_at TIMESTAMPTZ,
    resolution TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT valid_review_status CHECK (status IN ('pending', 'reviewed', 'dismissed', 'resolved'))
);

CREATE INDEX IF NOT EXISTS idx_review_items_status ON manual_review_items(status);
CREATE INDEX IF NOT EXISTS idx_review_items_severity ON manual_review_items(severity DESC);
CREATE INDEX IF NOT EXISTS idx_review_items_type ON manual_review_items(item_type);

-- Security events for audit logging (if not exists)
CREATE TABLE IF NOT EXISTS security_events (
    id UUID PRIMARY KEY,
    event_type TEXT NOT NULL,
    category TEXT NOT NULL,
    severity TEXT NOT NULL DEFAULT 'info',
    message TEXT NOT NULL,
    metadata JSONB DEFAULT '{}',
    tenant_id UUID REFERENCES tenants(id) ON DELETE CASCADE,
    user_id UUID,
    ip_address INET,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_security_events_type ON security_events(event_type);
CREATE INDEX IF NOT EXISTS idx_security_events_category ON security_events(category);
CREATE INDEX IF NOT EXISTS idx_security_events_severity ON security_events(severity);
CREATE INDEX IF NOT EXISTS idx_security_events_created ON security_events(created_at DESC);
CREATE INDEX IF NOT EXISTS idx_security_events_tenant ON security_events(tenant_id) WHERE tenant_id IS NOT NULL;

-- Add is_lab_tenant to tenants if not exists
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                   WHERE table_name = 'tenants' AND column_name = 'is_lab_tenant') THEN
        ALTER TABLE tenants ADD COLUMN is_lab_tenant BOOLEAN NOT NULL DEFAULT FALSE;
    END IF;
END $$;

-- Anomaly detection cache (for performance)
CREATE TABLE IF NOT EXISTS anomaly_cache (
    id UUID PRIMARY KEY,
    anomaly_type TEXT NOT NULL,
    memory_id UUID REFERENCES memories(id) ON DELETE CASCADE,
    entity_id UUID REFERENCES entities(id) ON DELETE CASCADE,
    tenant_id UUID REFERENCES tenants(id) ON DELETE CASCADE,
    severity_score DOUBLE PRECISION NOT NULL,
    description TEXT,
    evidence JSONB DEFAULT '{}',
    detected_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    expires_at TIMESTAMPTZ NOT NULL DEFAULT NOW() + INTERVAL '1 hour'
);

CREATE INDEX IF NOT EXISTS idx_anomaly_cache_type ON anomaly_cache(anomaly_type);
CREATE INDEX IF NOT EXISTS idx_anomaly_cache_expires ON anomaly_cache(expires_at);
CREATE INDEX IF NOT EXISTS idx_anomaly_cache_tenant ON anomaly_cache(tenant_id) WHERE tenant_id IS NOT NULL;

-- Cleanup function for expired cache entries
CREATE OR REPLACE FUNCTION cleanup_anomaly_cache()
RETURNS void AS $$
BEGIN
    DELETE FROM anomaly_cache WHERE expires_at < NOW();
END;
$$ LANGUAGE plpgsql;

-- Row Level Security for multi-tenant isolation
ALTER TABLE self_healing_recommendations ENABLE ROW LEVEL SECURITY;
ALTER TABLE memory_reembed_jobs ENABLE ROW LEVEL SECURITY;
ALTER TABLE memory_recrawl_jobs ENABLE ROW LEVEL SECURITY;
ALTER TABLE manual_review_items ENABLE ROW LEVEL SECURITY;
ALTER TABLE security_events ENABLE ROW LEVEL SECURITY;
ALTER TABLE anomaly_cache ENABLE ROW LEVEL SECURITY;

-- Policies for self_healing_recommendations
CREATE POLICY IF NOT EXISTS recommendations_tenant_isolation ON self_healing_recommendations
    FOR ALL USING (
        tenant_id IS NULL
        OR tenant_id = COALESCE(current_setting('app.current_tenant_id', true)::uuid, tenant_id)
    );

-- Policies for security_events
CREATE POLICY IF NOT EXISTS security_events_tenant_isolation ON security_events
    FOR ALL USING (
        tenant_id IS NULL
        OR tenant_id = COALESCE(current_setting('app.current_tenant_id', true)::uuid, tenant_id)
    );

-- Policies for anomaly_cache
CREATE POLICY IF NOT EXISTS anomaly_cache_tenant_isolation ON anomaly_cache
    FOR ALL USING (
        tenant_id IS NULL
        OR tenant_id = COALESCE(current_setting('app.current_tenant_id', true)::uuid, tenant_id)
    );

-- Grant permissions (adjust role names as needed)
GRANT ALL ON self_healing_recommendations TO postgres;
GRANT ALL ON memory_reembed_jobs TO postgres;
GRANT ALL ON memory_recrawl_jobs TO postgres;
GRANT ALL ON manual_review_items TO postgres;
GRANT ALL ON security_events TO postgres;
GRANT ALL ON anomaly_cache TO postgres;
