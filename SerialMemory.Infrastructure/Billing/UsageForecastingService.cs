using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace SerialMemory.Infrastructure.Billing;

/// <summary>
/// Service for forecasting usage patterns and generating cost optimization recommendations.
/// Uses linear regression with confidence intervals for prediction.
/// </summary>
public sealed class UsageForecastingService
{
    private readonly ILogger<UsageForecastingService> _logger;
    private readonly string _connectionString;
    private readonly int _defaultTrainingWindowDays;
    private readonly double _defaultConfidenceLevel;

    public UsageForecastingService(
        IConfiguration configuration,
        ILogger<UsageForecastingService> logger)
    {
        _logger = logger;
        _connectionString = configuration.GetConnectionString("Postgres")
            ?? BuildConnectionString();

        _defaultTrainingWindowDays = configuration.GetValue("UsageForecasting:TrainingWindowDays", 30);
        _defaultConfidenceLevel = configuration.GetValue("UsageForecasting:ConfidenceLevel", 0.95);
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

    /// <summary>
    /// Generates usage forecasts for a tenant for the specified number of days ahead.
    /// </summary>
    public async Task<ForecastResult> GenerateForecastAsync(
        Guid tenantId,
        string workspaceId = "default",
        int daysAhead = 30,
        int? trainingWindowDays = null,
        CancellationToken ct = default)
    {
        var trainingDays = trainingWindowDays ?? _defaultTrainingWindowDays;

        _logger.LogInformation(
            "Generating {Days}-day forecast for tenant {TenantId} using {TrainingDays}-day training window",
            daysAhead, tenantId, trainingDays);

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        // Fetch historical daily usage
        var historicalUsage = await FetchHistoricalUsageAsync(conn, tenantId, workspaceId, trainingDays, ct);

        if (historicalUsage.Count < 7)
        {
            _logger.LogWarning(
                "Insufficient data for forecasting: only {Count} days available, minimum 7 required",
                historicalUsage.Count);

            return new ForecastResult(
                Success: false,
                Error: $"Insufficient historical data: {historicalUsage.Count} days available, 7 minimum required",
                Forecasts: [],
                ModelMetrics: null);
        }

        // Calculate linear regression
        var regression = CalculateLinearRegression(historicalUsage);

        // Generate forecasts
        var forecasts = new List<DailyForecast>();
        var startDay = historicalUsage.Count; // Day index after last historical day
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        for (var i = 0; i < daysAhead; i++)
        {
            var forecastDate = today.AddDays(i + 1);
            var dayIndex = startDay + i;

            var predictedCredits = Math.Max(0, regression.Slope * dayIndex + regression.Intercept);
            var stdError = regression.StandardError * Math.Sqrt(1 + 1.0 / historicalUsage.Count +
                Math.Pow(dayIndex - regression.MeanX, 2) / regression.SumSquaredDeviations);

            // T-value for 95% confidence interval (approximate for large samples)
            var tValue = GetTValue(historicalUsage.Count - 2, _defaultConfidenceLevel);
            var marginOfError = tValue * stdError;

            forecasts.Add(new DailyForecast(
                Date: forecastDate,
                PredictedCredits: (decimal)predictedCredits,
                ConfidenceIntervalLow: (decimal)Math.Max(0, predictedCredits - marginOfError),
                ConfidenceIntervalHigh: (decimal)(predictedCredits + marginOfError),
                ConfidenceLevel: (decimal)_defaultConfidenceLevel));
        }

        // Store forecasts in database
        await StoreForecastsAsync(conn, tenantId, workspaceId, forecasts, regression, trainingDays, ct);

        var metrics = new ModelMetrics(
            Slope: (decimal)regression.Slope,
            Intercept: (decimal)regression.Intercept,
            RSquared: (decimal)regression.RSquared,
            StandardError: (decimal)regression.StandardError,
            TrainingDataPoints: historicalUsage.Count,
            TrainingWindowDays: trainingDays);

        _logger.LogInformation(
            "Generated {Count} forecasts with R² = {RSquared:F4}",
            forecasts.Count, regression.RSquared);

        return new ForecastResult(
            Success: true,
            Error: null,
            Forecasts: forecasts,
            ModelMetrics: metrics);
    }

    /// <summary>
    /// Gets the current forecast for a tenant.
    /// </summary>
    public async Task<IReadOnlyList<DailyForecast>> GetForecastAsync(
        Guid tenantId,
        string workspaceId = "default",
        int daysAhead = 30,
        CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var endDate = today.AddDays(daysAhead);

        var forecasts = await conn.QueryAsync<ForecastRow>("""
            SELECT
                forecast_date,
                predicted_credits,
                confidence_interval_low,
                confidence_interval_high,
                confidence_level,
                actual_credits,
                prediction_error,
                mape
            FROM usage_forecasts
            WHERE tenant_id = @TenantId::uuid
              AND workspace_id = @WorkspaceId
              AND forecast_date >= @Today
              AND forecast_date <= @EndDate
            ORDER BY forecast_date
            """, new { TenantId = tenantId, WorkspaceId = workspaceId, Today = today, EndDate = endDate });

        return forecasts.Select(f => new DailyForecast(
            Date: f.forecast_date,
            PredictedCredits: f.predicted_credits,
            ConfidenceIntervalLow: f.confidence_interval_low ?? 0,
            ConfidenceIntervalHigh: f.confidence_interval_high ?? f.predicted_credits * 2,
            ConfidenceLevel: f.confidence_level ?? 0.95m,
            ActualCredits: f.actual_credits,
            PredictionError: f.prediction_error,
            Mape: f.mape)).ToList();
    }

    /// <summary>
    /// Updates forecasts with actual values for accuracy tracking.
    /// </summary>
    public async Task<int> UpdateActualsAsync(
        Guid tenantId,
        string workspaceId = "default",
        CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        // Get forecasts that need actuals updated (past dates without actual_credits)
        var yesterday = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1));

