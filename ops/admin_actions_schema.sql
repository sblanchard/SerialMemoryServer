-- Admin Actions Schema - Tamper-Evident Audit Log for Admin Operations
-- Run this AFTER multi_tenant_schema.sql
-- Creates hash-chained audit log specifically for admin tool invocations

-- =============================================================================
-- ADMIN ACTIONS TABLE
-- =============================================================================

CREATE TABLE IF NOT EXISTS admin_actions (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL REFERENCES tenants(id) ON DELETE RESTRICT,
    user_id TEXT NOT NULL,
    tool_name TEXT NOT NULL,
    params_hash TEXT NOT NULL,
    timestamp TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    prev_hash TEXT NOT NULL DEFAULT '',
    hash TEXT NOT NULL,
    execution_ms INTEGER,
    success BOOLEAN NOT NULL DEFAULT TRUE,
    error_message TEXT,
    metadata JSONB
);

-- Indexes for querying
CREATE INDEX IF NOT EXISTS idx_admin_actions_tenant ON admin_actions(tenant_id);
CREATE INDEX IF NOT EXISTS idx_admin_actions_tenant_timestamp ON admin_actions(tenant_id, timestamp DESC);
CREATE INDEX IF NOT EXISTS idx_admin_actions_user ON admin_actions(tenant_id, user_id);
CREATE INDEX IF NOT EXISTS idx_admin_actions_tool ON admin_actions(tenant_id, tool_name);

-- =============================================================================
-- APPEND FUNCTION WITH HASH CHAINING
-- =============================================================================

CREATE OR REPLACE FUNCTION append_admin_action(
    p_tenant_id UUID,
    p_user_id TEXT,
    p_tool_name TEXT,
    p_params_hash TEXT,
    p_metadata JSONB DEFAULT NULL
) RETURNS admin_actions AS $$
DECLARE
    v_prev_hash TEXT;
    v_hash TEXT;
    v_timestamp TIMESTAMPTZ := NOW();
    v_id UUID := gen_random_uuid();
    v_result admin_actions;
BEGIN
    -- Get the previous hash for this tenant (chain per tenant)
    SELECT hash INTO v_prev_hash
    FROM admin_actions
    WHERE tenant_id = p_tenant_id
    ORDER BY timestamp DESC
    LIMIT 1;

    -- Use empty string if no previous entry
    v_prev_hash := COALESCE(v_prev_hash, '');

    -- Compute hash: SHA256(tenant_id | user_id | tool_name | params_hash | timestamp | prev_hash)
    v_hash := encode(sha256(
        (p_tenant_id::text || '|' ||
         p_user_id || '|' ||
         p_tool_name || '|' ||
         p_params_hash || '|' ||
         v_timestamp::text || '|' ||
         v_prev_hash)::bytea
    ), 'hex');

    -- Insert the action
    INSERT INTO admin_actions (id, tenant_id, user_id, tool_name, params_hash, timestamp, prev_hash, hash, metadata)
    VALUES (v_id, p_tenant_id, p_user_id, p_tool_name, p_params_hash, v_timestamp, v_prev_hash, v_hash, p_metadata)
    RETURNING * INTO v_result;

    RETURN v_result;
END;
$$ LANGUAGE plpgsql;

-- =============================================================================
-- COMPLETE ACTION FUNCTION (updates execution result)
-- =============================================================================

CREATE OR REPLACE FUNCTION complete_admin_action(
    p_action_id UUID,
    p_execution_ms INTEGER,
    p_success BOOLEAN,
    p_error_message TEXT DEFAULT NULL
) RETURNS VOID AS $$
BEGIN
    UPDATE admin_actions
    SET execution_ms = p_execution_ms,
        success = p_success,
        error_message = p_error_message
    WHERE id = p_action_id;
END;
$$ LANGUAGE plpgsql;

-- =============================================================================
-- HASH CHAIN VERIFICATION FUNCTION
-- =============================================================================

CREATE OR REPLACE FUNCTION verify_admin_action_chain(p_tenant_id UUID)
RETURNS TABLE(
    is_valid BOOLEAN,
    total_entries INTEGER,
    verified_entries INTEGER,
    first_broken_id UUID,
    first_broken_reason TEXT
) AS $$
DECLARE
    v_prev_hash TEXT := '';
    v_expected_hash TEXT;
    v_row RECORD;
    v_count INTEGER := 0;
    v_verified INTEGER := 0;
    v_broken_id UUID := NULL;
    v_broken_reason TEXT := NULL;
