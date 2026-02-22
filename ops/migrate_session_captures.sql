-- Migration: Server-side session capture staging table
-- Stores JSONL-style capture entries received over HTTP.
-- Captures are staged here, then drained into memories.
-- Separate from the memories table to keep capture data isolated.

CREATE TABLE IF NOT EXISTS session_captures (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id       TEXT NOT NULL DEFAULT current_setting('app.tenant_id', true),
    session_id      TEXT,
    ts              TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    tool            TEXT,
    file            TEXT,
    result          TEXT,
    raw_json        JSONB,
    drained         BOOLEAN NOT NULL DEFAULT FALSE,
    drained_at      TIMESTAMPTZ,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- RLS policy for tenant isolation
ALTER TABLE session_captures ENABLE ROW LEVEL SECURITY;

CREATE POLICY session_captures_tenant_policy ON session_captures
    USING (tenant_id = current_setting('app.tenant_id', true))
    WITH CHECK (tenant_id = current_setting('app.tenant_id', true));

-- Indexes for common queries
CREATE INDEX IF NOT EXISTS idx_session_captures_drained ON session_captures (drained) WHERE drained = FALSE;
CREATE INDEX IF NOT EXISTS idx_session_captures_session ON session_captures (session_id) WHERE session_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_session_captures_ts ON session_captures (ts DESC);
CREATE INDEX IF NOT EXISTS idx_session_captures_tenant ON session_captures (tenant_id);

-- Record migration
INSERT INTO schema_migrations (name) VALUES ('migrate_session_captures')
ON CONFLICT (name) DO NOTHING;
