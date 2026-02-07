-- Fix unsafe RLS policies that cast current_setting('app.tenant_id')::uuid
-- without protection against empty strings. Replace with get_safe_tenant_uuid()
-- and is_internal_admin() bypass pattern.
-- Idempotent: DROP IF EXISTS + CREATE.

BEGIN;

-- event_log: tenant_read_event_log
DROP POLICY IF EXISTS tenant_read_event_log ON event_log;
CREATE POLICY tenant_read_event_log ON event_log
    USING (is_internal_admin() OR (get_safe_tenant_uuid() IS NOT NULL AND tenant_id = get_safe_tenant_uuid()));

-- memory_events: tenant_read_memory_events (preserves NULL tenant_id allowance)
DROP POLICY IF EXISTS tenant_read_memory_events ON memory_events;
CREATE POLICY tenant_read_memory_events ON memory_events
    USING (is_internal_admin() OR tenant_id IS NULL OR (get_safe_tenant_uuid() IS NOT NULL AND tenant_id = get_safe_tenant_uuid()));

-- memory_timeline: tenant_read_memory_timeline
DROP POLICY IF EXISTS tenant_read_memory_timeline ON memory_timeline;
CREATE POLICY tenant_read_memory_timeline ON memory_timeline
    USING (is_internal_admin() OR (get_safe_tenant_uuid() IS NOT NULL AND tenant_id = get_safe_tenant_uuid()));

-- mind_health: tenant_read_mind_health
DROP POLICY IF EXISTS tenant_read_mind_health ON mind_health;
CREATE POLICY tenant_read_mind_health ON mind_health
    USING (is_internal_admin() OR (get_safe_tenant_uuid() IS NOT NULL AND tenant_id = get_safe_tenant_uuid()));

-- conflict_results: tenant_read_conflict_results
DROP POLICY IF EXISTS tenant_read_conflict_results ON conflict_results;
CREATE POLICY tenant_read_conflict_results ON conflict_results
    USING (is_internal_admin() OR (get_safe_tenant_uuid() IS NOT NULL AND tenant_id = get_safe_tenant_uuid()));

-- branch_metadata: tenant_isolation_branch_metadata
DROP POLICY IF EXISTS tenant_isolation_branch_metadata ON branch_metadata;
CREATE POLICY tenant_isolation_branch_metadata ON branch_metadata
    USING (is_internal_admin() OR (get_safe_tenant_uuid() IS NOT NULL AND tenant_id = get_safe_tenant_uuid()));

-- integrity_chain_anchors: integrity_chain_anchors_tenant_policy
DROP POLICY IF EXISTS integrity_chain_anchors_tenant_policy ON integrity_chain_anchors;
CREATE POLICY integrity_chain_anchors_tenant_policy ON integrity_chain_anchors
    USING (is_internal_admin() OR (get_safe_tenant_uuid() IS NOT NULL AND tenant_id = get_safe_tenant_uuid()));

-- integrity_verification_runs: integrity_verification_runs_tenant_policy
DROP POLICY IF EXISTS integrity_verification_runs_tenant_policy ON integrity_verification_runs;
CREATE POLICY integrity_verification_runs_tenant_policy ON integrity_verification_runs
    USING (is_internal_admin() OR (get_safe_tenant_uuid() IS NOT NULL AND tenant_id = get_safe_tenant_uuid()));

-- privacy_audit_entries: privacy_audit_entries_tenant_policy
DROP POLICY IF EXISTS privacy_audit_entries_tenant_policy ON privacy_audit_entries;
CREATE POLICY privacy_audit_entries_tenant_policy ON privacy_audit_entries
    USING (is_internal_admin() OR (get_safe_tenant_uuid() IS NOT NULL AND tenant_id = get_safe_tenant_uuid()));

-- tenant_privacy_settings: tenant_privacy_settings_tenant_policy
DROP POLICY IF EXISTS tenant_privacy_settings_tenant_policy ON tenant_privacy_settings;
CREATE POLICY tenant_privacy_settings_tenant_policy ON tenant_privacy_settings
    USING (is_internal_admin() OR (get_safe_tenant_uuid() IS NOT NULL AND tenant_id = get_safe_tenant_uuid()));

COMMIT;
