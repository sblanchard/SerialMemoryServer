using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using SerialMemory.Core.Interfaces;

namespace SerialMemory.Infrastructure;

/// <summary>
/// PostgreSQL-backed job supervision service with heartbeats, stuck detection,
/// auto-retry, and dead-letter queue support.
/// </summary>
public sealed class JobSupervisionService : IJobSupervisionService
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<JobSupervisionService> _logger;

    public JobSupervisionService(string connectionString, ILogger<JobSupervisionService> logger)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        _dataSource = builder.Build();
        _logger = logger;
    }

    public JobSupervisionService(NpgsqlDataSource dataSource, ILogger<JobSupervisionService> logger)
    {
        _dataSource = dataSource;
        _logger = logger;
    }

    public async Task<Guid> RegisterJobAsync(
        JobRegistration registration,
        CancellationToken cancellationToken = default)
    {
        var jobId = Guid.CreateVersion7();
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        await conn.ExecuteAsync(
            """
            INSERT INTO supervised_jobs
                          (job_id, tenant_id, job_type, payload, requested_by, max_retries,
                           heartbeat_timeout_seconds, priority, metadata, status, created_at)
                          VALUES
                          (@JobId, @TenantId, @JobType, @Payload, @RequestedBy, @MaxRetries,
                           @HeartbeatTimeoutSeconds, @Priority, @Metadata::jsonb, 'pending', NOW())
            """,
            new
            {
                JobId = jobId,
                registration.TenantId,
                registration.JobType,
                registration.Payload,
                registration.RequestedBy,
                registration.MaxRetries,
                HeartbeatTimeoutSeconds = (int)registration.HeartbeatTimeout.TotalSeconds,
                Priority = (int)registration.Priority,
                Metadata = registration.Metadata != null
                    ? JsonSerializer.Serialize(registration.Metadata)
                    : null
            });

        _logger.LogInformation(
            "Registered job {JobId} of type {JobType} for tenant {TenantId}",
            jobId, registration.JobType, registration.TenantId);

        return jobId;
    }

    public async Task HeartbeatAsync(
        Guid jobId,
        string? progressMessage = null,
        int? progressPercent = null,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        var affected = await conn.ExecuteAsync(
            """
            UPDATE supervised_jobs
                          SET last_heartbeat = NOW(),
                              progress_message = COALESCE(@ProgressMessage, progress_message),
                              progress_percent = COALESCE(@ProgressPercent, progress_percent),
                              status = CASE WHEN status = 'pending' THEN 'running' ELSE status END,
                              started_at = CASE WHEN started_at IS NULL THEN NOW() ELSE started_at END
                          WHERE job_id = @JobId AND status IN ('pending', 'running', 'retrying')
            """,
            new { JobId = jobId, ProgressMessage = progressMessage, ProgressPercent = progressPercent });

        if (affected == 0)
        {
            _logger.LogWarning("Heartbeat for job {JobId} had no effect (job not found or not running)", jobId);
        }
    }

    public async Task CompleteJobAsync(
        Guid jobId,
        string? resultSummary = null,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        await conn.ExecuteAsync(
            """
            UPDATE supervised_jobs
                          SET status = 'completed',
                              completed_at = NOW(),
                              result_summary = @ResultSummary,
                              progress_percent = 100
                          WHERE job_id = @JobId
            """,
            new { JobId = jobId, ResultSummary = resultSummary });

        _logger.LogInformation("Job {JobId} completed successfully", jobId);
    }

    public async Task FailJobAsync(
        Guid jobId,
        string errorMessage,
        bool shouldRetry = true,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        // Get current retry state
        var job = await conn.QueryFirstOrDefaultAsync<JobRetryInfo>(
            "SELECT retry_count, max_retries, error_history FROM supervised_jobs WHERE job_id = @JobId",
            new { JobId = jobId });

        if (job == null)
        {
            _logger.LogWarning("Failed to record failure for job {JobId}: not found", jobId);
            return;
        }

        // Append error to history
        var errorHistory = job.error_history ?? [];
        errorHistory = errorHistory.Append(
            $"[{DateTimeOffset.UtcNow:O}] {errorMessage}"
        ).ToArray();

        var newRetryCount = job.retry_count + 1;
        var canRetry = shouldRetry && newRetryCount < job.max_retries;

        if (canRetry)
        {
            // Schedule retry
            await conn.ExecuteAsync(
                """
                UPDATE supervised_jobs
                                  SET status = 'retrying',
                                      retry_count = @RetryCount,
                                      error_message = @ErrorMessage,
                                      error_history = @ErrorHistory,
                                      last_heartbeat = NULL,
                                      scheduled_retry_at = NOW() + INTERVAL '1 minute' * POWER(2, @RetryCount)
                                  WHERE job_id = @JobId
                """,
                new
                {
                    JobId = jobId,
                    RetryCount = newRetryCount,
                    ErrorMessage = errorMessage,
                    ErrorHistory = errorHistory
                });

            _logger.LogWarning(
                "Job {JobId} failed (attempt {Attempt}/{Max}), scheduled for retry: {Error}",
                jobId, newRetryCount, job.max_retries, errorMessage);
        }
        else
        {
            // Move to dead letter
            await conn.ExecuteAsync(
                """
                UPDATE supervised_jobs
                                  SET status = 'failed',
                                      retry_count = @RetryCount,
                                      error_message = @ErrorMessage,
                                      error_history = @ErrorHistory,
                                      completed_at = NOW()
                                  WHERE job_id = @JobId
                """,
                new
                {
                    JobId = jobId,
                    RetryCount = newRetryCount,
                    ErrorMessage = errorMessage,
                    ErrorHistory = errorHistory
                });

            _logger.LogError(
                "Job {JobId} failed permanently after {Attempts} attempts: {Error}",
                jobId, newRetryCount, errorMessage);
        }
    }

    public async Task<IReadOnlyList<StuckJob>> GetStuckJobsAsync(
        TimeSpan heartbeatTimeout,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        var jobs = await conn.QueryAsync<StuckJobDto>(
            """
            SELECT job_id, tenant_id, job_type, started_at, last_heartbeat,
                                 retry_count, max_retries,
                                 EXTRACT(EPOCH FROM (NOW() - last_heartbeat)) AS seconds_since_heartbeat
                          FROM supervised_jobs
                          WHERE status = 'running'
                            AND last_heartbeat < NOW() - @Timeout::interval
            """,
            new { Timeout = $"{(int)heartbeatTimeout.TotalSeconds} seconds" });

        return jobs.Select(j => new StuckJob
        {
            JobId = j.job_id,
            TenantId = j.tenant_id,
            JobType = j.job_type,
            StartedAt = j.started_at,
            LastHeartbeat = j.last_heartbeat,
            TimeSinceHeartbeat = TimeSpan.FromSeconds(j.seconds_since_heartbeat),
            RetryCount = j.retry_count,
            MaxRetries = j.max_retries
        }).ToList();
    }

    public async Task MoveToDeadLetterAsync(
        Guid jobId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        await conn.ExecuteAsync(
            """
            UPDATE supervised_jobs
                          SET status = 'dead_letter',
                              error_message = @Reason,
                              moved_to_dlq_at = NOW()
                          WHERE job_id = @JobId
            """,
            new { JobId = jobId, Reason = reason });

        _logger.LogWarning("Job {JobId} moved to dead-letter queue: {Reason}", jobId, reason);
    }

    public async Task<IReadOnlyList<DeadLetterJob>> GetDeadLetterJobsAsync(
        Guid? tenantId = null,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        var jobs = await conn.QueryAsync<DeadLetterJobDto>(
            """
            SELECT job_id, tenant_id, job_type, payload, error_message,
                                 moved_to_dlq_at, retry_count, error_history
                          FROM supervised_jobs
                          WHERE status = 'dead_letter'
                            AND (@TenantId IS NULL OR tenant_id = @TenantId)
                          ORDER BY moved_to_dlq_at DESC
                          LIMIT @Limit
            """,
            new { TenantId = tenantId, Limit = limit });

        return jobs.Select(j => new DeadLetterJob
        {
            JobId = j.job_id,
            TenantId = j.tenant_id,
            JobType = j.job_type,
            Payload = j.payload,
            FailureReason = j.error_message ?? "Unknown",
            FailedAt = j.moved_to_dlq_at ?? DateTimeOffset.UtcNow,
            TotalAttempts = j.retry_count + 1,
            ErrorHistory = j.error_history
        }).ToList();
    }

    public async Task<bool> RetryDeadLetterJobAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        var affected = await conn.ExecuteAsync(
            """
            UPDATE supervised_jobs
                          SET status = 'pending',
                              retry_count = 0,
                              error_message = NULL,
                              moved_to_dlq_at = NULL,
                              started_at = NULL,
                              completed_at = NULL,
                              last_heartbeat = NULL,
                              progress_message = 'Retrying from DLQ',
                              progress_percent = 0
                          WHERE job_id = @JobId AND status = 'dead_letter'
            """,
            new { JobId = jobId });

        if (affected > 0)
        {
            _logger.LogInformation("Job {JobId} moved from dead-letter queue back to pending", jobId);
            return true;
        }

        return false;
    }

    public async Task<JobStatus?> GetJobStatusAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        var job = await conn.QueryFirstOrDefaultAsync<JobStatusDto>(
            """
            SELECT job_id, tenant_id, job_type, status, created_at, started_at,
                                 completed_at, last_heartbeat, progress_message, progress_percent,
                                 retry_count, max_retries, error_message, result_summary
                          FROM supervised_jobs
                          WHERE job_id = @JobId
            """,
            new { JobId = jobId });

        if (job == null)
            return null;

        return new JobStatus
        {
            JobId = job.job_id,
            TenantId = job.tenant_id,
            JobType = job.job_type,
            Status = ParseStatus(job.status),
            CreatedAt = job.created_at,
            StartedAt = job.started_at,
            CompletedAt = job.completed_at,
            LastHeartbeat = job.last_heartbeat,
            ProgressMessage = job.progress_message,
            ProgressPercent = job.progress_percent,
            RetryCount = job.retry_count,
            MaxRetries = job.max_retries,
            ErrorMessage = job.error_message,
            ResultSummary = job.result_summary
        };
    }

    private static JobExecutionStatus ParseStatus(string status) => status switch
    {
        "pending" => JobExecutionStatus.Pending,
        "running" => JobExecutionStatus.Running,
        "completed" => JobExecutionStatus.Completed,
        "failed" => JobExecutionStatus.Failed,
        "retrying" => JobExecutionStatus.Retrying,
        "dead_letter" => JobExecutionStatus.DeadLetter,
        "cancelled" => JobExecutionStatus.Cancelled,
        _ => JobExecutionStatus.Pending
    };

    // DTOs for Dapper
    private sealed class JobRetryInfo
    {
        public int retry_count { get; set; }
        public int max_retries { get; set; }
        public string[]? error_history { get; set; }
    }

    private sealed class StuckJobDto
    {
        public Guid job_id { get; set; }
        public Guid tenant_id { get; set; }
        public string job_type { get; set; } = "";
        public DateTimeOffset started_at { get; set; }
        public DateTimeOffset last_heartbeat { get; set; }
        public int retry_count { get; set; }
        public int max_retries { get; set; }
        public double seconds_since_heartbeat { get; set; }
    }

    private sealed class DeadLetterJobDto
    {
        public Guid job_id { get; set; }
        public Guid tenant_id { get; set; }
        public string job_type { get; set; } = "";
        public string payload { get; set; } = "";
        public string? error_message { get; set; }
        public DateTimeOffset? moved_to_dlq_at { get; set; }
        public int retry_count { get; set; }
        public string[]? error_history { get; set; }
    }

    private sealed class JobStatusDto
    {
        public Guid job_id { get; set; }
        public Guid tenant_id { get; set; }
        public string job_type { get; set; } = "";
        public string status { get; set; } = "";
        public DateTimeOffset created_at { get; set; }
        public DateTimeOffset? started_at { get; set; }
        public DateTimeOffset? completed_at { get; set; }
        public DateTimeOffset? last_heartbeat { get; set; }
        public string? progress_message { get; set; }
        public int? progress_percent { get; set; }
        public int retry_count { get; set; }
        public int max_retries { get; set; }
        public string? error_message { get; set; }
        public string? result_summary { get; set; }
    }
}
