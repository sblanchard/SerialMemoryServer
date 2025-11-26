# SerialMemory SaaS Hardening Implementation Plan

## Executive Summary

Multi-phase hardening of SerialMemory for public SaaS deployment. Adds strict multi-tenancy, JWT authentication, usage enforcement, tamper-evident admin logging, and tenant dashboard APIs.

---

## Current State Analysis

### What Already Exists
- **MCP Split**: `SerialMemory.Mcp.Core` (12 tools) and `SerialMemory.Mcp.Admin` (21 tools)
- **Usage Metering**: `usage_events`, `billing_cycles`, `usage_daily_rollups`, `tenant_plans`, `tenant_subscriptions`
- **Audit Log with Hash Chains**: `audit_logs` table with `previous_hash`/`content_hash`
- **Rate Limiting**: `rate_limit_buckets` table and `check_rate_limit()` SQL function
- **ITenantContext Interface**: Defined with `TenantId`, `WorkspaceId`, `UserId`, `SessionId`
- **UsageService & UsageLimitService**: Already tenant-scoped implementations

### Gaps to Fill
1. Core tables (`memories`, `entities`, etc.) lack `tenant_id` column
2. No PostgreSQL Row-Level Security (RLS) policies
3. No JWT authentication - MCP servers rely on env vars
4. Keyword-based admin gating in `AdminGating.cs`
5. `UsageLimitService.CheckLimitsAsync()` not wired into tool execution
6. Missing `admin_actions` table for tamper-evident admin audit
7. No tenant management tables (`tenants`, `tenant_users`, `tenant_settings`)
8. No dashboard API endpoints

---

## PHASE 1: Multi-Tenant Isolation (Critical)

### 1.1 New Tables

**File**: `ops/multi_tenant_schema.sql`

```sql
-- Tenant registry
CREATE TABLE tenants (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    name TEXT NOT NULL,
    slug TEXT NOT NULL UNIQUE,
    status TEXT NOT NULL DEFAULT 'active'
        CHECK (status IN ('active', 'suspended', 'deleted')),
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Tenant users (many-to-many with roles)
CREATE TABLE tenant_users (
    tenant_id UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    user_id TEXT NOT NULL,
    role TEXT NOT NULL DEFAULT 'member'
        CHECK (role IN ('owner', 'admin', 'member', 'readonly')),
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (tenant_id, user_id)
);

-- Tenant settings
CREATE TABLE tenant_settings (
    tenant_id UUID PRIMARY KEY REFERENCES tenants(id) ON DELETE CASCADE,
    retention_days INTEGER DEFAULT 365,
    region TEXT DEFAULT 'us-east-1',
    plan TEXT NOT NULL DEFAULT 'free',
    max_workspaces INTEGER DEFAULT 1,
    features JSONB DEFAULT '{}',
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Default tenant for self-hosted
INSERT INTO tenants (id, name, slug, status)
VALUES ('00000000-0000-0000-0000-000000000000', 'Self-Hosted', 'self-hosted', 'active');
```

### 1.2 Add tenant_id to Core Tables

**File**: `ops/add_tenant_id_migration.sql`

