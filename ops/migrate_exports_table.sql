-- =============================================================================
-- EXPORTS TABLE - tracks export jobs triggered from the dashboard
-- =============================================================================

CREATE TABLE IF NOT EXISTS exports (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL,
    format TEXT NOT NULL,
    status TEXT NOT NULL DEFAULT 'pending',
    file_path TEXT,
    file_size_bytes BIGINT,
    options JSONB,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    completed_at TIMESTAMPTZ,
    error_message TEXT
);

CREATE INDEX IF NOT EXISTS idx_exports_tenant ON exports(tenant_id, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_exports_status ON exports(status) WHERE status != 'completed';

-- RLS
ALTER TABLE exports ENABLE ROW LEVEL SECURITY;

DO $$
BEGIN
    DROP POLICY IF EXISTS rls_exports ON exports;
    CREATE POLICY rls_exports ON exports FOR ALL
        USING (
            current_setting('app.role', true) = 'internal_admin'
            OR tenant_id = current_setting('app.current_tenant_id', true)::uuid
        );
    RAISE NOTICE 'Created RLS policy on exports';
END $$;

GRANT SELECT, INSERT, UPDATE ON exports TO contextdb_app;
