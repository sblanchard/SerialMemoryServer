-- OAuth Support for ChatGPT Integration
-- Adds OAuth 2.0 client credentials to tenant_api_keys

-- Add OAuth columns to tenant_api_keys
ALTER TABLE tenant_api_keys
ADD COLUMN IF NOT EXISTS oauth_enabled BOOLEAN DEFAULT FALSE,
ADD COLUMN IF NOT EXISTS client_secret_hash TEXT,
ADD COLUMN IF NOT EXISTS redirect_uris TEXT[] DEFAULT ARRAY[]::TEXT[],
ADD COLUMN IF NOT EXISTS oauth_metadata JSONB DEFAULT '{}';

-- Index for OAuth lookups
CREATE INDEX IF NOT EXISTS idx_tenant_api_keys_oauth ON tenant_api_keys(key_prefix) WHERE oauth_enabled = TRUE;

-- OAuth authorization codes table (short-lived, for auth code flow)
CREATE TABLE IF NOT EXISTS oauth_authorization_codes (
    code TEXT PRIMARY KEY,
    client_id UUID NOT NULL REFERENCES tenant_api_keys(id) ON DELETE CASCADE,
    tenant_id UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    redirect_uri TEXT NOT NULL,
    scope TEXT[] NOT NULL DEFAULT ARRAY['serialmemory.core'],
    state TEXT,
    code_challenge TEXT,  -- PKCE support
    code_challenge_method TEXT,  -- 'S256' or 'plain'
    expires_at TIMESTAMPTZ NOT NULL,
    used_at TIMESTAMPTZ,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_oauth_codes_client ON oauth_authorization_codes(client_id);
CREATE INDEX IF NOT EXISTS idx_oauth_codes_expires ON oauth_authorization_codes(expires_at);

-- OAuth refresh tokens table
CREATE TABLE IF NOT EXISTS oauth_refresh_tokens (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    token_hash TEXT NOT NULL UNIQUE,
    client_id UUID NOT NULL REFERENCES tenant_api_keys(id) ON DELETE CASCADE,
    tenant_id UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    scope TEXT[] NOT NULL DEFAULT ARRAY['serialmemory.core'],
    expires_at TIMESTAMPTZ NOT NULL,
    revoked_at TIMESTAMPTZ,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_oauth_refresh_client ON oauth_refresh_tokens(client_id);
CREATE INDEX IF NOT EXISTS idx_oauth_refresh_token ON oauth_refresh_tokens(token_hash);

-- Comments
COMMENT ON COLUMN tenant_api_keys.oauth_enabled IS 'Whether this API key can be used as OAuth client credentials';
COMMENT ON COLUMN tenant_api_keys.client_secret_hash IS 'SHA-256 hash of OAuth client secret';
COMMENT ON COLUMN tenant_api_keys.redirect_uris IS 'Allowed OAuth redirect URIs';
COMMENT ON COLUMN tenant_api_keys.oauth_metadata IS 'Additional OAuth client metadata (client_name, logo_uri, etc.)';
COMMENT ON TABLE oauth_authorization_codes IS 'Short-lived authorization codes for OAuth code flow';
COMMENT ON TABLE oauth_refresh_tokens IS 'OAuth refresh tokens for token renewal';
