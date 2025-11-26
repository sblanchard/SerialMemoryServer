-- Multi-Tenant Schema for SerialMemory SaaS
-- Creates core tenant management tables
-- Run this BEFORE add_tenant_id_migration.sql

-- Ensure pgcrypto is available for gen_random_uuid()
CREATE EXTENSION IF NOT EXISTS pgcrypto;

-- =============================================================================
-- TENANT REGISTRY
-- =============================================================================

CREATE TABLE IF NOT EXISTS tenants (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    name TEXT NOT NULL,
    slug TEXT NOT NULL UNIQUE,
    status TEXT NOT NULL DEFAULT 'active'
        CHECK (status IN ('active', 'suspended', 'deleted', 'pending')),
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    deleted_at TIMESTAMPTZ,
    metadata JSONB DEFAULT '{}'
);

CREATE INDEX IF NOT EXISTS idx_tenants_slug ON tenants(slug);
CREATE INDEX IF NOT EXISTS idx_tenants_status ON tenants(status);

-- =============================================================================
-- TENANT USERS (many-to-many with roles)
-- =============================================================================

CREATE TABLE IF NOT EXISTS tenant_users (
    tenant_id UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    user_id TEXT NOT NULL,
    role TEXT NOT NULL DEFAULT 'member'
        CHECK (role IN ('owner', 'admin', 'member', 'readonly')),
    invited_by TEXT,
    invited_at TIMESTAMPTZ,
    accepted_at TIMESTAMPTZ,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    PRIMARY KEY (tenant_id, user_id)
);

CREATE INDEX IF NOT EXISTS idx_tenant_users_user ON tenant_users(user_id);
CREATE INDEX IF NOT EXISTS idx_tenant_users_role ON tenant_users(tenant_id, role);

-- =============================================================================
-- TENANT SETTINGS
-- =============================================================================

