namespace SerialMemory.Core.Interfaces;

/// <summary>
/// Service for tenant self-audit capabilities.
/// Allows tenants to generate reports and export their audit data.
/// </summary>
public interface ISelfAuditService
{
    /// <summary>
    /// Generates a comprehensive audit report for a tenant.
    /// </summary>
    Task<TenantAuditReport> GenerateAuditReportAsync(
        string tenantId,
        AuditReportOptions options,
        CancellationToken ct = default);

    /// <summary>
    /// Exports audit logs for a tenant in the specified format.
    /// </summary>
    Task<AuditLogExport> ExportAuditLogsAsync(
        string tenantId,
        AuditLogExportOptions options,
        CancellationToken ct = default);

    /// <summary>
    /// Gets activity summary for a tenant.
    /// </summary>
    Task<ActivitySummary> GetActivitySummaryAsync(
        string tenantId,
        DateTimeOffset fromDate,
        DateTimeOffset toDate,
        CancellationToken ct = default);

    /// <summary>
    /// Gets access patterns analysis for a tenant.
    /// </summary>
    Task<AccessPatternAnalysis> AnalyzeAccessPatternsAsync(
        string tenantId,
        DateTimeOffset fromDate,
        DateTimeOffset toDate,
        CancellationToken ct = default);

    /// <summary>
    /// Gets security events for a tenant.
    /// </summary>
    Task<IReadOnlyList<SecurityEvent>> GetSecurityEventsAsync(
        string tenantId,
        DateTimeOffset fromDate,
        DateTimeOffset toDate,
        CancellationToken ct = default);

    /// <summary>
    /// Gets data lineage for a specific memory.
    /// </summary>
    Task<DataLineage> GetDataLineageAsync(
        string tenantId,
        Guid memoryId,
        CancellationToken ct = default);
}

/// <summary>
/// Options for generating audit reports.
/// </summary>
public sealed record AuditReportOptions
{
    public required DateTimeOffset FromDate { get; init; }
    public required DateTimeOffset ToDate { get; init; }
    public bool IncludeActivityDetails { get; init; } = true;
    public bool IncludeAccessPatterns { get; init; } = true;
    public bool IncludeSecurityEvents { get; init; } = true;
    public bool IncludeComplianceStatus { get; init; } = true;
    public bool IncludeUsageMetrics { get; init; } = true;
}

/// <summary>
/// Comprehensive tenant audit report.
/// </summary>
public sealed record TenantAuditReport
{
    public required string TenantId { get; init; }
    public required DateTimeOffset GeneratedAt { get; init; }
    public required DateTimeOffset PeriodStart { get; init; }
    public required DateTimeOffset PeriodEnd { get; init; }
    public required ActivitySummary ActivitySummary { get; init; }
    public AccessPatternAnalysis? AccessPatterns { get; init; }
    public IReadOnlyList<SecurityEvent>? SecurityEvents { get; init; }
    public ComplianceStatus? ComplianceStatus { get; init; }
    public UsageMetrics? UsageMetrics { get; init; }
    public required string ReportHash { get; init; }
}

/// <summary>
/// Activity summary for a tenant.
/// </summary>
public sealed record ActivitySummary
{
    public required long TotalOperations { get; init; }
    public required IReadOnlyDictionary<string, long> OperationCounts { get; init; }
    public required long MemoriesCreated { get; init; }
    public required long MemoriesUpdated { get; init; }
    public required long MemoriesDeleted { get; init; }
    public required long SearchesPerformed { get; init; }
    public required long ExportsGenerated { get; init; }
    public required IReadOnlyList<DailyActivity> DailyBreakdown { get; init; }
    public required IReadOnlyDictionary<string, long> TopOperations { get; init; }
}

/// <summary>
/// Daily activity breakdown.
/// </summary>
public sealed record DailyActivity
{
    public required DateOnly Date { get; init; }
    public required long OperationCount { get; init; }
    public required long UniqueUsers { get; init; }
    public required decimal CreditsConsumed { get; init; }
}

/// <summary>
/// Access pattern analysis.
/// </summary>
public sealed record AccessPatternAnalysis
{
    public required IReadOnlyList<HourlyAccessPattern> HourlyPatterns { get; init; }
    public required IReadOnlyList<string> PeakHours { get; init; }
    public required IReadOnlyList<AccessSource> TopAccessSources { get; init; }
    public required IReadOnlyDictionary<string, long> EndpointUsage { get; init; }
    public required decimal AverageRequestsPerDay { get; init; }
    public required long MaxConcurrentSessions { get; init; }
}

