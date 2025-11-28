using Dapper;
using Npgsql;

namespace SerialMemory.Api.Analysis;

/// <summary>
/// Predictive Forward Simulation - READ-ONLY analysis of future state.
/// Computes memory growth trends, conflict likelihood, hallucination risk, and graph expansion.
/// </summary>
public interface IPredictiveSimulationService
{
    Task<PredictedState> PredictAsync(PredictionRequest request, CancellationToken ct = default);
}

public sealed class PredictiveSimulationService : IPredictiveSimulationService
{
    private readonly string _connectionString;
    private readonly ILogger<PredictiveSimulationService> _logger;

    public PredictiveSimulationService(string connectionString, ILogger<PredictiveSimulationService> logger)
    {
        _connectionString = connectionString;
        _logger = logger;
    }

    public async Task<PredictedState> PredictAsync(PredictionRequest request, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        // Gather current statistics
        var currentStats = await GetCurrentStatsAsync(conn, ct);

        // Calculate growth trends
        var growthTrend = await CalculateGrowthTrendAsync(conn, request.HorizonMinutes, ct);

        // Predict conflict likelihood
        var conflictPrediction = await PredictConflictsAsync(conn, ct);

        // Predict hallucination risk
        var hallucinationRisk = await PredictHallucinationRiskAsync(conn, ct);

        // Predict graph expansion
        var graphExpansion = await PredictGraphExpansionAsync(conn, request.HorizonMinutes, ct);

        // Generate predicted nodes/edges for visualization
        var predictedGraph = GeneratePredictedGraph(currentStats, growthTrend, request.HorizonMinutes);

        sw.Stop();

        return new PredictedState
        {
            HorizonMinutes = request.HorizonMinutes,
            Timestamp = DateTimeOffset.UtcNow,
            ProcessingTimeMs = sw.ElapsedMilliseconds,
            CurrentStats = currentStats,
            GrowthTrend = growthTrend,
            ConflictPrediction = conflictPrediction,
            HallucinationRisk = hallucinationRisk,
            GraphExpansion = graphExpansion,
            PredictedGraph = predictedGraph
        };
    }

    private async Task<CurrentStats> GetCurrentStatsAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var stats = await conn.QueryFirstOrDefaultAsync<(
            int MemoryCount,
            int EntityCount,
            int RelationshipCount,
            int ActiveMemories,
            double AvgConfidence)>(@"
            SELECT
                (SELECT COUNT(*) FROM memories)::int AS ""MemoryCount"",
                (SELECT COUNT(*) FROM entities)::int AS ""EntityCount"",
                (SELECT COUNT(*) FROM entity_relationships)::int AS ""RelationshipCount"",
                (SELECT COUNT(*) FROM memories WHERE is_active = true)::int AS ""ActiveMemories"",
                (SELECT COALESCE(AVG(confidence_score), 0) FROM memories WHERE is_active = true) AS ""AvgConfidence""
        ");

        return new CurrentStats
        {
            TotalMemories = stats.MemoryCount,
            TotalEntities = stats.EntityCount,
            TotalRelationships = stats.RelationshipCount,
            ActiveMemories = stats.ActiveMemories,
            AverageConfidence = stats.AvgConfidence
        };
    }

