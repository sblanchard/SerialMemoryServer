-- =============================================================================
-- WORKSPACE SCOPING MIGRATION
-- Adds workspace_id to workspace-scoped tables (memories, memory_entities,
-- user_personas, conversation_sessions). Entities/relationships remain
-- tenant-scoped only.
-- =============================================================================

BEGIN;

-- =============================================================================
-- 1. WORKSPACES TABLE
-- =============================================================================

CREATE TABLE IF NOT EXISTS workspaces (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL,
    workspace_id TEXT NOT NULL,
    display_name TEXT,
    description TEXT,
    metadata JSONB,
    created_at TIMESTAMP NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMP NOT NULL DEFAULT NOW(),
    UNIQUE(tenant_id, workspace_id)
);

CREATE INDEX IF NOT EXISTS idx_workspaces_tenant ON workspaces(tenant_id);
CREATE INDEX IF NOT EXISTS idx_workspaces_tenant_workspace ON workspaces(tenant_id, workspace_id);

-- =============================================================================
-- 2. WORKSPACE SNAPSHOTS TABLE (for Phase 4)
-- =============================================================================

CREATE TABLE IF NOT EXISTS workspace_snapshots (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL,
    workspace_id TEXT NOT NULL,
    snapshot_name TEXT NOT NULL,
    state_data JSONB NOT NULL,
    created_at TIMESTAMP NOT NULL DEFAULT NOW(),
    UNIQUE(tenant_id, workspace_id, snapshot_name)
);

CREATE INDEX IF NOT EXISTS idx_workspace_snapshots_tenant ON workspace_snapshots(tenant_id);
CREATE INDEX IF NOT EXISTS idx_workspace_snapshots_tenant_workspace ON workspace_snapshots(tenant_id, workspace_id);

-- =============================================================================
-- 3. ADD workspace_id TO WORKSPACE-SCOPED TABLES
-- =============================================================================

-- memories
ALTER TABLE memories ADD COLUMN IF NOT EXISTS workspace_id TEXT NOT NULL DEFAULT 'default';
CREATE INDEX IF NOT EXISTS idx_memories_tenant_workspace ON memories(tenant_id, workspace_id);
CREATE INDEX IF NOT EXISTS idx_memories_tenant_workspace_created ON memories(tenant_id, workspace_id, created_at DESC);

-- memory_entities
ALTER TABLE memory_entities ADD COLUMN IF NOT EXISTS workspace_id TEXT NOT NULL DEFAULT 'default';
CREATE INDEX IF NOT EXISTS idx_memory_entities_tenant_workspace ON memory_entities(tenant_id, workspace_id);

-- user_personas
ALTER TABLE user_personas ADD COLUMN IF NOT EXISTS workspace_id TEXT NOT NULL DEFAULT 'default';
CREATE INDEX IF NOT EXISTS idx_user_personas_tenant_workspace ON user_personas(tenant_id, workspace_id);

-- Update unique constraint to include workspace_id
-- Drop old constraint and create new one
ALTER TABLE user_personas DROP CONSTRAINT IF EXISTS user_personas_tenant_id_user_id_attribute_type_attribute_key_key;
ALTER TABLE user_personas ADD CONSTRAINT user_personas_tenant_workspace_unique
    UNIQUE(tenant_id, workspace_id, user_id, attribute_type, attribute_key);

-- conversation_sessions
ALTER TABLE conversation_sessions ADD COLUMN IF NOT EXISTS workspace_id TEXT NOT NULL DEFAULT 'default';
CREATE INDEX IF NOT EXISTS idx_conversation_sessions_tenant_workspace ON conversation_sessions(tenant_id, workspace_id);

-- =============================================================================
-- 4. WORKSPACE CONTEXT FUNCTIONS
-- =============================================================================

-- Set workspace context per connection (matches set_tenant_context pattern)
CREATE OR REPLACE FUNCTION set_workspace_context(p_workspace_id TEXT)
RETURNS VOID AS $$
BEGIN
    PERFORM set_config('app.workspace_id', p_workspace_id, false);
END;
$$ LANGUAGE plpgsql;

-- Get current workspace context (defaults to 'default' for backward compat)
CREATE OR REPLACE FUNCTION get_workspace_context()
RETURNS TEXT AS $$
BEGIN
    RETURN COALESCE(NULLIF(current_setting('app.workspace_id', true), ''), 'default');
END;
$$ LANGUAGE plpgsql STABLE;

COMMENT ON FUNCTION set_workspace_context(TEXT) IS 'Sets app.workspace_id for workspace-scoped RLS. Called after set_tenant_context.';
COMMENT ON FUNCTION get_workspace_context() IS 'Returns current workspace_id from session, defaults to ''default''.';

-- =============================================================================
-- 5. RLS POLICIES FOR WORKSPACE-SCOPED TABLES
-- =============================================================================
-- Update RLS policies to filter by BOTH tenant_id AND workspace_id
-- Uses COALESCE for backward compatibility when workspace_id is not set

