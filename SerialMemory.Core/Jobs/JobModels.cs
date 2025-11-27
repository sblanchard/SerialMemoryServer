using System.Threading.Channels;

namespace SerialMemory.Core.Jobs;

/// <summary>
/// Job status enum for tracking job lifecycle.
/// </summary>
public enum JobStatus
{
    Queued,
    Running,
    Completed,
    Failed,
    Cancelled
}

/// <summary>
/// Job type enum for different background workloads.
/// </summary>
public enum JobType
{
    Analysis,
    Decay,
    Reembed,
    Export,
    Import,
    Crawl
}

/// <summary>
/// Base class for all background jobs.
/// </summary>
public abstract class BackgroundJob
{
    public Guid JobId { get; init; } = Guid.CreateVersion7();
    public string TenantId { get; init; } = "self";
    public string WorkspaceId { get; init; } = "default";
    public abstract JobType JobType { get; }
    public JobStatus Status { get; set; } = JobStatus.Queued;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public int TotalItems { get; set; }
    public int ProcessedItems { get; set; }
    public int FailedItems { get; set; }
    public string? ErrorMessage { get; set; }
    public CancellationTokenSource Cts { get; } = new();
}

/// <summary>
/// Analysis job - runs engineering analysis on knowledge graph.
/// </summary>
public sealed class AnalysisJob : BackgroundJob
{
    public override JobType JobType => JobType.Analysis;
    public string? Project { get; init; }
    public Guid? MemoryId { get; init; }
    public int MaxDurationMs { get; init; } = 30000;
    public List<AnalysisInsight> Insights { get; } = [];
}

/// <summary>
/// Decay job - applies confidence decay to old memories.
/// </summary>
public sealed class DecayJob : BackgroundJob
{
    public override JobType JobType => JobType.Decay;
    public int BatchSize { get; init; } = 1000;
    public int DaysThreshold { get; init; } = 1;
    public float MinConfidence { get; init; } = 0.01f;
}

/// <summary>
/// Reembed job - regenerates embeddings for memories.
/// </summary>
public sealed class ReembedJob : BackgroundJob
{
    public override JobType JobType => JobType.Reembed;
    public int BatchSize { get; init; } = 100;
    public bool ForceAll { get; init; }
}

/// <summary>
/// Insight from analysis job.
/// </summary>
public sealed class AnalysisInsight
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public string Type { get; init; } = "";
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public string Severity { get; init; } = "info";
    public float Confidence { get; init; }
    public string? SourceModel { get; init; }
}

/// <summary>
/// Progress update emitted via SignalR.
/// </summary>
public sealed record JobProgressEvent
{
    public Guid JobId { get; init; }
    public JobType JobType { get; init; }
    public JobStatus Status { get; init; }
    public string TenantId { get; init; } = "self";
    public int TotalItems { get; init; }
    public int ProcessedItems { get; init; }
    public int FailedItems { get; init; }
    public double ProgressPercent => TotalItems > 0 ? Math.Round((double)ProcessedItems / TotalItems * 100, 1) : 0;
    public string? CurrentItem { get; init; }
    public string? Message { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Job completion event emitted via SignalR.
/// </summary>
public sealed record JobCompletedEvent
{
    public Guid JobId { get; init; }
    public JobType JobType { get; init; }
    public JobStatus Status { get; init; }
    public string TenantId { get; init; } = "self";
    public int TotalItems { get; init; }
    public int ProcessedItems { get; init; }
    public int FailedItems { get; init; }
    public int DurationMs { get; init; }
    public string? ErrorMessage { get; init; }
    public object? Result { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Channel-based job queue for a specific job type.
/// </summary>
public sealed class JobChannel<TJob> where TJob : BackgroundJob
{
    private readonly Channel<TJob> _channel;

    public JobChannel(int capacity = 1000)
    {
        _channel = Channel.CreateBounded<TJob>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
    }

    public ChannelWriter<TJob> Writer => _channel.Writer;
    public ChannelReader<TJob> Reader => _channel.Reader;

    public bool TryWrite(TJob job) => _channel.Writer.TryWrite(job);

    public ValueTask WriteAsync(TJob job, CancellationToken ct = default)
        => _channel.Writer.WriteAsync(job, ct);

    public ValueTask<TJob> ReadAsync(CancellationToken ct = default)
        => _channel.Reader.ReadAsync(ct);

    public IAsyncEnumerable<TJob> ReadAllAsync(CancellationToken ct = default)
        => _channel.Reader.ReadAllAsync(ct);
}

/// <summary>
/// Registry for tracking active jobs.
/// </summary>
public sealed class JobRegistry
{
    private readonly Dictionary<Guid, BackgroundJob> _jobs = new();
    private readonly object _lock = new();

    public void Register(BackgroundJob job)
    {
        lock (_lock)
        {
            _jobs[job.JobId] = job;
        }
    }

    public void Unregister(Guid jobId)
    {
        lock (_lock)
        {
            _jobs.Remove(jobId);
        }
    }

    public BackgroundJob? Get(Guid jobId)
    {
        lock (_lock)
        {
            return _jobs.GetValueOrDefault(jobId);
        }
    }

    public IReadOnlyList<BackgroundJob> GetAll()
    {
        lock (_lock)
        {
            return _jobs.Values.ToList();
        }
    }

    public IReadOnlyList<BackgroundJob> GetByType(JobType type)
    {
        lock (_lock)
        {
            return _jobs.Values.Where(j => j.JobType == type).ToList();
        }
    }

    public IReadOnlyList<BackgroundJob> GetByTenant(string tenantId)
    {
        lock (_lock)
        {
            return _jobs.Values.Where(j => j.TenantId == tenantId).ToList();
        }
    }

    public bool TryCancel(Guid jobId)
    {
        lock (_lock)
        {
            if (_jobs.TryGetValue(jobId, out var job))
            {
                job.Cts.Cancel();
                job.Status = JobStatus.Cancelled;
                return true;
            }
            return false;
        }
    }
}