        var updated = await conn.ExecuteAsync("""
            WITH daily_actuals AS (
                SELECT
                    DATE(event_timestamp) as usage_date,
                    SUM(credits_consumed) as total_credits
                FROM usage_events
                WHERE tenant_id = @TenantId::uuid
                  AND workspace_id = @WorkspaceId
                  AND DATE(event_timestamp) <= @Yesterday
                GROUP BY DATE(event_timestamp)
            )
            UPDATE usage_forecasts uf
            SET
                actual_credits = da.total_credits,
                prediction_error = ABS(uf.predicted_credits - da.total_credits),
                mape = CASE
                    WHEN da.total_credits > 0
                    THEN ABS(uf.predicted_credits - da.total_credits) / da.total_credits * 100
                    ELSE NULL
                END,
                updated_at = NOW()
            FROM daily_actuals da
            WHERE uf.tenant_id = @TenantId::uuid
              AND uf.workspace_id = @WorkspaceId
              AND uf.forecast_date = da.usage_date
              AND uf.actual_credits IS NULL
            """, new { TenantId = tenantId, WorkspaceId = workspaceId, Yesterday = yesterday });

        if (updated > 0)
        {
            _logger.LogInformation(
                "Updated {Count} forecasts with actual values for tenant {TenantId}",
                updated, tenantId);
        }

