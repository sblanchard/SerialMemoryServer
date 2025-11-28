using Dapper;
using Npgsql;

namespace SerialMemory.Api.Analysis;

/// <summary>
/// Auto-Scaling Prediction Layer - READ-ONLY advisory capacity recommendations.
/// Analyzes load patterns and recommends scaling for API, Workers, and DB.
/// </summary>
public interface IScalePredictionService
{
    Task<CurrentLoadStatus> GetCurrentLoadAsync(CancellationToken ct = default);
    Task<ScalePrediction> PredictScaleAsync(TimeSpan horizon, CancellationToken ct = default);
}

public sealed class ScalePredictionService : IScalePredictionService
{
    private readonly string _connectionString;
    private readonly ILogger<ScalePredictionService> _logger;

    // Configurable thresholds
    private const int TargetP95LatencyMs = 200;
    private const int MaxBacklogDrainMinutes = 10;
    private const double HighLoadRpsThreshold = 100;
    private const double CriticalLoadRpsThreshold = 500;

    public ScalePredictionService(string connectionString, ILogger<ScalePredictionService> logger)
    {
        _connectionString = connectionString;
        _logger = logger;
    }

    public async Task<CurrentLoadStatus> GetCurrentLoadAsync(CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var status = new CurrentLoadStatus
        {
            Timestamp = DateTimeOffset.UtcNow,
            RpsMetrics = await GetRpsMetricsAsync(conn, ct),
            LatencyMetrics = await GetLatencyMetricsAsync(conn, ct),
            QueueMetrics = await GetQueueMetricsAsync(conn, ct),
            DatabaseMetrics = await GetDatabaseMetricsAsync(conn, ct)
        };

        return status;
    }

    public async Task<ScalePrediction> PredictScaleAsync(TimeSpan horizon, CancellationToken ct = default)
    {
        var currentLoad = await GetCurrentLoadAsync(ct);

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        // Get historical trends for prediction
        var trends = await GetTrendsAsync(conn, horizon, ct);

        var prediction = new ScalePrediction
        {
            HorizonMinutes = (int)horizon.TotalMinutes,
            Timestamp = DateTimeOffset.UtcNow,
            CurrentApiInstances = 1, // Assume single instance for self-hosted
            CurrentWorkerInstances = 1,
            RecommendedApiInstances = CalculateApiInstances(currentLoad, trends),
            RecommendedWorkerInstances = CalculateWorkerInstances(currentLoad, trends),
            ApiReason = GenerateApiReason(currentLoad, trends),
            WorkerReason = GenerateWorkerReason(currentLoad, trends),
            Database = CalculateDatabaseAdvice(currentLoad),
            AutoscalerHint = GenerateAutoscalerHint(currentLoad, trends)
        };

        return prediction;
    }

    private async Task<RpsMetrics> GetRpsMetricsAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        // Get RPS from usage_events in last 15 minutes
        var rpsData = await conn.QueryFirstOrDefaultAsync<(double? Current, double? Avg, double? Peak)>(@"
            SELECT
                COUNT(*) FILTER (WHERE created_at > NOW() - INTERVAL '1 minute')::float / 60.0 AS current_rps,
                COUNT(*)::float / 900.0 AS avg_rps,
                (SELECT COUNT(*)::float / 60.0
                 FROM usage_events
                 WHERE created_at > NOW() - INTERVAL '15 minutes'
                 GROUP BY date_trunc('minute', created_at)
                 ORDER BY 1 DESC
                 LIMIT 1) AS peak_rps
            FROM usage_events
            WHERE created_at > NOW() - INTERVAL '15 minutes'");

        return new RpsMetrics
        {
            CurrentRps = rpsData.Current ?? 0,
            AvgRps15Min = rpsData.Avg ?? 0,
            PeakRps15Min = rpsData.Peak ?? 0
        };
    }

    private async Task<LatencyMetrics> GetLatencyMetricsAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        // Get latency percentiles from graph_metrics_hourly or security_metrics_hourly
        var latencyData = await conn.QueryFirstOrDefaultAsync<(double? P50, double? P95, double? P99)>(@"
            SELECT
                AVG(p50_latency_ms) AS ""P50"",
                AVG(p95_latency_ms) AS ""P95"",
                AVG(p99_latency_ms) AS ""P99""
            FROM graph_metrics_hourly
            WHERE hour_bucket > NOW() - INTERVAL '1 hour'");

        return new LatencyMetrics
        {
            P50Ms = latencyData.P50 ?? 0,
            P95Ms = latencyData.P95 ?? 0,
            P99Ms = latencyData.P99 ?? 0
        };
    }

    private async Task<QueueMetrics> GetQueueMetricsAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        // Get queue depth from maintenance_tasks and supervised_jobs
        var queueData = await conn.QueryFirstOrDefaultAsync<(int? Pending, int? Running, int? Failed)>(@"
            SELECT
                (SELECT COUNT(*) FROM maintenance_tasks WHERE status = 'pending')::int AS pending,
                (SELECT COUNT(*) FROM supervised_jobs WHERE status = 'running')::int AS running,
                (SELECT COUNT(*) FROM maintenance_tasks WHERE status = 'failed' AND created_at > NOW() - INTERVAL '1 hour')::int AS failed");

        return new QueueMetrics
        {
            PendingTasks = queueData.Pending ?? 0,
            RunningJobs = queueData.Running ?? 0,
            FailedLastHour = queueData.Failed ?? 0
        };
    }

