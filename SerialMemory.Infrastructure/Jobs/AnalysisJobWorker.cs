using System.Diagnostics;
using System.Threading.Channels;
using Dapper;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using SerialMemory.Core.Interfaces;

namespace SerialMemory.Infrastructure.Jobs;

/// <summary>
/// Request to queue an analysis job.
/// </summary>
public sealed class AnalysisJobRequest
{
    public Guid JobId { get; init; } = Guid.CreateVersion7();
    public string TenantId { get; init; } = "self";
    public string? Project { get; init; }
    public Guid? MemoryId { get; init; }
    public int MaxDurationMs { get; init; } = 30000;
}

/// <summary>
/// Background worker for analysis jobs using Channel<T>.
/// Runs engineering analysis off request threads.
/// </summary>
public sealed class AnalysisJobWorker : BackgroundService
{
    private readonly Channel<AnalysisJobRequest> _channel;
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILiveEventEmitter _liveEventEmitter;
    private readonly ILogger<AnalysisJobWorker> _logger;
    private readonly Dictionary<Guid, AnalysisJobRequest> _activeJobs = new();
    private readonly object _lock = new();

    public AnalysisJobWorker(
        string connectionString,
        ILiveEventEmitter liveEventEmitter,
        ILogger<AnalysisJobWorker> logger)
    {
        _channel = Channel.CreateBounded<AnalysisJobRequest>(new BoundedChannelOptions(100)
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

    public ChannelWriter<AnalysisJobRequest> Writer => _channel.Writer;

    public bool TryQueueJob(AnalysisJobRequest job)
    {
        if (_channel.Writer.TryWrite(job))
        {
            lock (_lock) { _activeJobs[job.JobId] = job; }
            return true;
        }
        return false;
    }

    public IReadOnlyList<AnalysisJobRequest> GetActiveJobs()
    {
        lock (_lock) { return _activeJobs.Values.ToList(); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("AnalysisJobWorker started");

        await foreach (var job in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ProcessJobAsync(job, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing analysis job {JobId}", job.JobId);

                _ = _liveEventEmitter.EmitJobCompletedAsync(new JobCompletedEvent
                {
                    JobId = job.JobId,
                    JobType = "analysis",
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

        _logger.LogInformation("AnalysisJobWorker stopped");
    }

    private async Task ProcessJobAsync(AnalysisJobRequest job, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        // Emit start event
        await _liveEventEmitter.EmitJobProgressAsync(new JobProgressEvent
        {
            JobId = job.JobId,
            JobType = "analysis",
            Status = "running",
            TenantId = job.TenantId,
            Message = "Starting analysis job"
        });

        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        // Step 1: Get entities for analysis
        await _liveEventEmitter.EmitJobProgressAsync(new JobProgressEvent
        {
            JobId = job.JobId,
            JobType = "analysis",
            Status = "running",
            TenantId = job.TenantId,
            ProcessedItems = 1,
            TotalItems = 5,
            Message = "Loading entities"
        });

        var entityQuery = job.Project != null
            ? "SELECT id, name, entity_type, canonical_name FROM entities WHERE canonical_name ILIKE @Pattern LIMIT 500"
            : "SELECT id, name, entity_type, canonical_name FROM entities LIMIT 500";

        var entities = (await conn.QueryAsync<(Guid Id, string Name, string EntityType, string? CanonicalName)>(
            entityQuery, new { Pattern = $"%{job.Project}%" })).ToList();

        // Step 2: Get relationships
        await _liveEventEmitter.EmitJobProgressAsync(new JobProgressEvent
        {
            JobId = job.JobId,
            JobType = "analysis",
            Status = "running",
            TenantId = job.TenantId,
            ProcessedItems = 2,
            TotalItems = 5,
            Message = $"Loaded {entities.Count} entities, loading relationships"
        });

        var relationships = (await conn.QueryAsync<(Guid Id, Guid SourceId, Guid TargetId, string RelationshipType, float Confidence)>(
            "SELECT id, source_entity_id, target_entity_id, relationship_type, confidence FROM entity_relationships LIMIT 1000")).ToList();

        // Step 3: Run structural analysis
        await _liveEventEmitter.EmitJobProgressAsync(new JobProgressEvent
        {
            JobId = job.JobId,
            JobType = "analysis",
            Status = "running",
            TenantId = job.TenantId,
            ProcessedItems = 3,
            TotalItems = 5,
            Message = "Running structural analysis"
        });

        var insights = new List<object>();

        // Find isolated nodes
        var connectedEntityIds = relationships.SelectMany(r => new[] { r.SourceId, r.TargetId }).ToHashSet();
        var isolatedEntities = entities.Where(e => !connectedEntityIds.Contains(e.Id)).ToList();

        if (isolatedEntities.Count > 0)
        {
            insights.Add(new
            {
                id = Guid.CreateVersion7(),
                type = "structural",
                title = "Isolated Entities Detected",
                description = $"{isolatedEntities.Count} entities have no relationships",
                severity = isolatedEntities.Count > 10 ? "warning" : "info",
                confidence = 0.95f,
                sourceModel = "StructuralModel",
                entities = isolatedEntities.Take(10).Select(e => e.Name).ToList()
            });
        }

        // Step 4: Run risk analysis
        await _liveEventEmitter.EmitJobProgressAsync(new JobProgressEvent
        {
            JobId = job.JobId,
            JobType = "analysis",
            Status = "running",
            TenantId = job.TenantId,
            ProcessedItems = 4,
            TotalItems = 5,
            Message = "Running risk analysis"
        });

        // Find low confidence relationships
        var lowConfidenceRels = relationships.Where(r => r.Confidence < 0.5f).ToList();
        if (lowConfidenceRels.Count > 0)
        {
            insights.Add(new
            {
                id = Guid.CreateVersion7(),
                type = "risk",
                title = "Low Confidence Relationships",
                description = $"{lowConfidenceRels.Count} relationships have confidence below 50%",
                severity = "warning",
                confidence = 0.9f,
                sourceModel = "RiskModel",
                count = lowConfidenceRels.Count
            });
        }

        // Find highly connected entities (potential hubs)
        var entityConnectionCounts = relationships
            .SelectMany(r => new[] { r.SourceId, r.TargetId })
            .GroupBy(id => id)
            .Select(g => new { Id = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .Take(5)
            .ToList();

        if (entityConnectionCounts.Any(x => x.Count > 10))
        {
            var hubEntities = entityConnectionCounts.Where(x => x.Count > 10).ToList();
            var hubNames = entities.Where(e => hubEntities.Any(h => h.Id == e.Id)).Select(e => e.Name).ToList();

            insights.Add(new
            {
                id = Guid.CreateVersion7(),
                type = "optimization",
                title = "Hub Entities Identified",
                description = $"{hubNames.Count} entities are highly connected (>10 relationships)",
                severity = "info",
                confidence = 0.85f,
                sourceModel = "OptimizationModel",
                entities = hubNames
            });
        }

        // Step 5: Complete
        sw.Stop();

        await _liveEventEmitter.EmitJobProgressAsync(new JobProgressEvent
        {
            JobId = job.JobId,
            JobType = "analysis",
            Status = "running",
            TenantId = job.TenantId,
            ProcessedItems = 5,
            TotalItems = 5,
            Message = "Analysis complete"
        });

        await _liveEventEmitter.EmitJobCompletedAsync(new JobCompletedEvent
        {
            JobId = job.JobId,
            JobType = "analysis",
            Status = "completed",
            TenantId = job.TenantId,
            TotalItems = entities.Count + relationships.Count,
            ProcessedItems = entities.Count + relationships.Count,
            DurationMs = (int)sw.ElapsedMilliseconds,
            Result = new
            {
                entityCount = entities.Count,
                relationshipCount = relationships.Count,
                insightCount = insights.Count,
                insights
            }
        });

        _logger.LogInformation(
            "Analysis job {JobId} completed: {EntityCount} entities, {RelationshipCount} relationships, {InsightCount} insights in {DurationMs}ms",
            job.JobId, entities.Count, relationships.Count, insights.Count, sw.ElapsedMilliseconds);
    }
}
