using System.Globalization;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using SerialMemory.Core.Interfaces;

namespace SerialMemory.Infrastructure;

/// <summary>
/// Service for exporting usage data to JSON and CSV formats.
/// </summary>
public sealed class UsageExportService : IUsageExportService
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<UsageExportService> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public UsageExportService(string connectionString, ILogger<UsageExportService> logger)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        _dataSource = builder.Build();
        _logger = logger;
    }

    public UsageExportService(NpgsqlDataSource dataSource, ILogger<UsageExportService> logger)
    {
        _dataSource = dataSource;
        _logger = logger;
    }

    public async Task<UsageExportResult> ExportToJsonAsync(
        string tenantId,
        string workspaceId,
        DateTimeOffset? fromDate = null,
        DateTimeOffset? toDate = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

            var events = await QueryEventsAsync(conn, tenantId, workspaceId, fromDate, toDate);

            var exportData = new UsageExportData
            {
                TenantId = tenantId,
                WorkspaceId = workspaceId,
                ExportedAt = DateTimeOffset.UtcNow,
                FromDate = fromDate,
                ToDate = toDate,
                RecordCount = events.Count,
                Events = events.Select(MapToExportEvent).ToList()
            };

            var json = JsonSerializer.SerializeToUtf8Bytes(exportData, JsonOptions);

            _logger.LogInformation(
                "Exported {Count} usage events for tenant {TenantId} to JSON",
                events.Count, tenantId);

            return new UsageExportResult
            {
                Success = true,
                ContentType = "application/json",
                FileName = $"usage_export_{tenantId}_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}.json",
                Data = json,
                RecordCount = events.Count,
                FromDate = fromDate,
                ToDate = toDate
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export usage to JSON for tenant {TenantId}", tenantId);
            return new UsageExportResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                Data = []
            };
        }
    }

    public async Task<UsageExportResult> ExportToCsvAsync(
        string tenantId,
        string workspaceId,
        DateTimeOffset? fromDate = null,
        DateTimeOffset? toDate = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

            var events = await QueryEventsAsync(conn, tenantId, workspaceId, fromDate, toDate);

            var sb = new StringBuilder();

            // Header
            sb.AppendLine("id,tenant_id,workspace_id,event_type,credits_consumed,event_timestamp,memory_id,user_id,session_id,latency_ms,success,error_message");

            // Rows
            foreach (var evt in events)
            {
                sb.AppendLine(string.Join(",",
                    CsvEscape(evt.id.ToString()),
                    CsvEscape(evt.tenant_id),
                    CsvEscape(evt.workspace_id),
                    CsvEscape(evt.event_type),
                    evt.credits_consumed.ToString(CultureInfo.InvariantCulture),
                    CsvEscape(evt.event_timestamp.ToString("O")),
                    CsvEscape(evt.memory_id?.ToString() ?? ""),
                    CsvEscape(evt.user_id ?? ""),
                    CsvEscape(evt.session_id?.ToString() ?? ""),
                    evt.latency_ms?.ToString() ?? "",
                    evt.success.ToString().ToLowerInvariant(),
                    CsvEscape(evt.error_message ?? "")
                ));
            }

            var csvBytes = Encoding.UTF8.GetBytes(sb.ToString());

            _logger.LogInformation(
                "Exported {Count} usage events for tenant {TenantId} to CSV",
                events.Count, tenantId);

            return new UsageExportResult
            {
                Success = true,
                ContentType = "text/csv",
                FileName = $"usage_export_{tenantId}_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}.csv",
                Data = csvBytes,
                RecordCount = events.Count,
                FromDate = fromDate,
                ToDate = toDate
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export usage to CSV for tenant {TenantId}", tenantId);
            return new UsageExportResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                Data = []
            };
        }
    }

    public async Task<UsageExportResult> ExportRollupsToJsonAsync(
        string tenantId,
        string workspaceId,
        DateOnly? fromDate = null,
        DateOnly? toDate = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

            var from = fromDate ?? DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(-1));
            var to = toDate ?? DateOnly.FromDateTime(DateTime.UtcNow);

            var rollups = await conn.QueryAsync<RollupDto>(
                """
                SELECT id, tenant_id, workspace_id, rollup_date, event_type,
                                         event_count, total_credits, success_count, failure_count,
                                         avg_latency_ms, p95_latency_ms, p99_latency_ms,
                                         created_at, updated_at
                                  FROM usage_daily_rollups
                                  WHERE tenant_id = @TenantId AND workspace_id = @WorkspaceId
                                    AND rollup_date >= @From AND rollup_date <= @To
                                  ORDER BY rollup_date DESC, event_type
                """,
                new
                {
                    TenantId = tenantId,
                    WorkspaceId = workspaceId,
                    From = from.ToDateTime(TimeOnly.MinValue),
                    To = to.ToDateTime(TimeOnly.MaxValue)
                });

            var rollupList = rollups.ToList();

            var exportData = new RollupExportData
            {
                TenantId = tenantId,
                WorkspaceId = workspaceId,
                ExportedAt = DateTimeOffset.UtcNow,
                FromDate = from,
                ToDate = to,
                RecordCount = rollupList.Count,
                Rollups = rollupList.Select(r => new RollupExportEntry
                {
                    Id = r.id,
                    RollupDate = DateOnly.FromDateTime(r.rollup_date),
                    EventType = r.event_type,
                    EventCount = r.event_count,
                    TotalCredits = r.total_credits,
                    SuccessCount = r.success_count,
                    FailureCount = r.failure_count,
                    AvgLatencyMs = r.avg_latency_ms,
                    P95LatencyMs = r.p95_latency_ms,
                    P99LatencyMs = r.p99_latency_ms
                }).ToList()
            };

            var json = JsonSerializer.SerializeToUtf8Bytes(exportData, JsonOptions);

            return new UsageExportResult
            {
                Success = true,
                ContentType = "application/json",
                FileName = $"usage_rollups_{tenantId}_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}.json",
                Data = json,
                RecordCount = rollupList.Count,
                FromDate = from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                ToDate = to.ToDateTime(TimeOnly.MaxValue, DateTimeKind.Utc)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export rollups for tenant {TenantId}", tenantId);
            return new UsageExportResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                Data = []
            };
        }
    }

    public async Task<UsageExportResult> ExportBillingHistoryAsync(
        string tenantId,
        string workspaceId,
        int? limitCycles = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

            var cycles = await conn.QueryAsync<BillingCycleExportDto>(
                """
                SELECT bc.id, bc.tenant_id, bc.workspace_id, tp.plan_name,
                                         bc.cycle_start, bc.cycle_end, bc.credits_allocated, bc.credits_used,
                                         bc.is_current, bc.closed_at, bc.created_at
                                  FROM billing_cycles bc
                                  LEFT JOIN tenant_plans tp ON bc.plan_id = tp.id
                                  WHERE bc.tenant_id = @TenantId AND bc.workspace_id = @WorkspaceId
                                  ORDER BY bc.cycle_start DESC
                                  LIMIT @Limit
                """,
                new
                {
                    TenantId = tenantId,
                    WorkspaceId = workspaceId,
                    Limit = limitCycles ?? 12
                });

            var cycleList = cycles.ToList();

            // Get credit adjustments for each cycle
            var cycleIds = cycleList.Select(c => c.id).ToList();
            var adjustments = cycleIds.Count > 0
                ? (await conn.QueryAsync<CreditAdjustmentDto>(
                    """
                    SELECT billing_cycle_id, adjustment_type, amount, reason, created_at
                                          FROM credit_adjustments
                                          WHERE billing_cycle_id = ANY(@CycleIds)
                                          ORDER BY created_at
                    """,
                    new { CycleIds = cycleIds })).ToList()
                : [];

            var exportData = new BillingHistoryExportData
            {
                TenantId = tenantId,
                WorkspaceId = workspaceId,
                ExportedAt = DateTimeOffset.UtcNow,
                CycleCount = cycleList.Count,
                Cycles = cycleList.Select(c => new BillingCycleExportEntry
                {
                    Id = c.id,
                    PlanName = c.plan_name ?? "unknown",
                    CycleStart = c.cycle_start,
                    CycleEnd = c.cycle_end,
                    CreditsAllocated = c.credits_allocated,
                    CreditsUsed = c.credits_used,
                    CreditsRemaining = c.credits_allocated - c.credits_used,
                    IsCurrent = c.is_current,
                    ClosedAt = c.closed_at,
                    Adjustments = adjustments
                        .Where(a => a.billing_cycle_id == c.id)
                        .Select(a => new CreditAdjustmentEntry
                        {
                            Type = a.adjustment_type,
                            Amount = a.amount,
                            Reason = a.reason,
                            CreatedAt = a.created_at
                        }).ToList()
                }).ToList()
            };

            var json = JsonSerializer.SerializeToUtf8Bytes(exportData, JsonOptions);

            return new UsageExportResult
            {
                Success = true,
                ContentType = "application/json",
                FileName = $"billing_history_{tenantId}_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}.json",
                Data = json,
                RecordCount = cycleList.Count
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export billing history for tenant {TenantId}", tenantId);
            return new UsageExportResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                Data = []
            };
        }
    }

    private static async Task<List<EventDto>> QueryEventsAsync(
        NpgsqlConnection conn,
        string tenantId,
        string workspaceId,
        DateTimeOffset? fromDate,
        DateTimeOffset? toDate)
    {
        var from = fromDate ?? DateTimeOffset.UtcNow.AddMonths(-1);
        var to = toDate ?? DateTimeOffset.UtcNow;

        var events = await conn.QueryAsync<EventDto>(
            """
            SELECT id, tenant_id, workspace_id, billing_cycle_id, event_type,
                                 credits_consumed, event_timestamp, memory_id, user_id, session_id,
                                 latency_ms, success, error_message
                          FROM usage_events
                          WHERE tenant_id = @TenantId AND workspace_id = @WorkspaceId
                            AND event_timestamp >= @From AND event_timestamp <= @To
                          ORDER BY event_timestamp DESC
            """,
            new { TenantId = tenantId, WorkspaceId = workspaceId, From = from, To = to });

        return events.ToList();
    }

    private static UsageEventExportEntry MapToExportEvent(EventDto dto) => new()
    {
        Id = dto.id,
        EventType = dto.event_type,
        CreditsConsumed = dto.credits_consumed,
        EventTimestamp = dto.event_timestamp,
        MemoryId = dto.memory_id,
        UserId = dto.user_id,
        SessionId = dto.session_id,
        LatencyMs = dto.latency_ms,
        Success = dto.success,
        ErrorMessage = dto.error_message
    };

    private static string CsvEscape(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }
        return value;
    }

    // DTOs
    private sealed class EventDto
    {
        public Guid id { get; set; }
        public string tenant_id { get; set; } = "";
        public string workspace_id { get; set; } = "";
        public Guid? billing_cycle_id { get; set; }
        public string event_type { get; set; } = "";
        public decimal credits_consumed { get; set; }
        public DateTimeOffset event_timestamp { get; set; }
        public Guid? memory_id { get; set; }
        public string? user_id { get; set; }
        public Guid? session_id { get; set; }
        public int? latency_ms { get; set; }
        public bool success { get; set; }
        public string? error_message { get; set; }
    }

    private sealed class RollupDto
    {
        public Guid id { get; set; }
        public string tenant_id { get; set; } = "";
        public string workspace_id { get; set; } = "";
        public DateTime rollup_date { get; set; }
        public string event_type { get; set; } = "";
        public int event_count { get; set; }
        public decimal total_credits { get; set; }
        public int success_count { get; set; }
        public int failure_count { get; set; }
        public decimal? avg_latency_ms { get; set; }
        public int? p95_latency_ms { get; set; }
        public int? p99_latency_ms { get; set; }
        public DateTimeOffset created_at { get; set; }
        public DateTimeOffset updated_at { get; set; }
    }

    private sealed class BillingCycleExportDto
    {
        public Guid id { get; set; }
        public string tenant_id { get; set; } = "";
        public string workspace_id { get; set; } = "";
        public string? plan_name { get; set; }
        public DateTimeOffset cycle_start { get; set; }
        public DateTimeOffset cycle_end { get; set; }
        public decimal credits_allocated { get; set; }
        public decimal credits_used { get; set; }
        public bool is_current { get; set; }
        public DateTimeOffset? closed_at { get; set; }
        public DateTimeOffset created_at { get; set; }
    }

    private sealed class CreditAdjustmentDto
    {
        public Guid billing_cycle_id { get; set; }
        public string adjustment_type { get; set; } = "";
        public decimal amount { get; set; }
        public string reason { get; set; } = "";
        public DateTimeOffset created_at { get; set; }
    }
}

