namespace SerialMemory.Core.Interfaces;

/// <summary>
/// Service for supervising background jobs with heartbeats, stuck detection,
/// auto-retry, and dead-letter queue support.
/// </summary>
public interface IJobSupervisionService
{
    /// <summary>
    /// Registers a new job and returns its ID.
    /// </summary>
    Task<Guid> RegisterJobAsync(
        JobRegistration registration,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a heartbeat for a running job.
    /// </summary>
    Task HeartbeatAsync(
        Guid jobId,
        string? progressMessage = null,
        int? progressPercent = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks a job as completed successfully.
    /// </summary>
    Task CompleteJobAsync(
        Guid jobId,
        string? resultSummary = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks a job as failed with error details.
    /// </summary>
    Task FailJobAsync(
        Guid jobId,
        string errorMessage,
        bool shouldRetry = true,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets jobs that are stuck (no heartbeat within timeout).
    /// </summary>
    Task<IReadOnlyList<StuckJob>> GetStuckJobsAsync(
        TimeSpan heartbeatTimeout,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves a failed job to the dead-letter queue after max retries.
    /// </summary>
    Task MoveToDeadLetterAsync(
        Guid jobId,
        string reason,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets jobs from the dead-letter queue for manual review.
    /// </summary>
    Task<IReadOnlyList<DeadLetterJob>> GetDeadLetterJobsAsync(
        Guid? tenantId = null,
        int limit = 100,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retries a job from the dead-letter queue.
    /// </summary>
    Task<bool> RetryDeadLetterJobAsync(
        Guid jobId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets job status by ID.
    /// </summary>
    Task<JobStatus?> GetJobStatusAsync(
        Guid jobId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Job registration details.
/// </summary>
public sealed record JobRegistration
{
    public required Guid TenantId { get; init; }
    public required string JobType { get; init; }
    public required string Payload { get; init; }
    public string? RequestedBy { get; init; }
    public int MaxRetries { get; init; } = 3;
    public TimeSpan HeartbeatTimeout { get; init; } = TimeSpan.FromMinutes(5);
    public JobPriority Priority { get; init; } = JobPriority.Normal;
    public Dictionary<string, object>? Metadata { get; init; }
}

/// <summary>
/// Job priority levels.
/// </summary>
public enum JobPriority
{
    Low = 0,
    Normal = 1,
    High = 2,
    Critical = 3
}

/// <summary>
/// Job execution status.
/// </summary>
public enum JobExecutionStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Retrying,
    DeadLetter,
    Cancelled
}

/// <summary>
/// Current job status.
/// </summary>
public sealed record JobStatus
{
    public required Guid JobId { get; init; }
    public required Guid TenantId { get; init; }
    public required string JobType { get; init; }
    public required JobExecutionStatus Status { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public DateTimeOffset? LastHeartbeat { get; init; }
    public string? ProgressMessage { get; init; }
    public int? ProgressPercent { get; init; }
    public int RetryCount { get; init; }
    public int MaxRetries { get; init; }
    public string? ErrorMessage { get; init; }
    public string? ResultSummary { get; init; }
}

/// <summary>
/// A job that is considered stuck (no heartbeat).
/// </summary>
public sealed record StuckJob
{
    public required Guid JobId { get; init; }
    public required Guid TenantId { get; init; }
    public required string JobType { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset LastHeartbeat { get; init; }
    public required TimeSpan TimeSinceHeartbeat { get; init; }
    public int RetryCount { get; init; }
    public int MaxRetries { get; init; }
}

/// <summary>
/// A job in the dead-letter queue.
/// </summary>
public sealed record DeadLetterJob
{
    public required Guid JobId { get; init; }
    public required Guid TenantId { get; init; }
    public required string JobType { get; init; }
    public required string Payload { get; init; }
    public required string FailureReason { get; init; }
    public required DateTimeOffset FailedAt { get; init; }
    public required int TotalAttempts { get; init; }
    public IReadOnlyList<string>? ErrorHistory { get; init; }
}
