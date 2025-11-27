using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using SerialMemory.Core.Interfaces;

namespace SerialMemory.Infrastructure;

/// <summary>
/// PostgreSQL-backed mind health tracking service.
/// Persists confidence drift, hallucination events, and contradiction frequency.
/// </summary>
public sealed class PostgresMindHealthService : IMindHealthService
{
    private readonly string _connectionString;
    private readonly ILogger<PostgresMindHealthService> _logger;

    public PostgresMindHealthService(string connectionString, ILogger<PostgresMindHealthService> logger)
    {
        _connectionString = connectionString;
        _logger = logger;
    }

    // ==========================================
    // CONFIDENCE DRIFT
    // ==========================================

    public async Task RecordConfidenceAsync(ConfidenceObservation observation, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.ExecuteAsync(@"
            INSERT INTO mind_confidence_observations
                (id, operation_type, predicted_confidence, actual_accuracy, drift, timestamp, memory_id, context)
            VALUES
                (@Id, @OperationType, @PredictedConfidence, @ActualAccuracy, @Drift, @Timestamp, @MemoryId, @Context)",
            new
            {
                observation.Id,
                observation.OperationType,
                observation.PredictedConfidence,
                observation.ActualAccuracy,
                observation.Drift,
                observation.Timestamp,
                observation.MemoryId,
                observation.Context
            });
    }

    public async Task<ConfidenceDriftAnalysis> GetConfidenceDriftAsync(int days = 30, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        var cutoff = DateTimeOffset.UtcNow.AddDays(-days);

        var observations = await conn.QueryAsync<ConfidenceObservationRow>(@"
            SELECT operation_type, predicted_confidence, actual_accuracy, drift, timestamp
            FROM mind_confidence_observations
            WHERE timestamp >= @Cutoff AND actual_accuracy IS NOT NULL
            ORDER BY timestamp DESC",
            new { Cutoff = cutoff });

        var list = observations.ToList();
        if (list.Count == 0)
        {
            return new ConfidenceDriftAnalysis { TotalObservations = 0 };
        }

        var overallDrift = list.Average(o => o.Drift);
        var overconfident = list.Count(o => o.Drift > 0.1f);
        var underconfident = list.Count(o => o.Drift < -0.1f);

        var driftByOp = list
            .GroupBy(o => o.OperationType)
            .ToDictionary(g => g.Key, g => (float)g.Average(o => o.Drift));

        // Build calibration curve (buckets of 0.1)
        var calibrationCurve = list
            .GroupBy(o => (int)(o.PredictedConfidence * 10) / 10.0f)
            .Select(g => new ConfidenceBucket
            {
                PredictedRange = g.Key,
                ActualAccuracy = (float)g.Average(o => o.ActualAccuracy ?? 0),
                Count = g.Count()
            })
            .OrderBy(b => b.PredictedRange)
            .ToList();

        // Calibration score: how close predicted is to actual (1.0 = perfect)
        var calibrationScore = 1.0f - (float)list.Average(o => Math.Abs(o.Drift));

        return new ConfidenceDriftAnalysis
        {
            OverallDrift = (float)overallDrift,
            OverconfidenceRate = (float)overconfident / list.Count,
            UnderconfidenceRate = (float)underconfident / list.Count,
            CalibrationScore = calibrationScore,
            TotalObservations = list.Count,
            DriftByOperation = driftByOp,
            CalibrationCurve = calibrationCurve
        };
    }

    public async Task<ConfidenceTrend> GetConfidenceTrendAsync(string operationType, int days = 30, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        var cutoff = DateTimeOffset.UtcNow.AddDays(-days);

        var dailyDrift = await conn.QueryAsync<DriftPoint>(@"
            SELECT
                DATE(timestamp) as date,
                AVG(drift) as drift,
                COUNT(*) as observations
            FROM mind_confidence_observations
            WHERE operation_type = @OperationType
              AND timestamp >= @Cutoff
              AND actual_accuracy IS NOT NULL
            GROUP BY DATE(timestamp)
            ORDER BY DATE(timestamp)",
            new { OperationType = operationType, Cutoff = cutoff });

        var history = dailyDrift.ToList();
        var currentDrift = history.Count > 0 ? history.Last().Drift : 0;

        // Calculate trend direction
        float trendDirection = 0;
        if (history.Count >= 2)
        {
            var firstHalf = history.Take(history.Count / 2).Average(d => d.Drift);
            var secondHalf = history.Skip(history.Count / 2).Average(d => d.Drift);
            trendDirection = (float)(secondHalf - firstHalf);
        }

        return new ConfidenceTrend
        {
            OperationType = operationType,
            CurrentDrift = currentDrift,
            TrendDirection = trendDirection,
            History = history
        };
    }

    // ==========================================
    // HALLUCINATION TRACKING
    // ==========================================