-- Drop old tenant-only policies on workspace-scoped tables
DROP POLICY IF EXISTS tenant_isolation_memories ON memories;
DROP POLICY IF EXISTS tenant_isolation_memory_entities ON memory_entities;
DROP POLICY IF EXISTS tenant_isolation_user_personas ON user_personas;
DROP POLICY IF EXISTS tenant_isolation_conversation_sessions ON conversation_sessions;

-- Create workspace-scoped policies
CREATE POLICY workspace_isolation_memories ON memories
    FOR ALL
    USING (
        require_tenant_context()
        AND tenant_id = current_setting('app.tenant_id', true)::uuid
        AND workspace_id = COALESCE(NULLIF(current_setting('app.workspace_id', true), ''), 'default')
    )
    WITH CHECK (
        require_tenant_context()
        AND tenant_id = current_setting('app.tenant_id', true)::uuid
        AND workspace_id = COALESCE(NULLIF(current_setting('app.workspace_id', true), ''), 'default')
    );

CREATE POLICY workspace_isolation_memory_entities ON memory_entities
    FOR ALL
    USING (
        require_tenant_context()
        AND tenant_id = current_setting('app.tenant_id', true)::uuid
        AND workspace_id = COALESCE(NULLIF(current_setting('app.workspace_id', true), ''), 'default')
    )
    WITH CHECK (
        require_tenant_context()
        AND tenant_id = current_setting('app.tenant_id', true)::uuid
        AND workspace_id = COALESCE(NULLIF(current_setting('app.workspace_id', true), ''), 'default')
    );

CREATE POLICY workspace_isolation_user_personas ON user_personas
    FOR ALL
    USING (
        require_tenant_context()
        AND tenant_id = current_setting('app.tenant_id', true)::uuid
        AND workspace_id = COALESCE(NULLIF(current_setting('app.workspace_id', true), ''), 'default')
    )
    WITH CHECK (
        require_tenant_context()
        AND tenant_id = current_setting('app.tenant_id', true)::uuid
        AND workspace_id = COALESCE(NULLIF(current_setting('app.workspace_id', true), ''), 'default')
    );

CREATE POLICY workspace_isolation_conversation_sessions ON conversation_sessions
    FOR ALL
    USING (
        require_tenant_context()
        AND tenant_id = current_setting('app.tenant_id', true)::uuid
        AND workspace_id = COALESCE(NULLIF(current_setting('app.workspace_id', true), ''), 'default')
    )
    WITH CHECK (
        require_tenant_context()
        AND tenant_id = current_setting('app.tenant_id', true)::uuid
        AND workspace_id = COALESCE(NULLIF(current_setting('app.workspace_id', true), ''), 'default')
    );

-- RLS on new tables
ALTER TABLE workspaces ENABLE ROW LEVEL SECURITY;
ALTER TABLE workspace_snapshots ENABLE ROW LEVEL SECURITY;

-- Workspaces: tenant-scoped only (list all workspaces for tenant)
CREATE POLICY tenant_isolation_workspaces ON workspaces
    FOR ALL
    USING (require_tenant_context() AND tenant_id = current_setting('app.tenant_id', true)::uuid)
    WITH CHECK (require_tenant_context() AND tenant_id = current_setting('app.tenant_id', true)::uuid);

-- Snapshots: tenant-scoped only (workspace_id is filtered by explicit WHERE in queries,
-- allowing cross-workspace snapshot listing/loading without workspace context switching)
CREATE POLICY tenant_isolation_snapshots ON workspace_snapshots
    FOR ALL
    USING (require_tenant_context() AND tenant_id = current_setting('app.tenant_id', true)::uuid)
    WITH CHECK (require_tenant_context() AND tenant_id = current_setting('app.tenant_id', true)::uuid);

-- =============================================================================
-- 6. SEED DEFAULT WORKSPACE FOR EXISTING TENANTS
-- =============================================================================

INSERT INTO workspaces (id, tenant_id, workspace_id, display_name, description)
SELECT
    gen_random_uuid(),
    DISTINCT_TENANTS.tenant_id,
    'default',
    'Default Workspace',
    'Auto-created default workspace'
FROM (
    SELECT DISTINCT tenant_id FROM memories
    UNION
    SELECT DISTINCT tenant_id FROM conversation_sessions
) AS DISTINCT_TENANTS
ON CONFLICT (tenant_id, workspace_id) DO NOTHING;

-- =============================================================================
-- 7. TRIGGERS
-- =============================================================================

CREATE TRIGGER update_workspaces_updated_at BEFORE UPDATE ON workspaces
    FOR EACH ROW EXECUTE FUNCTION update_updated_at_column();

-- =============================================================================
-- 8. COMMENTS
-- =============================================================================

COMMENT ON TABLE workspaces IS 'Workspace definitions within a tenant';
COMMENT ON TABLE workspace_snapshots IS 'State snapshots for workspace checkpointing';
COMMENT ON COLUMN memories.workspace_id IS 'Workspace scope - enforced by RLS';
COMMENT ON COLUMN memory_entities.workspace_id IS 'Workspace scope - enforced by RLS';
COMMENT ON COLUMN user_personas.workspace_id IS 'Workspace scope - enforced by RLS';
COMMENT ON COLUMN conversation_sessions.workspace_id IS 'Workspace scope - enforced by RLS';

COMMIT;