```sql
-- Add tenant_id column (nullable initially for backfill)
ALTER TABLE memories ADD COLUMN IF NOT EXISTS tenant_id UUID;
ALTER TABLE entities ADD COLUMN IF NOT EXISTS tenant_id UUID;
ALTER TABLE entity_relationships ADD COLUMN IF NOT EXISTS tenant_id UUID;
ALTER TABLE memory_entities ADD COLUMN IF NOT EXISTS tenant_id UUID;
ALTER TABLE user_personas ADD COLUMN IF NOT EXISTS tenant_id UUID;
ALTER TABLE conversation_sessions ADD COLUMN IF NOT EXISTS tenant_id UUID;

-- Backfill with default tenant
UPDATE memories SET tenant_id = '00000000-0000-0000-0000-000000000000' WHERE tenant_id IS NULL;
UPDATE entities SET tenant_id = '00000000-0000-0000-0000-000000000000' WHERE tenant_id IS NULL;
UPDATE entity_relationships SET tenant_id = '00000000-0000-0000-0000-000000000000' WHERE tenant_id IS NULL;
UPDATE memory_entities SET tenant_id = '00000000-0000-0000-0000-000000000000' WHERE tenant_id IS NULL;
UPDATE user_personas SET tenant_id = '00000000-0000-0000-0000-000000000000' WHERE tenant_id IS NULL;
UPDATE conversation_sessions SET tenant_id = '00000000-0000-0000-0000-000000000000' WHERE tenant_id IS NULL;

-- Add NOT NULL constraint
ALTER TABLE memories ALTER COLUMN tenant_id SET NOT NULL;
ALTER TABLE entities ALTER COLUMN tenant_id SET NOT NULL;
ALTER TABLE entity_relationships ALTER COLUMN tenant_id SET NOT NULL;
ALTER TABLE memory_entities ALTER COLUMN tenant_id SET NOT NULL;
ALTER TABLE user_personas ALTER COLUMN tenant_id SET NOT NULL;
ALTER TABLE conversation_sessions ALTER COLUMN tenant_id SET NOT NULL;

-- Add indexes
CREATE INDEX IF NOT EXISTS idx_memories_tenant ON memories(tenant_id);
CREATE INDEX IF NOT EXISTS idx_entities_tenant ON entities(tenant_id);
CREATE INDEX IF NOT EXISTS idx_entity_relationships_tenant ON entity_relationships(tenant_id);
CREATE INDEX IF NOT EXISTS idx_memory_entities_tenant ON memory_entities(tenant_id);
CREATE INDEX IF NOT EXISTS idx_user_personas_tenant ON user_personas(tenant_id);
CREATE INDEX IF NOT EXISTS idx_conversation_sessions_tenant ON conversation_sessions(tenant_id);
```

### 1.3 PostgreSQL Row-Level Security (RLS)

**File**: `ops/rls_policies.sql`

```sql
-- Enable RLS on all tenant-scoped tables
ALTER TABLE memories ENABLE ROW LEVEL SECURITY;
ALTER TABLE entities ENABLE ROW LEVEL SECURITY;
ALTER TABLE entity_relationships ENABLE ROW LEVEL SECURITY;
ALTER TABLE memory_entities ENABLE ROW LEVEL SECURITY;
ALTER TABLE user_personas ENABLE ROW LEVEL SECURITY;
ALTER TABLE conversation_sessions ENABLE ROW LEVEL SECURITY;
ALTER TABLE usage_events ENABLE ROW LEVEL SECURITY;
ALTER TABLE billing_cycles ENABLE ROW LEVEL SECURITY;

-- Create isolation policies
CREATE POLICY tenant_isolation_memories ON memories
    FOR ALL USING (tenant_id = current_setting('app.tenant_id', true)::uuid);

CREATE POLICY tenant_isolation_entities ON entities
    FOR ALL USING (tenant_id = current_setting('app.tenant_id', true)::uuid);

-- ... (similar for all tables)

-- Function to set tenant context
CREATE OR REPLACE FUNCTION set_tenant_context(p_tenant_id UUID)
RETURNS VOID AS $$
BEGIN
    PERFORM set_config('app.tenant_id', p_tenant_id::text, true);
END;
$$ LANGUAGE plpgsql;
```

### 1.4 Update PostgresKnowledgeGraphStore

**File**: `SerialMemory.Infrastructure/PostgresKnowledgeGraphStore.cs`

Changes:
- Inject `ITenantContext` into constructor
- Call `set_tenant_context()` on every connection open
- Add `tenant_id` to all INSERT statements
- Remove any direct tenant_id parameters from methods

### 1.5 Tests

**File**: `SerialMemory.Tests/TenantIsolationTests.cs`

- Cross-tenant read prevention test
- Cross-tenant write prevention test
- RLS policy enforcement test