    public async Task RecordHallucinationAsync(HallucinationEvent evt, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.ExecuteAsync(@"
            INSERT INTO mind_hallucination_events
                (id, memory_id, type, severity, description, evidence, ground_truth, detected_at, source, resolved, resolution)
            VALUES
                (@Id, @MemoryId, @Type, @Severity, @Description, @Evidence, @GroundTruth, @DetectedAt, @Source, @Resolved, @Resolution)",
            new
            {
                evt.Id,
                evt.MemoryId,
                Type = evt.Type.ToString(),
                evt.Severity,
                evt.Description,
                evt.Evidence,
                evt.GroundTruth,
                evt.DetectedAt,
                Source = evt.Source.ToString(),
                evt.Resolved,
                evt.Resolution
            });
    }

    public async Task<HallucinationAnalysis> GetHallucinationAnalysisAsync(int days = 30, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        var cutoff = DateTimeOffset.UtcNow.AddDays(-days);

        var events = await conn.QueryAsync<HallucinationEventRow>(@"
            SELECT type, severity, source, resolved, detected_at
            FROM mind_hallucination_events
            WHERE detected_at >= @Cutoff
            ORDER BY detected_at DESC",
            new { Cutoff = cutoff });

        var list = events.ToList();

        // Get total memory count for rate calculation
        var totalMemories = await conn.ExecuteScalarAsync<int>(@"
            SELECT COUNT(*) FROM memories WHERE created_at >= @Cutoff",
            new { Cutoff = cutoff });

        var rate = totalMemories > 0 ? (float)list.Count / totalMemories : 0;

        var byType = list
            .GroupBy(e => Enum.Parse<HallucinationType>(e.Type))
            .ToDictionary(g => g.Key, g => g.Count());

        var bySource = list
            .GroupBy(e => Enum.Parse<HallucinationSource>(e.Source))
            .ToDictionary(g => g.Key, g => g.Count());

        var trend = list
            .GroupBy(e => e.DetectedAt.Date)
            .Select(g => new HallucinationTrendPoint
            {
                Date = g.Key,
                Rate = totalMemories > 0 ? (float)g.Count() / totalMemories : 0,
                Count = g.Count()
            })
            .OrderBy(t => t.Date)
            .ToList();

        return new HallucinationAnalysis
        {
            HallucinationRate = rate,
            TotalEvents = list.Count,
            ResolvedCount = list.Count(e => e.Resolved),
            AvgSeverity = list.Count > 0 ? (float)list.Average(e => e.Severity) : 0,
            ByType = byType,
            BySource = bySource,
            Trend = trend
        };
    }

    public async Task FlagAsHallucinationAsync(Guid memoryId, string reason, float severity, CancellationToken ct = default)
    {
        var evt = new HallucinationEvent
        {
            MemoryId = memoryId,
            Type = HallucinationType.FactualError,
            Severity = severity,
            Description = reason,
            Source = HallucinationSource.UserReported
        };

        await RecordHallucinationAsync(evt, ct);

        // Also update memory confidence
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.ExecuteAsync(@"
            UPDATE memories
            SET confidence = confidence * (1 - @Severity),
                metadata = COALESCE(metadata, '{}'::jsonb) || jsonb_build_object('hallucination_flagged', true)
            WHERE id = @MemoryId",
            new { MemoryId = memoryId, Severity = severity });
    }

    // ==========================================
    // CONTRADICTION TRACKING
    // ==========================================

    public async Task RecordContradictionAsync(ContradictionEvent evt, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.ExecuteAsync(@"
            INSERT INTO mind_contradiction_events
                (id, memory_a, memory_b, type, severity, description, similarity_score, detected_at, status, resolution_strategy, resolution_rationale, winner_id, merged_into_id, resolved_at)
            VALUES
                (@Id, @MemoryA, @MemoryB, @Type, @Severity, @Description, @SimilarityScore, @DetectedAt, @Status, @ResolutionStrategy, @ResolutionRationale, @WinnerId, @MergedIntoId, @ResolvedAt)",
            new
            {
                evt.Id,
                evt.MemoryA,
                evt.MemoryB,
                Type = evt.Type.ToString(),
                evt.Severity,
                evt.Description,
                evt.SimilarityScore,
                evt.DetectedAt,
                Status = evt.Status.ToString(),
                ResolutionStrategy = evt.Resolution?.Strategy,
                ResolutionRationale = evt.Resolution?.Rationale,
                WinnerId = evt.Resolution?.WinnerId,
                MergedIntoId = evt.Resolution?.MergedIntoId,
                evt.ResolvedAt
            });
    }

