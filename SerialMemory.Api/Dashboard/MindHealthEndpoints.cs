using System.Security.Claims;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using SerialMemory.Core.Interfaces;
using SerialMemory.Infrastructure;

namespace SerialMemory.Api.Dashboard;

/// <summary>
/// Dashboard endpoints for Mind Health monitoring.
/// FIX #4: 30-day trend and calibration missing.
/// </summary>
public static class MindHealthEndpoints
{
    public static void MapMindHealthEndpoints(this WebApplication app, bool selfHostedMode)
    {
        var group = app.MapGroup("/api/mind")
            .WithTags("Mind Health")
            .RequireAuthorization();

        // =============================================================================
        // GET /mind/health - Get current mind health status
        // =============================================================================
        group.MapGet("/health", async (
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            CancellationToken ct) =>
        {
            var tenantId = GetTenantId(user, selfHostedMode);

            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.SetInternalAdminWithTenantAsync(tenantId);

            // Get latest daily score
            var latestScore = await conn.QueryFirstOrDefaultAsync<MindHealthDailyDto>(
                """
                SELECT id, date, overall_score, confidence_drift, l3_fact_stability,
                       entity_consistency, memory_decay_rate, anomaly_count, total_memories,
                       active_contradictions, hallucination_rate, created_at
                FROM mind_health_daily
                WHERE tenant_id = @TenantId
                ORDER BY date DESC
                LIMIT 1
                """,
                new { TenantId = tenantId });

            // Get current calibration
            var calibration = await conn.QueryFirstOrDefaultAsync<MindCalibrationDto>(
                """
                SELECT id, calibration_date, baseline_confidence, decay_half_life_days,
                       hallucination_threshold, contradiction_threshold, auto_resolve_enabled,
                       calibration_method, model_version, notes
                FROM mind_calibration
                WHERE tenant_id = @TenantId
                ORDER BY calibration_date DESC
                LIMIT 1
                """,
                new { TenantId = tenantId });

            // Calculate health status
            var healthStatus = "healthy";
            var overallScore = latestScore?.overall_score ?? 0.8f;

            if (overallScore < 0.5f)
                healthStatus = "critical";
            else if (overallScore < 0.7f)
                healthStatus = "warning";

            return Results.Ok(new
            {
                status = healthStatus,
                overallScore,
                lastUpdated = latestScore?.date ?? DateOnly.FromDateTime(DateTime.UtcNow),
                metrics = latestScore ?? new MindHealthDailyDto
                {
                    overall_score = 0.8f,
                    confidence_drift = 0,
                    l3_fact_stability = 1.0f,
                    entity_consistency = 1.0f,
                    memory_decay_rate = 0,
                    anomaly_count = 0,
                    total_memories = 0
                },
                calibration = calibration ?? new MindCalibrationDto
                {
                    baseline_confidence = 0.5f,
                    decay_half_life_days = 30,
                    hallucination_threshold = 0.1f,
                    contradiction_threshold = 0.85f,
                    auto_resolve_enabled = false,
                    calibration_method = "default"
                }
            });
        })
        .WithName("GetMindHealth")
        .WithDescription("Gets current mind health status")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized);