        return updated;
    }

    /// <summary>
    /// Gets forecast accuracy metrics for a tenant.
    /// </summary>
    public async Task<ForecastAccuracy> GetAccuracyMetricsAsync(
        Guid tenantId,
        string workspaceId = "default",
        int lookbackDays = 30,
        CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var startDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-lookbackDays));

        var metrics = await conn.QueryFirstOrDefaultAsync<AccuracyRow>("""
            SELECT
                COUNT(*) as total_forecasts,
                COUNT(actual_credits) as completed_forecasts,
                AVG(mape) as avg_mape,
                AVG(prediction_error) as avg_error,
                STDDEV(prediction_error) as error_stddev,
                MIN(mape) as min_mape,
                MAX(mape) as max_mape
            FROM usage_forecasts
            WHERE tenant_id = @TenantId::uuid
              AND workspace_id = @WorkspaceId
              AND forecast_date >= @StartDate
              AND actual_credits IS NOT NULL
            """, new { TenantId = tenantId, WorkspaceId = workspaceId, StartDate = startDate });

        if (metrics == null || metrics.completed_forecasts == 0)
        {
            return new ForecastAccuracy(
                TotalForecasts: 0,
                CompletedForecasts: 0,
                AverageMape: null,
                AverageError: null,
                ErrorStdDev: null,
                AccuracyScore: null);
        }

        // Accuracy score: 100 - MAPE (capped at 0)
        var accuracyScore = metrics.avg_mape.HasValue
            ? Math.Max(0, 100 - metrics.avg_mape.Value)
            : (decimal?)null;

        return new ForecastAccuracy(
            TotalForecasts: metrics.total_forecasts,
            CompletedForecasts: metrics.completed_forecasts,
            AverageMape: metrics.avg_mape,
            AverageError: metrics.avg_error,
            ErrorStdDev: metrics.error_stddev,
            AccuracyScore: accuracyScore,
            MinMape: metrics.min_mape,
            MaxMape: metrics.max_mape);
    }

    /// <summary>
    /// Generates cost optimization recommendations based on usage patterns.
    /// </summary>
    public async Task<IReadOnlyList<CostRecommendation>> GenerateRecommendationsAsync(
        Guid tenantId,
        string workspaceId = "default",
        CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var recommendations = new List<CostRecommendation>();

        // Analyze usage patterns
        var patterns = await AnalyzeUsagePatternsAsync(conn, tenantId, workspaceId, ct);

        // 1. Off-peak usage recommendation
        if (patterns.PeakHourUsagePercent > 60)
        {
            recommendations.Add(await CreateRecommendationAsync(conn, tenantId, workspaceId,
                "off_peak",
                "cost_reduction",
                "Shift Usage to Off-Peak Hours",
                $"Your usage is concentrated during peak hours ({patterns.PeakHourUsagePercent:F0}%). " +
                "Shifting batch operations to off-peak hours (10 PM - 6 AM) could reduce costs.",
                estimatedSavingsPercent: 15,
                priority: 3,
                supportingData: new { patterns.PeakHourUsagePercent, patterns.PeakHours },
                ct: ct));
        }

        // 2. Batching recommendation
        if (patterns.AverageOperationsPerMinute < 10 && patterns.TotalOperations > 1000)
        {
            recommendations.Add(await CreateRecommendationAsync(conn, tenantId, workspaceId,
                "batching",
                "efficiency",
                "Batch Your API Operations",
                "Your operations are spread thinly across time. Batching multiple operations together " +
                "reduces overhead and can improve throughput by 20-40%.",
                estimatedSavingsPercent: 10,
                priority: 4,
                supportingData: new { patterns.AverageOperationsPerMinute, patterns.TotalOperations },
                ct: ct));
        }

        // 3. Plan change recommendation
        var currentPlan = await GetCurrentPlanAsync(conn, tenantId, ct);
        if (currentPlan != null)
        {
            var projectedUsage = await GetProjectedMonthlyUsageAsync(conn, tenantId, workspaceId, ct);
            var planLimit = currentPlan.CreditsPerMonth;

            if (projectedUsage > planLimit * 0.9m && currentPlan.Name != "enterprise")
            {
                var upgradePlan = currentPlan.Name == "free" ? "Pro" : "Enterprise";
                recommendations.Add(await CreateRecommendationAsync(conn, tenantId, workspaceId,
                    "plan_change",
                    "cost_reduction",
                    $"Consider Upgrading to {upgradePlan}",
                    $"Your projected usage ({projectedUsage:N0} credits) approaches your plan limit " +
                    $"({planLimit:N0} credits). Upgrading to {upgradePlan} may prevent overage charges.",
                    estimatedSavingsPercent: 5,
                    priority: 2,
                    supportingData: new { projectedUsage, planLimit, RecommendedPlan = upgradePlan.ToLower() },
                    ct: ct));
            }
            else if (projectedUsage < planLimit * 0.3m && currentPlan.Name != "free")
            {
                var downgradePlan = currentPlan.Name == "enterprise" ? "Pro" : "Free";
                recommendations.Add(await CreateRecommendationAsync(conn, tenantId, workspaceId,
                    "plan_change",
                    "cost_reduction",
                    $"Consider Downgrading to {downgradePlan}",
                    $"Your projected usage ({projectedUsage:N0} credits) is well below your plan limit " +
                    $"({planLimit:N0} credits). You might save money by downgrading to {downgradePlan}.",
                    estimatedSavingsPercent: 30,
                    priority: 3,
                    supportingData: new { projectedUsage, planLimit, RecommendedPlan = downgradePlan.ToLower() },
                    ct: ct));
            }
        }

        // 4. Caching recommendation
        if (patterns.RepeatedQueryPercent > 30)
        {
            recommendations.Add(await CreateRecommendationAsync(conn, tenantId, workspaceId,
                "caching",
                "efficiency",
                "Implement Query Caching",
                $"{patterns.RepeatedQueryPercent:F0}% of your queries appear to be repeated. " +
                "Implementing client-side caching for common queries could reduce costs significantly.",
                estimatedSavingsPercent: (int)Math.Min(25, patterns.RepeatedQueryPercent * 0.8),
                priority: 2,
                supportingData: new { patterns.RepeatedQueryPercent },
                ct: ct));
        }

        _logger.LogInformation(
            "Generated {Count} cost recommendations for tenant {TenantId}",
            recommendations.Count, tenantId);

        return recommendations;
    }

    /// <summary>
    /// Gets active recommendations for a tenant.
    /// </summary>
    public async Task<IReadOnlyList<CostRecommendation>> GetActiveRecommendationsAsync(
        Guid tenantId,
        string workspaceId = "default",
        CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var rows = await conn.QueryAsync<RecommendationRow>("""
            SELECT
                id,
                recommendation_type,
                category,
                title,
                description,
                estimated_savings_credits,
                estimated_savings_percent,
                confidence,
                priority,
                supporting_data,
                action_items,
                created_at
            FROM cost_recommendations
            WHERE tenant_id = @TenantId::uuid
              AND workspace_id = @WorkspaceId
              AND status = 'active'
              AND is_dismissed = FALSE
              AND (expires_at IS NULL OR expires_at > NOW())
            ORDER BY priority, created_at DESC
            """, new { TenantId = tenantId, WorkspaceId = workspaceId });

        return rows.Select(r => new CostRecommendation(
            Id: r.id,
            Type: r.recommendation_type,
            Category: r.category,
            Title: r.title,
            Description: r.description,
            EstimatedSavingsCredits: r.estimated_savings_credits,
            EstimatedSavingsPercent: r.estimated_savings_percent,
            Confidence: r.confidence,
            Priority: r.priority,
            SupportingData: string.IsNullOrEmpty(r.supporting_data) ? null :
                JsonSerializer.Deserialize<Dictionary<string, object>>(r.supporting_data),
            ActionItems: string.IsNullOrEmpty(r.action_items) ? [] :
                JsonSerializer.Deserialize<List<string>>(r.action_items) ?? [],
            CreatedAt: r.created_at)).ToList();
    }

    /// <summary>
    /// Dismisses a recommendation.
    /// </summary>
    public async Task DismissRecommendationAsync(
        Guid recommendationId,
        Guid tenantId,
        string? reason = null,
        CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await conn.ExecuteAsync("""
            UPDATE cost_recommendations
            SET
                is_dismissed = TRUE,
                dismissed_at = NOW(),
                dismissed_reason = @Reason,
                status = 'dismissed',
                updated_at = NOW()
            WHERE id = @Id::uuid
              AND tenant_id = @TenantId::uuid
            """, new { Id = recommendationId, TenantId = tenantId, Reason = reason });
    }

    /// <summary>
    /// Marks a recommendation as implemented.
    /// </summary>
    public async Task MarkRecommendationImplementedAsync(
        Guid recommendationId,
        Guid tenantId,
        CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await conn.ExecuteAsync("""
            UPDATE cost_recommendations
            SET
                status = 'implemented',
                implemented_at = NOW(),
                updated_at = NOW()
            WHERE id = @Id::uuid
              AND tenant_id = @TenantId::uuid
            """, new { Id = recommendationId, TenantId = tenantId });
    }

    #region Private Methods

    private async Task<List<DailyUsage>> FetchHistoricalUsageAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        string workspaceId,
        int days,
        CancellationToken ct)
    {
        var startDate = DateTime.UtcNow.AddDays(-days);

        var usage = await conn.QueryAsync<DailyUsageRow>("""
            SELECT
                DATE(event_timestamp) as usage_date,
                SUM(credits_consumed) as total_credits,
                COUNT(*) as operation_count
            FROM usage_events
            WHERE tenant_id = @TenantId::uuid
              AND workspace_id = @WorkspaceId
              AND event_timestamp >= @StartDate
            GROUP BY DATE(event_timestamp)
            ORDER BY usage_date
            """, new { TenantId = tenantId, WorkspaceId = workspaceId, StartDate = startDate });

        return usage.Select(u => new DailyUsage(
            Date: DateOnly.FromDateTime(u.usage_date),
            Credits: u.total_credits,
            Operations: u.operation_count)).ToList();
    }

    private static LinearRegressionResult CalculateLinearRegression(List<DailyUsage> data)
    {
        var n = data.Count;
        if (n < 2)
            throw new InvalidOperationException("Need at least 2 data points for regression");

        // Convert to arrays for calculation
        var x = new double[n];
        var y = new double[n];

        for (var i = 0; i < n; i++)
        {
            x[i] = i;
            y[i] = (double)data[i].Credits;
        }

        // Calculate means
        var meanX = x.Average();
        var meanY = y.Average();

        // Calculate slope and intercept
        var sumXY = 0.0;
        var sumXX = 0.0;
        var sumYY = 0.0;

        for (var i = 0; i < n; i++)
        {
            var dx = x[i] - meanX;
            var dy = y[i] - meanY;
            sumXY += dx * dy;
            sumXX += dx * dx;
            sumYY += dy * dy;
        }

        var slope = sumXX > 0 ? sumXY / sumXX : 0;
        var intercept = meanY - slope * meanX;

        // Calculate R-squared
        var rSquared = sumXX > 0 && sumYY > 0 ? Math.Pow(sumXY, 2) / (sumXX * sumYY) : 0;

        // Calculate standard error of estimate
        var residualSumSquares = 0.0;
        for (var i = 0; i < n; i++)
        {
            var predicted = slope * x[i] + intercept;
            var residual = y[i] - predicted;
            residualSumSquares += residual * residual;
        }

        var standardError = n > 2 ? Math.Sqrt(residualSumSquares / (n - 2)) : 0;

        return new LinearRegressionResult(
            Slope: slope,
            Intercept: intercept,
            RSquared: rSquared,
            StandardError: standardError,
            MeanX: meanX,
            SumSquaredDeviations: sumXX);
    }

    private static double GetTValue(int degreesOfFreedom, double confidenceLevel)
    {
        // Approximate t-values for common confidence levels
        // For large samples (>30), use z-value approximation
        if (degreesOfFreedom >= 30)
        {
            return confidenceLevel switch
            {
                >= 0.99 => 2.576,
                >= 0.95 => 1.96,
                >= 0.90 => 1.645,
                _ => 1.645
            };
        }

        // For smaller samples, use conservative approximation
        return confidenceLevel switch
        {
            >= 0.99 => 2.8 + 5.0 / degreesOfFreedom,
            >= 0.95 => 2.0 + 3.0 / degreesOfFreedom,
            >= 0.90 => 1.7 + 2.0 / degreesOfFreedom,
            _ => 1.7 + 2.0 / degreesOfFreedom
        };
    }

    private async Task StoreForecastsAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        string workspaceId,
        List<DailyForecast> forecasts,
        LinearRegressionResult regression,
        int trainingWindowDays,
        CancellationToken ct)
    {
        foreach (var forecast in forecasts)
        {
            await conn.ExecuteAsync("""
                INSERT INTO usage_forecasts (
                    id, tenant_id, workspace_id, forecast_date,
                    predicted_credits, confidence_interval_low, confidence_interval_high,
                    confidence_level, model_version, model_type, training_window_days,
                    created_at, updated_at
                )
                VALUES (
                    @Id, @TenantId::uuid, @WorkspaceId, @ForecastDate,
                    @PredictedCredits, @Low, @High,
                    @ConfidenceLevel, 'v1.0', 'linear_regression', @TrainingDays,
                    NOW(), NOW()
                )
                ON CONFLICT (tenant_id, workspace_id, forecast_date)
                DO UPDATE SET
                    predicted_credits = EXCLUDED.predicted_credits,
                    confidence_interval_low = EXCLUDED.confidence_interval_low,
                    confidence_interval_high = EXCLUDED.confidence_interval_high,
                    confidence_level = EXCLUDED.confidence_level,
                    model_version = EXCLUDED.model_version,
                    training_window_days = EXCLUDED.training_window_days,
                    updated_at = NOW()
                """, new
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                WorkspaceId = workspaceId,
                ForecastDate = forecast.Date,
                PredictedCredits = forecast.PredictedCredits,
                Low = forecast.ConfidenceIntervalLow,
                High = forecast.ConfidenceIntervalHigh,
                forecast.ConfidenceLevel,
                TrainingDays = trainingWindowDays
            });
        }
    }

    private async Task<UsagePatterns> AnalyzeUsagePatternsAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        string workspaceId,
        CancellationToken ct)
    {
        var thirtyDaysAgo = DateTime.UtcNow.AddDays(-30);

        // Analyze hourly distribution
        var hourlyStats = await conn.QueryAsync<HourlyStatRow>("""
            SELECT
                EXTRACT(HOUR FROM event_timestamp) as hour,
                COUNT(*) as operation_count,
                SUM(credits_consumed) as credits
            FROM usage_events
            WHERE tenant_id = @TenantId::uuid
              AND workspace_id = @WorkspaceId
              AND event_timestamp >= @StartDate
            GROUP BY EXTRACT(HOUR FROM event_timestamp)
            ORDER BY hour
            """, new { TenantId = tenantId, WorkspaceId = workspaceId, StartDate = thirtyDaysAgo });

        var hourlyList = hourlyStats.ToList();
        var totalOperations = hourlyList.Sum(h => h.operation_count);

        // Peak hours are 9 AM - 6 PM (working hours)
        var peakHourOperations = hourlyList
            .Where(h => h.hour >= 9 && h.hour < 18)
            .Sum(h => h.operation_count);

        var peakHourPercent = totalOperations > 0
            ? (double)peakHourOperations / totalOperations * 100
            : 0;

        // Calculate operations per minute (average)
        var minutesInPeriod = 30 * 24 * 60; // 30 days in minutes
        var avgOpsPerMinute = (double)totalOperations / minutesInPeriod;

        // Analyze repeated queries (simplified - check for similar contents in same day)
        var repeatedQueryPercent = await conn.QueryFirstOrDefaultAsync<double?>("""
            WITH query_hashes AS (
                SELECT
                    metadata->>'query_hash' as query_hash,
                    DATE(event_timestamp) as query_date,
                    COUNT(*) as count
                FROM usage_events
                WHERE tenant_id = @TenantId::uuid
                  AND workspace_id = @WorkspaceId
                  AND event_timestamp >= @StartDate
                  AND event_type = 'search'
                  AND metadata->>'query_hash' IS NOT NULL
                GROUP BY metadata->>'query_hash', DATE(event_timestamp)
                HAVING COUNT(*) > 1
            )
            SELECT
                COALESCE(
                    SUM(count)::float / NULLIF((SELECT COUNT(*) FROM usage_events
                        WHERE tenant_id = @TenantId::uuid
                          AND workspace_id = @WorkspaceId
                          AND event_timestamp >= @StartDate
                          AND event_type = 'search'), 0) * 100,
                    0
                )
            FROM query_hashes
            """, new { TenantId = tenantId, WorkspaceId = workspaceId, StartDate = thirtyDaysAgo }) ?? 0;

        return new UsagePatterns(
            PeakHourUsagePercent: peakHourPercent,
            PeakHours: "9 AM - 6 PM",
            TotalOperations: totalOperations,
            AverageOperationsPerMinute: avgOpsPerMinute,
            RepeatedQueryPercent: repeatedQueryPercent);
    }

    private async Task<CostRecommendation> CreateRecommendationAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        string workspaceId,
        string type,
        string category,
        string title,
        string description,
        int estimatedSavingsPercent,
        int priority,
        object supportingData,
        CancellationToken ct)
    {
        var id = Guid.CreateVersion7();

        // Check if similar recommendation already exists
        var existing = await conn.QueryFirstOrDefaultAsync<Guid?>("""
            SELECT id FROM cost_recommendations
            WHERE tenant_id = @TenantId::uuid
              AND workspace_id = @WorkspaceId
              AND recommendation_type = @Type
              AND status = 'active'
              AND is_dismissed = FALSE
            LIMIT 1
            """, new { TenantId = tenantId, WorkspaceId = workspaceId, Type = type });

        if (existing.HasValue)
        {
            // Update existing recommendation
            await conn.ExecuteAsync("""
                UPDATE cost_recommendations
                SET
                    title = @Title,
                    description = @Description,
                    estimated_savings_percent = @SavingsPercent,
                    supporting_data = @SupportingData::jsonb,
                    updated_at = NOW()
                WHERE id = @Id::uuid
                """, new
            {
                Id = existing.Value,
                Title = title,
                Description = description,
                SavingsPercent = estimatedSavingsPercent,
                SupportingData = JsonSerializer.Serialize(supportingData)
            });

            id = existing.Value;
        }
        else
        {
            // Create new recommendation
            await conn.ExecuteAsync("""
                INSERT INTO cost_recommendations (
                    id, tenant_id, workspace_id, recommendation_type, category,
                    title, description, estimated_savings_percent, priority,
                    supporting_data, expires_at, created_at, updated_at
                )
                VALUES (
                    @Id, @TenantId::uuid, @WorkspaceId, @Type, @Category,
                    @Title, @Description, @SavingsPercent, @Priority,
                    @SupportingData::jsonb, @ExpiresAt, NOW(), NOW()
                )
                """, new
            {
                Id = id,
                TenantId = tenantId,
                WorkspaceId = workspaceId,
                Type = type,
                Category = category,
                Title = title,
                Description = description,
                SavingsPercent = estimatedSavingsPercent,
                Priority = priority,
                SupportingData = JsonSerializer.Serialize(supportingData),
                ExpiresAt = DateTime.UtcNow.AddDays(30) // Recommendations expire after 30 days
            });
        }

        return new CostRecommendation(
            Id: id,
            Type: type,
            Category: category,
            Title: title,
            Description: description,
            EstimatedSavingsCredits: null,
            EstimatedSavingsPercent: estimatedSavingsPercent,
            Confidence: 0.8m,
            Priority: priority,
            SupportingData: supportingData as Dictionary<string, object>,
            ActionItems: [],
            CreatedAt: DateTime.UtcNow);
    }

    private async Task<PlanInfo?> GetCurrentPlanAsync(NpgsqlConnection conn, Guid tenantId, CancellationToken ct)
    {
        return await conn.QueryFirstOrDefaultAsync<PlanInfo>("""
            SELECT
                p.name,
                p.credits_per_month
            FROM tenant_plans tp
            JOIN plans p ON tp.plan_id = p.id
            WHERE tp.tenant_id = @TenantId::uuid
              AND tp.status = 'active'
            ORDER BY tp.created_at DESC
            LIMIT 1
            """, new { TenantId = tenantId });
    }

    private async Task<decimal> GetProjectedMonthlyUsageAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        string workspaceId,
        CancellationToken ct)
    {
        // Get usage so far this month and project to end of month
        var now = DateTime.UtcNow;
        var startOfMonth = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var daysInMonth = DateTime.DaysInMonth(now.Year, now.Month);
        var daysElapsed = (now - startOfMonth).TotalDays;

        var usageSoFar = await conn.QueryFirstOrDefaultAsync<decimal>("""
            SELECT COALESCE(SUM(credits_consumed), 0)
            FROM usage_events
            WHERE tenant_id = @TenantId::uuid
              AND workspace_id = @WorkspaceId
              AND event_timestamp >= @StartOfMonth
            """, new { TenantId = tenantId, WorkspaceId = workspaceId, StartOfMonth = startOfMonth });

        if (daysElapsed < 1)
            return usageSoFar;

        // Linear projection to end of month
        return usageSoFar / (decimal)daysElapsed * daysInMonth;
    }

    #endregion

    #region DTOs

    private sealed class DailyUsageRow
    {
        public DateTime usage_date { get; set; }
        public decimal total_credits { get; set; }
        public long operation_count { get; set; }
    }

    private sealed class ForecastRow
    {
        public DateOnly forecast_date { get; set; }
        public decimal predicted_credits { get; set; }
        public decimal? confidence_interval_low { get; set; }
        public decimal? confidence_interval_high { get; set; }
        public decimal? confidence_level { get; set; }
        public decimal? actual_credits { get; set; }
        public decimal? prediction_error { get; set; }
        public decimal? mape { get; set; }
    }

    private sealed class AccuracyRow
    {
        public int total_forecasts { get; set; }
        public int completed_forecasts { get; set; }
        public decimal? avg_mape { get; set; }
        public decimal? avg_error { get; set; }
        public decimal? error_stddev { get; set; }
        public decimal? min_mape { get; set; }
        public decimal? max_mape { get; set; }
    }

    private sealed class RecommendationRow
    {
        public Guid id { get; set; }
        public string recommendation_type { get; set; } = "";
        public string category { get; set; } = "";
        public string title { get; set; } = "";
        public string description { get; set; } = "";
        public decimal? estimated_savings_credits { get; set; }
        public decimal? estimated_savings_percent { get; set; }
        public decimal confidence { get; set; }
        public int priority { get; set; }
        public string? supporting_data { get; set; }
        public string? action_items { get; set; }
        public DateTime created_at { get; set; }
    }

    private sealed class HourlyStatRow
    {
        public int hour { get; set; }
        public long operation_count { get; set; }
        public decimal credits { get; set; }
    }

    private sealed class PlanInfo
    {
        public string Name { get; set; } = "";
        public decimal Credits_Per_Month { get; set; }
        public decimal CreditsPerMonth => Credits_Per_Month;
    }

    private sealed record DailyUsage(DateOnly Date, decimal Credits, long Operations);

    private sealed record LinearRegressionResult(
        double Slope,
        double Intercept,
        double RSquared,
        double StandardError,
        double MeanX,
        double SumSquaredDeviations);

    private sealed record UsagePatterns(
        double PeakHourUsagePercent,
        string PeakHours,
        long TotalOperations,
        double AverageOperationsPerMinute,
        double RepeatedQueryPercent);

    #endregion
}