---

## PHASE 2: Authentication & Authorization

### 2.1 JWT Authentication Service

**New File**: `SerialMemory.Core/Auth/Scopes.cs`

```csharp
public static class Scopes
{
    public const string Core = "serialmemory.core";
    public const string Admin = "serialmemory.admin";
    public const string Export = "serialmemory.export";
    public const string Delete = "serialmemory.delete";
}

public static class Roles
{
    public const string Owner = "owner";
    public const string Admin = "admin";
    public const string Member = "member";
    public const string ReadOnly = "readonly";

    public static readonly IReadOnlySet<string> AdminRoles =
        new HashSet<string> { Owner, Admin };
}
```

**New File**: `SerialMemory.Core/Interfaces/IJwtAuthenticationService.cs`

```csharp
public interface IJwtAuthenticationService
{
    Task<AuthenticationResult> ValidateTokenAsync(string token);
}

public record AuthenticationResult
{
    public bool IsValid { get; init; }
    public Guid? TenantId { get; init; }
    public string? UserId { get; init; }
    public string? Role { get; init; }
    public IReadOnlySet<string> Scopes { get; init; } = new HashSet<string>();
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}
```

**New File**: `SerialMemory.Infrastructure/JwtAuthenticationService.cs`

- Validates JWT tokens using configured keys
- Extracts tenant_id, user_id, role, scopes from claims
- Returns structured AuthenticationResult

### 2.2 Update MCP Servers

**Update**: `SerialMemory.Mcp.Shared/McpServerBase.cs`

- Add `IJwtAuthenticationService` and `ITenantContext`
- Add abstract `RequiredScope` property
- Add `AuthenticateRequestAsync()` method that:
  - Extracts token from request or environment
  - Validates token
  - Checks required scope
  - Sets tenant context from JWT (NEVER from request body)
- Allow self-hosted mode bypass with `SERIALMEMORY_MODE=self-hosted`

**Update**: `SerialMemory.Mcp.Core/Program.cs`

```csharp
protected override string RequiredScope => Scopes.Core;
```

**Update**: `SerialMemory.Mcp.Admin/Program.cs`

```csharp
protected override string RequiredScope => Scopes.Admin;

// Also verify admin role before tool execution
if (!Roles.AdminRoles.Contains(CurrentAuth?.Role ?? ""))
    return CreateErrorResponse("Unauthorized: Admin role required");
```

**Delete**: `SerialMemory.Mcp.Shared/AdminGating.cs` - Remove keyword-based gating entirely

### 2.3 Tests

**File**: `SerialMemory.Tests/AuthenticationTests.cs`

- Invalid token rejected
- Missing scope rejected
- Wrong role rejected
- Self-hosted mode bypass works

---

## PHASE 3: Usage Enforcement

### 3.1 Pre-Execution Limit Check

**Update**: `SerialMemory.Mcp.Shared/McpServerBase.cs`

Add `EnforceUsageLimitsAsync()` method:

```csharp
protected async Task<object?> EnforceUsageLimitsAsync(UsageEventType eventType)
{
    var result = await UsageLimitService.CheckLimitsAsync(
        TenantContext.TenantId,
        TenantContext.WorkspaceId,
        eventType);

    if (!result.IsAllowed)
    {
        // Return structured error response
        return new
        {
            content = new[] { new { type = "text", text = JsonSerializer.Serialize(new
            {
                error = result.Violation!.Code.ToLowerInvariant(),
                plan = await GetCurrentPlanNameAsync(),
                next_reset = result.Violation.RetryAfter?.ToString("O")
            })}},
            isError = true
        };
    }

    // Record rate limit hit
    await UsageLimitService.RecordRateLimitHitAsync(...);
    return null; // Allowed
}
```

### 3.2 Integrate into Tool Execution

**Update**: `SerialMemory.Mcp.Core/Program.cs` and `SerialMemory.Mcp.Admin/Program.cs`