        // =============================================================================
        // GET /mind/health/trend - Get 30-day health trend (FIX #4)
        // =============================================================================
        group.MapGet("/health/trend", async (
            int? days,
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            CancellationToken ct) =>
        {
            var tenantId = GetTenantId(user, selfHostedMode);
            var trendDays = days ?? 30;

            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.SetInternalAdminWithTenantAsync(tenantId);

            // Get daily scores for trend
            var trend = await conn.QueryAsync<TrendPointDto>(
                """
                SELECT
                    date,
                    overall_score,
                    confidence_drift,
                    l3_fact_stability AS l3_stability,
                    entity_consistency,
                    memory_decay_rate AS decay_rate,
                    anomaly_count
                FROM mind_health_daily
                WHERE tenant_id = @TenantId
                    AND date >= CURRENT_DATE - @Days
                ORDER BY date ASC
                """,
                new { TenantId = tenantId, Days = trendDays });

            var trendList = trend.ToList();

            // Calculate regression (simple linear trend)
            float trendSlope = 0;
            string trendDirection = "stable";

            if (trendList.Count >= 2)
            {
                var n = trendList.Count;
                var sumX = 0.0;
                var sumY = 0.0;
                var sumXY = 0.0;
                var sumX2 = 0.0;

                for (int i = 0; i < n; i++)
                {
                    sumX += i;
                    sumY += trendList[i].overall_score;
                    sumXY += i * trendList[i].overall_score;
                    sumX2 += i * i;
                }

                trendSlope = (float)((n * sumXY - sumX * sumY) / (n * sumX2 - sumX * sumX));

                if (trendSlope > 0.01)
                    trendDirection = "improving";
                else if (trendSlope < -0.01)
                    trendDirection = "declining";
            }

            // Calculate averages
            var avgScore = trendList.Count > 0 ? trendList.Average(t => t.overall_score) : 0.8f;
            var avgDrift = trendList.Count > 0 ? trendList.Average(t => t.confidence_drift) : 0f;

            return Results.Ok(new
            {
                days = trendDays,
                dataPoints = trendList.Count,
                trend = trendList,
                analysis = new
                {
                    averageScore = avgScore,
                    averageDrift = avgDrift,
                    trendDirection,
                    trendSlope,
                    healthStatus = avgScore >= 0.7f ? "healthy" : avgScore >= 0.5f ? "warning" : "critical"
                }
            });
        })
        .WithName("GetMindHealthTrend")
        .WithDescription("Gets 30-day mind health trend")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized);

        // =============================================================================
        // GET /mind/health/calibration - Get current calibration settings (FIX #4)
        // =============================================================================
        group.MapGet("/health/calibration", async (
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            CancellationToken ct) =>
        {
            var tenantId = GetTenantId(user, selfHostedMode);

            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.SetInternalAdminWithTenantAsync(tenantId);

            var calibration = await conn.QueryFirstOrDefaultAsync<MindCalibrationDto>(
                """
                SELECT id, calibration_date, baseline_confidence, decay_half_life_days,
                       hallucination_threshold, contradiction_threshold, auto_resolve_enabled,
                       calibration_method, model_version, notes
                FROM mind_calibration
                WHERE tenant_id = @TenantId
                ORDER BY calibration_date DESC
                LIMIT 1
                """,
                new { TenantId = tenantId });

            if (calibration == null)
            {
                // Return default calibration
                calibration = new MindCalibrationDto
                {
                    baseline_confidence = 0.5f,
                    decay_half_life_days = 30,
                    hallucination_threshold = 0.1f,
                    contradiction_threshold = 0.85f,
                    auto_resolve_enabled = false,
                    calibration_method = "default",
                    calibration_date = DateTimeOffset.UtcNow
                };
            }

            // Get recommended calibration based on current data
            var recommendations = await CalculateRecommendationsAsync(conn, tenantId);

            return Results.Ok(new
            {
                current = calibration,
                recommended = recommendations,
                lastCalibrated = calibration.calibration_date
            });
        })
        .WithName("GetMindCalibration")
        .WithDescription("Gets current mind calibration settings")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized);

        // =============================================================================
        // POST /mind/health/calibration - Update calibration settings (FIX #4)
        // =============================================================================
        group.MapPost("/health/calibration", async (
            [FromBody] UpdateCalibrationRequest request,
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            CancellationToken ct) =>
        {
            var tenantId = GetTenantId(user, selfHostedMode);
            var userId = GetUserId(user);

            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.SetInternalAdminWithTenantAsync(tenantId);

            var calibrationId = Guid.CreateVersion7();

            await conn.ExecuteAsync(
                """
                INSERT INTO mind_calibration (
                    id, tenant_id, calibration_date, baseline_confidence, decay_half_life_days,
                    hallucination_threshold, contradiction_threshold, auto_resolve_enabled,
                    calibration_method, model_version, notes
                ) VALUES (
                    @Id, @TenantId, NOW(), @BaselineConfidence, @DecayHalfLifeDays,
                    @HallucinationThreshold, @ContradictionThreshold, @AutoResolve,
                    @Method, @ModelVersion, @Notes
                )
                """,
                new
                {
                    Id = calibrationId,
                    TenantId = tenantId,
                    BaselineConfidence = request.BaselineConfidence ?? 0.5f,
                    DecayHalfLifeDays = request.DecayHalfLifeDays ?? 30,
                    HallucinationThreshold = request.HallucinationThreshold ?? 0.1f,
                    ContradictionThreshold = request.ContradictionThreshold ?? 0.85f,
                    AutoResolve = request.AutoResolveEnabled ?? false,
                    Method = request.CalibrationMethod ?? "manual",
                    ModelVersion = request.ModelVersion,
                    Notes = request.Notes
                });

            return Results.Created($"/mind/health/calibration", new
            {
                id = calibrationId,
                message = "Calibration updated successfully",
                calibratedAt = DateTimeOffset.UtcNow
            });
        })
        .WithName("UpdateMindCalibration")
        .WithDescription("Updates mind calibration settings")
        .Produces(StatusCodes.Status201Created)
        .Produces(StatusCodes.Status401Unauthorized);

        // =============================================================================
        // POST /mind/health/compute - Trigger health computation
        // =============================================================================
        group.MapPost("/health/compute", async (
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            [FromServices] IMindHealthService? mindHealthService,
            CancellationToken ct) =>
        {
            var tenantId = GetTenantId(user, selfHostedMode);

            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.SetInternalAdminWithTenantAsync(tenantId);

            // Use service if available, otherwise use SQL function
            if (mindHealthService != null)
            {
                await mindHealthService.ComputeDailyHealthAsync(tenantId, DateOnly.FromDateTime(DateTime.UtcNow), ct);
            }
            else
            {
                await conn.ExecuteAsync(
                    "SELECT compute_daily_mind_health(@TenantId, CURRENT_DATE)",
                    new { TenantId = tenantId });
            }

            // Get the computed score
            var score = await conn.QueryFirstOrDefaultAsync<MindHealthDailyDto>(
                """
                SELECT id, date, overall_score, confidence_drift, l3_fact_stability,
                       entity_consistency, memory_decay_rate, anomaly_count, total_memories,
                       active_contradictions, hallucination_rate
                FROM mind_health_daily
                WHERE tenant_id = @TenantId AND date = CURRENT_DATE
                """,
                new { TenantId = tenantId });

            return Results.Ok(new
            {
                message = "Health computation completed",
                score
            });
        })
        .WithName("ComputeMindHealth")
        .WithDescription("Triggers mind health computation for today")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized);

        // =============================================================================
        // GET /mind/health/anomalies - Get detected anomalies
        // =============================================================================
        group.MapGet("/health/anomalies", async (
            int? limit,
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            CancellationToken ct) =>
        {
            var tenantId = GetTenantId(user, selfHostedMode);

            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.SetInternalAdminWithTenantAsync(tenantId);

            var anomalies = await conn.QueryAsync<AnomalyDto>(
                """
                SELECT
                    m.id AS memory_id,
                    m.content,
                    m.confidence,
                    m.layer,
                    m.created_at,
                    CASE
                        WHEN m.confidence < 0.3 THEN 'low_confidence'
                        WHEN (m.metadata->>'hallucination_flagged')::boolean = true THEN 'hallucination'
                        ELSE 'other'
                    END AS anomaly_type,
                    m.confidence AS anomaly_score
                FROM memories m
                WHERE m.tenant_id = @TenantId
                    AND (m.confidence < 0.3 OR (m.metadata->>'hallucination_flagged')::boolean = true)
                ORDER BY m.created_at DESC
                LIMIT @Limit
                """,
                new { TenantId = tenantId, Limit = limit ?? 50 });

            return Results.Ok(new
            {
                anomalies = anomalies.ToList(),
                totalCount = anomalies.Count()
            });
        })
        .WithName("GetMindAnomalies")
        .WithDescription("Gets detected mind anomalies")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized);
    }

    private static async Task<RecommendedCalibrationDto> CalculateRecommendationsAsync(
        NpgsqlConnection conn,
        Guid tenantId)
    {
        // Get average confidence from recent memories
        var avgConfidence = await conn.ExecuteScalarAsync<decimal?>(
            """
            SELECT AVG(confidence)
            FROM memories
            WHERE tenant_id = @TenantId AND created_at > NOW() - INTERVAL '30 days'
            """,
            new { TenantId = tenantId }) ?? 0.5m;

        // Get contradiction rate
        var contradictionRate = await conn.ExecuteScalarAsync<decimal?>(
            """
            SELECT
                COUNT(*) FILTER (WHERE status = 'pending')::decimal /
                NULLIF(COUNT(*), 0)
            FROM conflict_log
            WHERE tenant_id = @TenantId
            """,
            new { TenantId = tenantId }) ?? 0m;

        // Calculate recommended thresholds
        var recommendedBaseline = Math.Max(0.3f, Math.Min(0.7f, (float)avgConfidence));
        var recommendedContradictionThreshold = contradictionRate > 0.2m ? 0.9f : 0.85f;
        var recommendedHallucinationThreshold = avgConfidence < 0.5m ? 0.15f : 0.1f;

        return new RecommendedCalibrationDto
        {
            baseline_confidence = recommendedBaseline,
            decay_half_life_days = 30,
            hallucination_threshold = recommendedHallucinationThreshold,
            contradiction_threshold = recommendedContradictionThreshold,
            reasoning = new[]
            {
                $"Average confidence: {avgConfidence:P0}",
                $"Contradiction rate: {contradictionRate:P0}",
                avgConfidence < 0.5m ? "Low average confidence suggests increasing hallucination threshold" : "",
                contradictionRate > 0.2m ? "High contradiction rate suggests increasing similarity threshold" : ""
            }.Where(s => !string.IsNullOrEmpty(s)).ToList()
        };
    }

    private static Guid GetTenantId(ClaimsPrincipal user, bool selfHosted)
    {
        if (selfHosted)
            return Guid.Parse("00000000-0000-0000-0000-000000000000");

        var tenantIdClaim = user.FindFirst("tenant_id")?.Value
            ?? throw new UnauthorizedAccessException("Missing tenant_id claim");

        return Guid.Parse(tenantIdClaim);
    }

    private static string GetUserId(ClaimsPrincipal user)
    {
        return user.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? user.FindFirst("sub")?.Value
            ?? "system";
    }
}

