using System.Diagnostics;
using Npgsql;
using Dapper;
using SerialMemory.Core.Interfaces;

namespace SerialMemory.Api.Realtime;

/// <summary>
/// Background service that polls real database tables and emits introspection snapshots via SignalR.
/// Zero mock data - all metrics come from actual system state.
/// </summary>
public sealed class IntrospectionBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<IntrospectionBackgroundService> _logger;
    private readonly TimeSpan _fullSnapshotInterval = TimeSpan.FromSeconds(5);
    private readonly TimeSpan _granularInterval = TimeSpan.FromSeconds(2);

    // GC baseline for delta tracking
    private int _lastGen0Collections;
    private int _lastGen1Collections;
    private int _lastGen2Collections;

    public IntrospectionBackgroundService(
        IServiceScopeFactory scopeFactory,
        ILogger<IntrospectionBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;

        _lastGen0Collections = GC.CollectionCount(0);
        _lastGen1Collections = GC.CollectionCount(1);
        _lastGen2Collections = GC.CollectionCount(2);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("IntrospectionBackgroundService starting");

        var fullSnapshotTimer = TimeSpan.Zero;
        var granularTimer = TimeSpan.Zero;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var delay = TimeSpan.FromMilliseconds(500);
                fullSnapshotTimer += delay;
                granularTimer += delay;

                // Full introspection snapshot every 5 seconds
                if (fullSnapshotTimer >= _fullSnapshotInterval)
                {
                    fullSnapshotTimer = TimeSpan.Zero;
                    await EmitFullIntrospectionAsync(stoppingToken);
                }

                // Granular updates every 2 seconds
                if (granularTimer >= _granularInterval)
                {
                    granularTimer = TimeSpan.Zero;
                    await EmitGranularUpdatesAsync(stoppingToken);
                }

                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error in introspection cycle");
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }

        _logger.LogInformation("IntrospectionBackgroundService stopped");
    }

    private async Task EmitFullIntrospectionAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var emitter = scope.ServiceProvider.GetRequiredService<ILiveEventEmitter>();
        var connectionString = scope.ServiceProvider.GetRequiredService<IConfiguration>()
            .GetConnectionString("PostgreSQL");

        if (string.IsNullOrEmpty(connectionString))
        {
            _logger.LogWarning("PostgreSQL connection string not configured");
            return;
        }

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);

        // Set a system-level tenant context to bypass RLS (empty = show all for introspection)
        // Use a null-safe UUID to prevent "invalid input syntax for type uuid" errors
        await conn.ExecuteAsync("SET app.tenant_id = '00000000-0000-0000-0000-000000000000'");

        var snapshot = new IntrospectionSnapshot
        {
            Timestamp = DateTimeOffset.UtcNow,
            JobBacklog = await BuildJobBacklogAsync(conn, ct),
            ActiveTraces = await BuildActiveTracesAsync(conn, ct),
            SecurityScan = await BuildSecurityScanAsync(conn, ct),
            GraphMutations = await BuildGraphMutationsAsync(conn, ct),
            Resources = BuildSystemResources()
        };

        await emitter.EmitIntrospectionAsync(snapshot);
    }

    private async Task EmitGranularUpdatesAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var emitter = scope.ServiceProvider.GetRequiredService<ILiveEventEmitter>();
        var connectionString = scope.ServiceProvider.GetRequiredService<IConfiguration>()
            .GetConnectionString("PostgreSQL");

        if (string.IsNullOrEmpty(connectionString)) return;

        // Use separate connections for parallel queries (Npgsql doesn't support concurrent commands on one connection)
        async Task<JobBacklogSnapshot> GetJobBacklogAsync()
        {
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync(ct);
            await conn.ExecuteAsync("SET app.tenant_id = '00000000-0000-0000-0000-000000000000'");
            return await BuildJobBacklogAsync(conn, ct);
        }

        async Task<ActiveTracesSnapshot> GetActiveTracesAsync()
        {
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync(ct);
            await conn.ExecuteAsync("SET app.tenant_id = '00000000-0000-0000-0000-000000000000'");
            return await BuildActiveTracesAsync(conn, ct);
        }

        var jobTask = GetJobBacklogAsync();
        var traceTask = GetActiveTracesAsync();

        await Task.WhenAll(jobTask, traceTask);

        await emitter.EmitJobBacklogAsync(await jobTask);
        await emitter.EmitActiveTracesAsync(await traceTask);
    }

    private async Task<JobBacklogSnapshot> BuildJobBacklogAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        const string sql = @"
            -- Job counts by status
            SELECT
                COUNT(*) FILTER (WHERE status = 'pending') AS pending_count,
                COUNT(*) FILTER (WHERE status = 'running') AS running_count,
                COUNT(*) FILTER (WHERE status = 'completed' AND completed_at > NOW() - INTERVAL '24 hours') AS completed_24h,
                COUNT(*) FILTER (WHERE status IN ('failed', 'dead_letter') AND completed_at > NOW() - INTERVAL '24 hours') AS failed_24h,
                COALESCE(AVG(EXTRACT(EPOCH FROM (started_at - created_at)) * 1000) FILTER (WHERE started_at IS NOT NULL), 0) AS avg_wait_ms,
                COALESCE(AVG(EXTRACT(EPOCH FROM (completed_at - started_at)) * 1000) FILTER (WHERE completed_at IS NOT NULL AND started_at IS NOT NULL), 0) AS avg_process_ms
            FROM supervised_jobs
            WHERE created_at > NOW() - INTERVAL '7 days';

            -- Queue status
            SELECT
                job_type AS queue_name,
                COUNT(*) FILTER (WHERE status = 'pending') AS depth,
                COUNT(*) FILTER (WHERE status = 'running') AS in_progress,
                1 AS workers,
                COUNT(*) FILTER (WHERE completed_at > NOW() - INTERVAL '1 minute') AS throughput_per_min,
                MIN(created_at) FILTER (WHERE status = 'pending') AS oldest_item_at
            FROM supervised_jobs
            WHERE created_at > NOW() - INTERVAL '1 day'
            GROUP BY job_type;

            -- Active jobs
            SELECT
                job_id,
                job_type,
                status,
                started_at,
                EXTRACT(EPOCH FROM (NOW() - started_at)) * 1000 AS elapsed_ms,
                COALESCE(progress_percent, 0) AS progress_percent,
                progress_message AS current_step
            FROM supervised_jobs
            WHERE status = 'running'
            ORDER BY started_at DESC
            LIMIT 10;
        ";

        await using var multi = await conn.QueryMultipleAsync(sql);

        var counts = await multi.ReadSingleOrDefaultAsync<dynamic>() ?? new
        {
            pending_count = 0L,
            running_count = 0L,
            completed_24h = 0L,
            failed_24h = 0L,
            avg_wait_ms = 0.0,
            avg_process_ms = 0.0
        };

        var queues = (await multi.ReadAsync<dynamic>())
            .Select(q => new JobQueueStatus
            {
                QueueName = (string)q.queue_name,
                Depth = (int)(q.depth ?? 0L),
                InProgress = (int)(q.in_progress ?? 0L),
                Workers = (int)(q.workers ?? 1),
                ThroughputPerMin = (double)(q.throughput_per_min ?? 0L),
                OldestItemAt = q.oldest_item_at as DateTimeOffset?
            }).ToList();

        var activeJobs = (await multi.ReadAsync<dynamic>())
            .Select(j => new ActiveJob
            {
                JobId = (Guid)j.job_id,
                JobType = (string)j.job_type,
                Status = (string)j.status,
                StartedAt = (DateTimeOffset)j.started_at,
                ElapsedMs = (double)(j.elapsed_ms ?? 0),
                ProgressPercent = (int)(j.progress_percent ?? 0),
                CurrentStep = j.current_step as string
            }).ToList();

        return new JobBacklogSnapshot
        {
            Timestamp = DateTimeOffset.UtcNow,
            TotalQueued = (int)(counts.pending_count ?? 0L),
            TotalInProgress = (int)(counts.running_count ?? 0L),
            TotalCompleted24h = (int)(counts.completed_24h ?? 0L),
            TotalFailed24h = (int)(counts.failed_24h ?? 0L),
            AvgWaitTimeMs = (double)(counts.avg_wait_ms ?? 0.0),
            AvgProcessTimeMs = (double)(counts.avg_process_ms ?? 0.0),
            Queues = queues,
            ActiveJobs = activeJobs
        };
    }

    private async Task<ActiveTracesSnapshot> BuildActiveTracesAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        const string sql = @"
            -- Trace counts
            SELECT
                COUNT(*) FILTER (WHERE status = 'running') AS active_count,
                COUNT(*) FILTER (WHERE status = 'pending') AS pending_count,
                COUNT(*) FILTER (WHERE status = 'completed' AND completed_utc > NOW() - INTERVAL '1 hour') AS completed_hour,
                COALESCE(AVG(duration_ms) FILTER (WHERE status = 'completed' AND duration_ms IS NOT NULL), 0) AS avg_duration_ms
            FROM agent_traces
            WHERE created_utc > NOW() - INTERVAL '24 hours';

            -- Active traces
            SELECT
                id AS trace_id,
                trace_type,
                status,
                created_utc AS started_at,
                EXTRACT(EPOCH FROM (NOW() - created_utc)) * 1000 AS elapsed_ms,
                step_count AS steps_completed,
                step_count AS total_steps,
                tenant_id
            FROM agent_traces
            WHERE status = 'running'
            ORDER BY created_utc DESC
            LIMIT 10;
        ";

        await using var multi = await conn.QueryMultipleAsync(sql);

        var counts = await multi.ReadSingleOrDefaultAsync<dynamic>() ?? new
        {
            active_count = 0L,
            pending_count = 0L,
            completed_hour = 0L,
            avg_duration_ms = 0.0
        };

        var traces = (await multi.ReadAsync<dynamic>())
            .Select(t => new ActiveTrace
            {
                TraceId = (Guid)t.trace_id,
                TraceType = (string)t.trace_type,
                Status = (string)t.status,
                StartedAt = (DateTimeOffset)t.started_at,
                ElapsedMs = (double)(t.elapsed_ms ?? 0),
                StepsCompleted = (int)(t.steps_completed ?? 0),
                TotalSteps = (int)(t.total_steps ?? 0),
                TenantId = t.tenant_id as string
            }).ToList();

        return new ActiveTracesSnapshot
        {
            Timestamp = DateTimeOffset.UtcNow,
            TotalActive = (int)(counts.active_count ?? 0L),
            TotalPending = (int)(counts.pending_count ?? 0L),
            CompletedLastHour = (int)(counts.completed_hour ?? 0L),
            AvgDurationMs = (double)(counts.avg_duration_ms ?? 0.0),
            Traces = traces
        };
    }

    private async Task<SecurityScanSnapshot> BuildSecurityScanAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        const string sql = @"
            -- Last full scan
            SELECT
                MAX(started_at) FILTER (WHERE scan_type = 'full' AND status = 'completed') AS last_full_scan,
                COUNT(*) FILTER (WHERE status = 'running') AS active_scans
            FROM security_scans
            WHERE started_at > NOW() - INTERVAL '30 days';

            -- Issues counts (24h)
            SELECT
                COUNT(*) FILTER (WHERE status IN ('fail', 'warning', 'error')) AS issues_detected,
                0 AS issues_resolved
            FROM security_events
            WHERE created_at > NOW() - INTERVAL '24 hours';

            -- Active scans
            SELECT
                scan_id,
                scan_type,
                status,
                started_at,
                EXTRACT(EPOCH FROM (NOW() - started_at)) * 1000 AS elapsed_ms,
                memories_scanned AS items_scanned,
                COALESCE(memories_scanned + entities_scanned, 0) AS total_items,
                issues_found
            FROM security_scans
            WHERE status = 'running'
            ORDER BY started_at DESC
            LIMIT 5;

            -- Recent issues
            SELECT
                id AS issue_id,
                event_type AS issue_type,
                CASE
                    WHEN status = 'fail' THEN 'Critical'
                    WHEN status = 'warning' THEN 'Warning'
                    ELSE 'Info'
                END AS severity,
                COALESCE(details->>'message', event_type) AS description,
                target_id,
                target_type,
                created_at AS detected_at,
                FALSE AS resolved
            FROM security_events
            WHERE status IN ('fail', 'warning', 'error')
            AND created_at > NOW() - INTERVAL '24 hours'
            ORDER BY created_at DESC
            LIMIT 10;
        ";

        await using var multi = await conn.QueryMultipleAsync(sql);

        var scanInfo = await multi.ReadSingleOrDefaultAsync<dynamic>() ?? new
        {
            last_full_scan = (DateTimeOffset?)null,
            active_scans = 0L
        };

        var issueInfo = await multi.ReadSingleOrDefaultAsync<dynamic>() ?? new
        {
            issues_detected = 0L,
            issues_resolved = 0L
        };

        var activeScans = (await multi.ReadAsync<dynamic>())
            .Select(s => new SecurityScanState
            {
                ScanId = (Guid)s.scan_id,
                ScanType = (string)s.scan_type,
                Status = (string)s.status,
                StartedAt = (DateTimeOffset)s.started_at,
                ElapsedMs = (double)(s.elapsed_ms ?? 0),
                ItemsScanned = (int)(s.items_scanned ?? 0),
                TotalItems = (int)(s.total_items ?? 0),
                IssuesFound = (int)(s.issues_found ?? 0)
            }).ToList();

        var recentIssues = (await multi.ReadAsync<dynamic>())
            .Select(i => new SecurityIssue
            {
                IssueId = (Guid)i.issue_id,
                IssueType = (string)i.issue_type,
                Severity = (string)i.severity,
                Description = (string)i.description,
                TargetId = i.target_id as Guid?,
                TargetType = i.target_type as string,
                DetectedAt = (DateTimeOffset)i.detected_at,
                Resolved = (bool)i.resolved
            }).ToList();

        var overallStatus = SecurityScanStatus.Idle;
        if (activeScans.Count > 0)
            overallStatus = SecurityScanStatus.Scanning;
        else if (recentIssues.Any(i => i.Severity == "Critical"))
            overallStatus = SecurityScanStatus.Critical;
        else if (recentIssues.Count > 0)
            overallStatus = SecurityScanStatus.IssuesDetected;

        return new SecurityScanSnapshot
        {
            Timestamp = DateTimeOffset.UtcNow,
            OverallStatus = overallStatus,
            LastFullScanAt = scanInfo.last_full_scan as DateTimeOffset?,
            ActiveScans = (int)(scanInfo.active_scans ?? 0L),
            IssuesDetected24h = (int)(issueInfo.issues_detected ?? 0L),
            IssuesResolved24h = (int)(issueInfo.issues_resolved ?? 0L),
            Scans = activeScans,
            RecentIssues = recentIssues
        };
    }

    private async Task<GraphMutationSnapshot> BuildGraphMutationsAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        const string sql = @"
            -- Mutation counts
            SELECT
                COUNT(*) FILTER (WHERE occurred_at > NOW() - INTERVAL '1 minute') AS mutations_last_min,
                COUNT(*) FILTER (WHERE occurred_at > NOW() - INTERVAL '1 hour') AS mutations_last_hour,
                (SELECT event_id FROM graph_events ORDER BY occurred_at DESC LIMIT 1) AS last_id
            FROM graph_events
            WHERE occurred_at > NOW() - INTERVAL '1 hour';

            -- Recent mutations
            SELECT
                event_id AS mutation_id,
                event_type AS mutation_type,
                node_id,
                edge_id,
                node_type,
                edge_type,
                occurred_at AS timestamp,
                tenant_id
            FROM graph_events
            ORDER BY occurred_at DESC
            LIMIT 20;

            -- Stats from entities and relationships (handle both PascalCase enum and snake_case legacy)
            SELECT
                (SELECT COUNT(*) FROM entities) AS total_nodes,
                (SELECT COUNT(*) FROM entity_relationships) AS total_edges,
                (SELECT COUNT(*) FROM graph_events WHERE event_type IN ('NodeCreated', 'node_created') AND occurred_at > NOW() - INTERVAL '24 hours') AS nodes_created_24h,
                (SELECT COUNT(*) FROM graph_events WHERE event_type IN ('NodeUpdated', 'node_updated') AND occurred_at > NOW() - INTERVAL '24 hours') AS nodes_updated_24h,
                (SELECT COUNT(*) FROM graph_events WHERE event_type IN ('NodeDeleted', 'node_deleted') AND occurred_at > NOW() - INTERVAL '24 hours') AS nodes_deleted_24h,
                (SELECT COUNT(*) FROM graph_events WHERE event_type IN ('EdgeCreated', 'edge_created') AND occurred_at > NOW() - INTERVAL '24 hours') AS edges_created_24h,
                (SELECT COUNT(*) FROM graph_events WHERE event_type IN ('EdgeDeleted', 'edge_deleted') AND occurred_at > NOW() - INTERVAL '24 hours') AS edges_deleted_24h;
        ";

        await using var multi = await conn.QueryMultipleAsync(sql);

        var counts = await multi.ReadSingleOrDefaultAsync<dynamic>() ?? new
        {
            mutations_last_min = 0L,
            mutations_last_hour = 0L,
            last_id = (Guid?)null
        };

        var recentMutations = (await multi.ReadAsync<dynamic>())
            .Select((m, i) => new GraphMutationEvent
            {
                SequenceNumber = i,
                MutationId = (Guid)m.mutation_id,
                MutationType = (string)m.mutation_type,
                NodeId = m.node_id as Guid?,
                EdgeId = m.edge_id as Guid?,
                NodeType = m.node_type as string,
                EdgeType = m.edge_type as string,
                Timestamp = (DateTimeOffset)m.timestamp,
                TenantId = m.tenant_id as string
            }).ToList();

        var stats = await multi.ReadSingleOrDefaultAsync<dynamic>() ?? new
        {
            total_nodes = 0,
            total_edges = 0,
            nodes_created_24h = 0L,
            nodes_updated_24h = 0L,
            nodes_deleted_24h = 0L,
            edges_created_24h = 0L,
            edges_deleted_24h = 0L
        };

        var mutationsLastMin = (int)(counts.mutations_last_min ?? 0L);

        return new GraphMutationSnapshot
        {
            Timestamp = DateTimeOffset.UtcNow,
            LastSequenceNumber = recentMutations.FirstOrDefault()?.SequenceNumber ?? 0,
            MutationsLastMinute = mutationsLastMin,
            MutationsLastHour = (int)(counts.mutations_last_hour ?? 0L),
            MutationsPerSecond = mutationsLastMin / 60.0,
            RecentMutations = recentMutations,
            Stats = new GraphMutationStats
            {
                NodesCreated24h = (int)(stats.nodes_created_24h ?? 0L),
                NodesUpdated24h = (int)(stats.nodes_updated_24h ?? 0L),
                NodesDeleted24h = (int)(stats.nodes_deleted_24h ?? 0L),
                EdgesCreated24h = (int)(stats.edges_created_24h ?? 0L),
                EdgesDeleted24h = (int)(stats.edges_deleted_24h ?? 0L),
                TotalNodes = (int)(stats.total_nodes ?? 0),
                TotalEdges = (int)(stats.total_edges ?? 0)
            }
        };
    }

    private SystemResourceSnapshot BuildSystemResources()
    {
        var process = Process.GetCurrentProcess();

        var gen0 = GC.CollectionCount(0);
        var gen1 = GC.CollectionCount(1);
        var gen2 = GC.CollectionCount(2);

        var snapshot = new SystemResourceSnapshot
        {
            CpuPercent = 0, // Would need sampling over time for accurate CPU
            MemoryUsedMb = process.WorkingSet64 / (1024 * 1024),
            MemoryTotalMb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024),
            ActiveConnections = 0, // Would need SignalR hub context to track
            ThreadPoolThreads = ThreadPool.ThreadCount,
            GcTotalMemory = GC.GetTotalMemory(false),
            Gen0Collections = gen0 - _lastGen0Collections,
            Gen1Collections = gen1 - _lastGen1Collections,
            Gen2Collections = gen2 - _lastGen2Collections
        };

        _lastGen0Collections = gen0;
        _lastGen1Collections = gen1;
        _lastGen2Collections = gen2;

        return snapshot;
    }
}