In `HandleToolsCall()`:
1. Map tool name to `UsageEventType`
2. Call `EnforceUsageLimitsAsync()` BEFORE execution
3. If blocked, return the error response immediately
4. Track usage after execution (success/failure)

### 3.3 Tests

**File**: `SerialMemory.Tests/UsageEnforcementTests.cs`

- Free plan exhausted blocks request
- Pro plan has higher limits
- Rate limit exceeded returns retry_after
- Structured error response format

---

## PHASE 4: Tamper-Evident Admin Audit Log

### 4.1 Admin Actions Table

**File**: `ops/admin_actions_schema.sql`

```sql
CREATE TABLE admin_actions (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL REFERENCES tenants(id),
    user_id TEXT NOT NULL,
    tool_name TEXT NOT NULL,
    params_hash TEXT NOT NULL,
    timestamp TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    prev_hash TEXT NOT NULL DEFAULT '',
    hash TEXT NOT NULL,
    execution_ms INTEGER,
    success BOOLEAN NOT NULL DEFAULT TRUE,
    error_message TEXT
);

-- Append function with hash chaining
CREATE OR REPLACE FUNCTION append_admin_action(
    p_tenant_id UUID,
    p_user_id TEXT,
    p_tool_name TEXT,
    p_params_hash TEXT
) RETURNS admin_actions AS $$
DECLARE
    v_prev_hash TEXT;
    v_hash TEXT;
    v_timestamp TIMESTAMPTZ := NOW();
    v_result admin_actions;
BEGIN
    SELECT hash INTO v_prev_hash FROM admin_actions
    WHERE tenant_id = p_tenant_id ORDER BY timestamp DESC LIMIT 1;
    v_prev_hash := COALESCE(v_prev_hash, '');

    v_hash := encode(sha256(
        (p_tenant_id::text || '|' || p_user_id || '|' || p_tool_name || '|' ||
         p_params_hash || '|' || v_timestamp::text || '|' || v_prev_hash)::bytea
    ), 'hex');

    INSERT INTO admin_actions (tenant_id, user_id, tool_name, params_hash, timestamp, prev_hash, hash)
    VALUES (p_tenant_id, p_user_id, p_tool_name, p_params_hash, v_timestamp, v_prev_hash, v_hash)
    RETURNING * INTO v_result;

    RETURN v_result;
END;
$$ LANGUAGE plpgsql;

-- Verification function
CREATE OR REPLACE FUNCTION verify_admin_log_chain(p_tenant_id UUID)
RETURNS TABLE(is_valid BOOLEAN, total_entries INTEGER, verified_entries INTEGER,
              first_broken_id UUID, first_broken_reason TEXT) AS $$
-- Iterates through entries, verifies hash chain
-- Returns first broken entry if any
$$ LANGUAGE plpgsql;
```

### 4.2 Admin Audit Service

**New File**: `SerialMemory.Infrastructure/AdminAuditService.cs`

```csharp
public interface IAdminAuditService
{
    Task<AdminActionEntry> LogActionAsync(Guid tenantId, string userId,
        string toolName, object? parameters);
    Task CompleteActionAsync(Guid actionId, int executionMs, bool success,
        string? errorMessage = null);
    Task<AdminLogVerificationResult> VerifyChainAsync(Guid tenantId);
}
```

### 4.3 Integrate into Admin MCP

**Update**: `SerialMemory.Mcp.Admin/Program.cs`

Every tool call:
1. Log action BEFORE execution
2. Execute tool
3. Complete audit entry with result

### 4.4 Tests

**File**: `SerialMemory.Tests/AdminAuditTests.cs`

- Detect broken hash chain
- Detect missing entries
- Detect tampered content

---

## PHASE 5: Tenant Dashboard Backend APIs

### 5.1 New API Project

**New Project**: `SerialMemory.Api.Dashboard`

Minimal API with JWT authentication.

### 5.2 Endpoints