CREATE TABLE IF NOT EXISTS tenant_settings (
    tenant_id UUID PRIMARY KEY REFERENCES tenants(id) ON DELETE CASCADE,
    retention_days INTEGER DEFAULT 365,
    region TEXT DEFAULT 'us-east-1',
    plan TEXT NOT NULL DEFAULT 'free',
    max_workspaces INTEGER DEFAULT 1,
    max_users INTEGER DEFAULT 5,
    features JSONB DEFAULT '{}',

    -- Data isolation settings
    data_encryption_enabled BOOLEAN DEFAULT FALSE,
    encryption_key_id TEXT,

    -- Compliance settings
    audit_retention_days INTEGER DEFAULT 90,
    gdpr_consent_required BOOLEAN DEFAULT FALSE,

    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- =============================================================================
-- TENANT API KEYS (for programmatic access)
-- =============================================================================

CREATE TABLE IF NOT EXISTS tenant_api_keys (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    name TEXT NOT NULL,
    key_hash TEXT NOT NULL,  -- SHA-256 hash of the API key
    key_prefix TEXT NOT NULL,  -- First 8 chars for identification
    scopes TEXT[] NOT NULL DEFAULT ARRAY['serialmemory.core'],
    created_by TEXT NOT NULL,
    last_used_at TIMESTAMPTZ,
    expires_at TIMESTAMPTZ,
    revoked_at TIMESTAMPTZ,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    CONSTRAINT tenant_api_keys_name_unique UNIQUE (tenant_id, name)
);

CREATE INDEX IF NOT EXISTS idx_tenant_api_keys_tenant ON tenant_api_keys(tenant_id);
CREATE INDEX IF NOT EXISTS idx_tenant_api_keys_prefix ON tenant_api_keys(key_prefix);

-- =============================================================================
-- TENANT WORKSPACES (for multi-workspace support)
-- =============================================================================

CREATE TABLE IF NOT EXISTS tenant_workspaces (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    name TEXT NOT NULL,
    slug TEXT NOT NULL,
    is_default BOOLEAN DEFAULT FALSE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    CONSTRAINT tenant_workspaces_slug_unique UNIQUE (tenant_id, slug)
);

CREATE INDEX IF NOT EXISTS idx_tenant_workspaces_tenant ON tenant_workspaces(tenant_id);

-- =============================================================================
-- DEFAULT TENANT FOR SELF-HOSTED DEPLOYMENTS
-- =============================================================================

-- Insert default tenant for self-hosted mode
-- Uses nil UUID (v4 format) for easy identification
INSERT INTO tenants (id, name, slug, status)
VALUES ('00000000-0000-0000-0000-000000000000', 'Self-Hosted', 'self-hosted', 'active')
ON CONFLICT (id) DO NOTHING;

-- Default settings for self-hosted tenant
INSERT INTO tenant_settings (tenant_id, plan, max_workspaces, max_users, retention_days)
VALUES ('00000000-0000-0000-0000-000000000000', 'self', 999, 999, 36500)
ON CONFLICT (tenant_id) DO NOTHING;

-- Default workspace for self-hosted tenant
INSERT INTO tenant_workspaces (tenant_id, name, slug, is_default)
VALUES ('00000000-0000-0000-0000-000000000000', 'Default', 'default', TRUE)
ON CONFLICT (tenant_id, slug) DO NOTHING;

-- =============================================================================
-- HELPER FUNCTIONS
-- =============================================================================

-- Function to get tenant by slug
CREATE OR REPLACE FUNCTION get_tenant_by_slug(p_slug TEXT)
RETURNS tenants AS $$
    SELECT * FROM tenants WHERE slug = p_slug AND status = 'active' LIMIT 1;
$$ LANGUAGE sql STABLE;

-- Function to check if user belongs to tenant
CREATE OR REPLACE FUNCTION user_belongs_to_tenant(p_user_id TEXT, p_tenant_id UUID)
RETURNS BOOLEAN AS $$
    SELECT EXISTS (
        SELECT 1 FROM tenant_users
        WHERE user_id = p_user_id AND tenant_id = p_tenant_id
    );
$$ LANGUAGE sql STABLE;

-- Function to get user's role in tenant
CREATE OR REPLACE FUNCTION get_user_role(p_user_id TEXT, p_tenant_id UUID)
RETURNS TEXT AS $$
    SELECT role FROM tenant_users
    WHERE user_id = p_user_id AND tenant_id = p_tenant_id
    LIMIT 1;
$$ LANGUAGE sql STABLE;

-- Function to check if user has admin access
CREATE OR REPLACE FUNCTION user_is_admin(p_user_id TEXT, p_tenant_id UUID)
RETURNS BOOLEAN AS $$
    SELECT EXISTS (
        SELECT 1 FROM tenant_users
        WHERE user_id = p_user_id
        AND tenant_id = p_tenant_id
        AND role IN ('owner', 'admin')
    );
$$ LANGUAGE sql STABLE;

-- =============================================================================
-- TRIGGERS
-- =============================================================================

-- Trigger to update updated_at timestamp
CREATE OR REPLACE FUNCTION update_updated_at_column()
RETURNS TRIGGER AS $$
BEGIN
    NEW.updated_at = NOW();
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS update_tenants_updated_at ON tenants;
CREATE TRIGGER update_tenants_updated_at
    BEFORE UPDATE ON tenants
    FOR EACH ROW EXECUTE FUNCTION update_updated_at_column();

DROP TRIGGER IF EXISTS update_tenant_users_updated_at ON tenant_users;
CREATE TRIGGER update_tenant_users_updated_at
    BEFORE UPDATE ON tenant_users
    FOR EACH ROW EXECUTE FUNCTION update_updated_at_column();

DROP TRIGGER IF EXISTS update_tenant_settings_updated_at ON tenant_settings;
CREATE TRIGGER update_tenant_settings_updated_at
    BEFORE UPDATE ON tenant_settings
    FOR EACH ROW EXECUTE FUNCTION update_updated_at_column();

DROP TRIGGER IF EXISTS update_tenant_workspaces_updated_at ON tenant_workspaces;
CREATE TRIGGER update_tenant_workspaces_updated_at
    BEFORE UPDATE ON tenant_workspaces
    FOR EACH ROW EXECUTE FUNCTION update_updated_at_column();

-- =============================================================================
-- COMMENTS
-- =============================================================================

COMMENT ON TABLE tenants IS 'Core tenant registry for multi-tenant SaaS';
COMMENT ON TABLE tenant_users IS 'Maps users to tenants with roles (owner, admin, member, readonly)';
COMMENT ON TABLE tenant_settings IS 'Tenant-specific configuration and limits';
COMMENT ON TABLE tenant_api_keys IS 'API keys for programmatic tenant access';
COMMENT ON TABLE tenant_workspaces IS 'Logical workspaces within a tenant';
COMMENT ON COLUMN tenants.slug IS 'URL-safe unique identifier for the tenant';
COMMENT ON COLUMN tenant_users.role IS 'owner: full control, admin: manage users/settings, member: read/write data, readonly: read only';