    public async Task<ContradictionAnalysis> GetContradictionAnalysisAsync(int days = 30, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        var cutoff = DateTimeOffset.UtcNow.AddDays(-days);

        var events = await conn.QueryAsync<ContradictionEventRow>(@"
            SELECT type, severity, status, detected_at, resolved_at
            FROM mind_contradiction_events
            WHERE detected_at >= @Cutoff
            ORDER BY detected_at DESC",
            new { Cutoff = cutoff });

        var list = events.ToList();

        var totalMemories = await conn.ExecuteScalarAsync<int>(@"
            SELECT COUNT(*) FROM memories WHERE created_at >= @Cutoff",
            new { Cutoff = cutoff });

        var rate = totalMemories > 0 ? (float)list.Count / totalMemories : 0;
        var resolved = list.Where(e => e.Status == "Resolved").ToList();
        var resolutionRate = list.Count > 0 ? (float)resolved.Count / list.Count : 0;

        var avgTimeToResolve = resolved.Count > 0
            ? TimeSpan.FromTicks((long)resolved
                .Where(e => e.ResolvedAt.HasValue)
                .Average(e => (e.ResolvedAt!.Value - e.DetectedAt).Ticks))
            : TimeSpan.Zero;

        var byType = list
            .GroupBy(e => Enum.Parse<ContradictionType>(e.Type))
            .ToDictionary(g => g.Key, g => g.Count());

        var trend = list
            .GroupBy(e => e.DetectedAt.Date)
            .Select(g => new ContradictionTrendPoint
            {
                Date = g.Key,
                Rate = totalMemories > 0 ? (float)g.Count() / totalMemories : 0,
                Detected = g.Count(),
                Resolved = g.Count(e => e.Status == "Resolved")
            })
            .OrderBy(t => t.Date)
            .ToList();

        return new ContradictionAnalysis
        {
            ContradictionRate = rate,
            TotalContradictions = list.Count,
            UnresolvedCount = list.Count(e => e.Status != "Resolved"),
            AvgSeverity = list.Count > 0 ? (float)list.Average(e => e.Severity) : 0,
            ResolutionRate = resolutionRate,
            AvgTimeToResolve = avgTimeToResolve,
            ByType = byType,
            Trend = trend
        };
    }

    public async Task<IReadOnlyList<ContradictionEvent>> GetUnresolvedContradictionsAsync(int limit = 100, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        var rows = await conn.QueryAsync<ContradictionEventRow>(@"
            SELECT id, memory_a, memory_b, type, severity, description, similarity_score, detected_at, status
            FROM mind_contradiction_events
            WHERE status != 'Resolved'
            ORDER BY severity DESC, detected_at DESC
            LIMIT @Limit",
            new { Limit = limit });

        return rows.Select(r => new ContradictionEvent
        {
            Id = r.Id,
            MemoryA = r.MemoryA,
            MemoryB = r.MemoryB,
            Type = Enum.Parse<ContradictionType>(r.Type),
            Severity = r.Severity,
            Description = r.Description,
            SimilarityScore = r.SimilarityScore,
            DetectedAt = r.DetectedAt,
            Status = Enum.Parse<ContradictionStatus>(r.Status)
        }).ToList();
    }

    // ==========================================
    // AGGREGATE MIND HEALTH
    // ==========================================

    public async Task<MindHealthSnapshot> GetHealthSnapshotAsync(CancellationToken ct = default)
    {
        var confidenceAnalysis = await GetConfidenceDriftAsync(7, ct);
        var hallucinationAnalysis = await GetHallucinationAnalysisAsync(7, ct);
        var contradictionAnalysis = await GetContradictionAnalysisAsync(7, ct);

        // Calculate component scores (0-1, higher is better)
        var confidenceScore = confidenceAnalysis.CalibrationScore;
        var hallucinationScore = 1.0f - Math.Min(hallucinationAnalysis.HallucinationRate * 10, 1.0f);
        var contradictionScore = 1.0f - Math.Min(contradictionAnalysis.ContradictionRate * 10, 1.0f);

        // Weighted overall score
        var overallScore = (confidenceScore * 0.4f + hallucinationScore * 0.35f + contradictionScore * 0.25f);

        var status = overallScore switch
        {
            >= 0.9f => HealthStatus.Excellent,
            >= 0.75f => HealthStatus.Good,
            >= 0.5f => HealthStatus.Fair,
            >= 0.25f => HealthStatus.Degraded,
            _ => HealthStatus.Critical
        };

        var alerts = new List<HealthAlert>();

        // Generate alerts
        if (confidenceAnalysis.OverconfidenceRate > 0.3f)
        {
            alerts.Add(new HealthAlert
            {
                Severity = AlertSeverity.Warning,
                Category = "Confidence",
                Message = $"High overconfidence rate: {confidenceAnalysis.OverconfidenceRate:P0}",
                Recommendation = "Review high-confidence predictions for accuracy"
            });
        }

        if (hallucinationAnalysis.HallucinationRate > 0.05f)
        {
            alerts.Add(new HealthAlert
            {
                Severity = AlertSeverity.Error,
                Category = "Hallucination",
                Message = $"Elevated hallucination rate: {hallucinationAnalysis.HallucinationRate:P1}",
                Recommendation = "Increase validation checks and source verification"
            });
        }

        if (contradictionAnalysis.UnresolvedCount > 10)
        {
            alerts.Add(new HealthAlert
            {
                Severity = AlertSeverity.Warning,
                Category = "Contradiction",
                Message = $"{contradictionAnalysis.UnresolvedCount} unresolved contradictions",
                Recommendation = "Review and resolve contradicting memories"
            });
        }

        return new MindHealthSnapshot
        {
            OverallScore = overallScore,
            Status = status,
            ConfidenceCalibration = confidenceScore,
            HallucinationRate = hallucinationAnalysis.HallucinationRate,
            ContradictionRate = contradictionAnalysis.ContradictionRate,
            ActiveIssues = contradictionAnalysis.UnresolvedCount + hallucinationAnalysis.TotalEvents - hallucinationAnalysis.ResolvedCount,
            Alerts = alerts
        };
    }

