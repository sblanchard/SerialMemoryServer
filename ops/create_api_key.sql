-- =============================================================================
-- API KEY GENERATION SCRIPT
-- =============================================================================
-- This script creates an API key for a tenant. For production self-hosted
-- deployments, run this to create proper API keys instead of using "self-hosted".
--
-- Usage:
--   1. Generate a secure random key (32+ chars recommended)
--   2. Replace YOUR_API_KEY_HERE with your generated key
--   3. Run this script against your database
--   4. Use the generated key in SERIALMEMORY_API_KEY environment variable
--
-- Generate a key example (PowerShell):
--   [System.Convert]::ToBase64String([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
--
-- Generate a key example (Bash):
--   openssl rand -base64 32
-- =============================================================================

DO $$
DECLARE
    v_api_key TEXT := 'sm_REPLACE_WITH_YOUR_SECURE_KEY_HERE';  -- CHANGE THIS
    v_tenant_id UUID := '00000000-0000-0000-0000-000000000000';  -- Self-hosted tenant
    v_key_name TEXT := 'Local Development Key';
    v_created_by TEXT := 'admin';
    v_key_hash TEXT;
    v_key_prefix TEXT;
BEGIN
    -- Calculate SHA-256 hash (lowercase hex)
    v_key_hash := encode(sha256(v_api_key::bytea), 'hex');
    v_key_prefix := left(v_api_key, 8);

    -- Insert the API key
    INSERT INTO tenant_api_keys (
        tenant_id,
        name,
        key_hash,
        key_prefix,
        scopes,
        created_by
    ) VALUES (
        v_tenant_id,
        v_key_name,
        v_key_hash,
        v_key_prefix,
        ARRAY['serialmemory.core', 'serialmemory.admin'],
        v_created_by
    )
    ON CONFLICT (tenant_id, name) DO UPDATE SET
        key_hash = EXCLUDED.key_hash,
        key_prefix = EXCLUDED.key_prefix,
        revoked_at = NULL;  -- Unrevoke if re-creating

    RAISE NOTICE 'API Key created successfully!';
    RAISE NOTICE 'Key prefix: %', v_key_prefix;
    RAISE NOTICE 'Hash: %', v_key_hash;
    RAISE NOTICE '';
    RAISE NOTICE 'Add to your mcp.json:';
    RAISE NOTICE '  "SERIALMEMORY_API_KEY": "%"', v_api_key;
END $$;

-- =============================================================================
-- CREATE API KEY FUNCTION (for programmatic use)
-- =============================================================================

CREATE OR REPLACE FUNCTION create_api_key(
    p_tenant_id UUID,
    p_name TEXT,
    p_api_key TEXT,
    p_scopes TEXT[] DEFAULT ARRAY['serialmemory.core'],
    p_created_by TEXT DEFAULT 'system',
    p_expires_in_days INTEGER DEFAULT NULL
)
RETURNS TABLE (
    id UUID,
    key_prefix TEXT,
    scopes TEXT[],
    expires_at TIMESTAMPTZ
) AS $$
DECLARE
    v_key_hash TEXT;
    v_key_prefix TEXT;
    v_expires_at TIMESTAMPTZ;
    v_id UUID;
BEGIN
    -- Validate tenant exists
    IF NOT EXISTS (SELECT 1 FROM tenants WHERE tenants.id = p_tenant_id) THEN
        RAISE EXCEPTION 'Tenant % not found', p_tenant_id;
    END IF;

    -- Calculate hash
    v_key_hash := encode(sha256(p_api_key::bytea), 'hex');
    v_key_prefix := left(p_api_key, 8);

    -- Calculate expiration
    IF p_expires_in_days IS NOT NULL THEN
        v_expires_at := NOW() + (p_expires_in_days || ' days')::INTERVAL;
    END IF;

    -- Insert and return
    INSERT INTO tenant_api_keys (
        tenant_id,
        name,
        key_hash,
        key_prefix,
        scopes,
        created_by,
        expires_at
    ) VALUES (
        p_tenant_id,
        p_name,
        v_key_hash,
        v_key_prefix,
        p_scopes,
        p_created_by,
        v_expires_at
    )
    RETURNING tenant_api_keys.id INTO v_id;

    RETURN QUERY SELECT v_id, v_key_prefix, p_scopes, v_expires_at;
END;
$$ LANGUAGE plpgsql;

-- =============================================================================
-- REVOKE API KEY FUNCTION
-- =============================================================================

CREATE OR REPLACE FUNCTION revoke_api_key(
    p_key_id UUID,
    p_tenant_id UUID DEFAULT NULL  -- For security, require tenant match if provided
)
RETURNS BOOLEAN AS $$
DECLARE
    v_updated INT;
BEGIN
    UPDATE tenant_api_keys
    SET revoked_at = NOW()
    WHERE id = p_key_id
      AND (p_tenant_id IS NULL OR tenant_id = p_tenant_id)
      AND revoked_at IS NULL;

    GET DIAGNOSTICS v_updated = ROW_COUNT;
    RETURN v_updated > 0;
END;
$$ LANGUAGE plpgsql;

-- =============================================================================
-- LIST API KEYS FUNCTION (without exposing hashes)
-- =============================================================================

CREATE OR REPLACE FUNCTION list_api_keys(p_tenant_id UUID)
RETURNS TABLE (
    id UUID,
    name TEXT,
    key_prefix TEXT,
    scopes TEXT[],
    created_by TEXT,
    created_at TIMESTAMPTZ,
    last_used_at TIMESTAMPTZ,
    expires_at TIMESTAMPTZ,
    is_active BOOLEAN
) AS $$
BEGIN
    RETURN QUERY
    SELECT
        ak.id,
        ak.name,
        ak.key_prefix,
        ak.scopes,
        ak.created_by,
        ak.created_at,
        ak.last_used_at,
        ak.expires_at,
        (ak.revoked_at IS NULL AND (ak.expires_at IS NULL OR ak.expires_at > NOW())) AS is_active
    FROM tenant_api_keys ak
    WHERE ak.tenant_id = p_tenant_id
    ORDER BY ak.created_at DESC;
END;
$$ LANGUAGE plpgsql;
