using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using SerialMemory.Core.Interfaces;

namespace SerialMemory.Infrastructure;

/// <summary>
/// Service for audit-quality logging with tamper detection via hash chains.
/// </summary>
public sealed class AuditLogService : IAuditLogService
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<AuditLogService> _logger;

    public AuditLogService(string connectionString, ILogger<AuditLogService> logger)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        _dataSource = builder.Build();
        _logger = logger;
    }

    public AuditLogService(NpgsqlDataSource dataSource, ILogger<AuditLogService> logger)
    {
        _dataSource = dataSource;
        _logger = logger;
    }

    /// <summary>
    /// Opens a connection with tenant context for RLS.
    /// </summary>
    private async Task<NpgsqlConnection> OpenTenantConnectionAsync(string tenantId, CancellationToken ct)
    {
        var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            """
            SELECT set_config('app.tenant_id', @TenantId, false);
            SELECT set_config('app.current_tenant_id', @TenantId, false);
            """,
            new { TenantId = tenantId });
        return conn;
    }

    /// <summary>
    /// Opens a connection with internal admin role for verification operations.
    /// </summary>
    private async Task<NpgsqlConnection> OpenInternalConnectionAsync(CancellationToken ct)
    {
        var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync("SELECT set_config('app.role', 'internal_admin', false)");
        return conn;
    }

    public async Task<AuditLogEntry> AppendAsync(
        string tenantId,
        string workspaceId,
        AuditEventType eventType,
        string description,
        Dictionary<string, object>? data = null,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenTenantConnectionAsync(tenantId, cancellationToken);

        // Get current billing cycle ID
        var billingCycleId = await conn.QueryFirstOrDefaultAsync<Guid?>(
            """
            SELECT id FROM billing_cycles
                          WHERE tenant_id = @TenantId AND workspace_id = @WorkspaceId AND is_current = TRUE
            """,
            new { TenantId = tenantId, WorkspaceId = workspaceId });

        var eventTypeString = eventType.ToString();
        var dataJson = data != null ? JsonSerializer.Serialize(data) : null;

        // Use the SQL function for append
        var dto = await conn.QueryFirstAsync<AuditLogDto>(
            """
            SELECT * FROM append_audit_log(
                            @TenantId, @WorkspaceId, @BillingCycleId,
                            @EventType, @Description, @Data::jsonb,
                            @UserId, @IpAddress::inet)
            """,
            new
            {
                TenantId = tenantId,
                WorkspaceId = workspaceId,
                BillingCycleId = billingCycleId,
                EventType = eventTypeString,
                Description = description,
                Data = dataJson,
                UserId = (string?)null,
                IpAddress = (string?)null
            });

        _logger.LogDebug(
            "Appended audit log entry {Id} for tenant {TenantId}: {EventType}",
            dto.id, tenantId, eventType);

        return MapToEntry(dto);
    }

    public async Task<AuditVerificationResult> VerifyIntegrityAsync(
        Guid billingCycleId,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenInternalConnectionAsync(cancellationToken);

        // Get billing cycle info
        var cycleInfo = await conn.QueryFirstOrDefaultAsync<dynamic>(
            "SELECT tenant_id, workspace_id FROM billing_cycles WHERE id = @Id",
            new { Id = billingCycleId });

        if (cycleInfo == null)
        {
            return new AuditVerificationResult
            {
                IsValid = false,
                Issues = [new AuditIntegrityIssue
                {
                    IssueType = AuditIntegrityIssueType.MissingEntry,
                    Description = $"Billing cycle {billingCycleId} not found"
                }]
            };
        }

        // Use the SQL verification function
        var verifyResult = await conn.QueryFirstAsync<VerificationResultDto>(
            @"SELECT * FROM verify_audit_log_integrity(@TenantId, @WorkspaceId, @BillingCycleId)",
            new
            {
                TenantId = (string)cycleInfo.tenant_id,
                WorkspaceId = (string)cycleInfo.workspace_id,
                BillingCycleId = billingCycleId
            });

        var issues = new List<AuditIntegrityIssue>();

        if (!verifyResult.is_valid && verifyResult.first_issue_sequence.HasValue)
        {
            issues.Add(new AuditIntegrityIssue
            {
                IssueType = ParseIssueType(verifyResult.first_issue_type ?? ""),
                SequenceNumber = verifyResult.first_issue_sequence,
                Description = verifyResult.first_issue_description ?? ""
            });
        }

        // Get first and last hashes
        var hashes = await conn.QueryFirstOrDefaultAsync<dynamic>(
            """
            SELECT
                            (SELECT content_hash FROM audit_logs
                             WHERE billing_cycle_id = @CycleId ORDER BY sequence_number LIMIT 1) AS first_hash,
                            (SELECT content_hash FROM audit_logs
                             WHERE billing_cycle_id = @CycleId ORDER BY sequence_number DESC LIMIT 1) AS last_hash
            """,
            new { CycleId = billingCycleId });

        _logger.LogInformation(
            "Verified audit log integrity for cycle {CycleId}: {Valid}, {Total} entries, {Verified} verified",
            billingCycleId, verifyResult.is_valid, verifyResult.total_entries, verifyResult.verified_entries);

        return new AuditVerificationResult
        {
            IsValid = verifyResult.is_valid,
            TotalEntries = verifyResult.total_entries,
            VerifiedEntries = verifyResult.verified_entries,
            Issues = issues,
            FirstEntryHash = hashes?.first_hash,
            LastEntryHash = hashes?.last_hash
        };
    }

    public async Task<IReadOnlyList<AuditLogEntry>> GetEntriesAsync(
        Guid billingCycleId,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenInternalConnectionAsync(cancellationToken);

        var entries = await conn.QueryAsync<AuditLogDto>(
            """
            SELECT id, sequence_number, tenant_id, workspace_id, billing_cycle_id,
                                 event_type, description, data, user_id, ip_address,
                                 timestamp, previous_hash, content_hash
                          FROM audit_logs
                          WHERE billing_cycle_id = @BillingCycleId
                          ORDER BY sequence_number DESC
                          LIMIT @Limit
            """,
            new { BillingCycleId = billingCycleId, Limit = limit ?? 1000 });

        return entries.Select(MapToEntry).ToList();
    }

    public async Task<IReadOnlyList<AuditLogEntry>> GetEntriesByDateRangeAsync(
        string tenantId,
        string workspaceId,
        DateTimeOffset fromDate,
        DateTimeOffset toDate,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenTenantConnectionAsync(tenantId, cancellationToken);

        var entries = await conn.QueryAsync<AuditLogDto>(
            """
            SELECT id, sequence_number, tenant_id, workspace_id, billing_cycle_id,
                                 event_type, description, data, user_id, ip_address,
                                 timestamp, previous_hash, content_hash
                          FROM audit_logs
                          WHERE tenant_id = @TenantId AND workspace_id = @WorkspaceId
                            AND timestamp >= @From AND timestamp <= @To
                          ORDER BY sequence_number DESC
            """,
            new { TenantId = tenantId, WorkspaceId = workspaceId, From = fromDate, To = toDate });

        return entries.Select(MapToEntry).ToList();
    }

    /// <summary>
    /// Computes a content hash for verification purposes (matches SQL function).
    /// </summary>
    public static string ComputeContentHash(
        string tenantId,
        string workspaceId,
        string eventType,
        string description,
        string? dataJson,
        DateTimeOffset timestamp,
        string previousHash)
    {
        var content = string.Join("|",
            tenantId ?? "",
            workspaceId ?? "",
            eventType ?? "",
            description ?? "",
            dataJson ?? "",
            timestamp.ToString("O"),
            previousHash ?? "");

        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static AuditLogEntry MapToEntry(AuditLogDto dto)
    {
        Dictionary<string, object>? data = null;
        if (!string.IsNullOrEmpty(dto.data))
        {
            try
            {
                data = JsonSerializer.Deserialize<Dictionary<string, object>>(dto.data);
            }
            catch
            {
                // Ignore deserialization errors
            }
        }

        return new AuditLogEntry
        {
            Id = dto.id,
            SequenceNumber = dto.sequence_number,
            TenantId = dto.tenant_id,
            WorkspaceId = dto.workspace_id,
            BillingCycleId = dto.billing_cycle_id,
            EventType = ParseEventType(dto.event_type),
            Description = dto.description,
            Data = data,
            UserId = dto.user_id,
            IpAddress = dto.ip_address,
            Timestamp = dto.timestamp,
            PreviousHash = dto.previous_hash,
            ContentHash = dto.content_hash
        };
    }

    private static AuditEventType ParseEventType(string eventType) => eventType switch
    {
        "UsageEventRecorded" => AuditEventType.UsageEventRecorded,
        "UsageLimitExceeded" => AuditEventType.UsageLimitExceeded,
        "RateLimitExceeded" => AuditEventType.RateLimitExceeded,
        "PlanSubscribed" => AuditEventType.PlanSubscribed,
        "PlanUpgraded" => AuditEventType.PlanUpgraded,
        "PlanDowngraded" => AuditEventType.PlanDowngraded,
        "PlanCancelled" => AuditEventType.PlanCancelled,
        "BillingCycleCreated" => AuditEventType.BillingCycleCreated,
        "BillingCycleClosed" => AuditEventType.BillingCycleClosed,
        "CreditAdjustment" => AuditEventType.CreditAdjustment,
        "CreditCarryOver" => AuditEventType.CreditCarryOver,
        "CreditExpired" => AuditEventType.CreditExpired,
        "TenantCreated" => AuditEventType.TenantCreated,
        "TenantSuspended" => AuditEventType.TenantSuspended,
        "TenantReactivated" => AuditEventType.TenantReactivated,
        "SettingsChanged" => AuditEventType.SettingsChanged,
        "UsageExported" => AuditEventType.UsageExported,
        "AuthenticationFailed" => AuditEventType.AuthenticationFailed,
        "UnauthorizedAccess" => AuditEventType.UnauthorizedAccess,
        "IntegrityViolation" => AuditEventType.IntegrityViolation,
        _ => AuditEventType.UsageEventRecorded
    };

    private static AuditIntegrityIssueType ParseIssueType(string issueType) => issueType switch
    {
        "hash_mismatch" => AuditIntegrityIssueType.HashMismatch,
        "sequence_gap" => AuditIntegrityIssueType.SequenceGap,
        "chain_broken" => AuditIntegrityIssueType.ChainBroken,
        "missing_entry" => AuditIntegrityIssueType.MissingEntry,
        "duplicate_sequence" => AuditIntegrityIssueType.DuplicateSequence,
        "timestamp_anomaly" => AuditIntegrityIssueType.TimestampAnomaly,
        _ => AuditIntegrityIssueType.HashMismatch
    };

    private sealed class AuditLogDto
    {
        public Guid id { get; set; }
        public long sequence_number { get; set; }
        public string tenant_id { get; set; } = "";
        public string workspace_id { get; set; } = "";
        public Guid? billing_cycle_id { get; set; }
        public string event_type { get; set; } = "";
        public string description { get; set; } = "";
        public string? data { get; set; }
        public string? user_id { get; set; }
        public string? ip_address { get; set; }
        public DateTimeOffset timestamp { get; set; }
        public string previous_hash { get; set; } = "";
        public string content_hash { get; set; } = "";
    }

    private sealed class VerificationResultDto
    {
        public bool is_valid { get; set; }
        public int total_entries { get; set; }
        public int verified_entries { get; set; }
        public long? first_issue_sequence { get; set; }
        public string? first_issue_type { get; set; }
        public string? first_issue_description { get; set; }
    }
}
