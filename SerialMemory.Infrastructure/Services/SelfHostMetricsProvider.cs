using System.Diagnostics;
using Dapper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using SerialMemory.Core.Interfaces;

namespace SerialMemory.Infrastructure.Services;

/// <summary>
/// Self-host metrics provider with explicit demo mode handling.
/// Never silently shows fake data - always indicates when in demo mode.
/// </summary>
public sealed class SelfHostMetricsProvider : ISelfHostMetricsProvider
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly SelfHostMetricsConfig _config;
    private readonly ILogger<SelfHostMetricsProvider> _logger;

    private SelfHostMetricsResult? _cachedMetrics;
    private DateTimeOffset _cacheExpiry = DateTimeOffset.MinValue;
    private readonly SemaphoreSlim _cacheLock = new(1, 1);

    public SelfHostMetricsProvider(
        NpgsqlDataSource dataSource,
        IOptions<SelfHostMetricsConfig> config,
        ILogger<SelfHostMetricsProvider> logger)
    {
        _dataSource = dataSource;
        _config = config.Value;
        _logger = logger;
    }

    public bool IsLiveMode => _config.Mode.Equals("live", StringComparison.OrdinalIgnoreCase)
        || (_config.Mode.Equals("auto", StringComparison.OrdinalIgnoreCase) && CanConnectToDatabase());

    public async Task<SelfHostMetricsResult> GetMetricsAsync(CancellationToken ct = default)
    {
        // Check cache first
        if (_cachedMetrics != null && DateTimeOffset.UtcNow < _cacheExpiry)
        {
            return _cachedMetrics;
        }

        await _cacheLock.WaitAsync(ct);
        try
        {
            // Double-check cache after acquiring lock
            if (_cachedMetrics != null && DateTimeOffset.UtcNow < _cacheExpiry)
            {
                return _cachedMetrics;
            }

            var result = await FetchMetricsInternalAsync(ct);
            _cachedMetrics = result;
            _cacheExpiry = DateTimeOffset.UtcNow.Add(_config.CacheExpiry);
            return result;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    public async Task<SelfHostLoadStatus> GetCurrentLoadAsync(CancellationToken ct = default)
    {
        // Force demo mode if configured
        if (_config.Mode.Equals("demo", StringComparison.OrdinalIgnoreCase))
        {
            return GetDemoLoadStatus("Demo mode enabled via configuration");
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_config.FetchTimeout);

            await using var conn = await _dataSource.OpenConnectionAsync(cts.Token);
            await conn.ExecuteAsync("SELECT set_config('app.role', 'internal_admin', false)");

            var rps = await GetCurrentRpsAsync(conn, cts.Token);
            var latency = await GetP95LatencyAsync(conn, cts.Token);
            var queue = await GetQueueDepthAsync(conn, cts.Token);
            var db = await GetDbConnectionsAsync(conn, cts.Token);

            return new SelfHostLoadStatus
            {
                IsDemoMode = false,
                CurrentRps = rps,
                P95LatencyMs = latency,
                PendingTasks = queue,
                ActiveConnections = db.Active,
                DbConnectionUtilization = db.MaxConnections > 0
                    ? (double)db.Active / db.MaxConnections
                    : 0
            };
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Load status fetch timed out after {Timeout}", _config.FetchTimeout);
            return new SelfHostLoadStatus
            {
                IsDemoMode = false,
                HasError = true,
                ErrorMessage = "Metrics fetch timed out"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch current load status");
            return new SelfHostLoadStatus
            {
                IsDemoMode = false,
                HasError = true,
                ErrorMessage = $"Failed to fetch metrics: {ex.Message}"
            };
        }
    }

    private async Task<SelfHostMetricsResult> FetchMetricsInternalAsync(CancellationToken ct)
    {
        // Force demo mode if configured
        if (_config.Mode.Equals("demo", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("Returning demo metrics (mode=demo)");
            return GetDemoMetrics("Demo mode enabled via configuration");
        }

        var sw = Stopwatch.StartNew();

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_config.FetchTimeout);

            await using var conn = await _dataSource.OpenConnectionAsync(cts.Token);
            await conn.ExecuteAsync("SELECT set_config('app.role', 'internal_admin', false)");

            // Fetch all metrics in parallel
            var rpsTask = GetRpsMetricsAsync(conn, cts.Token);
            var latencyTask = GetLatencyMetricsAsync(conn, cts.Token);
            var queueTask = GetQueueMetricsAsync(conn, cts.Token);
            var dbTask = GetDatabaseMetricsAsync(conn, cts.Token);
            var apiStatsTask = GetApiStatsAsync(conn, cts.Token);
            var trendsTask = GetLoadTrendsAsync(conn, cts.Token);

            await Task.WhenAll(rpsTask, latencyTask, queueTask, dbTask, apiStatsTask, trendsTask);

            var rps = await rpsTask;
            var latency = await latencyTask;
            var queue = await queueTask;
            var db = await dbTask;
            var apiStats = await apiStatsTask;
            var trends = await trendsTask;

            sw.Stop();
            _logger.LogDebug("Fetched live metrics in {ElapsedMs}ms", sw.ElapsedMilliseconds);

            // Build result
            var apiMetrics = new SelfHostApiMetrics
            {
                InstanceCount = 1, // Self-hosted assumption
                CurrentRps = rps.CurrentRps,
                AvgRps15Min = rps.AvgRps15Min,
                PeakRps15Min = rps.PeakRps15Min,
                P50LatencyMs = latency.P50Ms,
                P95LatencyMs = latency.P95Ms,
                P99LatencyMs = latency.P99Ms,
                RequestsLastHour = apiStats.RequestsLastHour,
                ErrorsLastHour = apiStats.ErrorsLastHour,
                ErrorRate = apiStats.RequestsLastHour > 0
                    ? (double)apiStats.ErrorsLastHour / apiStats.RequestsLastHour
                    : 0
            };

            var workerMetrics = new SelfHostWorkerMetrics
            {
                InstanceCount = 1,
                PendingTasks = queue.PendingTasks,
                RunningJobs = queue.RunningJobs,
                FailedLastHour = queue.FailedLastHour,
                CompletedLastHour = queue.CompletedLastHour
            };

            var dbMetrics = new SelfHostDatabaseMetrics
            {
                ActiveConnections = db.ActiveConnections,
                IdleConnections = db.IdleConnections,
                TotalConnections = db.TotalConnections,
                MaxConnections = db.MaxConnections,
                ConnectionUtilization = db.MaxConnections > 0
                    ? (double)db.ActiveConnections / db.MaxConnections
                    : 0,
                DatabaseSizeBytes = db.DatabaseSizeBytes,
                CacheHitRatio = db.CacheHitRatio,
                Status = "connected"
            };

            var trafficMetrics = new SelfHostTrafficMetrics
            {
                CurrentRps = rps.CurrentRps,
                AvgRps = rps.AvgRps15Min,
                PeakRps = rps.PeakRps15Min,
                TrendDirection = trends.TrendDirection,
                GrowthRate = trends.GrowthRate
            };

            var scalePrediction = CalculateScalePrediction(apiMetrics, workerMetrics, dbMetrics, trends);

            return new SelfHostMetricsResult
            {
                IsDemoMode = false,
                ScalePrediction = scalePrediction,
                Api = apiMetrics,
                Workers = workerMetrics,
                Database = dbMetrics,
                Traffic = trafficMetrics
            };
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Metrics fetch timed out after {Timeout}", _config.FetchTimeout);
            return new SelfHostMetricsResult
            {
                IsDemoMode = false,
                HasError = true,
                ErrorMessage = "Metrics fetch timed out. Check database connectivity."
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch self-host metrics");

            // If auto mode and we can't connect, return error (not demo)
            return new SelfHostMetricsResult
            {
                IsDemoMode = false,
                HasError = true,
                ErrorMessage = $"Failed to fetch metrics: {ex.Message}"
            };
        }
    }

    private SelfHostScalePrediction CalculateScalePrediction(
        SelfHostApiMetrics api,
        SelfHostWorkerMetrics workers,
        SelfHostDatabaseMetrics db,
        LoadTrendsData trends)
    {
        const int TargetP95LatencyMs = 200;
        const double HighLoadRpsThreshold = 100;
        const int MaxBacklogDrainMinutes = 10;

        // API scaling logic
        var latencyFactor = api.P95LatencyMs > TargetP95LatencyMs
            ? api.P95LatencyMs / TargetP95LatencyMs
            : 1.0;
        var growthFactor = trends.TrendDirection == "increasing" ? 1.0 + trends.GrowthRate : 1.0;
        var recommendedApi = Math.Max(1, (int)Math.Ceiling(
            (api.CurrentRps / HighLoadRpsThreshold) * latencyFactor * growthFactor));
        recommendedApi = Math.Min(recommendedApi, 10);

        var apiIndicator = recommendedApi > 1 ? "scale_up" :
                          recommendedApi < 1 ? "scale_down" : "stable";
        var apiReason = GenerateApiReason(api, trends, TargetP95LatencyMs, HighLoadRpsThreshold);

        // Worker scaling logic
        var tasksPerWorkerPerMinute = 100;
        var minutesToDrain = (double)workers.PendingTasks / tasksPerWorkerPerMinute;
        var recommendedWorkers = minutesToDrain > MaxBacklogDrainMinutes
            ? Math.Max(1, (int)Math.Ceiling(minutesToDrain / MaxBacklogDrainMinutes))
            : 1;

        var workerIndicator = recommendedWorkers > 1 ? "scale_up" : "stable";
        var workerReason = GenerateWorkerReason(workers);

        // Database risk assessment
        var dbRiskLevel = db.ConnectionUtilization > 0.8 ? "High" :
                         db.ConnectionUtilization > 0.5 ? "Medium" : "Low";
        var dbSuggestion = dbRiskLevel switch
        {
            "High" => "Consider increasing max_connections or adding read replicas",
            "Medium" => "Monitor connection pool usage",
            _ => "Database capacity adequate"
        };

        return new SelfHostScalePrediction
        {
            CurrentApiInstances = 1,
            RecommendedApiInstances = recommendedApi,
            ApiScaleIndicator = apiIndicator,
            ApiReason = apiReason,
            CurrentWorkerInstances = 1,
            RecommendedWorkerInstances = recommendedWorkers,
            WorkerScaleIndicator = workerIndicator,
            WorkerReason = workerReason,
            DbRiskLevel = dbRiskLevel,
            DbConnectionUtilization = db.ConnectionUtilization,
            DbSuggestion = dbSuggestion
        };
    }

    private static string GenerateApiReason(
        SelfHostApiMetrics api,
        LoadTrendsData trends,
        int targetLatency,
        double highLoadThreshold)
    {
        var reasons = new List<string>();

        if (api.P95LatencyMs > targetLatency)
            reasons.Add($"P95 latency {api.P95LatencyMs:F0}ms exceeds target {targetLatency}ms");

        if (api.CurrentRps > highLoadThreshold)
            reasons.Add($"High RPS ({api.CurrentRps:F1})");

        if (trends.TrendDirection == "increasing")
            reasons.Add($"Load increasing ({trends.GrowthRate:P0} growth)");

        return reasons.Count > 0 ? string.Join("; ", reasons) : "Load within normal parameters";
    }

    private static string GenerateWorkerReason(SelfHostWorkerMetrics workers)
    {
        var reasons = new List<string>();

        if (workers.PendingTasks > 1000)
            reasons.Add($"High backlog ({workers.PendingTasks} pending)");

        if (workers.FailedLastHour > 10)
            reasons.Add($"Elevated failures ({workers.FailedLastHour} in last hour)");

        return reasons.Count > 0 ? string.Join("; ", reasons) : "Queue depth normal";
    }

    #region Database Queries

    private async Task<RpsData> GetRpsMetricsAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        try
        {
            var result = await conn.QueryFirstOrDefaultAsync<RpsData>(@"
                SELECT
                    COALESCE(COUNT(*) FILTER (WHERE event_timestamp > NOW() - INTERVAL '1 minute')::float / 60.0, 0) AS CurrentRps,
                    COALESCE(COUNT(*)::float / 900.0, 0) AS AvgRps15Min,
                    COALESCE((SELECT COUNT(*)::float / 60.0
                     FROM usage_events
                     WHERE event_timestamp > NOW() - INTERVAL '15 minutes'
                     GROUP BY date_trunc('minute', event_timestamp)
                     ORDER BY 1 DESC
                     LIMIT 1), 0) AS PeakRps15Min
                FROM usage_events
                WHERE event_timestamp > NOW() - INTERVAL '15 minutes'");

            return result ?? new RpsData();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get RPS metrics, table may not exist");
            return new RpsData();
        }
    }

    private async Task<LatencyData> GetLatencyMetricsAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        try
        {
            var result = await conn.QueryFirstOrDefaultAsync<LatencyData>(@"
                SELECT
                    COALESCE(AVG(p50_latency_ms), 0) AS P50Ms,
                    COALESCE(AVG(p95_latency_ms), 0) AS P95Ms,
                    COALESCE(AVG(p99_latency_ms), 0) AS P99Ms
                FROM graph_metrics_hourly
                WHERE bucket_hour > NOW() - INTERVAL '1 hour'");

            return result ?? new LatencyData();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get latency metrics, table may not exist");
            return new LatencyData();
        }
    }

    private async Task<QueueData> GetQueueMetricsAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        try
        {
            var result = await conn.QueryFirstOrDefaultAsync<QueueData>(@"
                SELECT
                    COALESCE((SELECT COUNT(*) FROM maintenance_tasks WHERE status = 'pending'), 0)::int AS PendingTasks,
                    COALESCE((SELECT COUNT(*) FROM supervised_jobs WHERE status = 'running'), 0)::int AS RunningJobs,
                    COALESCE((SELECT COUNT(*) FROM maintenance_tasks WHERE status = 'failed' AND scheduled_at > NOW() - INTERVAL '1 hour'), 0)::int AS FailedLastHour,
                    COALESCE((SELECT COUNT(*) FROM maintenance_tasks WHERE status = 'completed' AND scheduled_at > NOW() - INTERVAL '1 hour'), 0)::int AS CompletedLastHour");

            return result ?? new QueueData();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get queue metrics, tables may not exist");
            return new QueueData();
        }
    }

    private async Task<DbMetricsData> GetDatabaseMetricsAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        try
        {
            var result = await conn.QueryFirstOrDefaultAsync<DbMetricsData>(@"
                SELECT
                    COUNT(*) FILTER (WHERE state = 'active')::int AS ActiveConnections,
                    COUNT(*) FILTER (WHERE state = 'idle')::int AS IdleConnections,
                    COUNT(*)::int AS TotalConnections,
                    (SELECT setting::int FROM pg_settings WHERE name = 'max_connections') AS MaxConnections,
                    pg_database_size(current_database()) AS DatabaseSizeBytes,
                    COALESCE((SELECT ROUND(
                        CASE WHEN (blks_hit + blks_read) > 0
                        THEN blks_hit::float / (blks_hit + blks_read) * 100
                        ELSE 0 END, 2)
                    FROM pg_stat_database WHERE datname = current_database()), 0) AS CacheHitRatio
                FROM pg_stat_activity
                WHERE datname = current_database()");

            return result ?? new DbMetricsData { MaxConnections = 100 };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get database metrics");
            return new DbMetricsData { MaxConnections = 100 };
        }
    }

    private async Task<ApiStatsData> GetApiStatsAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        try
        {
            var result = await conn.QueryFirstOrDefaultAsync<ApiStatsData>(@"
                SELECT
                    COUNT(*) AS RequestsLastHour,
                    COUNT(*) FILTER (WHERE severity = 'error') AS ErrorsLastHour
                FROM event_log
                WHERE created_at > NOW() - INTERVAL '1 hour'
                    AND event_type LIKE 'api_%'");

            return result ?? new ApiStatsData();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get API stats, table may not exist");
            return new ApiStatsData();
        }
    }

    private async Task<LoadTrendsData> GetLoadTrendsAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        try
        {
            var trendData = await conn.QueryAsync<(DateTimeOffset Hour, int Count)>(@"
                SELECT
                    date_trunc('hour', rollup_date::timestamp) AS Hour,
                    SUM(event_count)::int AS Count
                FROM usage_daily_rollups
                WHERE rollup_date > CURRENT_DATE - INTERVAL '24 hours'
                GROUP BY date_trunc('hour', rollup_date::timestamp)
                ORDER BY 1 DESC
                LIMIT 24");

            var dataPoints = trendData.ToList();
            var trends = new LoadTrendsData();

            if (dataPoints.Count >= 2)
            {
                var recent = dataPoints.Take(6).Average(d => d.Count);
                var olderPoints = dataPoints.Skip(6).Take(6).ToList();
                var older = olderPoints.Count > 0 ? olderPoints.Average(d => d.Count) : 0.0;
                trends.TrendDirection = recent > older * 1.1 ? "increasing" :
                                       recent < older * 0.9 ? "decreasing" : "stable";
                trends.GrowthRate = older > 0 ? (recent - older) / older : 0;
            }

            return trends;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get load trends, table may not exist");
            return new LoadTrendsData();
        }
    }

    private async Task<double> GetCurrentRpsAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        try
        {
            var result = await conn.ExecuteScalarAsync<double?>(@"
                SELECT COUNT(*)::float / 60.0
                FROM usage_events
                WHERE event_timestamp > NOW() - INTERVAL '1 minute'");
            return result ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    private async Task<double> GetP95LatencyAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        try
        {
            var result = await conn.ExecuteScalarAsync<double?>(@"
                SELECT COALESCE(AVG(p95_latency_ms), 0)
                FROM graph_metrics_hourly
                WHERE bucket_hour > NOW() - INTERVAL '1 hour'");
            return result ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    private async Task<int> GetQueueDepthAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        try
        {
            var result = await conn.ExecuteScalarAsync<int?>(@"
                SELECT COUNT(*)::int FROM maintenance_tasks WHERE status = 'pending'");
            return result ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    private async Task<(int Active, int MaxConnections)> GetDbConnectionsAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        try
        {
            var result = await conn.QueryFirstOrDefaultAsync<(int Active, int Max)>(@"
                SELECT
                    COUNT(*) FILTER (WHERE state = 'active')::int AS Active,
                    (SELECT setting::int FROM pg_settings WHERE name = 'max_connections') AS Max
                FROM pg_stat_activity
                WHERE datname = current_database()");
            return (result.Active, result.Max > 0 ? result.Max : 100);
        }
        catch
        {
            return (0, 100);
        }
    }

    #endregion

    #region Demo Mode

    private bool CanConnectToDatabase()
    {
        try
        {
            using var conn = _dataSource.CreateConnection();
            conn.Open();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static SelfHostMetricsResult GetDemoMetrics(string reason)
    {
        return new SelfHostMetricsResult
        {
            IsDemoMode = true,
            DemoModeReason = reason,
            ScalePrediction = new SelfHostScalePrediction
            {
                CurrentApiInstances = 1,
                RecommendedApiInstances = 1,
                ApiScaleIndicator = "stable",
                ApiReason = "Demo data - Load within normal parameters",
                CurrentWorkerInstances = 1,
                RecommendedWorkerInstances = 1,
                WorkerScaleIndicator = "stable",
                WorkerReason = "Demo data - Queue depth normal",
                DbRiskLevel = "Low",
                DbConnectionUtilization = 0.15,
                DbSuggestion = "Demo data - Database capacity adequate"
            },
            Api = new SelfHostApiMetrics
            {
                InstanceCount = 1,
                CurrentRps = 12.5,
                AvgRps15Min = 10.2,
                PeakRps15Min = 25.8,
                P50LatencyMs = 45,
                P95LatencyMs = 120,
                P99LatencyMs = 180,
                RequestsLastHour = 2500,
                ErrorsLastHour = 12,
                ErrorRate = 0.0048
            },
            Workers = new SelfHostWorkerMetrics
            {
                InstanceCount = 1,
                PendingTasks = 42,
                RunningJobs = 3,
                FailedLastHour = 2,
                CompletedLastHour = 156,
                AvgProcessingTimeMs = 850
            },
            Database = new SelfHostDatabaseMetrics
            {
                ActiveConnections = 8,
                IdleConnections = 12,
                TotalConnections = 20,
                MaxConnections = 100,
                ConnectionUtilization = 0.08,
                DatabaseSizeBytes = 524_288_000, // ~500MB
                CacheHitRatio = 98.5,
                Status = "demo"
            },
            Traffic = new SelfHostTrafficMetrics
            {
                CurrentRps = 12.5,
                AvgRps = 10.2,
                PeakRps = 25.8,
                TrendDirection = "stable",
                GrowthRate = 0.02
            }
        };
    }

    private static SelfHostLoadStatus GetDemoLoadStatus(string reason)
    {
        return new SelfHostLoadStatus
        {
            IsDemoMode = true,
            CurrentRps = 12.5,
            P95LatencyMs = 120,
            PendingTasks = 42,
            ActiveConnections = 8,
            DbConnectionUtilization = 0.08
        };
    }

    #endregion

    #region Data Classes

    private sealed class RpsData
    {
        public double CurrentRps { get; init; }
        public double AvgRps15Min { get; init; }
        public double PeakRps15Min { get; init; }
    }

    private sealed class LatencyData
    {
        public double P50Ms { get; init; }
        public double P95Ms { get; init; }
        public double P99Ms { get; init; }
    }

    private sealed class QueueData
    {
        public int PendingTasks { get; init; }
        public int RunningJobs { get; init; }
        public int FailedLastHour { get; init; }
        public int CompletedLastHour { get; init; }
    }

    private sealed class DbMetricsData
    {
        public int ActiveConnections { get; init; }
        public int IdleConnections { get; init; }
        public int TotalConnections { get; init; }
        public int MaxConnections { get; init; } = 100;
        public long DatabaseSizeBytes { get; init; }
        public double CacheHitRatio { get; init; }
    }

    private sealed class ApiStatsData
    {
        public long RequestsLastHour { get; init; }
        public long ErrorsLastHour { get; init; }
    }

    private sealed class LoadTrendsData
    {
        public string TrendDirection { get; set; } = "stable";
        public double GrowthRate { get; set; }
    }

    #endregion
}
