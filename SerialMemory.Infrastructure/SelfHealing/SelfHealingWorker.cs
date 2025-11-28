using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using SerialMemory.Core.Interfaces;

namespace SerialMemory.Infrastructure.SelfHealing;

/// <summary>
/// Background worker for self-healing memory operations.
/// Runs periodic scans and reacts to integrity events.
/// IMPORTANT: Detection only by default - no silent mutations.
/// </summary>
public sealed class SelfHealingWorker : BackgroundService, ISelfHealingTrigger
{
    private readonly IMemorySelfHealing _selfHealing;
    private readonly IAnomalyDetectionService? _anomalyDetector;
    private readonly IHealingRecommendationService? _healingRecommendations;
    private readonly ILogger<SelfHealingWorker> _logger;
    private readonly string _connectionString;
    private readonly TimeSpan _scanInterval;
    private readonly bool _autoRepairEnabled;
    private readonly bool _mlAnalysisEnabled;
    private readonly string _workerId;

    public SelfHealingWorker(
        IMemorySelfHealing selfHealing,
        IConfiguration configuration,
        ILogger<SelfHealingWorker> logger,
        IAnomalyDetectionService? anomalyDetector = null,
        IHealingRecommendationService? healingRecommendations = null)
    {
        _selfHealing = selfHealing;
        _anomalyDetector = anomalyDetector;
        _healingRecommendations = healingRecommendations;
        _logger = logger;
        _connectionString = configuration.GetConnectionString("Postgres")
            ?? BuildConnectionString();

        // Default: 5 minute scan interval
        _scanInterval = TimeSpan.FromMinutes(
            int.TryParse(configuration["SelfHealing:ScanIntervalMinutes"], out var mins) ? mins : 5);

        // Default: auto-repair DISABLED (detection only)
        _autoRepairEnabled = bool.TryParse(configuration["SelfHealing:AutoRepairEnabled"], out var enabled) && enabled;

        // ML-style analysis enabled if services are available
        _mlAnalysisEnabled = bool.TryParse(configuration["SelfHealing:MLAnalysisEnabled"], out var mlEnabled) && mlEnabled;

        _workerId = $"worker-{Environment.MachineName}-{Environment.ProcessId}";

        _logger.LogInformation(
            "SelfHealingWorker initialized: Interval={Interval}m, AutoRepair={AutoRepair}, MLAnalysis={MLAnalysis}, WorkerId={WorkerId}",
            _scanInterval.TotalMinutes, _autoRepairEnabled, _mlAnalysisEnabled && _anomalyDetector != null, _workerId);
    }

    private static string BuildConnectionString()
    {
        var host = Environment.GetEnvironmentVariable("POSTGRES_HOST") ?? "localhost";
        var port = Environment.GetEnvironmentVariable("POSTGRES_PORT") ?? "5434";
        var user = Environment.GetEnvironmentVariable("POSTGRES_USER") ?? "postgres";
        var password = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD") ?? "postgres";
        var database = Environment.GetEnvironmentVariable("POSTGRES_DB") ?? "contextdb";
        return $"Host={host};Port={port};Username={user};Password={password};Database={database}";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SelfHealingWorker starting periodic scans");

        // Initial delay to let system stabilize
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunScheduledScanAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Self-healing scheduled scan failed");
                await LogScanEventAsync("scheduled_scan_failed", "error", ex.Message, stoppingToken);
            }

