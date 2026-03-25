using System.Diagnostics;
using System.Threading.Channels;
using Dapper;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using SerialMemory.Core.Interfaces;

namespace SerialMemory.Infrastructure.Jobs;

/// <summary>
/// Request to queue a reembed job.
/// </summary>
public sealed class ReembedJobRequest
{
    public Guid JobId { get; init; } = Guid.CreateVersion7();
    public string TenantId { get; init; } = "self";
    public int BatchSize { get; init; } = 100;
    public bool ForceAll { get; init; }
}

/// <summary>
/// Background worker for reembedding jobs using Channel<T>.
/// Regenerates embeddings for memories off request threads.
/// </summary>
public sealed class ReembedJobWorker : BackgroundService
{
    private readonly Channel<ReembedJobRequest> _channel;
    private readonly NpgsqlDataSource _dataSource;
    private readonly IEmbeddingService _embeddingService;
    private readonly ILiveEventEmitter _liveEventEmitter;
    private readonly ILogger<ReembedJobWorker> _logger;
    private readonly Dictionary<Guid, ReembedJobRequest> _activeJobs = new();
    private readonly object _lock = new();

    public ReembedJobWorker(
        string connectionString,
        IEmbeddingService embeddingService,
        ILiveEventEmitter liveEventEmitter,
        ILogger<ReembedJobWorker> logger)
    {
        _channel = Channel.CreateBounded<ReembedJobRequest>(new BoundedChannelOptions(20)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });

        var builder = new NpgsqlDataSourceBuilder(connectionString);
        _dataSource = builder.Build();
        _embeddingService = embeddingService;
        _liveEventEmitter = liveEventEmitter;
        _logger = logger;
    }

    public ChannelWriter<ReembedJobRequest> Writer => _channel.Writer;

    public bool TryQueueJob(ReembedJobRequest job)
    {
        if (_channel.Writer.TryWrite(job))
        {
            lock (_lock) { _activeJobs[job.JobId] = job; }
            return true;
        }
        return false;
    }

    public IReadOnlyList<ReembedJobRequest> GetActiveJobs()
    {
        lock (_lock) { return _activeJobs.Values.ToList(); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ReembedJobWorker started");

        await foreach (var job in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ProcessJobAsync(job, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing reembed job {JobId}", job.JobId);

                _ = _liveEventEmitter.EmitJobCompletedAsync(new JobCompletedEvent
                {
                    JobId = job.JobId,
                    JobType = "reembed",
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

        _logger.LogInformation("ReembedJobWorker stopped");
    }

    private async Task ProcessJobAsync(ReembedJobRequest job, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        await _liveEventEmitter.EmitJobProgressAsync(new JobProgressEvent
        {
            JobId = job.JobId,
            JobType = "reembed",
            Status = "running",
            TenantId = job.TenantId,
            Message = "Starting reembed job"
        });

        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        // Get memories needing reembedding
        var query = job.ForceAll
            ? "SELECT id, content FROM memories WHERE is_active = TRUE ORDER BY id"
            : "SELECT id, content FROM memories WHERE is_active = TRUE AND embedding IS NULL ORDER BY id LIMIT @BatchSize";

        var memories = (await conn.QueryAsync<(Guid Id, string Content)>(
            query, new { job.BatchSize })).ToList();

        var totalItems = memories.Count;

        await _liveEventEmitter.EmitJobProgressAsync(new JobProgressEvent
        {
            JobId = job.JobId,
            JobType = "reembed",
            Status = "running",
            TenantId = job.TenantId,
            TotalItems = totalItems,
            ProcessedItems = 0,
            Message = $"Found {totalItems} memories for reembedding"
        });

        if (totalItems == 0)
        {
            await _liveEventEmitter.EmitJobCompletedAsync(new JobCompletedEvent
            {
                JobId = job.JobId,
                JobType = "reembed",
                Status = "completed",
                TenantId = job.TenantId,
                TotalItems = 0,
                ProcessedItems = 0,
                DurationMs = (int)sw.ElapsedMilliseconds,
                Result = new { message = "No memories need reembedding" }
            });
            return;
        }

        var reembeddedCount = 0;
        var failedCount = 0;

        // Process in smaller batches for embedding service
        var embeddingBatchSize = 10;
        for (var i = 0; i < memories.Count; i += embeddingBatchSize)
        {
            var batch = memories.Skip(i).Take(embeddingBatchSize).ToList();
            var contents = batch.Select(m => m.Content).ToList();

            try
            {
                // Generate embeddings for batch
                var embeddings = await _embeddingService.EmbedBatchAsync(contents, ct);

                // Update each memory
                for (var j = 0; j < batch.Count && j < embeddings.Count; j++)
                {
                    try
                    {
                        var embedding = embeddings[j];
                        var embeddingString = "[" + string.Join(",", embedding) + "]";

                        await conn.ExecuteAsync(
                            """
                            UPDATE memories
                            SET embedding = @Embedding::vector,
                                updated_at = NOW()
                            WHERE id = @MemoryId
                            """,
                            new { MemoryId = batch[j].Id, Embedding = embeddingString });

                        reembeddedCount++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to update embedding for memory {MemoryId}", batch[j].Id);
                        failedCount++;
                    }
                }

                // Emit progress
                await _liveEventEmitter.EmitJobProgressAsync(new JobProgressEvent
                {
                    JobId = job.JobId,
                    JobType = "reembed",
                    Status = "running",
                    TenantId = job.TenantId,
                    TotalItems = totalItems,
                    ProcessedItems = reembeddedCount + failedCount,
                    FailedItems = failedCount,
                    Message = $"Processed {reembeddedCount + failedCount}/{totalItems} memories"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate embeddings for batch starting at {Index}", i);
                failedCount += batch.Count;
            }

            // Small delay to avoid overwhelming the embedding service
            await Task.Delay(100, ct);
        }

        sw.Stop();

        await _liveEventEmitter.EmitJobCompletedAsync(new JobCompletedEvent
        {
            JobId = job.JobId,
            JobType = "reembed",
            Status = failedCount > 0 && reembeddedCount == 0 ? "failed" : "completed",
            TenantId = job.TenantId,
            TotalItems = totalItems,
            ProcessedItems = reembeddedCount,
            FailedItems = failedCount,
            DurationMs = (int)sw.ElapsedMilliseconds,
            Result = new
            {
                reembedded = reembeddedCount,
                failed = failedCount,
                forceAll = job.ForceAll,
                batchSize = job.BatchSize
            }
        });

        _logger.LogInformation(
            "Reembed job {JobId} completed: {ReembeddedCount} reembedded, {FailedCount} failed in {DurationMs}ms",
            job.JobId, reembeddedCount, failedCount, sw.ElapsedMilliseconds);
    }
}
