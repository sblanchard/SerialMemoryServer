using System.Collections.Concurrent;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using SerialMemory.Core.Deployment;
using SerialMemory.Core.Interfaces;

namespace SerialMemory.Infrastructure.KillSwitch;

/// <summary>
/// Service for managing kill switches with in-memory caching.
/// Caches states for 5-10 seconds to avoid DB churn while remaining responsive.
/// </summary>
public sealed class KillSwitchService : IKillSwitchService
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly IDeploymentContext _deploymentContext;
    private readonly ILogger<KillSwitchService> _logger;

    // Cache with TTL of 5-10 seconds
    private readonly ConcurrentDictionary<string, CachedState> _cache = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(7);

    // Global state cache keys
    private const string GlobalStateKey = "__GLOBAL__";

    public KillSwitchService(
        NpgsqlDataSource dataSource,
        IDeploymentContext deploymentContext,
        ILogger<KillSwitchService> logger)
    {
        _dataSource = dataSource;
        _deploymentContext = deploymentContext;
        _logger = logger;
    }

    public KillSwitchService(
        string connectionString,
        IDeploymentContext deploymentContext,
        ILogger<KillSwitchService> logger)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        _dataSource = builder.Build();
        _deploymentContext = deploymentContext;
        _logger = logger;
    }

    public async Task<KillSwitchState> GetStateAsync(
        string? tenantId,
        string? apiKeyId = null,
        CancellationToken ct = default)
    {
        // Check global cache first
        var globalState = await GetGlobalStateAsync(ct);

        // If global kill switch is active, return immediately
        if (globalState.GlobalOff || globalState.GlobalReadOnly)
        {
            return globalState;
        }

        // If no tenant, return global state
        if (string.IsNullOrEmpty(tenantId))
        {
            return globalState;
        }

        // Check tenant cache
        var cacheKey = GetTenantCacheKey(tenantId, apiKeyId);
        if (_cache.TryGetValue(cacheKey, out var cached) && !cached.IsExpired)
        {
            return MergeWithGlobal(cached.State, globalState);
        }

        // Fetch from database
        var tenantState = await FetchTenantStateAsync(tenantId, apiKeyId, ct);

        // Cache the result
        _cache[cacheKey] = new CachedState(tenantState, DateTimeOffset.UtcNow.Add(CacheTtl));

        return MergeWithGlobal(tenantState, globalState);
    }

    private async Task<KillSwitchState> GetGlobalStateAsync(CancellationToken ct)
    {
        if (_cache.TryGetValue(GlobalStateKey, out var cached) && !cached.IsExpired)
        {
            return cached.State;
        }

        var state = await FetchGlobalStateAsync(ct);
        _cache[GlobalStateKey] = new CachedState(state, DateTimeOffset.UtcNow.Add(CacheTtl));
        return state;
    }

    private async Task<KillSwitchState> FetchGlobalStateAsync(CancellationToken ct)
    {
        await using var conn = await OpenInternalConnectionAsync(ct);

        // Check for global emergency cutoff
        var globalCutoff = await conn.QueryFirstOrDefaultAsync<EmergencyCutoffDto>(
            """
            SELECT reason, ends_at, metadata
            FROM emergency_cutoffs
            WHERE tenant_id IS NULL
              AND is_active = TRUE
              AND (ends_at IS NULL OR ends_at > NOW())
            ORDER BY triggered_at DESC
            LIMIT 1
            """);

        if (globalCutoff != null)
        {
            var mode = GetModeFromMetadata(globalCutoff.metadata);
            return new KillSwitchState
            {
                GlobalOff = mode == KillSwitchMode.Off,
                GlobalReadOnly = mode == KillSwitchMode.ReadOnly,
                Reason = globalCutoff.reason,
                ExpiresAt = globalCutoff.ends_at
            };
        }

        return KillSwitchState.AllClear;
    }

    private async Task<KillSwitchState> FetchTenantStateAsync(
        string tenantId,
        string? apiKeyId,
        CancellationToken ct)
    {
        await using var conn = await OpenInternalConnectionAsync(ct);

        // Check tenant emergency cutoff
        var tenantCutoff = await conn.QueryFirstOrDefaultAsync<EmergencyCutoffDto>(
            """
            SELECT reason, ends_at, metadata
            FROM emergency_cutoffs
            WHERE tenant_id = @TenantId::uuid
              AND is_active = TRUE
              AND (ends_at IS NULL OR ends_at > NOW())
            ORDER BY triggered_at DESC
            LIMIT 1
            """,
            new { TenantId = tenantId });

        var state = new KillSwitchState();
        string? reason = null;
        DateTimeOffset? expiresAt = null;

        if (tenantCutoff != null)
        {
            var mode = GetModeFromMetadata(tenantCutoff.metadata);
            state = state with
            {
                TenantOff = mode == KillSwitchMode.Off,
                TenantReadOnly = mode == KillSwitchMode.ReadOnly,
                TenantNoIngest = mode == KillSwitchMode.NoIngest
            };
            reason = tenantCutoff.reason;
            expiresAt = tenantCutoff.ends_at;
        }

        // Check API key status if provided
        if (!string.IsNullOrEmpty(apiKeyId))
        {
            var apiKeyDisabled = await conn.ExecuteScalarAsync<bool>(
                """
                SELECT EXISTS(
                    SELECT 1 FROM tenant_api_keys
                    WHERE id = @ApiKeyId::uuid
                      AND (revoked_at IS NOT NULL OR expires_at < NOW())
                )
                """,
                new { ApiKeyId = apiKeyId });

            if (apiKeyDisabled)
            {
                state = state with
                {
                    ApiKeyDisabled = true
                };
                reason ??= "API key has been disabled or expired";
            }
        }

        return state with
        {
            Reason = reason,
            ExpiresAt = expiresAt
        };
    }

    private static KillSwitchState MergeWithGlobal(KillSwitchState tenant, KillSwitchState global)
    {
        return tenant with
        {
            GlobalOff = global.GlobalOff,
            GlobalReadOnly = global.GlobalReadOnly,
            Reason = global.GlobalOff || global.GlobalReadOnly ? global.Reason : tenant.Reason,
            ExpiresAt = global.GlobalOff || global.GlobalReadOnly ? global.ExpiresAt : tenant.ExpiresAt
        };
    }

    public async Task EnableGlobalKillSwitchAsync(
        string reason,
        string triggeredBy,
        TimeSpan? duration = null,
        CancellationToken ct = default)
    {
        await SetGlobalStateAsync(KillSwitchMode.Off, reason, triggeredBy, duration, ct);
        _logger.LogCritical(
            "GLOBAL KILL SWITCH ENABLED by {TriggeredBy}: {Reason}. Duration: {Duration}",
            triggeredBy, reason, duration?.ToString() ?? "indefinite");
    }

    public async Task DisableGlobalKillSwitchAsync(
        string reason,
        string liftedBy,
        CancellationToken ct = default)
    {
        await LiftGlobalStateAsync(reason, liftedBy, ct);
        _logger.LogWarning(
            "Global kill switch DISABLED by {LiftedBy}: {Reason}",
            liftedBy, reason);
    }

    public async Task EnableGlobalReadOnlyAsync(
        string reason,
        string triggeredBy,
        TimeSpan? duration = null,
        CancellationToken ct = default)
    {
        await SetGlobalStateAsync(KillSwitchMode.ReadOnly, reason, triggeredBy, duration, ct);
        _logger.LogWarning(
            "GLOBAL READ-ONLY MODE ENABLED by {TriggeredBy}: {Reason}. Duration: {Duration}",
            triggeredBy, reason, duration?.ToString() ?? "indefinite");
    }

    public async Task DisableGlobalReadOnlyAsync(
        string reason,
        string liftedBy,
        CancellationToken ct = default)
    {
        await LiftGlobalStateAsync(reason, liftedBy, ct);
        _logger.LogWarning(
            "Global read-only mode DISABLED by {LiftedBy}: {Reason}",
            liftedBy, reason);
    }

    public async Task EnableTenantKillSwitchAsync(
        string tenantId,
        string reason,
        string triggeredBy,
        TimeSpan? duration = null,
        CancellationToken ct = default)
    {
        await SetTenantStateAsync(tenantId, KillSwitchMode.Off, reason, triggeredBy, duration, ct);
        _logger.LogCritical(
            "TENANT KILL SWITCH ENABLED for {TenantId} by {TriggeredBy}: {Reason}. Duration: {Duration}",
            tenantId, triggeredBy, reason, duration?.ToString() ?? "indefinite");
    }

    public async Task DisableTenantKillSwitchAsync(
        string tenantId,
        string reason,
        string liftedBy,
        CancellationToken ct = default)
    {
        await LiftTenantStateAsync(tenantId, reason, liftedBy, ct);
        _logger.LogWarning(
            "Tenant kill switch DISABLED for {TenantId} by {LiftedBy}: {Reason}",
            tenantId, liftedBy, reason);
    }

    public async Task EnableTenantReadOnlyAsync(
        string tenantId,
        string reason,
        string triggeredBy,
        TimeSpan? duration = null,
        CancellationToken ct = default)
    {
        await SetTenantStateAsync(tenantId, KillSwitchMode.ReadOnly, reason, triggeredBy, duration, ct);
        _logger.LogWarning(
            "TENANT READ-ONLY MODE ENABLED for {TenantId} by {TriggeredBy}: {Reason}. Duration: {Duration}",
            tenantId, triggeredBy, reason, duration?.ToString() ?? "indefinite");
    }

    public async Task DisableTenantReadOnlyAsync(
        string tenantId,
        string reason,
        string liftedBy,
        CancellationToken ct = default)
    {
        await LiftTenantStateAsync(tenantId, reason, liftedBy, ct);
        _logger.LogWarning(
            "Tenant read-only mode DISABLED for {TenantId} by {LiftedBy}: {Reason}",
            tenantId, liftedBy, reason);
    }

    public async Task EnableTenantNoIngestAsync(
        string tenantId,
        string reason,
        string triggeredBy,
        TimeSpan? duration = null,
        CancellationToken ct = default)
    {
        await SetTenantStateAsync(tenantId, KillSwitchMode.NoIngest, reason, triggeredBy, duration, ct);
        _logger.LogWarning(
            "TENANT NO-INGEST MODE ENABLED for {TenantId} by {TriggeredBy}: {Reason}. Duration: {Duration}",
            tenantId, triggeredBy, reason, duration?.ToString() ?? "indefinite");
    }

    public async Task DisableTenantNoIngestAsync(
        string tenantId,
        string reason,
        string liftedBy,
        CancellationToken ct = default)
    {
        await LiftTenantStateAsync(tenantId, reason, liftedBy, ct);
        _logger.LogWarning(
            "Tenant no-ingest mode DISABLED for {TenantId} by {LiftedBy}: {Reason}",
            tenantId, liftedBy, reason);
    }

    public async Task DisableApiKeyAsync(
        string apiKeyId,
        string reason,
        string disabledBy,
        CancellationToken ct = default)
    {
        await using var conn = await OpenInternalConnectionAsync(ct);

        await conn.ExecuteAsync(
            """
            UPDATE tenant_api_keys
            SET revoked_at = NOW(),
                metadata = COALESCE(metadata, '{}'::jsonb) || jsonb_build_object('disabled_by', @DisabledBy, 'revoked_reason', @Reason)
            WHERE id = @ApiKeyId::uuid
            """,
            new { ApiKeyId = apiKeyId, Reason = reason, DisabledBy = disabledBy });

        await LogAdminActionAsync(conn, "disable_api_key", "api_key", apiKeyId, reason, disabledBy, null, ct);

        InvalidateCache();
        _logger.LogWarning(
            "API key {ApiKeyId} DISABLED by {DisabledBy}: {Reason}",
            apiKeyId, disabledBy, reason);
    }

    public async Task EnableApiKeyAsync(
        string apiKeyId,
        string reason,
        string enabledBy,
        CancellationToken ct = default)
    {
        await using var conn = await OpenInternalConnectionAsync(ct);

        await conn.ExecuteAsync(
            """
            UPDATE tenant_api_keys
            SET revoked_at = NULL,
                metadata = COALESCE(metadata, '{}'::jsonb) || jsonb_build_object('enabled_by', @EnabledBy, 'enabled_at', NOW()::text) - 'revoked_reason'
            WHERE id = @ApiKeyId::uuid
            """,
            new { ApiKeyId = apiKeyId, EnabledBy = enabledBy });

        await LogAdminActionAsync(conn, "enable_api_key", "api_key", apiKeyId, reason, enabledBy, null, ct);

        InvalidateCache();
        _logger.LogWarning(
            "API key {ApiKeyId} ENABLED by {EnabledBy}: {Reason}",
            apiKeyId, enabledBy, reason);
    }

    public async Task<KillSwitchDashboardState> GetDashboardStateAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenInternalConnectionAsync(ct);

        // Get global state
        var globalCutoff = await conn.QueryFirstOrDefaultAsync<EmergencyCutoffDto>(
            """
            SELECT reason, ends_at, metadata
            FROM emergency_cutoffs
            WHERE tenant_id IS NULL
              AND is_active = TRUE
              AND (ends_at IS NULL OR ends_at > NOW())
            ORDER BY triggered_at DESC
            LIMIT 1
            """);

        bool globalKillSwitch = false;
        bool globalReadOnly = false;
        string? globalReason = null;
        DateTimeOffset? globalExpiresAt = null;

        if (globalCutoff != null)
        {
            var mode = GetModeFromMetadata(globalCutoff.metadata);
            globalKillSwitch = mode == KillSwitchMode.Off;
            globalReadOnly = mode == KillSwitchMode.ReadOnly;
            globalReason = globalCutoff.reason;
            globalExpiresAt = globalCutoff.ends_at;
        }

        // Get tenant kill switches
        var tenantCutoffs = await conn.QueryAsync<TenantCutoffDto>(
            """
            SELECT ec.tenant_id, t.name AS tenant_name, ec.reason, ec.triggered_by, ec.triggered_at, ec.ends_at, ec.metadata
            FROM emergency_cutoffs ec
            JOIN tenants t ON ec.tenant_id = t.id
            WHERE ec.tenant_id IS NOT NULL
              AND ec.is_active = TRUE
              AND (ec.ends_at IS NULL OR ec.ends_at > NOW())
            ORDER BY ec.triggered_at DESC
            """);

        var tenantKillSwitches = tenantCutoffs.Select(tc => new TenantKillSwitchInfo
        {
            TenantId = tc.tenant_id.ToString(),
            TenantName = tc.tenant_name ?? "Unknown",
            Mode = GetModeFromMetadata(tc.metadata),
            Reason = tc.reason ?? "No reason provided",
            TriggeredBy = tc.triggered_by ?? "system",
            TriggeredAt = tc.triggered_at,
            ExpiresAt = tc.ends_at
        }).ToList();

        // Get disabled API keys (revoked_reason stored in metadata jsonb)
        var disabledKeys = await conn.QueryAsync<DisabledKeyDto>(
            """
            SELECT ak.id AS api_key_id, ak.tenant_id, ak.key_prefix,
                   ak.metadata->>'revoked_reason' AS reason,
                   ak.revoked_at AS disabled_at,
                   COALESCE(ak.metadata->>'disabled_by', 'admin') AS disabled_by
            FROM tenant_api_keys ak
            WHERE ak.revoked_at IS NOT NULL
            ORDER BY ak.revoked_at DESC
            LIMIT 100
            """);

        var disabledApiKeys = disabledKeys.Select(dk => new DisabledApiKeyInfo
        {
            ApiKeyId = dk.api_key_id.ToString(),
            TenantId = dk.tenant_id.ToString(),
            KeyPrefix = dk.key_prefix ?? "****",
            Reason = dk.reason ?? "No reason provided",
            DisabledBy = dk.disabled_by ?? "unknown",
            DisabledAt = dk.disabled_at ?? DateTimeOffset.UtcNow
        }).ToList();

        return new KillSwitchDashboardState
        {
            GlobalKillSwitchActive = globalKillSwitch,
            GlobalReadOnlyActive = globalReadOnly,
            GlobalReason = globalReason,
            GlobalExpiresAt = globalExpiresAt,
            TenantKillSwitches = tenantKillSwitches,
            DisabledApiKeys = disabledApiKeys
        };
    }

    public async Task<IReadOnlyList<KillSwitchAction>> GetRecentActionsAsync(
        int limit = 50,
        CancellationToken ct = default)
    {
        await using var conn = await OpenInternalConnectionAsync(ct);

        var actions = await conn.QueryAsync<AdminActionDto>(
            """
            SELECT id, tool_name, tenant_id, user_id, timestamp, params_hash, metadata, success, error_message
            FROM admin_actions
            WHERE tool_name LIKE 'kill_switch_%' OR tool_name LIKE '%api_key%'
            ORDER BY timestamp DESC
            LIMIT @Limit
            """,
            new { Limit = limit });

        return actions.Select(a => new KillSwitchAction
        {
            Id = a.id,
            ActionType = a.tool_name ?? "unknown",
            TargetType = GetTargetTypeFromToolName(a.tool_name),
            TargetId = a.tenant_id?.ToString(),
            Reason = GetReasonFromMetadata(a.metadata),
            PerformedBy = a.user_id ?? "system",
            PerformedAt = a.timestamp,
            Metadata = ParseMetadata(a.metadata)
        }).ToList();
    }

    public void InvalidateCache(string? tenantId = null)
    {
        if (tenantId == null)
        {
            _cache.Clear();
        }
        else
        {
            // Remove all cache entries for this tenant
            var keysToRemove = _cache.Keys.Where(k => k.Contains(tenantId)).ToList();
            foreach (var key in keysToRemove)
            {
                _cache.TryRemove(key, out _);
            }
            // Also invalidate global cache
            _cache.TryRemove(GlobalStateKey, out _);
        }
    }

    #region Private Helpers
    /// <summary>
    /// Opens a connection with internal admin role for RLS bypass.
    /// </summary>
    private async Task<NpgsqlConnection> OpenInternalConnectionAsync(CancellationToken ct)
    {
        var conn = await _dataSource.OpenConnectionAsync(ct);
        // Use false to set for session (not just transaction) - ensures RLS bypass works across statements
        await conn.ExecuteAsync("SELECT set_config('app.role', 'internal_admin', false)");
        return conn;
    }


    private async Task SetGlobalStateAsync(
        KillSwitchMode mode,
        string reason,
        string triggeredBy,
        TimeSpan? duration,
        CancellationToken ct)
    {
        await using var conn = await OpenInternalConnectionAsync(ct);

        var endsAt = duration.HasValue ? DateTimeOffset.UtcNow.Add(duration.Value) : (DateTimeOffset?)null;
        var metadata = System.Text.Json.JsonSerializer.Serialize(new { mode = mode.ToString() });

        // Deactivate any existing global cutoff
        await conn.ExecuteAsync(
            """
            UPDATE emergency_cutoffs
            SET is_active = FALSE, lifted_at = NOW(), lift_reason = 'Superseded by new global state'
            WHERE tenant_id IS NULL AND is_active = TRUE
            """);

        // Insert new global cutoff
        await conn.ExecuteAsync(
            """
            INSERT INTO emergency_cutoffs (id, tenant_id, reason, triggered_at, triggered_by, ends_at, is_active, metadata)
            VALUES (@Id, NULL, @Reason, NOW(), @TriggeredBy, @EndsAt, TRUE, @Metadata::jsonb)
            """,
            new
            {
                Id = Guid.CreateVersion7(),
                Reason = reason,
                TriggeredBy = triggeredBy,
                EndsAt = endsAt,
                Metadata = metadata
            });

        await LogAdminActionAsync(conn, $"kill_switch_global_{mode.ToString().ToLowerInvariant()}", "global", null, reason, triggeredBy, duration, ct);

        InvalidateCache();
    }

    private async Task LiftGlobalStateAsync(string reason, string liftedBy, CancellationToken ct)
    {
        await using var conn = await OpenInternalConnectionAsync(ct);

        await conn.ExecuteAsync(
            """
            UPDATE emergency_cutoffs
            SET is_active = FALSE, lifted_at = NOW(), lift_reason = @Reason, lifted_by = @LiftedBy
            WHERE tenant_id IS NULL AND is_active = TRUE
            """,
            new { Reason = reason, LiftedBy = liftedBy });

        await LogAdminActionAsync(conn, "kill_switch_global_lift", "global", null, reason, liftedBy, null, ct);

        InvalidateCache();
    }

    private async Task SetTenantStateAsync(
        string tenantId,
        KillSwitchMode mode,
        string reason,
        string triggeredBy,
        TimeSpan? duration,
        CancellationToken ct)
    {
        await using var conn = await OpenInternalConnectionAsync(ct);

        var endsAt = duration.HasValue ? DateTimeOffset.UtcNow.Add(duration.Value) : (DateTimeOffset?)null;
        var metadata = System.Text.Json.JsonSerializer.Serialize(new { mode = mode.ToString() });

        // Deactivate any existing tenant cutoff
        await conn.ExecuteAsync(
            """
            UPDATE emergency_cutoffs
            SET is_active = FALSE, lifted_at = NOW(), lift_reason = 'Superseded by new tenant state'
            WHERE tenant_id = @TenantId::uuid AND is_active = TRUE
            """,
            new { TenantId = tenantId });

        // Insert new tenant cutoff
        await conn.ExecuteAsync(
            """
            INSERT INTO emergency_cutoffs (id, tenant_id, reason, triggered_at, triggered_by, ends_at, is_active, metadata)
            VALUES (@Id, @TenantId::uuid, @Reason, NOW(), @TriggeredBy, @EndsAt, TRUE, @Metadata::jsonb)
            """,
            new
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                Reason = reason,
                TriggeredBy = triggeredBy,
                EndsAt = endsAt,
                Metadata = metadata
            });

        await LogAdminActionAsync(conn, $"kill_switch_tenant_{mode.ToString().ToLowerInvariant()}", "tenant", tenantId, reason, triggeredBy, duration, ct);

        InvalidateCache(tenantId);
    }

    private async Task LiftTenantStateAsync(string tenantId, string reason, string liftedBy, CancellationToken ct)
    {
        await using var conn = await OpenInternalConnectionAsync(ct);

        await conn.ExecuteAsync(
            """
            UPDATE emergency_cutoffs
            SET is_active = FALSE, lifted_at = NOW(), lift_reason = @Reason, lifted_by = @LiftedBy
            WHERE tenant_id = @TenantId::uuid AND is_active = TRUE
            """,
            new { TenantId = tenantId, Reason = reason, LiftedBy = liftedBy });

        await LogAdminActionAsync(conn, "kill_switch_tenant_lift", "tenant", tenantId, reason, liftedBy, null, ct);

        InvalidateCache(tenantId);
    }

    private static async Task LogAdminActionAsync(
        NpgsqlConnection conn,
        string toolName,
        string targetType,
        string? targetId,
        string reason,
        string userId,
        TimeSpan? duration,
        CancellationToken ct)
    {
        var metadata = System.Text.Json.JsonSerializer.Serialize(new
        {
            target_type = targetType,
            target_id = targetId,
            reason,
            duration = duration?.ToString()
        });

        // Parse tenant ID - if targetId is a valid GUID, use it; otherwise use a system tenant
        Guid? tenantId = null;
        if (!string.IsNullOrEmpty(targetId) && Guid.TryParse(targetId, out var tid))
        {
            tenantId = tid;
        }

        // Use the append_admin_action function which handles hash chaining
        await conn.ExecuteAsync(
            """
            SELECT append_admin_action(@TenantId, @UserId, @ToolName, @ParamsHash, @Metadata::jsonb)
            """,
            new
            {
                TenantId = tenantId,
                UserId = userId,
                ToolName = toolName,
                ParamsHash = ComputeHash(metadata),
                Metadata = metadata
            });
    }

    private static string ComputeHash(string input)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static KillSwitchMode GetModeFromMetadata(string? metadata)
    {
        if (string.IsNullOrEmpty(metadata)) return KillSwitchMode.Off;

        try
        {
            var doc = System.Text.Json.JsonDocument.Parse(metadata);
            if (doc.RootElement.TryGetProperty("mode", out var modeElement))
            {
                var modeStr = modeElement.GetString();
                return modeStr switch
                {
                    "Off" => KillSwitchMode.Off,
                    "ReadOnly" => KillSwitchMode.ReadOnly,
                    "NoIngest" => KillSwitchMode.NoIngest,
                    _ => KillSwitchMode.Off
                };
            }
        }
        catch
        {
            // Ignore parse errors
        }

        return KillSwitchMode.Off;
    }

    private static string GetTargetTypeFromToolName(string? toolName)
    {
        return toolName switch
        {
            var t when t?.Contains("global") == true => "global",
            var t when t?.Contains("tenant") == true => "tenant",
            var t when t?.Contains("api_key") == true => "api_key",
            _ => "unknown"
        };
    }

    private static string GetReasonFromMetadata(string? metadata)
    {
        if (string.IsNullOrEmpty(metadata)) return "No reason provided";

        try
        {
            var doc = System.Text.Json.JsonDocument.Parse(metadata);
            if (doc.RootElement.TryGetProperty("reason", out var reasonElement))
            {
                return reasonElement.GetString() ?? "No reason provided";
            }
        }
        catch
        {
            // Ignore parse errors
        }

        return "No reason provided";
    }

    private static Dictionary<string, object>? ParseMetadata(string? metadata)
    {
        if (string.IsNullOrEmpty(metadata)) return null;

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(metadata);
        }
        catch
        {
            return null;
        }
    }

    private static string GetTenantCacheKey(string tenantId, string? apiKeyId) =>
        apiKeyId != null ? $"{tenantId}:{apiKeyId}" : tenantId;

    #endregion

    #region DTOs

    private sealed class CachedState(KillSwitchState state, DateTimeOffset expiresAt)
    {
        public KillSwitchState State { get; } = state;
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
        public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt;
    }

    private sealed class EmergencyCutoffDto
    {
        public string? reason { get; set; }
        public DateTimeOffset? ends_at { get; set; }
        public string? metadata { get; set; }
    }

    private sealed class TenantCutoffDto
    {
        public Guid tenant_id { get; set; }
        public string? tenant_name { get; set; }
        public string? reason { get; set; }
        public string? triggered_by { get; set; }
        public DateTimeOffset triggered_at { get; set; }
        public DateTimeOffset? ends_at { get; set; }
        public string? metadata { get; set; }
    }

    private sealed class DisabledKeyDto
    {
        public Guid api_key_id { get; set; }
        public Guid tenant_id { get; set; }
        public string? key_prefix { get; set; }
        public string? reason { get; set; }
        public string? disabled_by { get; set; }
        public DateTimeOffset? disabled_at { get; set; }
    }

    private sealed class AdminActionDto
    {
        public Guid id { get; set; }
        public string? tool_name { get; set; }
        public Guid? tenant_id { get; set; }
        public string? user_id { get; set; }
        public DateTimeOffset timestamp { get; set; }
        public string? params_hash { get; set; }
        public string? metadata { get; set; }
        public bool success { get; set; }
        public string? error_message { get; set; }
    }

    #endregion
}
