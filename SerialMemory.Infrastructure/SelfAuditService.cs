using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using SerialMemory.Core.Interfaces;

namespace SerialMemory.Infrastructure;

/// <summary>
/// Service for tenant self-audit capabilities.
/// </summary>
public sealed class SelfAuditService : ISelfAuditService
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly IProofService _proofService;
    private readonly ILogger<SelfAuditService> _logger;

    public SelfAuditService(
        string connectionString,
        IProofService proofService,
        ILogger<SelfAuditService> logger)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        _dataSource = builder.Build();
        _proofService = proofService;
        _logger = logger;
    }

    public SelfAuditService(
        NpgsqlDataSource dataSource,
        IProofService proofService,
        ILogger<SelfAuditService> logger)
    {
        _dataSource = dataSource;
        _proofService = proofService;
        _logger = logger;
    }

    public async Task<TenantAuditReport> GenerateAuditReportAsync(
        string tenantId,
        AuditReportOptions options,
        CancellationToken ct = default)
    {
        var generatedAt = DateTimeOffset.UtcNow;

        // Always get activity summary
        var activitySummary = await GetActivitySummaryAsync(tenantId, options.FromDate, options.ToDate, ct);

        // Optionally get other sections
        AccessPatternAnalysis? accessPatterns = null;
        if (options.IncludeAccessPatterns)
        {
            accessPatterns = await AnalyzeAccessPatternsAsync(tenantId, options.FromDate, options.ToDate, ct);
        }

        IReadOnlyList<SecurityEvent>? securityEvents = null;
        if (options.IncludeSecurityEvents)
        {
            securityEvents = await GetSecurityEventsAsync(tenantId, options.FromDate, options.ToDate, ct);
        }

        ComplianceStatus? complianceStatus = null;
        if (options.IncludeComplianceStatus)
        {
            var complianceReport = await _proofService.GenerateComplianceReportAsync(tenantId, ct);
            complianceStatus = new ComplianceStatus
            {
                IsCompliant = complianceReport.OverallScore.Grade != ComplianceGrade.F,
                ComplianceScore = complianceReport.OverallScore.Percentage,
                Checks = complianceReport.Recommendations.Select(r => new ComplianceCheckResult
                {
                    CheckName = r.Title,
                    Category = r.Category,
                    Passed = false,
                    Details = r.Description
                }).ToList(),
                LastAuditDate = generatedAt,
                NextAuditDue = generatedAt.AddDays(30)
            };
        }

        UsageMetrics? usageMetrics = null;
        if (options.IncludeUsageMetrics)
        {
            usageMetrics = await GetUsageMetricsAsync(tenantId, options.FromDate, options.ToDate, ct);
        }

        var reportData = new
        {
            TenantId = tenantId,
            GeneratedAt = generatedAt,
            PeriodStart = options.FromDate,
            PeriodEnd = options.ToDate,
            TotalOperations = activitySummary.TotalOperations
        };

        return new TenantAuditReport
        {
            TenantId = tenantId,
            GeneratedAt = generatedAt,
            PeriodStart = options.FromDate,
            PeriodEnd = options.ToDate,
            ActivitySummary = activitySummary,
            AccessPatterns = accessPatterns,
            SecurityEvents = securityEvents,
            ComplianceStatus = complianceStatus,
            UsageMetrics = usageMetrics,
            ReportHash = ComputeHash(JsonSerializer.Serialize(reportData))
        };
    }

    public async Task<AuditLogExport> ExportAuditLogsAsync(
        string tenantId,
        AuditLogExportOptions options,
        CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var generatedAt = DateTimeOffset.UtcNow;

        var whereClause = "WHERE tenant_id = @TenantId::uuid AND occurred_at >= @FromDate AND occurred_at <= @ToDate";
        var parameters = new DynamicParameters();
        parameters.Add("TenantId", tenantId);
        parameters.Add("FromDate", options.FromDate);
        parameters.Add("ToDate", options.ToDate);

        if (options.EventTypes?.Any() == true)
        {
            whereClause += " AND action = ANY(@EventTypes)";
            parameters.Add("EventTypes", options.EventTypes.ToArray());
        }

        var limitClause = options.MaxRecords.HasValue ? $"LIMIT {options.MaxRecords}" : "";

        var records = await conn.QueryAsync<AuditLogRecordDto>(
            $@"SELECT
                id, tenant_id, actor_type, actor_id, action, resource_type, resource_id,
                details, occurred_at, ip_address, user_agent, entry_hash
               FROM admin_audit_log
               {whereClause}
               ORDER BY occurred_at DESC
               {limitClause}",
            parameters);

        var recordList = records.ToList();
        var content = options.Format switch
        {
            AuditLogExportFormat.Json => SerializeToJson(recordList),
            AuditLogExportFormat.Csv => SerializeToCsv(recordList),
            AuditLogExportFormat.Xml => SerializeToXml(recordList),
            _ => SerializeToJson(recordList)
        };

        var contentBytes = Encoding.UTF8.GetBytes(content);

        return new AuditLogExport
        {
            TenantId = tenantId,
            GeneratedAt = generatedAt,
            PeriodStart = options.FromDate,
            PeriodEnd = options.ToDate,
            Format = options.Format,
            RecordCount = recordList.Count,
            Content = content,
            ContentHash = ComputeHash(content),
            ContentSizeBytes = contentBytes.Length
        };
    }

    public async Task<ActivitySummary> GetActivitySummaryAsync(
        string tenantId,
        DateTimeOffset fromDate,
        DateTimeOffset toDate,
        CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        // Get operation counts
        var operationCounts = await conn.QueryAsync<OperationCountDto>(
            @"SELECT action, COUNT(*) AS count
              FROM admin_audit_log
              WHERE tenant_id = @TenantId::uuid
                AND occurred_at >= @FromDate
                AND occurred_at <= @ToDate
              GROUP BY action
              ORDER BY count DESC",
            new { TenantId = tenantId, FromDate = fromDate, ToDate = toDate });

        var counts = operationCounts.ToDictionary(x => x.action, x => x.count);
        var totalOperations = counts.Values.Sum();

        // Get memory-specific counts
        var memoriesCreated = counts.GetValueOrDefault("memory_created", 0);
        var memoriesUpdated = counts.GetValueOrDefault("memory_updated", 0);
        var memoriesDeleted = counts.GetValueOrDefault("memory_deleted", 0);
        var searchesPerformed = counts.GetValueOrDefault("memory_search", 0);
        var exportsGenerated = counts.GetValueOrDefault("export_generated", 0);

        // Get daily breakdown
        var dailyBreakdown = await conn.QueryAsync<DailyActivityDto>(
            @"SELECT
                DATE(occurred_at) AS date,
                COUNT(*) AS operation_count,
                COUNT(DISTINCT actor_id) AS unique_users,
                0 AS credits_consumed
              FROM admin_audit_log
              WHERE tenant_id = @TenantId::uuid
                AND occurred_at >= @FromDate
                AND occurred_at <= @ToDate
              GROUP BY DATE(occurred_at)
              ORDER BY date",
            new { TenantId = tenantId, FromDate = fromDate, ToDate = toDate });

        // Get top operations
        var topOps = counts
            .OrderByDescending(x => x.Value)
            .Take(10)
            .ToDictionary(x => x.Key, x => x.Value);

        return new ActivitySummary
        {
            TotalOperations = totalOperations,
            OperationCounts = counts,
            MemoriesCreated = memoriesCreated,
            MemoriesUpdated = memoriesUpdated,
            MemoriesDeleted = memoriesDeleted,
            SearchesPerformed = searchesPerformed,
            ExportsGenerated = exportsGenerated,
            DailyBreakdown = dailyBreakdown.Select(d => new DailyActivity
            {
                Date = DateOnly.FromDateTime(d.date),
                OperationCount = d.operation_count,
                UniqueUsers = d.unique_users,
                CreditsConsumed = d.credits_consumed
            }).ToList(),
            TopOperations = topOps
        };
    }

    public async Task<AccessPatternAnalysis> AnalyzeAccessPatternsAsync(
        string tenantId,
        DateTimeOffset fromDate,
        DateTimeOffset toDate,
        CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        // Get hourly patterns
        var hourlyPatterns = await conn.QueryAsync<HourlyPatternDto>(
            @"SELECT
                EXTRACT(HOUR FROM occurred_at)::INT AS hour,
                COUNT(*) AS request_count
              FROM admin_audit_log
              WHERE tenant_id = @TenantId::uuid
                AND occurred_at >= @FromDate
                AND occurred_at <= @ToDate
              GROUP BY EXTRACT(HOUR FROM occurred_at)
              ORDER BY hour",
            new { TenantId = tenantId, FromDate = fromDate, ToDate = toDate });

        var patternList = hourlyPatterns.ToList();
        var totalRequests = patternList.Sum(p => p.request_count);

        var hourlyResults = patternList.Select(p => new HourlyAccessPattern
        {
            Hour = p.hour,
            RequestCount = p.request_count,
            PercentageOfTotal = totalRequests > 0 ? Math.Round((decimal)p.request_count / totalRequests * 100, 2) : 0
        }).ToList();

        // Find peak hours (top 3 by request count)
        var peakHours = hourlyResults
            .OrderByDescending(h => h.RequestCount)
            .Take(3)
            .Select(h => $"{h.Hour:00}:00")
            .ToList();

        // Get access sources
        var sources = await conn.QueryAsync<AccessSourceDto>(
            @"SELECT
                COALESCE(details->>'source', 'unknown') AS source,
                COUNT(*) AS request_count
              FROM admin_audit_log
              WHERE tenant_id = @TenantId::uuid
                AND occurred_at >= @FromDate
                AND occurred_at <= @ToDate
              GROUP BY COALESCE(details->>'source', 'unknown')
              ORDER BY request_count DESC",
            new { TenantId = tenantId, FromDate = fromDate, ToDate = toDate });

        var sourceList = sources.ToList();
        var sourceTotal = sourceList.Sum(s => s.request_count);

        var topSources = sourceList.Take(5).Select(s => new AccessSource
        {
            Source = s.source,
            RequestCount = s.request_count,
            Percentage = sourceTotal > 0 ? Math.Round((decimal)s.request_count / sourceTotal * 100, 2) : 0
        }).ToList();

        // Get endpoint usage
        var endpoints = await conn.QueryAsync<EndpointUsageDto>(
            @"SELECT
                resource_type AS endpoint,
                COUNT(*) AS count
              FROM admin_audit_log
              WHERE tenant_id = @TenantId::uuid
                AND occurred_at >= @FromDate
                AND occurred_at <= @ToDate
              GROUP BY resource_type
              ORDER BY count DESC
              LIMIT 20",
            new { TenantId = tenantId, FromDate = fromDate, ToDate = toDate });

        var endpointUsage = endpoints.ToDictionary(e => e.endpoint ?? "unknown", e => e.count);

        // Calculate average requests per day
        var days = (toDate - fromDate).TotalDays;
        var avgPerDay = days > 0 ? Math.Round((decimal)totalRequests / (decimal)days, 2) : totalRequests;

        // Get max concurrent sessions (approximated by distinct actor_id per minute)
        var maxConcurrent = await conn.QueryFirstOrDefaultAsync<long>(
            @"SELECT MAX(concurrent_count) FROM (
                SELECT COUNT(DISTINCT actor_id) AS concurrent_count
                FROM admin_audit_log
                WHERE tenant_id = @TenantId::uuid
                  AND occurred_at >= @FromDate
                  AND occurred_at <= @ToDate
                GROUP BY DATE_TRUNC('minute', occurred_at)
              ) sub",
            new { TenantId = tenantId, FromDate = fromDate, ToDate = toDate });

        return new AccessPatternAnalysis
        {
            HourlyPatterns = hourlyResults,
            PeakHours = peakHours,
            TopAccessSources = topSources,
            EndpointUsage = endpointUsage,
            AverageRequestsPerDay = avgPerDay,
            MaxConcurrentSessions = maxConcurrent
        };
    }

    public async Task<IReadOnlyList<SecurityEvent>> GetSecurityEventsAsync(
        string tenantId,
        DateTimeOffset fromDate,
        DateTimeOffset toDate,
        CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        // Security-relevant actions
        var securityActions = new[]
        {
            "login_failed", "permission_denied", "rate_limit_exceeded",
            "suspicious_activity", "emergency_cutoff", "api_key_created",
            "api_key_revoked", "settings_changed", "role_changed"
        };

        var events = await conn.QueryAsync<SecurityEventDto>(
            @"SELECT
                id, occurred_at, action, actor_id, ip_address, details
              FROM admin_audit_log
              WHERE tenant_id = @TenantId::uuid
                AND occurred_at >= @FromDate
                AND occurred_at <= @ToDate
                AND action = ANY(@Actions)
              ORDER BY occurred_at DESC
              LIMIT 1000",
            new { TenantId = tenantId, FromDate = fromDate, ToDate = toDate, Actions = securityActions });

        return events.Select(e => new SecurityEvent
        {
            EventId = e.id,
            OccurredAt = e.occurred_at,
            EventType = e.action,
            Severity = DetermineSeverity(e.action),
            Description = GetEventDescription(e.action, e.details),
            SourceIp = e.ip_address,
            UserId = e.actor_id,
            Details = e.details
        }).ToList();
    }

    public async Task<DataLineage> GetDataLineageAsync(
        string tenantId,
        Guid memoryId,
        CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        // Get memory details
        var memory = await conn.QueryFirstOrDefaultAsync<MemoryDto>(
            @"SELECT id, created_at, content_hash, causal_parents
              FROM memories
              WHERE tenant_id = @TenantId::uuid AND id = @MemoryId",
            new { TenantId = tenantId, MemoryId = memoryId });

        if (memory == null)
        {
            throw new InvalidOperationException($"Memory {memoryId} not found for tenant {tenantId}");
        }

        // Get all events related to this memory
        var events = await conn.QueryAsync<LineageEventDto>(
            @"SELECT occurred_at, action, actor_id, details
              FROM admin_audit_log
              WHERE tenant_id = @TenantId::uuid
                AND resource_type = 'memory'
                AND resource_id = @MemoryId::text
              ORDER BY occurred_at",
            new { TenantId = tenantId, MemoryId = memoryId });

        var lineageEvents = events.Select(e => new LineageEvent
        {
            OccurredAt = e.occurred_at,
            EventType = e.action,
            Actor = e.actor_id ?? "system",
            Description = e.details
        }).ToList();

        // Get child memories (that reference this as causal parent)
        var children = await conn.QueryAsync<Guid>(
            @"SELECT id FROM memories
              WHERE tenant_id = @TenantId::uuid
                AND @MemoryId::uuid = ANY(causal_parents)",
            new { TenantId = tenantId, MemoryId = memoryId });

        return new DataLineage
        {
            RecordId = memoryId,
            TableName = "memories",
            CreatedAt = memory.created_at,
            Events = lineageEvents,
            ParentRecords = memory.causal_parents?.ToList(),
            ChildRecords = children.ToList(),
            CurrentHash = memory.content_hash ?? ""
        };
    }

    private async Task<UsageMetrics> GetUsageMetricsAsync(
        string tenantId,
        DateTimeOffset fromDate,
        DateTimeOffset toDate,
        CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        // Get credit usage
        var creditUsage = await conn.QueryFirstOrDefaultAsync<decimal>(
            @"SELECT COALESCE(SUM(credits_consumed), 0)
              FROM usage_events
              WHERE tenant_id = @TenantId::uuid
                AND event_timestamp >= @FromDate
                AND event_timestamp <= @ToDate",
            new { TenantId = tenantId, FromDate = fromDate, ToDate = toDate });

        // Get remaining credits
        var creditsRemaining = await conn.QueryFirstOrDefaultAsync<decimal>(
            @"SELECT COALESCE(credits_allocated - credits_used, 0)
              FROM billing_cycles
              WHERE tenant_id = @TenantId::uuid AND is_current = TRUE",
            new { TenantId = tenantId });

        // Get counts
        var counts = await conn.QueryFirstOrDefaultAsync<CountsDto>(
            @"SELECT
                (SELECT COUNT(*) FROM memories WHERE tenant_id = @TenantId::uuid) AS memory_count,
                (SELECT COUNT(*) FROM entities WHERE tenant_id = @TenantId::uuid) AS entity_count,
                (SELECT pg_total_relation_size('memories') / COUNT(DISTINCT tenant_id)) AS storage_bytes
              FROM memories
              WHERE tenant_id = @TenantId::uuid",
            new { TenantId = tenantId });

        // Get API calls
        var apiCalls = await conn.QueryFirstOrDefaultAsync<long>(
            @"SELECT COUNT(*)
              FROM admin_audit_log
              WHERE tenant_id = @TenantId::uuid
                AND occurred_at >= @FromDate
                AND occurred_at <= @ToDate",
            new { TenantId = tenantId, FromDate = fromDate, ToDate = toDate });

        // Get costs by operation
        var costsByOp = await conn.QueryAsync<CostByOpDto>(
            @"SELECT operation, SUM(cost) AS total_cost
              FROM cost_records
              WHERE tenant_id = @TenantId::uuid
                AND recorded_at >= @FromDate
                AND recorded_at <= @ToDate
              GROUP BY operation
              ORDER BY total_cost DESC",
            new { TenantId = tenantId, FromDate = fromDate, ToDate = toDate });

        return new UsageMetrics
        {
            TotalCreditsUsed = creditUsage,
            CreditsRemaining = creditsRemaining,
            StorageUsedBytes = counts?.storage_bytes ?? 0,
            MemoryCount = counts?.memory_count ?? 0,
            EntityCount = counts?.entity_count ?? 0,
            ApiCalls = apiCalls,
            CostsByOperation = costsByOp.ToDictionary(c => c.operation, c => c.total_cost)
        };
    }

    private static string SerializeToJson(List<AuditLogRecordDto> records)
    {
        return JsonSerializer.Serialize(records, new JsonSerializerOptions
        {
            WriteIndented = true
        });
    }

    private static string SerializeToCsv(List<AuditLogRecordDto> records)
    {
        var sb = new StringBuilder();
        sb.AppendLine("id,tenant_id,actor_type,actor_id,action,resource_type,resource_id,occurred_at,ip_address,entry_hash");

        foreach (var r in records)
        {
            sb.AppendLine($"\"{r.id}\",\"{r.tenant_id}\",\"{r.actor_type}\",\"{r.actor_id}\",\"{r.action}\",\"{r.resource_type}\",\"{r.resource_id}\",\"{r.occurred_at:O}\",\"{r.ip_address}\",\"{r.entry_hash}\"");
        }

        return sb.ToString();
    }

    private static string SerializeToXml(List<AuditLogRecordDto> records)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine("<AuditLogs>");

        foreach (var r in records)
        {
            sb.AppendLine("  <AuditLog>");
            sb.AppendLine($"    <Id>{r.id}</Id>");
            sb.AppendLine($"    <TenantId>{r.tenant_id}</TenantId>");
            sb.AppendLine($"    <ActorType>{r.actor_type}</ActorType>");
            sb.AppendLine($"    <ActorId>{r.actor_id}</ActorId>");
            sb.AppendLine($"    <Action>{r.action}</Action>");
            sb.AppendLine($"    <ResourceType>{r.resource_type}</ResourceType>");
            sb.AppendLine($"    <ResourceId>{r.resource_id}</ResourceId>");
            sb.AppendLine($"    <OccurredAt>{r.occurred_at:O}</OccurredAt>");
            sb.AppendLine($"    <IpAddress>{r.ip_address}</IpAddress>");
            sb.AppendLine($"    <EntryHash>{r.entry_hash}</EntryHash>");
            sb.AppendLine("  </AuditLog>");
        }

        sb.AppendLine("</AuditLogs>");
        return sb.ToString();
    }

    private static SecurityEventSeverity DetermineSeverity(string action)
    {
        return action switch
        {
            "login_failed" => SecurityEventSeverity.Warning,
            "permission_denied" => SecurityEventSeverity.Warning,
            "rate_limit_exceeded" => SecurityEventSeverity.Warning,
            "suspicious_activity" => SecurityEventSeverity.High,
            "emergency_cutoff" => SecurityEventSeverity.Critical,
            "api_key_revoked" => SecurityEventSeverity.High,
            _ => SecurityEventSeverity.Info
        };
    }

    private static string GetEventDescription(string action, string? details)
    {
        return action switch
        {
            "login_failed" => $"Failed login attempt. {details}",
            "permission_denied" => $"Access denied to resource. {details}",
            "rate_limit_exceeded" => $"Rate limit exceeded. {details}",
            "suspicious_activity" => $"Suspicious activity detected. {details}",
            "emergency_cutoff" => $"Emergency cutoff triggered. {details}",
            "api_key_created" => $"New API key created. {details}",
            "api_key_revoked" => $"API key revoked. {details}",
            "settings_changed" => $"Settings modified. {details}",
            "role_changed" => $"User role changed. {details}",
            _ => $"{action}: {details}"
        };
    }

    private static string ComputeHash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    // DTOs
    private sealed class AuditLogRecordDto
    {
        public Guid id { get; set; }
        public Guid tenant_id { get; set; }
        public string actor_type { get; set; } = "";
        public string? actor_id { get; set; }
        public string action { get; set; } = "";
        public string? resource_type { get; set; }
        public string? resource_id { get; set; }
        public string? details { get; set; }
        public DateTimeOffset occurred_at { get; set; }
        public string? ip_address { get; set; }
        public string? user_agent { get; set; }
        public string entry_hash { get; set; } = "";
    }

    private sealed class OperationCountDto
    {
        public string action { get; set; } = "";
        public long count { get; set; }
    }

    private sealed class DailyActivityDto
    {
        public DateTime date { get; set; }
        public long operation_count { get; set; }
        public long unique_users { get; set; }
        public decimal credits_consumed { get; set; }
    }

    private sealed class HourlyPatternDto
    {
        public int hour { get; set; }
        public long request_count { get; set; }
    }

    private sealed class AccessSourceDto
    {
        public string source { get; set; } = "";
        public long request_count { get; set; }
    }

    private sealed class EndpointUsageDto
    {
        public string? endpoint { get; set; }
        public long count { get; set; }
    }

    private sealed class SecurityEventDto
    {
        public Guid id { get; set; }
        public DateTimeOffset occurred_at { get; set; }
        public string action { get; set; } = "";
        public string? actor_id { get; set; }
        public string? ip_address { get; set; }
        public string? details { get; set; }
    }

    private sealed class MemoryDto
    {
        public Guid id { get; set; }
        public DateTimeOffset created_at { get; set; }
        public string? content_hash { get; set; }
        public Guid[]? causal_parents { get; set; }
    }

    private sealed class LineageEventDto
    {
        public DateTimeOffset occurred_at { get; set; }
        public string action { get; set; } = "";
        public string? actor_id { get; set; }
        public string? details { get; set; }
    }

    private sealed class CountsDto
    {
        public long memory_count { get; set; }
        public long entity_count { get; set; }
        public long storage_bytes { get; set; }
    }

    private sealed class CostByOpDto
    {
        public string operation { get; set; } = "";
        public decimal total_cost { get; set; }
    }
}