    public async Task<MindHealthTrends> GetHealthTrendsAsync(int days = 30, CancellationToken ct = default)
    {
        var dailyScores = await GetDailyScoresAsync(days, ct);

        var trends = new MindHealthTrends
        {
            DailyScores = dailyScores.ToList(),
            OverallTrend = CalculateTrend(dailyScores.Select(d => d.OverallScore)),
            ConfidenceTrend = CalculateTrend(dailyScores.Select(d => d.ConfidenceScore)),
            HallucinationTrend = CalculateTrend(dailyScores.Select(d => d.HallucinationScore)),
            ContradictionTrend = CalculateTrend(dailyScores.Select(d => d.ContradictionScore))
        };

        return trends;
    }

    public async Task<IReadOnlyList<DailyHealthScore>> GetDailyScoresAsync(int days = 30, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        var cutoff = DateTimeOffset.UtcNow.AddDays(-days);

        var scores = await conn.QueryAsync<DailyHealthScoreRow>(@"
            SELECT
                date,
                overall_score,
                confidence_score,
                hallucination_score,
                contradiction_score,
                observations
            FROM mind_daily_scores
            WHERE date >= @Cutoff
            ORDER BY date",
            new { Cutoff = cutoff.Date });

        return scores.Select(r => new DailyHealthScore
        {
            Date = DateOnly.FromDateTime(r.Date),
            OverallScore = r.OverallScore,
            ConfidenceScore = r.ConfidenceScore,
            HallucinationScore = r.HallucinationScore,
            ContradictionScore = r.ContradictionScore,
            Observations = r.Observations
        }).ToList();
    }

    private static TrendDirection CalculateTrend(IEnumerable<float> values)
    {
        var list = values.ToList();
        if (list.Count < 3) return TrendDirection.Stable;

        var firstThird = list.Take(list.Count / 3).Average();
        var lastThird = list.Skip(2 * list.Count / 3).Average();
        var diff = lastThird - firstThird;

        // Check volatility
        var stdDev = Math.Sqrt(list.Average(v => Math.Pow(v - list.Average(), 2)));
        if (stdDev > 0.15) return TrendDirection.Volatile;

        return diff switch
        {
            > 0.05f => TrendDirection.Improving,
            < -0.05f => TrendDirection.Declining,
            _ => TrendDirection.Stable
        };
    }

    // Row types for Dapper
    private sealed class ConfidenceObservationRow
    {
        public string OperationType { get; init; } = "";
        public float PredictedConfidence { get; init; }
        public float? ActualAccuracy { get; init; }
        public float Drift { get; init; }
        public DateTimeOffset Timestamp { get; init; }
    }

    private sealed class HallucinationEventRow
    {
        public string Type { get; init; } = "";
        public float Severity { get; init; }
        public string Source { get; init; } = "";
        public bool Resolved { get; init; }
        public DateTimeOffset DetectedAt { get; init; }
    }

    private sealed class ContradictionEventRow
    {
        public Guid Id { get; init; }
        public Guid MemoryA { get; init; }
        public Guid MemoryB { get; init; }
        public string Type { get; init; } = "";
        public float Severity { get; init; }
        public string Description { get; init; } = "";
        public float SimilarityScore { get; init; }
        public DateTimeOffset DetectedAt { get; init; }
        public string Status { get; init; } = "";
        public DateTimeOffset? ResolvedAt { get; init; }
    }

    private sealed class DailyHealthScoreRow
    {
        public DateTime Date { get; init; }
        public float OverallScore { get; init; }
        public float ConfidenceScore { get; init; }
        public float HallucinationScore { get; init; }
        public float ContradictionScore { get; init; }
        public int Observations { get; init; }
    }
}