#region Public Types

/// <summary>
/// Result of generating forecasts.
/// </summary>
public sealed record ForecastResult(
    bool Success,
    string? Error,
    IReadOnlyList<DailyForecast> Forecasts,
    ModelMetrics? ModelMetrics);

/// <summary>
/// A single day's forecast.
/// </summary>
public sealed record DailyForecast(
    DateOnly Date,
    decimal PredictedCredits,
    decimal ConfidenceIntervalLow,
    decimal ConfidenceIntervalHigh,
    decimal ConfidenceLevel,
    decimal? ActualCredits = null,
    decimal? PredictionError = null,
    decimal? Mape = null);

/// <summary>
/// Metrics about the forecasting model.
/// </summary>
public sealed record ModelMetrics(
    decimal Slope,
    decimal Intercept,
    decimal RSquared,
    decimal StandardError,
    int TrainingDataPoints,
    int TrainingWindowDays);

/// <summary>
/// Forecast accuracy metrics.
/// </summary>
public sealed record ForecastAccuracy(
    int TotalForecasts,
    int CompletedForecasts,
    decimal? AverageMape,
    decimal? AverageError,
    decimal? ErrorStdDev,
    decimal? AccuracyScore,
    decimal? MinMape = null,
    decimal? MaxMape = null);

/// <summary>
/// A cost optimization recommendation.
/// </summary>
public sealed record CostRecommendation(
    Guid Id,
    string Type,
    string Category,
    string Title,
    string Description,
    decimal? EstimatedSavingsCredits,
    decimal? EstimatedSavingsPercent,
    decimal Confidence,
    int Priority,
    Dictionary<string, object>? SupportingData,
    IReadOnlyList<string> ActionItems,
    DateTime CreatedAt);

#endregion
