# Tenant Isolation Audit Report

**Generated:** 2025-11-27
**Auditor:** Claude Code
**Status:** ✅ PASS - All tenant-scoped repositories use safe patterns

---

## Executive Summary

The `memories` table and related knowledge graph tables now have proper multi-tenant isolation via PostgreSQL Row-Level Security (RLS). All tenant-scoped data access goes through `TenantDbConnectionFactory` which enforces `SET app.tenant_id` before any queries.

---

## Deliverables

### 1. SQL Schema Updates

| File | Description |
|------|-------------|
| `ops/init.sql` | Base schema with `tenant_id` columns, indexes, RLS policies, and guardrail function |
| `ops/migrate_memories_tenant_id.sql` | Idempotent migration for existing databases |

### 2. Connection Factory

| File | Description |
|------|-------------|
| `SerialMemory.Core/Interfaces/ITenantDbConnectionFactory.cs` | Interface for tenant-scoped connections |
| `SerialMemory.Infrastructure/TenantDbConnectionFactory.cs` | Implementation that sets `app.tenant_id` |

### 3. Refactored Repositories

| File | Status |
|------|--------|
| `SerialMemory.Infrastructure/PostgresKnowledgeGraphStore.cs` | ✅ Uses `TenantDbConnectionFactory` |

### 4. Tests

| File | Description |
|------|-------------|
| `SerialMemory.Mcp.Tests/TenantIsolationTests.cs` | Comprehensive RLS isolation tests |
| `SerialMemory.Mcp.Tests/DatabaseAccessAuditTests.cs` | Static code audit tests |

---

## Database Access Patterns Audit

### ✅ SAFE: Using TenantDbConnectionFactory

These files correctly use the tenant connection factory:

```
SerialMemory.Infrastructure/PostgresKnowledgeGraphStore.cs
```

### ⚠️ SYSTEM-LEVEL SERVICES (Intentionally Direct Access)

These services access **system tables** (not tenant-scoped data) and are **ALLOWED** to use direct connections:

| File | Tables Accessed | Justification |
|------|-----------------|---------------|
| `StripeBillingService.cs` | `tenant_subscriptions`, `stripe_webhook_events`, `tenant_plans` | Billing/subscription management (platform admin) |
| `AdminService.cs` | `tenants`, `tenant_users`, `tenant_settings` | Platform administration |
| `UsageService.cs` | `usage_events`, `billing_cycles` | Usage tracking (has its own tenant_id pattern) |
| `AuditLogService.cs` | `audit_logs` | Audit logging |
| `ApiKeyService.cs` | `tenant_api_keys` | API key management |
| `PlanService.cs` | `tenant_plans` | Plan management |
| `UsageLimitService.cs` | `usage_limits` | Usage limits |
| `UsageExportService.cs` | `usage_events` | Usage export |
| `JobSupervisionService.cs` | `supervised_jobs` | Job supervision |
| `RateLimitingService.cs` | Rate limit tables | Rate limiting |
| `CostProtectionService.cs` | Cost tables | Cost protection |
| `TenantDashboardService.cs` | Dashboard tables | Tenant dashboard |

**Note:** These services access platform/system tables, not tenant data like `memories`. They are reviewed and approved for direct database access.

### ✅ TEST FILES (Allowed)

Test files are allowed to have direct database access for RLS verification:

- `SerialMemory.Mcp.Tests/*.cs`

### ✅ TOOLS (Allowed)

Standalone utilities are allowed:

- `tools/reembed_memories.cs`
- `tools/replay_tool.cs`

---

## RLS Policy Verification

### Guardrail Function

```sql
CREATE OR REPLACE FUNCTION require_tenant_context()
RETURNS BOOLEAN AS $$
DECLARE
    v_tenant_id UUID;
BEGIN
    v_tenant_id := NULLIF(current_setting('app.tenant_id', true), '')::uuid;

    IF v_tenant_id IS NULL THEN
        RAISE EXCEPTION 'SECURITY VIOLATION: tenant_id not set';
    END IF;

    RETURN TRUE;
END;
$$ LANGUAGE plpgsql STABLE;
```

### RLS Policy Pattern

All tenant-scoped tables use this policy pattern:

```sql
CREATE POLICY tenant_isolation_memories ON memories
    FOR ALL
    USING (require_tenant_context() AND tenant_id = current_setting('app.tenant_id', true)::uuid)
    WITH CHECK (require_tenant_context() AND tenant_id = current_setting('app.tenant_id', true)::uuid);
```

**Behavior:**
- If `app.tenant_id` is NOT set → `RAISE EXCEPTION` (hard fail)
- If `app.tenant_id` doesn't match row → Row is invisible (filtered)
- If `app.tenant_id` matches → Row is accessible

---

## Test Coverage

### TenantIsolationTests.cs

| Test | Verifies |
|------|----------|
| `TenantA_CannotRead_TenantB_Memories` | Cross-tenant read isolation |
| `TenantB_CannotUpdate_TenantA_Memories` | Cross-tenant update protection |
| `TenantB_CannotDelete_TenantA_Memories` | Cross-tenant delete protection |
| `QueryWithoutTenantContext_FailsWithGuardrail` | Guardrail enforcement |
| `WriteWithMismatchedTenantId_IsRejected` | RLS WITH CHECK enforcement |
| `TenantConnectionFactory_SetsTenantContext` | Factory behavior |
| `EachTenant_OnlySeesOwnMemories` | Complete isolation |
| `PostgresKnowledgeGraphStore_EnforcesTenantIsolation` | Repository integration |

### DatabaseAccessAuditTests.cs

| Test | Verifies |
|------|----------|
| `Audit_NoDirectNpgsqlConnectionInTenantScopedRepositories` | No unsafe connection creation |
| `Audit_NoManualTenantIdFilteringInQueries` | No manual WHERE tenant_id clauses |
| `Audit_NoRawOpenAsyncOutsideFactory` | No raw OpenAsync without context |
| `Report_SystemLevelServicesWithDirectAccess` | Documents allowed exceptions |
| `Report_AllDatabaseAccessLocations` | Comprehensive audit report |

---

## Migration Instructions

### For New Databases

```bash
psql -d contextdb -f ops/init.sql
```

### For Existing Databases (PRESERVES DATA)

```bash
# This migration is SAFE and IDEMPOTENT
# It adds tenant_id, backfills, then adds constraints
psql -d contextdb -f ops/migrate_memories_tenant_id.sql
```

**Default Tenant ID:** `019ac272-2239-7407-9f5e-b1d4e4232dc7` (stephan@serialcoder.com)

---

## Recommendations

1. **Run static audit tests on every CI build** to catch violations early
2. **Review system-level services periodically** to ensure they don't access tenant data
3. **Add FORCE ROW LEVEL SECURITY** for production (see `ops/rls_policies.sql`)
4. **Monitor RLS policy performance** - ensure tenant indexes are used

---

## Conclusion

The codebase is now properly isolated for multi-tenancy:

- ✅ `memories` table has `tenant_id NOT NULL`
- ✅ RLS policies enforce isolation at database level
- ✅ Guardrail function fails queries without tenant context
- ✅ Connection factory sets tenant context before any query
- ✅ Repository uses factory, not direct connections
- ✅ Tests verify isolation behavior
- ✅ Static audit tests catch violations

**No data loss risk:** Migration is additive (adds column, backfills, adds constraints).
