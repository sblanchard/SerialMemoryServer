using System.Diagnostics;
using System.Threading.Channels;
using Dapper;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using SerialMemory.Core.Interfaces;

namespace SerialMemory.Infrastructure.Jobs;

/// <summary>
/// Request to queue a decay job.
/// </summary>
public sealed class DecayJobRequest
{
    public Guid JobId { get; init; } = Guid.CreateVersion7();
    public string TenantId { get; init; } = "self";
    public int BatchSize { get; init; } = 1000;
    public int DaysThreshold { get; init; } = 1;
    public float MinConfidence { get; init; } = 0.01f;
}

/// <summary>
/// Background worker for decay jobs using Channel<T>.
/// Applies confidence decay to old memories off request threads.
/// </summary>
public sealed class DecayJobWorker : BackgroundService
{
    private readonly Channel<DecayJobRequest> _channel;
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILiveEventEmitter _liveEventEmitter;
    private readonly ILogger<DecayJobWorker> _logger;
    private readonly Dictionary<Guid, DecayJobRequest> _activeJobs = new();
    private readonly object _lock = new();

    public DecayJobWorker(
        string connectionString,
        ILiveEventEmitter liveEventEmitter,
        ILogger<DecayJobWorker> logger)
    {
        _channel = Channel.CreateBounded<DecayJobRequest>(new BoundedChannelOptions(50)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });

        var builder = new NpgsqlDataSourceBuilder(connectionString);
        _dataSource = builder.Build();
        _liveEventEmitter = liveEventEmitter;
        _logger = logger;
    }

    public ChannelWriter<DecayJobRequest> Writer => _channel.Writer;

    public bool TryQueueJob(DecayJobRequest job)
    {
        if (_channel.Writer.TryWrite(job))
        {
            lock (_lock) { _activeJobs[job.JobId] = job; }
            return true;
        }
        return false;
    }

    public IReadOnlyList<DecayJobRequest> GetActiveJobs()
    {
        lock (_lock) { return _activeJobs.Values.ToList(); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("DecayJobWorker started");

        await foreach (var job in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ProcessJobAsync(job, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing decay job {JobId}", job.JobId);

                _ = _liveEventEmitter.EmitJobCompletedAsync(new JobCompletedEvent
                {
                    JobId = job.JobId,
                    JobType = "decay",
                    Status = "failed",
                    TenantId = job.TenantId,
                    ErrorMessage = ex.Message
                });
            }
            finally
            {
                lock (_lock) { _activeJobs.Remove(job.JobId); }
            }
        }

        _logger.LogInformation("DecayJobWorker stopped");
    }

    private async Task ProcessJobAsync(DecayJobRequest job, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        await _liveEventEmitter.EmitJobProgressAsync(new JobProgressEvent
        {
            JobId = job.JobId,
            JobType = "decay",
            Status = "running",
            TenantId = job.TenantId,
            Message = "Starting decay job"
        });

        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        // Get memories needing decay
        var memoriesNeedingDecay = (await conn.QueryAsync<(Guid Id, float Confidence, int HalfLifeDays, DateTimeOffset LastReinforced)>(
            """
            SELECT id, confidence_score, half_life_days, last_reinforced_at
            FROM memories
            WHERE is_active = TRUE
                AND confidence_score > @MinConfidence
                AND last_reinforced_at < NOW() - @DaysThreshold * INTERVAL '1 day'
            LIMIT @BatchSize
            """,
            new { job.MinConfidence, job.DaysThreshold, job.BatchSize })).ToList();

        var totalItems = memoriesNeedingDecay.Count;

        await _liveEventEmitter.EmitJobProgressAsync(new JobProgressEvent
        {
            JobId = job.JobId,
            JobType = "decay",
            Status = "running",
            TenantId = job.TenantId,
            TotalItems = totalItems,
            ProcessedItems = 0,
            Message = $"Found {totalItems} memories for decay"
        });

        var decayedCount = 0;
        var failedCount = 0;

        foreach (var memory in memoriesNeedingDecay)
        {
            try
            {
                // Calculate decay: confidence * 0.5^(days/half_life)
                var daysSinceReinforcement = (DateTimeOffset.UtcNow - memory.LastReinforced).TotalDays;
                var halfLife = memory.HalfLifeDays > 0 ? memory.HalfLifeDays : 30;
                var decayFactor = Math.Pow(0.5, daysSinceReinforcement / halfLife);
                var newConfidence = (float)(memory.Confidence * decayFactor);

                // Don't decay below minimum
                if (newConfidence < job.MinConfidence)
                    newConfidence = 0;

                await conn.ExecuteAsync(
                    """
                    UPDATE memories
                    SET confidence_score = @NewConfidence,
                        updated_at = NOW()
                    WHERE id = @MemoryId
                    """,
                    new { MemoryId = memory.Id, NewConfidence = newConfidence });

                decayedCount++;

                // Emit progress every 100 items
                if (decayedCount % 100 == 0)
                {
                    await _liveEventEmitter.EmitJobProgressAsync(new JobProgressEvent
                    {
                        JobId = job.JobId,
                        JobType = "decay",
                        Status = "running",
                        TenantId = job.TenantId,
                        TotalItems = totalItems,
                        ProcessedItems = decayedCount,
                        FailedItems = failedCount,
                        Message = $"Processed {decayedCount}/{totalItems} memories"
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to decay memory {MemoryId}", memory.Id);
                failedCount++;
            }
        }

        sw.Stop();

        await _liveEventEmitter.EmitJobCompletedAsync(new JobCompletedEvent
        {
            JobId = job.JobId,
            JobType = "decay",
            Status = "completed",
            TenantId = job.TenantId,
            TotalItems = totalItems,
            ProcessedItems = decayedCount,
            FailedItems = failedCount,
            DurationMs = (int)sw.ElapsedMilliseconds,
            Result = new
            {
                decayed = decayedCount,
                failed = failedCount,
                batchSize = job.BatchSize,
                daysThreshold = job.DaysThreshold
            }
        });

        _logger.LogInformation(
            "Decay job {JobId} completed: {DecayedCount} decayed, {FailedCount} failed in {DurationMs}ms",
            job.JobId, decayedCount, failedCount, sw.ElapsedMilliseconds);
    }
}
