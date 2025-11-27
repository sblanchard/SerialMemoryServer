-- Privacy-Safe Audit Log Schema
-- NEVER stores user content - only hashes, timestamps, counts, and tenant IDs

-- Audit operation enum
CREATE TYPE privacy_audit_operation AS ENUM (
    'MemoryCreate',
    'MemoryRead',
    'MemoryUpdate',
    'MemoryDelete',
    'MemorySearch',
    'MemoryExport',
    'EntityCreate',
    'EntityRead',
    'EntityUpdate',
    'EntityDelete',
    'BatchIngest',
    'BatchExport',
    'BatchReembed',
    'AdminAccess',
    'SettingsChange',
    'SchemaChange',
    'IntegrityCheck',
    'ContradictionScan',
    'HallucinationScan'
);

-- Privacy audit entries - NO raw content ever stored
CREATE TABLE privacy_audit_entries (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    sequence_number BIGSERIAL NOT NULL,
    tenant_id VARCHAR(100) NOT NULL,
    operation privacy_audit_operation NOT NULL,
    timestamp TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    -- Hashes only - NEVER raw content
    content_hash VARCHAR(64),          -- SHA-256 of content (for verification)
    query_hash VARCHAR(64),            -- SHA-256 of query (for search auditing)
    target_memory_id UUID,             -- Reference only, no content

    -- Counts only - no content
    item_count INTEGER,
    content_length INTEGER,            -- Length, not content
    success_count INTEGER,
    fail_count INTEGER,

    -- Chain integrity
    previous_hash VARCHAR(64) NOT NULL DEFAULT '',
    entry_hash VARCHAR(64) NOT NULL,

    -- Constraints
    CONSTRAINT unique_sequence UNIQUE (tenant_id, sequence_number)
);

-- Audit proofs for external verification
CREATE TABLE privacy_audit_proofs (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id VARCHAR(100) NOT NULL,
    from_timestamp TIMESTAMPTZ NOT NULL,
    to_timestamp TIMESTAMPTZ NOT NULL,
    generated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    -- Proof data
    is_valid BOOLEAN NOT NULL,
    total_entries INTEGER NOT NULL,
    verified_entries INTEGER NOT NULL,
    first_entry_hash VARCHAR(64) NOT NULL,
    last_entry_hash VARCHAR(64) NOT NULL,
    merkle_root VARCHAR(64) NOT NULL,

    -- Signature
    proof_signature VARCHAR(256) NOT NULL
);

-- Indexes for privacy audit entries
CREATE INDEX idx_privacy_audit_tenant ON privacy_audit_entries(tenant_id);
CREATE INDEX idx_privacy_audit_timestamp ON privacy_audit_entries(timestamp DESC);
CREATE INDEX idx_privacy_audit_operation ON privacy_audit_entries(operation);
CREATE INDEX idx_privacy_audit_sequence ON privacy_audit_entries(tenant_id, sequence_number);
CREATE INDEX idx_privacy_audit_memory ON privacy_audit_entries(target_memory_id) WHERE target_memory_id IS NOT NULL;

-- Indexes for proofs
CREATE INDEX idx_privacy_proofs_tenant ON privacy_audit_proofs(tenant_id);
CREATE INDEX idx_privacy_proofs_time ON privacy_audit_proofs(tenant_id, from_timestamp, to_timestamp);

-- Function to append audit entry with chain integrity
CREATE OR REPLACE FUNCTION append_privacy_audit(
    p_tenant_id VARCHAR(100),
    p_operation privacy_audit_operation,
    p_content_hash VARCHAR(64) DEFAULT NULL,
    p_query_hash VARCHAR(64) DEFAULT NULL,
    p_target_memory_id UUID DEFAULT NULL,
    p_item_count INTEGER DEFAULT NULL,
    p_content_length INTEGER DEFAULT NULL,
    p_success_count INTEGER DEFAULT NULL,
    p_fail_count INTEGER DEFAULT NULL
)
RETURNS privacy_audit_entries AS $$
DECLARE
    v_prev_hash VARCHAR(64);
    v_new_hash VARCHAR(64);
    v_timestamp TIMESTAMPTZ;
    v_result privacy_audit_entries;
BEGIN
    v_timestamp := NOW();

    -- Get previous hash (or empty for first entry)
    SELECT entry_hash INTO v_prev_hash
    FROM privacy_audit_entries
    WHERE tenant_id = p_tenant_id
    ORDER BY sequence_number DESC
    LIMIT 1
    FOR UPDATE;

    v_prev_hash := COALESCE(v_prev_hash, '');

    -- Compute entry hash
    v_new_hash := encode(sha256(
        (p_tenant_id || '|' ||
         p_operation::text || '|' ||
         v_timestamp::text || '|' ||
         COALESCE(p_content_hash, '') || '|' ||
         COALESCE(p_query_hash, '') || '|' ||
         COALESCE(p_target_memory_id::text, '') || '|' ||
         COALESCE(p_item_count::text, '') || '|' ||
         v_prev_hash)::bytea
    ), 'hex');

    INSERT INTO privacy_audit_entries (
        tenant_id, operation, timestamp,
        content_hash, query_hash, target_memory_id,
        item_count, content_length, success_count, fail_count,
        previous_hash, entry_hash
    ) VALUES (
        p_tenant_id, p_operation, v_timestamp,
        p_content_hash, p_query_hash, p_target_memory_id,
        p_item_count, p_content_length, p_success_count, p_fail_count,
        v_prev_hash, v_new_hash
    )
    RETURNING * INTO v_result;

    RETURN v_result;
END;
$$ LANGUAGE plpgsql;