            await Task.Delay(_scanInterval, stoppingToken);
        }

        _logger.LogInformation("SelfHealingWorker stopped");
    }

    /// <summary>
    /// Runs a scheduled detection-only scan.
    /// </summary>
    private async Task RunScheduledScanAsync(CancellationToken cancellationToken)
    {
        var scanId = Guid.CreateVersion7();
        var startTime = DateTime.UtcNow;

        _logger.LogInformation("Starting scheduled self-healing scan {ScanId}", scanId);
        await LogScanEventAsync("scheduled_scan_started", "info", $"Scan {scanId} started", cancellationToken);

        // Detection-only options (no mutations)
        var options = new SelfHealingOptions(
            DetectContradictions: true,
            MergeDuplicates: false,  // Detection only - never auto-merge
            ApplyDecay: false,       // Detection only - never auto-decay
            ReinforceStable: false,  // Detection only - never auto-reinforce
            SimilarityThreshold: 0.85f,
            DecayMaxAge: TimeSpan.FromDays(30),
            DecayFactor: 0.9f,
            MaxOperationsPerCycle: 100);

        var result = await _selfHealing.RunCycleAsync(options, cancellationToken);

        // Log findings to self_healing_logs and security_events
        await LogScanResultAsync(scanId, result, cancellationToken);

        // Check for integrity issues and log to security_events
        if (result.ContradictionsDetected > 0)
        {
            await LogSecurityEventAsync(
                "memory_integrity",
                "contradiction_detected",
                "warning",
                $"Detected {result.ContradictionsDetected} memory contradictions",
                new { scanId, result.ContradictionsDetected, result.Operations },
                cancellationToken);
        }

        _logger.LogInformation(
            "Scheduled scan {ScanId} completed in {Duration}ms: {Contradictions} contradictions detected",
            scanId, result.Duration.TotalMilliseconds, result.ContradictionsDetected);

        await LogScanEventAsync("scheduled_scan_completed", "info",
            $"Scan {scanId}: {result.ContradictionsDetected} contradictions detected in {result.Duration.TotalMilliseconds}ms",
            cancellationToken);

        // Run ML-style anomaly analysis if enabled
        if (_mlAnalysisEnabled && _anomalyDetector != null && _healingRecommendations != null)
        {
            await RunMLAnalysisAsync(scanId, cancellationToken);
        }
    }

    /// <summary>
    /// Runs ML-style anomaly detection and generates healing recommendations.
    /// Does NOT auto-apply recommendations - just generates them for review.
    /// </summary>
    private async Task RunMLAnalysisAsync(Guid scanId, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Starting ML anomaly analysis for scan {ScanId}", scanId);
            var mlStartTime = DateTime.UtcNow;

            // Generate healing recommendations (detection only)
            var recommendations = await _healingRecommendations!.AnalyzeAndRecommendAsync(
                tenantId: null, // Run for all tenants in background
                maxRecommendations: 50,
                cancellationToken);

            var mlDuration = DateTime.UtcNow - mlStartTime;

            _logger.LogInformation(
                "ML analysis completed for scan {ScanId}: {RecommendationCount} recommendations generated in {Duration}ms",
                scanId, recommendations.Count, mlDuration.TotalMilliseconds);

            if (recommendations.Count > 0)
            {
                // Log to security events for audit trail
                await LogSecurityEventAsync(
                    "self_healing",
                    "ml_recommendations_generated",
                    "info",
                    $"Generated {recommendations.Count} healing recommendations",
                    new
                    {
                        scanId,
                        count = recommendations.Count,
                        byType = recommendations.GroupBy(r => r.AnomalyType).ToDictionary(g => g.Key, g => g.Count()),
                        byAction = recommendations.GroupBy(r => r.RecommendedAction).ToDictionary(g => g.Key, g => g.Count()),
                        avgConfidence = recommendations.Average(r => r.Confidence),
                        avgSeverity = recommendations.Average(r => r.Severity)
                    },
                    cancellationToken);
            }

            await LogScanEventAsync("ml_analysis_completed", "info",
                $"ML analysis for scan {scanId}: {recommendations.Count} recommendations in {mlDuration.TotalMilliseconds}ms",
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ML anomaly analysis failed for scan {ScanId}", scanId);
            await LogScanEventAsync("ml_analysis_failed", "error", ex.Message, cancellationToken);
        }
    }

    /// <summary>
    /// Reactive scan triggered by external events (conflicts, integrity failures, hallucinations).
    /// </summary>
    public async Task<SelfHealingResult> ReactiveScanAsync(
        string triggerType,
        Guid? relatedMemoryId = null,
        CancellationToken cancellationToken = default)
    {
        var scanId = Guid.CreateVersion7();

        _logger.LogInformation(
            "Starting reactive self-healing scan {ScanId} triggered by {TriggerType}",
            scanId, triggerType);

        await LogScanEventAsync("reactive_scan_started", "info",
            $"Scan {scanId} triggered by {triggerType}", cancellationToken);

        // Detection-only options
        var options = new SelfHealingOptions(
            DetectContradictions: true,
            MergeDuplicates: false,
            ApplyDecay: false,
            ReinforceStable: false,
            SimilarityThreshold: 0.85f,
            DecayMaxAge: TimeSpan.FromDays(30),
            DecayFactor: 0.9f,
            MaxOperationsPerCycle: 50);

        var result = await _selfHealing.RunCycleAsync(options, cancellationToken);

        // Log results
        await LogScanResultAsync(scanId, result, cancellationToken);

        // If triggered by specific memory, focus detection there
        if (relatedMemoryId.HasValue)
        {
            var contradictions = await _selfHealing.DetectContradictionsAsync(relatedMemoryId.Value, cancellationToken);
            if (contradictions.Any())
            {
                await LogSecurityEventAsync(
                    "memory_integrity",
                    "targeted_contradiction_detected",
                    "warning",
                    $"Memory {relatedMemoryId} has {contradictions.Count} contradictions",
                    new { scanId, relatedMemoryId, contradictions = contradictions.Select(c => c.ContradictionId) },
                    cancellationToken);
            }
        }

        _logger.LogInformation(
            "Reactive scan {ScanId} completed in {Duration}ms: {Contradictions} contradictions",
            scanId, result.Duration.TotalMilliseconds, result.ContradictionsDetected);

        return result;
    }

    // ISelfHealingTrigger implementation

    /// <summary>
    /// Triggers a reactive scan due to memory conflict.
    /// </summary>
    public async Task TriggerOnConflictAsync(Guid memoryId, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Triggering reactive scan for memory conflict: {MemoryId}", memoryId);
        await ReactiveScanAsync("memory_conflict", memoryId, cancellationToken);
    }

    /// <summary>
    /// Triggers a reactive scan due to integrity failure.
    /// </summary>
    public async Task TriggerOnIntegrityFailureAsync(Guid memoryId, string failureType, CancellationToken cancellationToken = default)
    {
        _logger.LogWarning("Triggering reactive scan for integrity failure: {MemoryId}, Type: {FailureType}", memoryId, failureType);
        await LogSecurityEventAsync("memory_integrity", "integrity_failure_trigger", "warning",
            $"Integrity failure detected: {failureType}", new { memoryId, failureType }, cancellationToken);
        await ReactiveScanAsync($"integrity_failure_{failureType}", memoryId, cancellationToken);
    }

    /// <summary>
    /// Triggers a reactive scan due to hallucination detection.
    /// </summary>
    public async Task TriggerOnHallucinationAsync(Guid memoryId, float confidence, CancellationToken cancellationToken = default)
    {
        _logger.LogWarning("Triggering reactive scan for hallucination: {MemoryId}, Confidence: {Confidence}", memoryId, confidence);
        await LogSecurityEventAsync("memory_integrity", "hallucination_trigger", "warning",
            $"Potential hallucination detected with confidence {confidence:F2}", new { memoryId, confidence }, cancellationToken);
        await ReactiveScanAsync("hallucination_detected", memoryId, cancellationToken);
    }

    async Task<IReadOnlyList<HealingScanResult>> ISelfHealingTrigger.GetRecentScansAsync(int limit, CancellationToken cancellationToken)
    {
        var results = await GetRecentScansAsync(limit, cancellationToken);
        return results.Select(r => new HealingScanResult(
            r.Id, r.OperationType, r.Result, r.DurationMs, r.WorkerId, r.CreatedAt
        )).ToList();
    }

    async Task<IReadOnlyList<HealingContradictionResult>> ISelfHealingTrigger.GetUnresolvedContradictionsAsync(int limit, CancellationToken cancellationToken)
    {
        var results = await GetUnresolvedContradictionsAsync(limit, cancellationToken);
        return results.Select(r => new HealingContradictionResult(
            r.ContradictionId, r.MemoryIdA, r.MemoryIdB, r.ContradictionType, r.Confidence, r.DetectedAt, r.DetectionMethod
        )).ToList();
    }

    async Task<HealingStatsResult> ISelfHealingTrigger.GetStatsAsync(CancellationToken cancellationToken)
    {
        var stats = await GetStatsAsync(cancellationToken);
        return new HealingStatsResult(
            stats.UnresolvedContradictions,
            stats.ResolvedContradictions,
            stats.TotalMerges,
            stats.ScansLast24Hours,
            stats.LastScanAt,
            stats.AutoRepairEnabled
        );
    }

    /// <summary>
    /// Gets recent scan results for dashboard display.
    /// </summary>
    public async Task<IReadOnlyList<ScanSummary>> GetRecentScansAsync(
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        const string sql = """
            SELECT
                id,
                operation_type,
                operation_result,
                duration_ms,
                worker_id,
                created_at,
                details
            FROM self_healing_logs
            WHERE operation_type IN ('scheduled_scan', 'reactive_scan', 'detect_contradiction')
            ORDER BY created_at DESC
            LIMIT @Limit
            """;

        var rows = await conn.QueryAsync<ScanLogRow>(sql, new { Limit = limit });

        return rows.Select(r => new ScanSummary(
            r.id,
            r.operation_type,
            r.operation_result,
            r.duration_ms,
            r.worker_id,
            r.created_at,
            r.details != null ? JsonSerializer.Deserialize<Dictionary<string, object>>(r.details) : null
        )).ToList();
    }

    /// <summary>
    /// Gets unresolved contradictions for dashboard.
    /// </summary>
    public async Task<IReadOnlyList<ContradictionSummary>> GetUnresolvedContradictionsAsync(
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        const string sql = """
            SELECT
                contradiction_id,
                memory_id_a,
                memory_id_b,
                contradiction_type,
                confidence,
                detected_at,
                detection_method
            FROM memory_contradictions
            WHERE resolved_at IS NULL
            ORDER BY detected_at DESC
            LIMIT @Limit
            """;

        var rows = await conn.QueryAsync<ContradictionRow>(sql, new { Limit = limit });

        return rows.Select(r => new ContradictionSummary(
            r.contradiction_id,
            r.memory_id_a,
            r.memory_id_b,
            r.contradiction_type,
            r.confidence,
            r.detected_at,
            r.detection_method
        )).ToList();
    }

    /// <summary>
    /// Gets self-healing statistics for dashboard.
    /// </summary>
    public async Task<SelfHealingStats> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        var stats = await conn.QueryFirstOrDefaultAsync<StatsRow>("""
            SELECT
                (SELECT COUNT(*) FROM memory_contradictions WHERE resolved_at IS NULL) as unresolved_contradictions,
                (SELECT COUNT(*) FROM memory_contradictions WHERE resolved_at IS NOT NULL) as resolved_contradictions,
                (SELECT COUNT(*) FROM memory_merges) as total_merges,
                (SELECT COUNT(*) FROM self_healing_logs WHERE created_at > NOW() - INTERVAL '24 hours') as scans_last_24h,
                (SELECT MAX(created_at) FROM self_healing_logs) as last_scan_at
            """);

        return new SelfHealingStats(
            stats?.unresolved_contradictions ?? 0,
            stats?.resolved_contradictions ?? 0,
            stats?.total_merges ?? 0,
            stats?.scans_last_24h ?? 0,
            stats?.last_scan_at,
            _autoRepairEnabled
        );
    }

    private async Task LogScanResultAsync(
        Guid scanId,
        SelfHealingResult result,
        CancellationToken cancellationToken)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        const string sql = """
            INSERT INTO self_healing_logs (
                operation_type, target_memory_ids, result_memory_id,
                operation_result, details, duration_ms, worker_id
            ) VALUES (
                @OperationType, @TargetMemoryIds, NULL,
                @OperationResult, @Details::jsonb, @DurationMs, @WorkerId
            )
            """;

        await conn.ExecuteAsync(sql, new
        {
            OperationType = "scheduled_scan",
            TargetMemoryIds = result.Operations.SelectMany(o => o.TargetMemoryIds).Distinct().ToArray(),
            OperationResult = $"detected_{result.ContradictionsDetected}_contradictions",
            Details = JsonSerializer.Serialize(new
            {
                scanId,
                result.ContradictionsDetected,
                result.MemoriesMerged,
                result.MemoriesDecayed,
                result.MemoriesReinforced,
                operationCount = result.Operations.Count
            }),
            DurationMs = (int)result.Duration.TotalMilliseconds,
            WorkerId = _workerId
        });
    }

    private async Task LogScanEventAsync(
        string eventType,
        string severity,
        string message,
        CancellationToken cancellationToken)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        const string sql = """
            INSERT INTO self_healing_logs (
                operation_type, target_memory_ids, operation_result,
                details, duration_ms, worker_id
            ) VALUES (
                @OperationType, @TargetMemoryIds, @OperationResult,
                @Details::jsonb, 0, @WorkerId
            )
            """;

        await conn.ExecuteAsync(sql, new
        {
            OperationType = eventType,
            TargetMemoryIds = Array.Empty<Guid>(),
            OperationResult = severity,
            Details = JsonSerializer.Serialize(new { message, timestamp = DateTime.UtcNow }),
            WorkerId = _workerId
        });
    }

    private async Task LogSecurityEventAsync(
        string category,
        string eventType,
        string severity,
        string message,
        object? metadata,
        CancellationToken cancellationToken)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        const string sql = """
            INSERT INTO security_events (
                id, event_type, category, severity, message, metadata, created_at
            ) VALUES (
                @Id, @EventType, @Category, @Severity, @Message, @Metadata::jsonb, NOW()
            )
            """;

        await conn.ExecuteAsync(sql, new
        {
            Id = Guid.CreateVersion7(),
            EventType = eventType,
            Category = category,
            Severity = severity,
            Message = message,
            Metadata = metadata != null ? JsonSerializer.Serialize(metadata) : "{}"
        });
    }

    // DTOs
    private sealed class ScanLogRow
    {
        public Guid id { get; set; }
        public string operation_type { get; set; } = "";
        public string operation_result { get; set; } = "";
        public int duration_ms { get; set; }
        public string worker_id { get; set; } = "";
        public DateTime created_at { get; set; }
        public string? details { get; set; }
    }

    private sealed class ContradictionRow
    {
        public Guid contradiction_id { get; set; }
        public Guid memory_id_a { get; set; }
        public Guid memory_id_b { get; set; }
        public string contradiction_type { get; set; } = "";
        public float confidence { get; set; }
        public DateTime detected_at { get; set; }
        public string detection_method { get; set; } = "";
    }

    private sealed class StatsRow
    {
        public int unresolved_contradictions { get; set; }
        public int resolved_contradictions { get; set; }
        public int total_merges { get; set; }
        public int scans_last_24h { get; set; }
        public DateTime? last_scan_at { get; set; }
    }
}

// Public DTOs for dashboard integration
public sealed record ScanSummary(
    Guid Id,
    string OperationType,
    string Result,
    int DurationMs,
    string WorkerId,
    DateTime CreatedAt,
    Dictionary<string, object>? Details);

public sealed record ContradictionSummary(
    Guid ContradictionId,
    Guid MemoryIdA,
    Guid MemoryIdB,
    string ContradictionType,
    float Confidence,
    DateTime DetectedAt,
    string DetectionMethod);

public sealed record SelfHealingStats(
    int UnresolvedContradictions,
    int ResolvedContradictions,
    int TotalMerges,
    int ScansLast24Hours,
    DateTime? LastScanAt,
    bool AutoRepairEnabled);
