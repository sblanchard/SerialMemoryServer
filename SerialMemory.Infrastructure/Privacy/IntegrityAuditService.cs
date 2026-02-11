using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using SerialMemory.Core.Interfaces;

namespace SerialMemory.Infrastructure.Privacy;

/// <summary>
/// PostgreSQL-backed integrity audit service.
/// Logs all memory operations and integrity-related events for audit trail.
/// </summary>
public sealed class IntegrityAuditService(
    string connectionString,
    ITenantContext tenantContext,
    ILogger<IntegrityAuditService> logger)
    : IIntegrityAuditService
{
    private readonly ITenantContext _tenantContext = tenantContext;
    private readonly Guid _tenantId = Guid.Parse(tenantContext.TenantId);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public async Task LogAsync(IntegrityAuditEntry entry, CancellationToken ct = default)
    {
        // Check if audit logging is enabled
        if (!await IsAuditEnabledAsync(ct))
            return;

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);

        // Set internal admin role to bypass RLS for audit log entries
        await conn.SetInternalAdminWithTenantAsync(_tenantId);

        var detailsJson = entry.Details != null
            ? JsonSerializer.Serialize(entry.Details, JsonOptions)
            : null;

        await conn.ExecuteAsync(@"
            INSERT INTO privacy_audit_entries
                (id, tenant_id, user_id, action, memory_id, timestamp, ip_address, user_agent, integrity_valid, details, request_id)
            VALUES
                (@Id, @TenantId, @UserId, @Action, @MemoryId, @Timestamp, @IpAddress, @UserAgent, @IntegrityValid, @Details::jsonb, @RequestId)",
            new
            {
                entry.Id,
                TenantId = entry.TenantId == Guid.Empty ? _tenantId : entry.TenantId,
                entry.UserId,
                Action = entry.Action.ToString(),
                entry.MemoryId,
                entry.Timestamp,
                entry.IpAddress,
                entry.UserAgent,
                entry.IntegrityValid,
                Details = detailsJson,
                entry.RequestId
            });

        logger.LogDebug("Integrity audit: {Action} by {UserId} on memory {MemoryId}",
            entry.Action, entry.UserId, entry.MemoryId);
    }

    public async Task<IntegrityPagedResult<IntegrityAuditEntry>> GetAuditEntriesAsync(
        IntegrityAuditQueryOptions options,
        CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await conn.SetTenantContextAsync(_tenantId);

        var whereClauses = new List<string> { "1=1" };
        var parameters = new DynamicParameters();

        if (options.Action.HasValue)
        {
            whereClauses.Add("action = @Action");
            parameters.Add("Action", options.Action.Value.ToString());
        }

        if (!string.IsNullOrEmpty(options.UserId))
        {
            whereClauses.Add("user_id = @UserId");
            parameters.Add("UserId", options.UserId);
        }

        if (options.MemoryId.HasValue)
        {
            whereClauses.Add("memory_id = @MemoryId");
            parameters.Add("MemoryId", options.MemoryId.Value);
        }

        if (options.From.HasValue)
        {
            whereClauses.Add("timestamp >= @From");
            parameters.Add("From", options.From.Value);
        }

        if (options.To.HasValue)
        {
            whereClauses.Add("timestamp <= @To");
            parameters.Add("To", options.To.Value);
        }

        if (options.IntegrityValid.HasValue)
        {
            whereClauses.Add("integrity_valid = @IntegrityValid");
            parameters.Add("IntegrityValid", options.IntegrityValid.Value);
        }

        var whereClause = string.Join(" AND ", whereClauses);
        var orderBy = options.SortDescending ? "DESC" : "ASC";
        var offset = (options.Page - 1) * options.PageSize;

        parameters.Add("Limit", options.PageSize);
        parameters.Add("Offset", offset);

        // Get total count
        var countSql = $"SELECT COUNT(*) FROM privacy_audit_entries WHERE {whereClause}";
        var totalCount = await conn.ExecuteScalarAsync<int>(countSql, parameters);

        // Get items
        var sql = $@"
            SELECT id, tenant_id, user_id, action, memory_id, timestamp,
                   ip_address, user_agent, integrity_valid, details, request_id
            FROM privacy_audit_entries
            WHERE {whereClause}
            ORDER BY timestamp {orderBy}
            LIMIT @Limit OFFSET @Offset";

        var rows = await conn.QueryAsync<AuditEntryRow>(sql, parameters);

        var items = rows.Select(r => new IntegrityAuditEntry
        {
            Id = r.id,
            TenantId = r.tenant_id,
            UserId = r.user_id,
            Action = Enum.TryParse<IntegrityAuditAction>(r.action, out var action) ? action : IntegrityAuditAction.MemoryRead,
            MemoryId = r.memory_id,
            Timestamp = r.timestamp,
            IpAddress = r.ip_address,
            UserAgent = r.user_agent,
            IntegrityValid = r.integrity_valid,
            Details = ParseDetails(r.details),
            RequestId = r.request_id
        }).ToList();

        return new IntegrityPagedResult<IntegrityAuditEntry>
        {
            Items = items,
            TotalCount = totalCount,
            Page = options.Page,
            PageSize = options.PageSize
        };
    }

    public async Task<IReadOnlyList<IntegrityAuditEntry>> GetMemoryAuditTrailAsync(
        Guid memoryId,
        CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await conn.SetTenantContextAsync(_tenantId);

        var rows = await conn.QueryAsync<AuditEntryRow>(@"
            SELECT id, tenant_id, user_id, action, memory_id, timestamp,
                   ip_address, user_agent, integrity_valid, details, request_id
            FROM privacy_audit_entries
            WHERE memory_id = @MemoryId
            ORDER BY timestamp DESC",
            new { MemoryId = memoryId });

        return rows.Select(r => new IntegrityAuditEntry
        {
            Id = r.id,
            TenantId = r.tenant_id,
            UserId = r.user_id,
            Action = Enum.TryParse<IntegrityAuditAction>(r.action, out var action) ? action : IntegrityAuditAction.MemoryRead,
            MemoryId = r.memory_id,
            Timestamp = r.timestamp,
            IpAddress = r.ip_address,
            UserAgent = r.user_agent,
            IntegrityValid = r.integrity_valid,
            Details = ParseDetails(r.details),
            RequestId = r.request_id
        }).ToList();
    }

    public async Task<TenantIntegritySettings> GetSettingsAsync(CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await conn.SetTenantContextAsync(_tenantId);

        var row = await conn.QueryFirstOrDefaultAsync<SettingsRow>(@"
            SELECT tenant_id, enable_privacy_mode, enable_hash_verification,
                   enable_audit_logs, hash_algorithm, auto_verify_interval_hours,
                   created_at, updated_at
            FROM tenant_privacy_settings
            WHERE tenant_id = @TenantId",
            new { TenantId = _tenantId });

        if (row == null)
        {
            return new TenantIntegritySettings
            {
                TenantId = _tenantId,
                EnablePrivacyMode = false,
                EnableHashVerification = true,
                EnableAuditLogs = true,
                HashAlgorithm = "SHA256",
                AutoVerifyIntervalHours = 24,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
        }

        return new TenantIntegritySettings
        {
            TenantId = row.tenant_id,
            EnablePrivacyMode = row.enable_privacy_mode,
            EnableHashVerification = row.enable_hash_verification,
            EnableAuditLogs = row.enable_audit_logs,
            HashAlgorithm = row.hash_algorithm,
            AutoVerifyIntervalHours = row.auto_verify_interval_hours,
            CreatedAt = row.created_at,
            UpdatedAt = row.updated_at
        };
    }

    public async Task<TenantIntegritySettings> UpdateSettingsAsync(
        TenantIntegritySettingsUpdate update,
        CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await conn.SetTenantContextAsync(_tenantId);

        var current = await GetSettingsAsync(ct);
        var now = DateTimeOffset.UtcNow;

        await conn.ExecuteAsync(@"
            INSERT INTO tenant_privacy_settings
                (tenant_id, enable_privacy_mode, enable_hash_verification,
                 enable_audit_logs, hash_algorithm, auto_verify_interval_hours, created_at, updated_at)
            VALUES
                (@TenantId, @EnablePrivacyMode, @EnableHashVerification,
                 @EnableAuditLogs, @HashAlgorithm, @AutoVerifyIntervalHours, @Now, @Now)
            ON CONFLICT (tenant_id) DO UPDATE SET
                enable_privacy_mode = @EnablePrivacyMode,
                enable_hash_verification = @EnableHashVerification,
                enable_audit_logs = @EnableAuditLogs,
                hash_algorithm = @HashAlgorithm,
                auto_verify_interval_hours = @AutoVerifyIntervalHours,
                updated_at = @Now",
            new
            {
                TenantId = _tenantId,
                EnablePrivacyMode = update.EnablePrivacyMode ?? current.EnablePrivacyMode,
                EnableHashVerification = update.EnableHashVerification ?? current.EnableHashVerification,
                EnableAuditLogs = update.EnableAuditLogs ?? current.EnableAuditLogs,
                HashAlgorithm = update.HashAlgorithm ?? current.HashAlgorithm,
                AutoVerifyIntervalHours = update.AutoVerifyIntervalHours ?? current.AutoVerifyIntervalHours,
                Now = now
            });

        await LogAsync(new IntegrityAuditEntry
        {
            TenantId = _tenantId,
            Action = IntegrityAuditAction.SettingsUpdated,
            Details = new Dictionary<string, object>
            {
                ["changes"] = update
            }
        }, ct);

        logger.LogInformation("Integrity settings updated for tenant {TenantId}", _tenantId);

        return await GetSettingsAsync(ct);
    }

    public async Task<bool> IsAuditEnabledAsync(CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);

        // Must set tenant context to bypass RLS (prevents UUID cast error on empty app.tenant_id)
        await conn.SetInternalAdminWithTenantAsync(_tenantId);

        var enabled = await conn.ExecuteScalarAsync<bool?>(@"
            SELECT enable_audit_logs FROM tenant_privacy_settings WHERE tenant_id = @TenantId",
            new { TenantId = _tenantId });

        return enabled ?? true;
    }

    private static Dictionary<string, object>? ParseDetails(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object>>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private sealed record AuditEntryRow
    {
        public Guid id { get; init; }
        public Guid tenant_id { get; init; }
        public string? user_id { get; init; }
        public string action { get; init; } = "";
        public Guid? memory_id { get; init; }
        public DateTimeOffset timestamp { get; init; }
        public string? ip_address { get; init; }
        public string? user_agent { get; init; }
        public bool? integrity_valid { get; init; }
        public string? details { get; init; }
        public Guid? request_id { get; init; }
    }

    private sealed record SettingsRow
    {
        public Guid tenant_id { get; init; }
        public bool enable_privacy_mode { get; init; }
        public bool enable_hash_verification { get; init; }
        public bool enable_audit_logs { get; init; }
        public string hash_algorithm { get; init; } = "SHA256";
        public int auto_verify_interval_hours { get; init; }
        public DateTimeOffset created_at { get; init; }
        public DateTimeOffset updated_at { get; init; }
    }
}
