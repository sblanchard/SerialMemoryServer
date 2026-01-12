-- Migration: Privacy & Integrity Layer
-- Adds tamper-evident hashing, proof chains, and audit trails

-- ==========================================
-- 1. Add integrity columns to memories table
-- ==========================================

ALTER TABLE memories
ADD COLUMN IF NOT EXISTS content_hash TEXT,
ADD COLUMN IF NOT EXISTS chain_hash TEXT,
ADD COLUMN IF NOT EXISTS integrity_status TEXT DEFAULT 'pending',
ADD COLUMN IF NOT EXISTS proof_bundle JSONB;

CREATE INDEX IF NOT EXISTS idx_memories_content_hash ON memories(content_hash);
CREATE INDEX IF NOT EXISTS idx_memories_chain_hash ON memories(chain_hash);
CREATE INDEX IF NOT EXISTS idx_memories_integrity_status ON memories(tenant_id, integrity_status);

-- ==========================================
-- 2. Tenant Privacy Settings
-- ==========================================

CREATE TABLE IF NOT EXISTS tenant_privacy_settings (
    tenant_id UUID PRIMARY KEY REFERENCES tenants(id),
    enable_privacy_mode BOOLEAN NOT NULL DEFAULT FALSE,
    enable_hash_verification BOOLEAN NOT NULL DEFAULT TRUE,
    enable_audit_logs BOOLEAN NOT NULL DEFAULT TRUE,
    hash_algorithm TEXT NOT NULL DEFAULT 'SHA256' CHECK (hash_algorithm IN ('SHA256', 'BLAKE3')),
    auto_verify_interval_hours INTEGER NOT NULL DEFAULT 24,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Enable RLS
ALTER TABLE tenant_privacy_settings ENABLE ROW LEVEL SECURITY;

DROP POLICY IF EXISTS tenant_privacy_settings_tenant_policy ON tenant_privacy_settings;
CREATE POLICY tenant_privacy_settings_tenant_policy ON tenant_privacy_settings
    FOR ALL USING (tenant_id = current_setting('app.tenant_id', true)::uuid);

GRANT ALL ON tenant_privacy_settings TO serialmemory;

-- ==========================================
-- 3. Privacy Audit Entries
-- ==========================================

CREATE TABLE IF NOT EXISTS privacy_audit_entries (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL REFERENCES tenants(id),
    user_id TEXT,
    action TEXT NOT NULL,
    memory_id UUID REFERENCES memories(id),
    timestamp TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    ip_address TEXT,
    user_agent TEXT,
    integrity_valid BOOLEAN,
    details JSONB,
    request_id UUID
);

CREATE INDEX IF NOT EXISTS idx_privacy_audit_tenant ON privacy_audit_entries(tenant_id);
CREATE INDEX IF NOT EXISTS idx_privacy_audit_timestamp ON privacy_audit_entries(tenant_id, timestamp DESC);
CREATE INDEX IF NOT EXISTS idx_privacy_audit_action ON privacy_audit_entries(tenant_id, action);
CREATE INDEX IF NOT EXISTS idx_privacy_audit_memory ON privacy_audit_entries(memory_id) WHERE memory_id IS NOT NULL;

-- Enable RLS
ALTER TABLE privacy_audit_entries ENABLE ROW LEVEL SECURITY;

DROP POLICY IF EXISTS privacy_audit_entries_tenant_policy ON privacy_audit_entries;
CREATE POLICY privacy_audit_entries_tenant_policy ON privacy_audit_entries
    FOR ALL USING (tenant_id = current_setting('app.tenant_id', true)::uuid);

GRANT ALL ON privacy_audit_entries TO serialmemory;

-- ==========================================
-- 4. Integrity Verification Runs
-- ==========================================

CREATE TABLE IF NOT EXISTS integrity_verification_runs (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL REFERENCES tenants(id),
    started_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    completed_at TIMESTAMPTZ,
    status TEXT NOT NULL DEFAULT 'running' CHECK (status IN ('running', 'completed', 'failed')),
    total_memories INTEGER,
    verified_count INTEGER DEFAULT 0,
    invalid_count INTEGER DEFAULT 0,
    orphan_count INTEGER DEFAULT 0,
    error_message TEXT,
    triggered_by TEXT NOT NULL DEFAULT 'system'
);

CREATE INDEX IF NOT EXISTS idx_integrity_runs_tenant ON integrity_verification_runs(tenant_id);
CREATE INDEX IF NOT EXISTS idx_integrity_runs_status ON integrity_verification_runs(tenant_id, status);

-- Enable RLS
ALTER TABLE integrity_verification_runs ENABLE ROW LEVEL SECURITY;

DROP POLICY IF EXISTS integrity_verification_runs_tenant_policy ON integrity_verification_runs;
CREATE POLICY integrity_verification_runs_tenant_policy ON integrity_verification_runs
    FOR ALL USING (tenant_id = current_setting('app.tenant_id', true)::uuid);

GRANT ALL ON integrity_verification_runs TO serialmemory;

-- ==========================================
-- 5. Chain Anchors (First memory per sequence)
-- ==========================================

CREATE TABLE IF NOT EXISTS integrity_chain_anchors (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL REFERENCES tenants(id),
    memory_id UUID NOT NULL REFERENCES memories(id),
    chain_hash TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    verified_at TIMESTAMPTZ,
    is_valid BOOLEAN DEFAULT TRUE
);

CREATE UNIQUE INDEX IF NOT EXISTS idx_chain_anchors_tenant_memory ON integrity_chain_anchors(tenant_id, memory_id);

-- Enable RLS
ALTER TABLE integrity_chain_anchors ENABLE ROW LEVEL SECURITY;

DROP POLICY IF EXISTS integrity_chain_anchors_tenant_policy ON integrity_chain_anchors;
CREATE POLICY integrity_chain_anchors_tenant_policy ON integrity_chain_anchors
    FOR ALL USING (tenant_id = current_setting('app.tenant_id', true)::uuid);

GRANT ALL ON integrity_chain_anchors TO serialmemory;

-- ==========================================
-- 6. Helper function to get previous chain hash
-- ==========================================

CREATE OR REPLACE FUNCTION get_previous_chain_hash(p_tenant_id UUID, p_memory_id UUID)
RETURNS TEXT AS $$
DECLARE
    prev_hash TEXT;
    current_created_at TIMESTAMPTZ;
BEGIN
    -- Get current memory's created_at
    SELECT created_at INTO current_created_at
    FROM memories
    WHERE id = p_memory_id AND tenant_id = p_tenant_id;

    -- Find the previous memory's chain_hash
    SELECT chain_hash INTO prev_hash
    FROM memories
    WHERE tenant_id = p_tenant_id
      AND created_at < current_created_at
      AND chain_hash IS NOT NULL
    ORDER BY created_at DESC
    LIMIT 1;

    RETURN COALESCE(prev_hash, 'GENESIS');
END;
$$ LANGUAGE plpgsql;

SELECT 'Privacy & Integrity migration complete' as status;
