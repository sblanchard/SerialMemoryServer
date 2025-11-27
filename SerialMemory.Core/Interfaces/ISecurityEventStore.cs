using SerialMemory.Core.Models;

namespace SerialMemory.Core.Interfaces;

/// <summary>
/// Repository for integrity/security events and metrics
/// </summary>
public interface ISecurityEventStore
{
    // Event logging
    Task<Guid> LogEventAsync(IntegrityEvent integrityEvent, CancellationToken ct = default);
    Task LogEventsAsync(IEnumerable<IntegrityEvent> events, CancellationToken ct = default);

    // Event queries
    Task<List<IntegrityEvent>> GetRecentEventsAsync(int limit = 100, CancellationToken ct = default);
    Task<List<IntegrityEvent>> GetEventsByTypeAsync(IntegrityEventType eventType, int limit = 100, CancellationToken ct = default);
    Task<List<IntegrityEvent>> GetEventsForMemoryAsync(Guid memoryId, CancellationToken ct = default);
    Task<List<IntegrityEvent>> GetEventsInTimeRangeAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);

    // Scan tracking
    Task<Guid> StartScanAsync(SecurityScan scan, CancellationToken ct = default);
    Task CompleteScanAsync(Guid scanId, int eventsGenerated, int issuesFound, int criticalIssues, CancellationToken ct = default);
    Task FailScanAsync(Guid scanId, string errorMessage, CancellationToken ct = default);
    Task<SecurityScan?> GetScanAsync(Guid scanId, CancellationToken ct = default);
    Task<List<SecurityScan>> GetRecentScansAsync(int limit = 20, CancellationToken ct = default);

    // Metrics
    Task<SecurityMetrics> GetMetricsAsync(CancellationToken ct = default);
    Task<SecurityMetrics> GetMetricsForTimeRangeAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);

    // Active issues
    Task<List<IntegrityEvent>> GetActiveIssuesAsync(int limit = 50, CancellationToken ct = default);
    Task<int> GetUnresolvedIssueCountAsync(CancellationToken ct = default);
}