-- Function to verify audit chain integrity
CREATE OR REPLACE FUNCTION verify_privacy_audit_chain(
    p_tenant_id VARCHAR(100),
    p_from_timestamp TIMESTAMPTZ DEFAULT NULL,
    p_to_timestamp TIMESTAMPTZ DEFAULT NULL
)
RETURNS TABLE (
    is_valid BOOLEAN,
    total_entries INTEGER,
    verified_entries INTEGER,
    first_issue_sequence BIGINT,
    first_issue_type TEXT,
    expected_hash VARCHAR(64),
    actual_hash VARCHAR(64)
) AS $$
DECLARE
    v_prev_hash VARCHAR(64) := '';
    v_computed_hash VARCHAR(64);
    v_entry RECORD;
    v_count INTEGER := 0;
    v_verified INTEGER := 0;
    v_is_valid BOOLEAN := TRUE;
    v_issue_seq BIGINT := NULL;
    v_issue_type TEXT := NULL;
    v_expected VARCHAR(64) := NULL;
    v_actual VARCHAR(64) := NULL;
BEGIN
    FOR v_entry IN
        SELECT *
        FROM privacy_audit_entries
        WHERE tenant_id = p_tenant_id
            AND (p_from_timestamp IS NULL OR timestamp >= p_from_timestamp)
            AND (p_to_timestamp IS NULL OR timestamp <= p_to_timestamp)
        ORDER BY sequence_number
    LOOP
        v_count := v_count + 1;

        -- Compute expected hash
        v_computed_hash := encode(sha256(
            (v_entry.tenant_id || '|' ||
             v_entry.operation::text || '|' ||
             v_entry.timestamp::text || '|' ||
             COALESCE(v_entry.content_hash, '') || '|' ||
             COALESCE(v_entry.query_hash, '') || '|' ||
             COALESCE(v_entry.target_memory_id::text, '') || '|' ||
             COALESCE(v_entry.item_count::text, '') || '|' ||
             v_prev_hash)::bytea
        ), 'hex');

        -- Check chain integrity
        IF v_entry.previous_hash != v_prev_hash THEN
            IF v_is_valid THEN
                v_is_valid := FALSE;
                v_issue_seq := v_entry.sequence_number;
                v_issue_type := 'chain_broken';
                v_expected := v_prev_hash;
                v_actual := v_entry.previous_hash;
            END IF;
        ELSIF v_entry.entry_hash != v_computed_hash THEN
            IF v_is_valid THEN
                v_is_valid := FALSE;
                v_issue_seq := v_entry.sequence_number;
                v_issue_type := 'hash_mismatch';
                v_expected := v_computed_hash;
                v_actual := v_entry.entry_hash;
            END IF;
        ELSE
            v_verified := v_verified + 1;
        END IF;

        v_prev_hash := v_entry.entry_hash;
    END LOOP;

    RETURN QUERY SELECT v_is_valid, v_count, v_verified, v_issue_seq, v_issue_type, v_expected, v_actual;
END;
$$ LANGUAGE plpgsql;

-- Function to compute Merkle root for proof
CREATE OR REPLACE FUNCTION compute_privacy_audit_merkle_root(
    p_tenant_id VARCHAR(100),
    p_from_timestamp TIMESTAMPTZ,
    p_to_timestamp TIMESTAMPTZ
)
RETURNS VARCHAR(64) AS $$
DECLARE
    v_hashes TEXT[];
    v_level TEXT[];
    v_combined TEXT;
    i INTEGER;
BEGIN
    -- Collect all entry hashes
    SELECT ARRAY_AGG(entry_hash ORDER BY sequence_number)
    INTO v_hashes
    FROM privacy_audit_entries
    WHERE tenant_id = p_tenant_id
        AND timestamp >= p_from_timestamp
        AND timestamp <= p_to_timestamp;

    IF v_hashes IS NULL OR array_length(v_hashes, 1) = 0 THEN
        RETURN '';
    END IF;

    -- Build Merkle tree
    WHILE array_length(v_hashes, 1) > 1 LOOP
        v_level := ARRAY[]::TEXT[];
        FOR i IN 1..array_length(v_hashes, 1) BY 2 LOOP
            IF i + 1 <= array_length(v_hashes, 1) THEN
                v_combined := v_hashes[i] || v_hashes[i + 1];
            ELSE
                v_combined := v_hashes[i] || v_hashes[i]; -- Duplicate odd leaf
            END IF;
            v_level := array_append(v_level, encode(sha256(v_combined::bytea), 'hex'));
        END LOOP;
        v_hashes := v_level;
    END LOOP;

    RETURN v_hashes[1];
END;
$$ LANGUAGE plpgsql;

-- Function to get aggregated stats
CREATE OR REPLACE FUNCTION get_privacy_audit_stats(
    p_tenant_id VARCHAR(100),
    p_from_timestamp TIMESTAMPTZ DEFAULT NULL,
    p_to_timestamp TIMESTAMPTZ DEFAULT NULL
)
RETURNS TABLE (
    total_operations BIGINT,
    operation_type TEXT,
    operation_count BIGINT,
    total_items_accessed BIGINT
) AS $$
BEGIN
    RETURN QUERY
    SELECT
        COUNT(*)::BIGINT AS total_operations,
        operation::text AS operation_type,
        COUNT(*)::BIGINT AS operation_count,
        COALESCE(SUM(item_count), 0)::BIGINT AS total_items_accessed
    FROM privacy_audit_entries
    WHERE tenant_id = p_tenant_id
        AND (p_from_timestamp IS NULL OR timestamp >= p_from_timestamp)
        AND (p_to_timestamp IS NULL OR timestamp <= p_to_timestamp)
    GROUP BY operation;
END;
$$ LANGUAGE plpgsql;
