-- =============================================================================
-- FIX: Workspace RLS policies to support internal_admin role bypass
-- and safe UUID casting (prevents "invalid input syntax for type uuid: ''" errors)
--
-- Root cause: PostgreSQL doesn't guarantee AND short-circuit evaluation, so
-- current_setting('app.tenant_id', true)::uuid could be evaluated even when
-- require_tenant_context() would have returned false. Workers that set
-- app.role = 'internal_admin' for cross-tenant access need explicit bypass.
-- =============================================================================

BEGIN;

-- =============================================================================
-- 1. UPDATE require_tenant_context() to support internal_admin bypass
-- =============================================================================

CREATE OR REPLACE FUNCTION require_tenant_context()
RETURNS BOOLEAN AS $$
DECLARE
    v_role TEXT;
    v_tenant_id UUID;
BEGIN
    -- Allow internal admin workers to bypass tenant context requirement
    v_role := current_setting('app.role', true);
    IF v_role = 'internal_admin' THEN
        RETURN TRUE;
    END IF;

    v_tenant_id := NULLIF(current_setting('app.tenant_id', true), '')::uuid;

    IF v_tenant_id IS NULL THEN
        RAISE EXCEPTION 'SECURITY VIOLATION: tenant_id not set. Call set_tenant_context() before querying.';
    END IF;

    RETURN TRUE;
EXCEPTION
    WHEN invalid_text_representation THEN
        RAISE EXCEPTION 'SECURITY VIOLATION: Invalid tenant_id format. Call set_tenant_context() with valid UUID.';
END;
$$ LANGUAGE plpgsql STABLE;

-- =============================================================================
-- 2. HELPER: Safe tenant UUID function (returns NULL instead of error)
-- =============================================================================

CREATE OR REPLACE FUNCTION get_safe_tenant_uuid()
RETURNS UUID AS $$
BEGIN
    RETURN NULLIF(current_setting('app.tenant_id', true), '')::uuid;
EXCEPTION WHEN OTHERS THEN
    RETURN NULL;
END;
$$ LANGUAGE plpgsql STABLE;

-- =============================================================================
-- 3. REPLACE workspace-scoped RLS policies with safe versions
-- =============================================================================

-- Drop existing workspace policies
DROP POLICY IF EXISTS workspace_isolation_memories ON memories;
DROP POLICY IF EXISTS workspace_isolation_memory_entities ON memory_entities;
DROP POLICY IF EXISTS workspace_isolation_user_personas ON user_personas;
DROP POLICY IF EXISTS workspace_isolation_conversation_sessions ON conversation_sessions;

-- Recreate with internal_admin bypass and safe UUID casting
CREATE POLICY workspace_isolation_memories ON memories
    FOR ALL
    USING (
        current_setting('app.role', true) = 'internal_admin'
        OR (
            get_safe_tenant_uuid() IS NOT NULL
            AND tenant_id = get_safe_tenant_uuid()
            AND workspace_id = COALESCE(NULLIF(current_setting('app.workspace_id', true), ''), 'default')
        )
    )
    WITH CHECK (
        current_setting('app.role', true) = 'internal_admin'
        OR (
            get_safe_tenant_uuid() IS NOT NULL
            AND tenant_id = get_safe_tenant_uuid()
            AND workspace_id = COALESCE(NULLIF(current_setting('app.workspace_id', true), ''), 'default')
        )
    );

CREATE POLICY workspace_isolation_memory_entities ON memory_entities
    FOR ALL
    USING (
        current_setting('app.role', true) = 'internal_admin'
        OR (
            get_safe_tenant_uuid() IS NOT NULL
            AND tenant_id = get_safe_tenant_uuid()
            AND workspace_id = COALESCE(NULLIF(current_setting('app.workspace_id', true), ''), 'default')
        )
    )
    WITH CHECK (
        current_setting('app.role', true) = 'internal_admin'
        OR (
            get_safe_tenant_uuid() IS NOT NULL
            AND tenant_id = get_safe_tenant_uuid()
            AND workspace_id = COALESCE(NULLIF(current_setting('app.workspace_id', true), ''), 'default')
        )
    );