BEGIN
    -- Iterate through all entries in order
    FOR v_row IN
        SELECT id, tenant_id, user_id, tool_name, params_hash, timestamp, prev_hash, hash
        FROM admin_actions
        WHERE tenant_id = p_tenant_id
        ORDER BY timestamp ASC
    LOOP
        v_count := v_count + 1;

        -- Verify prev_hash matches our tracked previous hash
        IF v_row.prev_hash != v_prev_hash AND v_broken_id IS NULL THEN
            v_broken_id := v_row.id;
            v_broken_reason := 'prev_hash mismatch: expected ' || LEFT(v_prev_hash, 16) ||
                               '..., got ' || LEFT(v_row.prev_hash, 16) || '...';
        END IF;

        -- Recompute the hash
        v_expected_hash := encode(sha256(
            (v_row.tenant_id::text || '|' ||
             v_row.user_id || '|' ||
             v_row.tool_name || '|' ||
             v_row.params_hash || '|' ||
             v_row.timestamp::text || '|' ||
             v_row.prev_hash)::bytea
        ), 'hex');

        -- Verify hash matches
        IF v_row.hash != v_expected_hash AND v_broken_id IS NULL THEN
            v_broken_id := v_row.id;
            v_broken_reason := 'hash mismatch: expected ' || LEFT(v_expected_hash, 16) ||
                               '..., got ' || LEFT(v_row.hash, 16) || '...';
        END IF;

        IF v_broken_id IS NULL THEN
            v_verified := v_verified + 1;
        END IF;

        -- Update prev_hash for next iteration
        v_prev_hash := v_row.hash;
    END LOOP;

    is_valid := v_broken_id IS NULL;
    total_entries := v_count;
    verified_entries := v_verified;
    first_broken_id := v_broken_id;
    first_broken_reason := v_broken_reason;

    RETURN NEXT;
END;
$$ LANGUAGE plpgsql;

-- =============================================================================
-- QUERY FUNCTIONS
-- =============================================================================

-- Get recent admin actions for a tenant
CREATE OR REPLACE FUNCTION get_recent_admin_actions(
    p_tenant_id UUID,
    p_limit INTEGER DEFAULT 100
)
RETURNS SETOF admin_actions AS $$
    SELECT * FROM admin_actions
    WHERE tenant_id = p_tenant_id
    ORDER BY timestamp DESC
    LIMIT p_limit;
$$ LANGUAGE sql STABLE;

-- Get admin actions by user
CREATE OR REPLACE FUNCTION get_admin_actions_by_user(
    p_tenant_id UUID,
    p_user_id TEXT,
    p_limit INTEGER DEFAULT 100
)
RETURNS SETOF admin_actions AS $$
    SELECT * FROM admin_actions
    WHERE tenant_id = p_tenant_id
      AND user_id = p_user_id
    ORDER BY timestamp DESC
    LIMIT p_limit;
$$ LANGUAGE sql STABLE;

-- Get admin actions by tool
CREATE OR REPLACE FUNCTION get_admin_actions_by_tool(
    p_tenant_id UUID,
    p_tool_name TEXT,
    p_limit INTEGER DEFAULT 100
)
RETURNS SETOF admin_actions AS $$
    SELECT * FROM admin_actions
    WHERE tenant_id = p_tenant_id
      AND tool_name = p_tool_name
    ORDER BY timestamp DESC
    LIMIT p_limit;
$$ LANGUAGE sql STABLE;

-- Get admin actions in date range
CREATE OR REPLACE FUNCTION get_admin_actions_in_range(
    p_tenant_id UUID,
    p_from TIMESTAMPTZ,
    p_to TIMESTAMPTZ
)
RETURNS SETOF admin_actions AS $$
    SELECT * FROM admin_actions
    WHERE tenant_id = p_tenant_id
      AND timestamp >= p_from
      AND timestamp <= p_to
    ORDER BY timestamp DESC;
$$ LANGUAGE sql STABLE;

-- =============================================================================
-- RLS POLICIES
-- =============================================================================

ALTER TABLE admin_actions ENABLE ROW LEVEL SECURITY;

-- Tenant isolation policy
DROP POLICY IF EXISTS tenant_isolation_admin_actions ON admin_actions;
CREATE POLICY tenant_isolation_admin_actions ON admin_actions
    FOR ALL
    USING (tenant_id = current_setting('app.tenant_id', true)::uuid)
    WITH CHECK (tenant_id = current_setting('app.tenant_id', true)::uuid);

-- Service bypass policy
DROP POLICY IF EXISTS service_bypass_admin_actions ON admin_actions;
CREATE POLICY service_bypass_admin_actions ON admin_actions
    FOR ALL
    TO serialmemory_service
    USING (true)
    WITH CHECK (true);

-- =============================================================================
-- COMMENTS
-- =============================================================================

COMMENT ON TABLE admin_actions IS 'Tamper-evident audit log for admin MCP tool invocations with SHA-256 hash chaining';
COMMENT ON COLUMN admin_actions.params_hash IS 'SHA-256 hash of the tool parameters JSON';
COMMENT ON COLUMN admin_actions.prev_hash IS 'Hash of the previous entry in the chain (per tenant)';
COMMENT ON COLUMN admin_actions.hash IS 'SHA-256 hash of this entry linking to prev_hash';
COMMENT ON FUNCTION append_admin_action(UUID, TEXT, TEXT, TEXT, JSONB) IS 'Appends a new admin action with automatic hash chaining';
COMMENT ON FUNCTION verify_admin_action_chain(UUID) IS 'Verifies the integrity of the admin action hash chain for a tenant';
