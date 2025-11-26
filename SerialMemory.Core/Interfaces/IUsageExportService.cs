using SerialMemory.Core.Models;

namespace SerialMemory.Core.Interfaces;

/// <summary>
/// Service for exporting usage data.
/// </summary>
public interface IUsageExportService
{
    /// <summary>
    /// Exports usage events to JSON format.
    /// </summary>
    Task<UsageExportResult> ExportToJsonAsync(
        string tenantId,
        string workspaceId,
        DateTimeOffset? fromDate = null,
        DateTimeOffset? toDate = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Exports usage events to CSV format.
    /// </summary>
    Task<UsageExportResult> ExportToCsvAsync(
        string tenantId,
        string workspaceId,
        DateTimeOffset? fromDate = null,
        DateTimeOffset? toDate = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Exports daily rollups to JSON format.
    /// </summary>
    Task<UsageExportResult> ExportRollupsToJsonAsync(
        string tenantId,
        string workspaceId,
        DateOnly? fromDate = null,
        DateOnly? toDate = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Exports billing cycle summary to JSON format.
    /// </summary>
    Task<UsageExportResult> ExportBillingHistoryAsync(
        string tenantId,
        string workspaceId,
        int? limitCycles = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of a usage export operation.
/// </summary>
public sealed class UsageExportResult
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public string ContentType { get; init; } = "application/json";
    public string FileName { get; init; } = "";
    public byte[] Data { get; init; } = [];
    public int RecordCount { get; init; }
    public DateTimeOffset? FromDate { get; init; }
    public DateTimeOffset? ToDate { get; init; }
    public DateTimeOffset ExportedAt { get; init; } = DateTimeOffset.UtcNow;
}
