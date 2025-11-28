namespace SerialMemory.Core.Interfaces;

/// <summary>
/// Service for re-crawling memories to extract entities/relationships.
/// Supports batched processing, retry logic, and resumable jobs.
/// Triggered by self-healing engine or explicit API calls.
/// </summary>
public interface IGraphRecrawlService
{
    /// <summary>
    /// Creates a new recrawl job for a tenant.
    /// </summary>
    Task<Guid> CreateRecrawlJobAsync(
        Guid tenantId,
        int batchSize = 100,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Processes a recrawl job in batches.
    /// </summary>
    Task<RecrawlJobResult> ProcessRecrawlJobAsync(
        Guid jobId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets statistics about recrawl progress for a tenant.
    /// </summary>
    Task<RecrawlStatistics> GetStatisticsAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retries failed memories from the dead letter table.
    /// </summary>
    Task<int> RetryFailedMemoriesAsync(
        Guid tenantId,
        int maxRetries = 1,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of a recrawl job execution.
/// </summary>
public record RecrawlJobResult(
    Guid JobId,
    string Status,
    int TotalProcessed,
    int SuccessCount,
    int FailureCount);

/// <summary>
/// Statistics about recrawl progress.
/// </summary>
public record RecrawlStatistics(
    long TotalMemories,
    long Version1Count,
    long Version2Count,
    long NeedsRecrawl,
    int ActiveJobs,
    long FailedExtractions);