CREATE POLICY workspace_isolation_user_personas ON user_personas
    FOR ALL
    USING (
        current_setting('app.role', true) = 'internal_admin'
        OR (
            get_safe_tenant_uuid() IS NOT NULL
            AND tenant_id = get_safe_tenant_uuid()
            AND workspace_id = COALESCE(NULLIF(current_setting('app.workspace_id', true), ''), 'default')
        )
    )
    WITH CHECK (
        current_setting('app.role', true) = 'internal_admin'
        OR (
            get_safe_tenant_uuid() IS NOT NULL
            AND tenant_id = get_safe_tenant_uuid()
            AND workspace_id = COALESCE(NULLIF(current_setting('app.workspace_id', true), ''), 'default')
        )
    );

CREATE POLICY workspace_isolation_conversation_sessions ON conversation_sessions
    FOR ALL
    USING (
        current_setting('app.role', true) = 'internal_admin'
        OR (
            get_safe_tenant_uuid() IS NOT NULL
            AND tenant_id = get_safe_tenant_uuid()
            AND workspace_id = COALESCE(NULLIF(current_setting('app.workspace_id', true), ''), 'default')
        )
    )
    WITH CHECK (
        current_setting('app.role', true) = 'internal_admin'
        OR (
            get_safe_tenant_uuid() IS NOT NULL
            AND tenant_id = get_safe_tenant_uuid()
            AND workspace_id = COALESCE(NULLIF(current_setting('app.workspace_id', true), ''), 'default')
        )
    );

-- =============================================================================
-- 4. ALSO FIX tenant-only policies on workspaces/snapshots tables
-- =============================================================================

DROP POLICY IF EXISTS tenant_isolation_workspaces ON workspaces;
DROP POLICY IF EXISTS tenant_isolation_snapshots ON workspace_snapshots;

CREATE POLICY tenant_isolation_workspaces ON workspaces
    FOR ALL
    USING (
        current_setting('app.role', true) = 'internal_admin'
        OR (get_safe_tenant_uuid() IS NOT NULL AND tenant_id = get_safe_tenant_uuid())
    )
    WITH CHECK (
        current_setting('app.role', true) = 'internal_admin'
        OR (get_safe_tenant_uuid() IS NOT NULL AND tenant_id = get_safe_tenant_uuid())
    );

CREATE POLICY tenant_isolation_snapshots ON workspace_snapshots
    FOR ALL
    USING (
        current_setting('app.role', true) = 'internal_admin'
        OR (get_safe_tenant_uuid() IS NOT NULL AND tenant_id = get_safe_tenant_uuid())
    )
    WITH CHECK (
        current_setting('app.role', true) = 'internal_admin'
        OR (get_safe_tenant_uuid() IS NOT NULL AND tenant_id = get_safe_tenant_uuid())
    );

-- =============================================================================
-- 5. FIX original tenant-only policies on entities/entity_relationships
--    (same unsafe ::uuid cast pattern from init.sql)
-- =============================================================================

DROP POLICY IF EXISTS tenant_isolation_entities ON entities;
DROP POLICY IF EXISTS tenant_isolation_entity_relationships ON entity_relationships;

CREATE POLICY tenant_isolation_entities ON entities
    FOR ALL
    USING (
        current_setting('app.role', true) = 'internal_admin'
        OR (get_safe_tenant_uuid() IS NOT NULL AND tenant_id = get_safe_tenant_uuid())
    )
    WITH CHECK (
        current_setting('app.role', true) = 'internal_admin'
        OR (get_safe_tenant_uuid() IS NOT NULL AND tenant_id = get_safe_tenant_uuid())
    );

CREATE POLICY tenant_isolation_entity_relationships ON entity_relationships
    FOR ALL
    USING (
        current_setting('app.role', true) = 'internal_admin'
        OR (get_safe_tenant_uuid() IS NOT NULL AND tenant_id = get_safe_tenant_uuid())
    )
    WITH CHECK (
        current_setting('app.role', true) = 'internal_admin'
        OR (get_safe_tenant_uuid() IS NOT NULL AND tenant_id = get_safe_tenant_uuid())
    );

-- =============================================================================
-- 6. GRANT EXECUTE on new function
-- =============================================================================

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'contextdb_app') THEN
        GRANT EXECUTE ON FUNCTION get_safe_tenant_uuid() TO contextdb_app;
    END IF;
END $$;

COMMIT;
