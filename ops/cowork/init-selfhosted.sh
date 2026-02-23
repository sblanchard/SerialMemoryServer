#!/bin/bash
set -e

# Bootstrap self-hosted tenant and API key
# Runs during PostgreSQL container initialization (after schema scripts)

API_KEY="${SERIALMEMORY_API_KEY:-sm_selfhosted_default_key}"
TENANT_ID="00000000-0000-0000-0000-000000000000"

echo "Bootstrapping self-hosted tenant..."

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" \
     -v api_key="$API_KEY" -v tenant_id="$TENANT_ID" <<'EOSQL'

-- Create self-hosted tenant
INSERT INTO tenants (id, name, slug, status)
VALUES (:'tenant_id'::uuid, 'Self-Hosted', 'self-hosted', 'active')
ON CONFLICT (id) DO NOTHING;

-- Tenant settings (unlimited)
INSERT INTO tenant_settings (tenant_id, plan, max_workspaces, max_users)
VALUES (:'tenant_id'::uuid, 'self-hosted', 1000, 100)
ON CONFLICT (tenant_id) DO NOTHING;

-- Owner user
INSERT INTO tenant_users (tenant_id, user_id, role)
VALUES (:'tenant_id'::uuid, 'admin', 'owner')
ON CONFLICT (tenant_id, user_id) DO NOTHING;

-- API key (hashed with SHA-256)
INSERT INTO tenant_api_keys (tenant_id, name, key_hash, key_prefix, scopes, created_by)
VALUES (
    :'tenant_id'::uuid,
    'Docker Deployment Key',
    encode(sha256(:'api_key'::bytea), 'hex'),
    left(:'api_key', 8),
    ARRAY['serialmemory.core', 'serialmemory.admin'],
    'bootstrap'
)
ON CONFLICT (tenant_id, name) DO UPDATE SET
    key_hash = EXCLUDED.key_hash,
    key_prefix = EXCLUDED.key_prefix,
    revoked_at = NULL;

EOSQL

echo "Self-hosted tenant bootstrapped (key prefix: ${API_KEY:0:8}...)"