// DTOs for Mind Health endpoints
public sealed class MindHealthDailyDto
{
    public Guid? id { get; set; }
    public DateOnly date { get; set; }
    public float overall_score { get; set; }
    public float confidence_drift { get; set; }
    public float l3_fact_stability { get; set; }
    public float entity_consistency { get; set; }
    public float memory_decay_rate { get; set; }
    public int anomaly_count { get; set; }
    public int total_memories { get; set; }
    public int active_contradictions { get; set; }
    public float hallucination_rate { get; set; }
    public DateTimeOffset? created_at { get; set; }
}

public sealed class TrendPointDto
{
    public DateOnly date { get; set; }
    public float overall_score { get; set; }
    public float confidence_drift { get; set; }
    public float l3_stability { get; set; }
    public float entity_consistency { get; set; }
    public float decay_rate { get; set; }
    public int anomaly_count { get; set; }
}

public sealed class MindCalibrationDto
{
    public Guid? id { get; set; }
    public DateTimeOffset calibration_date { get; set; }
    public float baseline_confidence { get; set; }
    public int decay_half_life_days { get; set; }
    public float hallucination_threshold { get; set; }
    public float contradiction_threshold { get; set; }
    public bool auto_resolve_enabled { get; set; }
    public string calibration_method { get; set; } = "";
    public string? model_version { get; set; }
    public string? notes { get; set; }
}

public sealed class RecommendedCalibrationDto
{
    public float baseline_confidence { get; set; }
    public int decay_half_life_days { get; set; }
    public float hallucination_threshold { get; set; }
    public float contradiction_threshold { get; set; }
    public List<string> reasoning { get; set; } = new();
}

public sealed class AnomalyDto
{
    public Guid memory_id { get; set; }
    public string? content { get; set; }
    public decimal confidence { get; set; }
    public string? layer { get; set; }
    public DateTimeOffset created_at { get; set; }
    public string anomaly_type { get; set; } = "";
    public decimal anomaly_score { get; set; }
}

public sealed record UpdateCalibrationRequest(
    float? BaselineConfidence = null,
    int? DecayHalfLifeDays = null,
    float? HallucinationThreshold = null,
    float? ContradictionThreshold = null,
    bool? AutoResolveEnabled = null,
    string? CalibrationMethod = null,
    string? ModelVersion = null,
    string? Notes = null
);