/// <summary>
/// Hourly access pattern.
/// </summary>
public sealed record HourlyAccessPattern
{
    public required int Hour { get; init; }
    public required long RequestCount { get; init; }
    public required decimal PercentageOfTotal { get; init; }
}

/// <summary>
/// Access source information.
/// </summary>
public sealed record AccessSource
{
    public required string Source { get; init; }  // 'api', 'mcp', 'web', etc.
    public required long RequestCount { get; init; }
    public required decimal Percentage { get; init; }
}

/// <summary>
/// Security event.
/// </summary>
public sealed record SecurityEvent
{
    public required Guid EventId { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
    public required string EventType { get; init; }
    public required SecurityEventSeverity Severity { get; init; }
    public required string Description { get; init; }
    public string? SourceIp { get; init; }
    public string? UserId { get; init; }
    public string? Details { get; init; }
}

/// <summary>
/// Security event severity levels.
/// </summary>
public enum SecurityEventSeverity
{
    Info,
    Warning,
    High,
    Critical
}

/// <summary>
/// Compliance status summary.
/// </summary>
public sealed record ComplianceStatus
{
    public required bool IsCompliant { get; init; }
    public required decimal ComplianceScore { get; init; }
    public required IReadOnlyList<ComplianceCheckResult> Checks { get; init; }
    public DateTimeOffset? LastAuditDate { get; init; }
    public DateTimeOffset? NextAuditDue { get; init; }
}

/// <summary>
/// Individual compliance check result.
/// </summary>
public sealed record ComplianceCheckResult
{
    public required string CheckName { get; init; }
    public required string Category { get; init; }
    public required bool Passed { get; init; }
    public string? Details { get; init; }
}

/// <summary>
/// Usage metrics for billing and capacity planning.
/// </summary>
public sealed record UsageMetrics
{
    public required decimal TotalCreditsUsed { get; init; }
    public required decimal CreditsRemaining { get; init; }
    public required long StorageUsedBytes { get; init; }
    public required long MemoryCount { get; init; }
    public required long EntityCount { get; init; }
    public required long ApiCalls { get; init; }
    public required IReadOnlyDictionary<string, decimal> CostsByOperation { get; init; }
}

/// <summary>
/// Options for exporting audit logs.
/// </summary>
public sealed record AuditLogExportOptions
{
    public required DateTimeOffset FromDate { get; init; }
    public required DateTimeOffset ToDate { get; init; }
    public AuditLogExportFormat Format { get; init; } = AuditLogExportFormat.Json;
    public bool IncludeDetails { get; init; } = true;
    public int? MaxRecords { get; init; }
    public IReadOnlyList<string>? EventTypes { get; init; }
}

/// <summary>
/// Audit log export format.
/// </summary>
public enum AuditLogExportFormat
{
    Json,
    Csv,
    Xml
}

/// <summary>
/// Result of audit log export.
/// </summary>
public sealed record AuditLogExport
{
    public required string TenantId { get; init; }
    public required DateTimeOffset GeneratedAt { get; init; }
    public required DateTimeOffset PeriodStart { get; init; }
    public required DateTimeOffset PeriodEnd { get; init; }
    public required AuditLogExportFormat Format { get; init; }
    public required long RecordCount { get; init; }
    public required string Content { get; init; }
    public required string ContentHash { get; init; }
    public required long ContentSizeBytes { get; init; }
}

/// <summary>
/// Data lineage for a specific record.
/// </summary>
public sealed record DataLineage
{
    public required Guid RecordId { get; init; }
    public required string TableName { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required IReadOnlyList<LineageEvent> Events { get; init; }
    public IReadOnlyList<Guid>? ParentRecords { get; init; }
    public IReadOnlyList<Guid>? ChildRecords { get; init; }
    public required string CurrentHash { get; init; }
}

/// <summary>
/// An event in the data lineage.
/// </summary>
public sealed record LineageEvent
{
    public required DateTimeOffset OccurredAt { get; init; }
    public required string EventType { get; init; }
    public required string Actor { get; init; }
    public string? Description { get; init; }
    public string? PreviousHash { get; init; }
    public string? NewHash { get; init; }
}