    private async Task<GrowthTrend> CalculateGrowthTrendAsync(NpgsqlConnection conn, int horizonMinutes, CancellationToken ct)
    {
        // Get memory creation rate over last 24 hours
        var creationRate = await conn.QueryAsync<(DateTimeOffset Hour, int Count)>(@"
            SELECT
                date_trunc('hour', created_at) AS ""Hour"",
                COUNT(*)::int AS ""Count""
            FROM memories
            WHERE created_at > NOW() - INTERVAL '24 hours'
            GROUP BY date_trunc('hour', created_at)
            ORDER BY ""Hour"" DESC");

        var dataPoints = creationRate.ToList();

        // Calculate average rate
        var avgPerHour = dataPoints.Count > 0 ? dataPoints.Average(d => d.Count) : 0;

        // Calculate trend (compare recent vs older)
        double trendSlope = 0;
        if (dataPoints.Count >= 4)
        {
            var recent = dataPoints.Take(4).Average(d => d.Count);
            var older = dataPoints.Skip(4).Take(4).DefaultIfEmpty().Average(d => d.Count);
            trendSlope = older > 0 ? (recent - older) / older : 0;
        }

        // Project forward
        var hoursAhead = horizonMinutes / 60.0;
        var projectedNew = avgPerHour * hoursAhead * (1 + trendSlope);

        return new GrowthTrend
        {
            MemoriesPerHour = avgPerHour,
            TrendSlope = trendSlope,
            ProjectedNewMemories = (int)Math.Max(0, projectedNew),
            Confidence = dataPoints.Count >= 12 ? 0.8 : dataPoints.Count >= 6 ? 0.5 : 0.3
        };
    }

    private async Task<ConflictPrediction> PredictConflictsAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        // Get current conflict metrics
        var conflictData = await conn.QueryFirstOrDefaultAsync<(int Current, int Recent, double Rate)>(@"
            SELECT
                (SELECT COUNT(*) FROM memory_conflicts WHERE resolved_at IS NULL)::int AS ""Current"",
                (SELECT COUNT(*) FROM memory_conflicts WHERE created_at > NOW() - INTERVAL '24 hours')::int AS ""Recent"",
                (SELECT COUNT(*)::float / NULLIF((SELECT COUNT(*) FROM memories WHERE created_at > NOW() - INTERVAL '24 hours'), 0)
                 FROM memory_conflicts WHERE created_at > NOW() - INTERVAL '24 hours') AS ""Rate""
        ");

        // Get contradiction count
        var contradictionCount = await conn.ExecuteScalarAsync<int>(@"
            SELECT COUNT(*) FROM memory_contradictions WHERE is_resolved = false");

        // Calculate likelihood based on current rate and patterns
        var conflictRate = conflictData.Rate > 0 ? conflictData.Rate : 0.01; // Assume 1% baseline
        var likelihood = Math.Min(1.0, conflictRate * 2); // Double the current rate as prediction

        return new ConflictPrediction
        {
            CurrentUnresolvedConflicts = conflictData.Current,
            RecentConflicts24h = conflictData.Recent,
            ConflictRate = conflictRate,
            PredictedLikelihood = likelihood,
            ActiveContradictions = contradictionCount,
            RiskLevel = likelihood > 0.3 ? "High" : likelihood > 0.1 ? "Medium" : "Low"
        };
    }

    private async Task<HallucinationRisk> PredictHallucinationRiskAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        // Get hallucination metrics
        var hallucinationData = await conn.QueryFirstOrDefaultAsync<(int Flagged, int Recent, int LowConfidence)>(@"
            SELECT
                (SELECT COUNT(*) FROM hallucination_reports WHERE is_resolved = false)::int AS ""Flagged"",
                (SELECT COUNT(*) FROM hallucination_reports WHERE created_at > NOW() - INTERVAL '24 hours')::int AS ""Recent"",
                (SELECT COUNT(*) FROM memories WHERE confidence_score < 0.3 AND is_active = true)::int AS ""LowConfidence""
        ");

        // Get memories with no validation
        var unvalidated = await conn.ExecuteScalarAsync<int>(@"
            SELECT COUNT(*) FROM memories
            WHERE is_active = true
            AND id NOT IN (SELECT DISTINCT memory_id FROM memory_reinforcements)");

        // Calculate risk score
        var totalActive = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM memories WHERE is_active = true");
        var lowConfidenceRatio = totalActive > 0 ? (double)hallucinationData.LowConfidence / totalActive : 0;
        var unvalidatedRatio = totalActive > 0 ? (double)unvalidated / totalActive : 0;

        var riskScore = (lowConfidenceRatio * 0.4) + (unvalidatedRatio * 0.3) +
                        (hallucinationData.Flagged > 0 ? 0.3 : 0);

        return new HallucinationRisk
        {
            FlaggedHallucinations = hallucinationData.Flagged,
            RecentReports24h = hallucinationData.Recent,
            LowConfidenceMemories = hallucinationData.LowConfidence,
            UnvalidatedMemories = unvalidated,
            RiskScore = Math.Min(1.0, riskScore),
            RiskLevel = riskScore > 0.5 ? "High" : riskScore > 0.2 ? "Medium" : "Low",
            Recommendations = GenerateHallucinationRecommendations(riskScore, hallucinationData.LowConfidence, unvalidated)
        };
    }

    private List<string> GenerateHallucinationRecommendations(double riskScore, int lowConfidence, int unvalidated)
    {
        var recommendations = new List<string>();

        if (lowConfidence > 100)
            recommendations.Add($"Review {lowConfidence} low-confidence memories for accuracy");

        if (unvalidated > 500)
            recommendations.Add($"Validate {unvalidated} unvalidated memories");

        if (riskScore > 0.5)
            recommendations.Add("Consider running contradiction detection scan");

        if (recommendations.Count == 0)
            recommendations.Add("Memory integrity appears healthy");

        return recommendations;
    }

    private async Task<GraphExpansion> PredictGraphExpansionAsync(NpgsqlConnection conn, int horizonMinutes, CancellationToken ct)
    {
        // Get entity and relationship creation rates
        var creationRates = await conn.QueryFirstOrDefaultAsync<(double EntityRate, double RelRate)>(@"
            SELECT
                (SELECT COUNT(*)::float / 24 FROM entities WHERE created_at > NOW() - INTERVAL '24 hours') AS ""EntityRate"",
                (SELECT COUNT(*)::float / 24 FROM entity_relationships WHERE created_at > NOW() - INTERVAL '24 hours') AS ""RelRate""
        ");

        var hoursAhead = horizonMinutes / 60.0;
        var projectedEntities = (int)(creationRates.EntityRate * hoursAhead);
        var projectedRelationships = (int)(creationRates.RelRate * hoursAhead);

        // Get graph density
        var graphStats = await conn.QueryFirstOrDefaultAsync<(int Nodes, int Edges)>(@"
            SELECT
                (SELECT COUNT(*) FROM entities)::int AS ""Nodes"",
                (SELECT COUNT(*) FROM entity_relationships)::int AS ""Edges""
        ");

        var currentDensity = graphStats.Nodes > 1
            ? (double)graphStats.Edges / (graphStats.Nodes * (graphStats.Nodes - 1))
            : 0;

        return new GraphExpansion
        {
            EntitiesPerHour = creationRates.EntityRate,
            RelationshipsPerHour = creationRates.RelRate,
            ProjectedNewEntities = projectedEntities,
            ProjectedNewRelationships = projectedRelationships,
            CurrentGraphDensity = currentDensity,
            ExpansionRate = creationRates.EntityRate + creationRates.RelRate,
            Complexity = currentDensity > 0.3 ? "High" : currentDensity > 0.1 ? "Medium" : "Low"
        };
    }

    private PredictedGraph GeneratePredictedGraph(CurrentStats stats, GrowthTrend trend, int horizonMinutes)
    {
        var predictedNodes = new List<PredictedNode>();
        var predictedEdges = new List<PredictedEdge>();

        // Generate some predicted nodes based on growth trend
        var newMemoryCount = Math.Min(trend.ProjectedNewMemories, 20); // Cap for visualization
        for (int i = 0; i < newMemoryCount; i++)
        {
            predictedNodes.Add(new PredictedNode
            {
                Id = $"predicted-memory-{i}",
                Type = "Memory",
                Probability = trend.Confidence * (1 - i * 0.02), // Decreasing probability
                Label = $"Predicted Memory {i + 1}"
            });
        }

        // Generate predicted entity nodes
        var newEntityCount = Math.Min((int)(trend.ProjectedNewMemories * 0.3), 10);
        for (int i = 0; i < newEntityCount; i++)
        {
            predictedNodes.Add(new PredictedNode
            {
                Id = $"predicted-entity-{i}",
                Type = "Entity",
                Probability = trend.Confidence * 0.8,
                Label = $"Predicted Entity {i + 1}"
            });
        }

        // Generate predicted edges
        for (int i = 0; i < newEntityCount - 1; i++)
        {
            predictedEdges.Add(new PredictedEdge
            {
                FromId = $"predicted-entity-{i}",
                ToId = $"predicted-entity-{i + 1}",
                Type = "RELATED_TO",
                Probability = trend.Confidence * 0.7
            });
        }

        return new PredictedGraph
        {
            Nodes = predictedNodes,
            Edges = predictedEdges,
            HorizonMinutes = horizonMinutes
        };
    }
}

#region Models

public sealed class PredictionRequest
{
    public int HorizonMinutes { get; set; } = 30;
}

public sealed class PredictedState
{
    public int HorizonMinutes { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public long ProcessingTimeMs { get; set; }
    public CurrentStats CurrentStats { get; set; } = new();
    public GrowthTrend GrowthTrend { get; set; } = new();
    public ConflictPrediction ConflictPrediction { get; set; } = new();
    public HallucinationRisk HallucinationRisk { get; set; } = new();
    public GraphExpansion GraphExpansion { get; set; } = new();
    public PredictedGraph PredictedGraph { get; set; } = new();
}

public sealed class CurrentStats
{
    public int TotalMemories { get; set; }
    public int TotalEntities { get; set; }
    public int TotalRelationships { get; set; }
    public int ActiveMemories { get; set; }
    public double AverageConfidence { get; set; }
}

public sealed class GrowthTrend
{
    public double MemoriesPerHour { get; set; }
    public double TrendSlope { get; set; }
    public int ProjectedNewMemories { get; set; }
    public double Confidence { get; set; }
}

public sealed class ConflictPrediction
{
    public int CurrentUnresolvedConflicts { get; set; }
    public int RecentConflicts24h { get; set; }
    public double ConflictRate { get; set; }
    public double PredictedLikelihood { get; set; }
    public int ActiveContradictions { get; set; }
    public string RiskLevel { get; set; } = "Low";
}

public sealed class HallucinationRisk
{
    public int FlaggedHallucinations { get; set; }
    public int RecentReports24h { get; set; }
    public int LowConfidenceMemories { get; set; }
    public int UnvalidatedMemories { get; set; }
    public double RiskScore { get; set; }
    public string RiskLevel { get; set; } = "Low";
    public List<string> Recommendations { get; set; } = new();
}

public sealed class GraphExpansion
{
    public double EntitiesPerHour { get; set; }
    public double RelationshipsPerHour { get; set; }
    public int ProjectedNewEntities { get; set; }
    public int ProjectedNewRelationships { get; set; }
    public double CurrentGraphDensity { get; set; }
    public double ExpansionRate { get; set; }
    public string Complexity { get; set; } = "Low";
}

public sealed class PredictedGraph
{
    public List<PredictedNode> Nodes { get; set; } = new();
    public List<PredictedEdge> Edges { get; set; } = new();
    public int HorizonMinutes { get; set; }
}

public sealed class PredictedNode
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public double Probability { get; set; }
    public string Label { get; set; } = "";
}

public sealed class PredictedEdge
{
    public string FromId { get; set; } = "";
    public string ToId { get; set; } = "";
    public string Type { get; set; } = "";
    public double Probability { get; set; }
}

#endregion