```csharp
// GET /me - Current user info
app.MapGet("/me", async (HttpContext ctx, ITenantContext tenant) => {
    return Results.Ok(new {
        user_id = ctx.User.FindFirst("sub")?.Value,
        tenant_id = tenant.TenantId,
        role = ctx.User.FindFirst("role")?.Value
    });
}).RequireAuthorization();

// GET /tenant/usage - Current cycle usage
app.MapGet("/tenant/usage", async (ITenantContext tenant, IUsageLimitService svc) => {
    var summary = await svc.GetUsageSummaryAsync(tenant.TenantId, tenant.WorkspaceId);
    return Results.Ok(new {
        plan = summary.PlanName,
        credits_allocated = summary.CreditsAllocated,
        credits_used = summary.CreditsUsed,
        cycle_end = summary.CycleEnd
    });
}).RequireAuthorization();

// GET /tenant/plan - Plan details
app.MapGet("/tenant/plan", async (...) => { ... }).RequireAuthorization();

// POST /tenant/export - Request data export
app.MapPost("/tenant/export", async (...) => { ... }).RequireAuthorization();

// DELETE /tenant - Request deletion (owner only)
app.MapDelete("/tenant", async (...) => {
    if (role != "owner") return Results.Forbid();
    // ...
}).RequireAuthorization();
```

### 5.3 Middleware

**New File**: `SerialMemory.Api.Dashboard/Middleware/TenantContextMiddleware.cs`

Extracts tenant_id from JWT and sets `ITenantContext`.

### 5.4 Tests

**File**: `SerialMemory.Tests/DashboardApiTests.cs`

- Auth required for all endpoints
- Tenant isolation verified
- Owner-only endpoints enforced

---

## File Structure Summary

```
NEW FILES:
├── SerialMemory.Core/
│   └── Auth/
│       └── Scopes.cs
├── SerialMemory.Infrastructure/
│   ├── JwtAuthenticationService.cs
│   ├── AdminAuditService.cs
│   └── TenantService.cs
├── SerialMemory.Api.Dashboard/
│   ├── Program.cs
│   └── Middleware/TenantContextMiddleware.cs
├── ops/
│   ├── multi_tenant_schema.sql
│   ├── add_tenant_id_migration.sql
│   ├── rls_policies.sql
│   └── admin_actions_schema.sql
└── SerialMemory.Tests/
    ├── TenantIsolationTests.cs
    ├── AuthenticationTests.cs
    ├── UsageEnforcementTests.cs
    ├── AdminAuditTests.cs
    └── DashboardApiTests.cs

UPDATED FILES:
├── SerialMemory.Infrastructure/PostgresKnowledgeGraphStore.cs
├── SerialMemory.Mcp.Shared/McpServerBase.cs
├── SerialMemory.Mcp.Core/Program.cs
└── SerialMemory.Mcp.Admin/Program.cs

DELETED FILES:
└── SerialMemory.Mcp.Shared/AdminGating.cs
```

---

## Execution Order

1. **Phase 1**: Database migrations → Update PostgresKnowledgeGraphStore → Enable RLS
2. **Phase 2**: Add JWT service → Update MCP servers → Remove AdminGating
3. **Phase 3**: Add usage enforcement to tool execution
4. **Phase 4**: Add admin_actions table → Integrate audit logging
5. **Phase 5**: Create dashboard API project

Each phase is independently testable and deployable.

---

## Breaking Changes

| Change | Mitigation |
|--------|------------|
| Self-hosted requires env var | Set `SERIALMEMORY_MODE=self-hosted` to bypass auth |
| Database migration needed | Backfill script included, additive changes only |
| API clients need JWT tokens | Self-hosted mode allows token-free operation |

---

## Rollback Plan

- All migrations are additive (no column drops)
- RLS can be disabled: `ALTER TABLE ... DISABLE ROW LEVEL SECURITY`
- Auth bypass: `SERIALMEMORY_MODE=self-hosted` environment variable
- Feature flags can disable individual phases