    private async Task<DatabaseMetrics> GetDatabaseMetricsAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var dbStats = await conn.QueryFirstOrDefaultAsync<(int? Active, int? Idle, int? Total, long? Size)>(@"
            SELECT
                COUNT(*) FILTER (WHERE state = 'active')::int AS ""Active"",
                COUNT(*) FILTER (WHERE state = 'idle')::int AS ""Idle"",
                COUNT(*)::int AS ""Total"",
                pg_database_size(current_database()) AS ""Size""
            FROM pg_stat_activity
            WHERE datname = current_database()");

        return new DatabaseMetrics
        {
            ActiveConnections = dbStats.Active ?? 0,
            IdleConnections = dbStats.Idle ?? 0,
            TotalConnections = dbStats.Total ?? 0,
            DatabaseSizeBytes = dbStats.Size ?? 0
        };
    }

    private async Task<LoadTrends> GetTrendsAsync(NpgsqlConnection conn, TimeSpan horizon, CancellationToken ct)
    {
        // Get hourly trends from usage_daily_rollups
        var trendData = await conn.QueryAsync<(DateTimeOffset Hour, int Count)>(@"
            SELECT
                date_trunc('hour', rollup_date) AS ""Hour"",
                total_requests AS ""Count""
            FROM usage_daily_rollups
            WHERE rollup_date > NOW() - INTERVAL '24 hours'
            ORDER BY rollup_date DESC
            LIMIT 24");

        var dataPoints = trendData.ToList();
        var trend = new LoadTrends
        {
            DataPoints = dataPoints.Select(d => new TrendDataPoint
            {
                Timestamp = d.Hour,
                Value = d.Count
            }).ToList()
        };

        // Calculate simple linear trend
        if (dataPoints.Count >= 2)
        {
            var recent = dataPoints.Take(6).Average(d => d.Count);
            var older = dataPoints.Skip(6).Take(6).DefaultIfEmpty().Average(d => d.Count);
            trend.TrendDirection = recent > older * 1.1 ? "increasing" :
                                   recent < older * 0.9 ? "decreasing" : "stable";
            trend.GrowthRate = older > 0 ? (recent - older) / older : 0;
        }

        return trend;
    }

    private int CalculateApiInstances(CurrentLoadStatus load, LoadTrends trends)
    {
        // Simple capacity model: 1 instance per ~100 RPS at target latency
        var currentRps = load.RpsMetrics.CurrentRps;
        var currentLatency = load.LatencyMetrics.P95Ms;

        // If latency is above target, we need more capacity
        var latencyFactor = currentLatency > TargetP95LatencyMs ? currentLatency / TargetP95LatencyMs : 1.0;

        // Account for growth trend
        var growthFactor = trends.TrendDirection == "increasing" ? 1.0 + trends.GrowthRate : 1.0;

        var recommendedInstances = Math.Max(1, (int)Math.Ceiling(
            (currentRps / HighLoadRpsThreshold) * latencyFactor * growthFactor));

        return Math.Min(recommendedInstances, 10); // Cap at 10 for self-hosted
    }

    private int CalculateWorkerInstances(CurrentLoadStatus load, LoadTrends trends)
    {
        // Workers scale based on queue depth
        var pendingTasks = load.QueueMetrics.PendingTasks;

        // Assume 1 worker can process ~100 tasks per minute
        var tasksPerWorkerPerMinute = 100;
        var minutesToDrain = (double)pendingTasks / tasksPerWorkerPerMinute;

        if (minutesToDrain > MaxBacklogDrainMinutes)
        {
            // Need more workers to meet drain target
            return Math.Max(1, (int)Math.Ceiling(minutesToDrain / MaxBacklogDrainMinutes));
        }

        return 1;
    }

    private string GenerateApiReason(CurrentLoadStatus load, LoadTrends trends)
    {
        var reasons = new List<string>();

        if (load.LatencyMetrics.P95Ms > TargetP95LatencyMs)
            reasons.Add($"P95 latency {load.LatencyMetrics.P95Ms:F0}ms exceeds target {TargetP95LatencyMs}ms");

        if (load.RpsMetrics.CurrentRps > HighLoadRpsThreshold)
            reasons.Add($"High RPS ({load.RpsMetrics.CurrentRps:F1})");

        if (trends.TrendDirection == "increasing")
            reasons.Add($"Load increasing ({trends.GrowthRate:P0} growth)");

        return reasons.Count > 0 ? string.Join("; ", reasons) : "Load within normal parameters";
    }

    private string GenerateWorkerReason(CurrentLoadStatus load, LoadTrends trends)
    {
        var reasons = new List<string>();

        if (load.QueueMetrics.PendingTasks > 1000)
            reasons.Add($"High backlog ({load.QueueMetrics.PendingTasks} pending)");

        if (load.QueueMetrics.FailedLastHour > 10)
            reasons.Add($"Elevated failures ({load.QueueMetrics.FailedLastHour} in last hour)");

        return reasons.Count > 0 ? string.Join("; ", reasons) : "Queue depth normal";
    }

    private DatabaseScaleAdvice CalculateDatabaseAdvice(CurrentLoadStatus load)
    {
        var connectionUtilization = load.DatabaseMetrics.TotalConnections > 0
            ? (double)load.DatabaseMetrics.ActiveConnections / 100 // Assume max 100
            : 0;

        var riskLevel = connectionUtilization > 0.8 ? "High" :
                        connectionUtilization > 0.5 ? "Medium" : "Low";

        var suggestion = riskLevel switch
        {
            "High" => "Consider increasing max_connections or adding read replicas",
            "Medium" => "Monitor connection pool usage",
            "Low" => "Database capacity adequate"
        };

        return new DatabaseScaleAdvice
        {
            RiskLevel = riskLevel,
            Suggestion = suggestion,
            ConnectionUtilization = connectionUtilization
        };
    }

    private AutoscalerHint GenerateAutoscalerHint(CurrentLoadStatus load, LoadTrends trends)
    {
        var recommendedApi = CalculateApiInstances(load, trends);
        var recommendedWorker = CalculateWorkerInstances(load, trends);

        var yaml = $@"# Example Kubernetes HPA configuration
# WARNING: This is advisory only - not automatically applied
apiVersion: autoscaling/v2
kind: HorizontalPodAutoscaler
metadata:
  name: serialmemory-api
spec:
  scaleTargetRef:
    apiVersion: apps/v1
    kind: Deployment
    name: serialmemory-api
  minReplicas: 1
  maxReplicas: {Math.Max(recommendedApi, 5)}
  metrics:
  - type: Resource
    resource:
      name: cpu
      target:
        type: Utilization
        averageUtilization: 70
  - type: Pods
    pods:
      metric:
        name: http_requests_per_second
      target:
        type: AverageValue
        averageValue: ""{HighLoadRpsThreshold}""
---
apiVersion: autoscaling/v2
kind: HorizontalPodAutoscaler
metadata:
  name: serialmemory-worker
spec:
  scaleTargetRef:
    apiVersion: apps/v1
    kind: Deployment
    name: serialmemory-worker
  minReplicas: 1
  maxReplicas: {Math.Max(recommendedWorker, 3)}
  metrics:
  - type: External
    external:
      metric:
        name: rabbitmq_queue_messages
      target:
        type: Value
        value: ""1000""";

        return new AutoscalerHint
        {
            Platform = "kubernetes",
            ExampleConfigYaml = yaml
        };
    }
}

#region Models

public sealed class CurrentLoadStatus
{
    public DateTimeOffset Timestamp { get; set; }
    public RpsMetrics RpsMetrics { get; set; } = new();
    public LatencyMetrics LatencyMetrics { get; set; } = new();
    public QueueMetrics QueueMetrics { get; set; } = new();
    public DatabaseMetrics DatabaseMetrics { get; set; } = new();
}

public sealed class RpsMetrics
{
    public double CurrentRps { get; set; }
    public double AvgRps15Min { get; set; }
    public double PeakRps15Min { get; set; }
}

public sealed class LatencyMetrics
{
    public double P50Ms { get; set; }
    public double P95Ms { get; set; }
    public double P99Ms { get; set; }
}

public sealed class QueueMetrics
{
    public int PendingTasks { get; set; }
    public int RunningJobs { get; set; }
    public int FailedLastHour { get; set; }
}

public sealed class DatabaseMetrics
{
    public int ActiveConnections { get; set; }
    public int IdleConnections { get; set; }
    public int TotalConnections { get; set; }
    public long DatabaseSizeBytes { get; set; }
}

public sealed class LoadTrends
{
    public List<TrendDataPoint> DataPoints { get; set; } = new();
    public string TrendDirection { get; set; } = "stable";
    public double GrowthRate { get; set; }
}

public sealed class TrendDataPoint
{
    public DateTimeOffset Timestamp { get; set; }
    public double Value { get; set; }
}

public sealed class ScalePrediction
{
    public int HorizonMinutes { get; set; }
    public DateTimeOffset Timestamp { get; set; }

    public int CurrentApiInstances { get; set; }
    public int RecommendedApiInstances { get; set; }

    public int CurrentWorkerInstances { get; set; }
    public int RecommendedWorkerInstances { get; set; }

    public string ApiReason { get; set; } = "";
    public string WorkerReason { get; set; } = "";

    public DatabaseScaleAdvice Database { get; set; } = new();
    public AutoscalerHint? AutoscalerHint { get; set; }
}

public sealed class DatabaseScaleAdvice
{
    public string RiskLevel { get; set; } = "Low";
    public string Suggestion { get; set; } = "";
    public double ConnectionUtilization { get; set; }
}

public sealed class AutoscalerHint
{
    public string Platform { get; set; } = "";
    public string ExampleConfigYaml { get; set; } = "";
}

#endregion