// Export data structures
public sealed class UsageExportData
{
    public string TenantId { get; init; } = "";
    public string WorkspaceId { get; init; } = "";
    public DateTimeOffset ExportedAt { get; init; }
    public DateTimeOffset? FromDate { get; init; }
    public DateTimeOffset? ToDate { get; init; }
    public int RecordCount { get; init; }
    public List<UsageEventExportEntry> Events { get; init; } = [];
}

public sealed class UsageEventExportEntry
{
    public Guid Id { get; init; }
    public string EventType { get; init; } = "";
    public decimal CreditsConsumed { get; init; }
    public DateTimeOffset EventTimestamp { get; init; }
    public Guid? MemoryId { get; init; }
    public string? UserId { get; init; }
    public Guid? SessionId { get; init; }
    public int? LatencyMs { get; init; }
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed class RollupExportData
{
    public string TenantId { get; init; } = "";
    public string WorkspaceId { get; init; } = "";
    public DateTimeOffset ExportedAt { get; init; }
    public DateOnly FromDate { get; init; }
    public DateOnly ToDate { get; init; }
    public int RecordCount { get; init; }
    public List<RollupExportEntry> Rollups { get; init; } = [];
}

public sealed class RollupExportEntry
{
    public Guid Id { get; init; }
    public DateOnly RollupDate { get; init; }
    public string EventType { get; init; } = "";
    public int EventCount { get; init; }
    public decimal TotalCredits { get; init; }
    public int SuccessCount { get; init; }
    public int FailureCount { get; init; }
    public decimal? AvgLatencyMs { get; init; }
    public int? P95LatencyMs { get; init; }
    public int? P99LatencyMs { get; init; }
}

public sealed class BillingHistoryExportData
{
    public string TenantId { get; init; } = "";
    public string WorkspaceId { get; init; } = "";
    public DateTimeOffset ExportedAt { get; init; }
    public int CycleCount { get; init; }
    public List<BillingCycleExportEntry> Cycles { get; init; } = [];
}

public sealed class BillingCycleExportEntry
{
    public Guid Id { get; init; }
    public string PlanName { get; init; } = "";
    public DateTimeOffset CycleStart { get; init; }
    public DateTimeOffset CycleEnd { get; init; }
    public decimal CreditsAllocated { get; init; }
    public decimal CreditsUsed { get; init; }
    public decimal CreditsRemaining { get; init; }
    public bool IsCurrent { get; init; }
    public DateTimeOffset? ClosedAt { get; init; }
    public List<CreditAdjustmentEntry> Adjustments { get; init; } = [];
}

public sealed class CreditAdjustmentEntry
{
    public string Type { get; init; } = "";
    public decimal Amount { get; init; }
    public string Reason { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
}
