using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using SerialMemory.Core.Interfaces;
using SerialMemory.Core.Models;

namespace SerialMemory.Infrastructure;

/// <summary>
/// PostgreSQL implementation of disagreement storage.
/// </summary>
public sealed class PostgresDisagreementStore(string connectionString, ILogger<PostgresDisagreementStore> logger)
    : IDisagreementStore
{
    public async Task<Guid> StoreReasoningRunAsync(
        MultiModelReasoningResult result,
        string? projectFilter = null,
        Guid? memoryId = null,
        string tenantId = "self",
        CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        try
        {
            var runId = Guid.CreateVersion7();
            var avgDisagreement = result.Disagreements.Count > 0
                ? result.Disagreements.Average(d => d.DisagreementScore)
                : (float?)null;

            // Insert reasoning run
            await conn.ExecuteAsync("""
                INSERT INTO reasoning_runs (
                    id, tenant_id, input_hash, models_used, successful_models,
                    total_duration_ms, overall_confidence, insight_count,
                    disagreement_count, avg_disagreement_score, project_filter, memory_id
                ) VALUES (
                    @Id, @TenantId, @InputHash, @ModelsUsed, @SuccessfulModels,
                    @TotalDurationMs, @OverallConfidence, @InsightCount,
                    @DisagreementCount, @AvgDisagreementScore, @ProjectFilter, @MemoryId
                )
                """,
                new
                {
                    Id = runId,
                    TenantId = tenantId,
                    InputHash = result.InputHash ?? "",
                    result.ModelsUsed,
                    result.SuccessfulModels,
                    result.TotalDurationMs,
                    result.OverallConfidence,
                    InsightCount = result.Insights.Count,
                    DisagreementCount = result.Disagreements.Count,
                    AvgDisagreementScore = avgDisagreement,
                    ProjectFilter = projectFilter,
                    MemoryId = memoryId
                },
                transaction: tx);

            // Insert disagreements
            foreach (var disagreement in result.Disagreements)
            {
                await conn.ExecuteAsync("""
                    INSERT INTO model_disagreements (
                        id, tenant_id, reasoning_run_id, primary_model, secondary_model,
                        disagreement_score, primary_only_insights, secondary_only_insights,
                        confidence_divergences, computed_at
                    ) VALUES (
                        @Id, @TenantId, @RunId, @PrimaryModel, @SecondaryModel,
                        @DisagreementScore, @PrimaryOnlyInsights::jsonb, @SecondaryOnlyInsights::jsonb,
                        @ConfidenceDivergences::jsonb, @ComputedAt
                    )
                    """,
                    new
                    {
                        disagreement.Id,
                        TenantId = tenantId,
                        RunId = runId,
                        disagreement.PrimaryModel,
                        disagreement.SecondaryModel,
                        disagreement.DisagreementScore,
                        PrimaryOnlyInsights = JsonSerializer.Serialize(disagreement.PrimaryOnlyInsights),
                        SecondaryOnlyInsights = JsonSerializer.Serialize(disagreement.SecondaryOnlyInsights),
                        ConfidenceDivergences = JsonSerializer.Serialize(disagreement.ConfidenceDivergences),
                        disagreement.ComputedAt
                    },
                    transaction: tx);
            }

            await tx.CommitAsync(ct);

            logger.LogInformation("Stored reasoning run {RunId} with {DisagreementCount} disagreements",
                runId, result.Disagreements.Count);

            return runId;
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            logger.LogError(ex, "Failed to store reasoning run");
            throw;
        }
    }

    public async Task<List<ReasoningRunSummary>> GetRecentRunsAsync(
        string tenantId = "self",
        int limit = 50,
        CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);

        var results = await conn.QueryAsync<ReasoningRunSummary>("""
            SELECT
                id AS Id,
                input_hash AS InputHash,
                models_used AS ModelsUsed,
                successful_models AS SuccessfulModels,
                total_duration_ms AS TotalDurationMs,
                overall_confidence AS OverallConfidence,
                insight_count AS InsightCount,
                disagreement_count AS DisagreementCount,
                avg_disagreement_score AS AvgDisagreementScore,
                project_filter AS ProjectFilter,
                memory_id AS MemoryId,
                created_at AS CreatedAt
            FROM reasoning_runs
            WHERE tenant_id = @TenantId
            ORDER BY created_at DESC
            LIMIT @Limit
            """,
            new { TenantId = tenantId, Limit = limit });

        return results.ToList();
    }

    public async Task<List<ModelDisagreement>> GetDisagreementsForRunAsync(
        Guid runId,
        CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);

        var results = await conn.QueryAsync<DisagreementRow>("""
            SELECT
                id AS Id,
                primary_model AS PrimaryModel,
                secondary_model AS SecondaryModel,
                disagreement_score AS DisagreementScore,
                primary_only_insights AS PrimaryOnlyInsightsJson,
                secondary_only_insights AS SecondaryOnlyInsightsJson,
                confidence_divergences AS ConfidenceDivergencesJson,
                computed_at AS ComputedAt
            FROM model_disagreements
            WHERE reasoning_run_id = @RunId
            ORDER BY disagreement_score DESC
            """,
            new { RunId = runId });

        return results.Select(r => new ModelDisagreement
        {
            Id = r.Id,
            PrimaryModel = r.PrimaryModel,
            SecondaryModel = r.SecondaryModel,
            DisagreementScore = r.DisagreementScore,
            PrimaryOnlyInsights = r.PrimaryOnlyInsightsJson != null
                ? JsonSerializer.Deserialize<List<string>>(r.PrimaryOnlyInsightsJson) ?? []
                : [],
            SecondaryOnlyInsights = r.SecondaryOnlyInsightsJson != null
                ? JsonSerializer.Deserialize<List<string>>(r.SecondaryOnlyInsightsJson) ?? []
                : [],
            ConfidenceDivergences = r.ConfidenceDivergencesJson != null
                ? JsonSerializer.Deserialize<List<ConfidenceDivergence>>(r.ConfidenceDivergencesJson) ?? []
                : [],
            ComputedAt = r.ComputedAt
        }).ToList();
    }

    public async Task<List<DisagreementTrend>> GetDisagreementTrendsAsync(
        string tenantId = "self",
        int days = 7,
        CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);

        var results = await conn.QueryAsync<DisagreementTrend>("""
            SELECT
                primary_model || ' vs ' || secondary_model AS ModelPair,
                AVG(disagreement_score)::REAL AS AvgDisagreement,
                MAX(disagreement_score)::REAL AS MaxDisagreement,
                COUNT(*)::INTEGER AS OccurrenceCount
            FROM model_disagreements
            WHERE tenant_id = @TenantId
                AND computed_at > NOW() - (@Days || ' days')::INTERVAL
            GROUP BY primary_model, secondary_model
            ORDER BY AVG(disagreement_score) DESC
            """,
            new { TenantId = tenantId, Days = days });

        return results.ToList();
    }

    public async Task<List<ConfidenceHistoryEntry>> GetConfidenceHistoryAsync(
        string tenantId = "self",
        int limit = 100,
        CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);

        var results = await conn.QueryAsync<ConfidenceHistoryEntry>("""
            SELECT
                id AS RunId,
                overall_confidence AS OverallConfidence,
                models_used AS ModelsUsed,
                successful_models AS SuccessfulModels,
                disagreement_count AS DisagreementCount,
                avg_disagreement_score AS AvgDisagreement,
                created_at AS CreatedAt
            FROM reasoning_runs
            WHERE tenant_id = @TenantId
            ORDER BY created_at DESC
            LIMIT @Limit
            """,
            new { TenantId = tenantId, Limit = limit });

        return results.ToList();
    }

    private sealed class DisagreementRow
    {
        public Guid Id { get; init; }
        public string PrimaryModel { get; init; } = "";
        public string SecondaryModel { get; init; } = "";
        public float DisagreementScore { get; init; }
        public string? PrimaryOnlyInsightsJson { get; init; }
        public string? SecondaryOnlyInsightsJson { get; init; }
        public string? ConfidenceDivergencesJson { get; init; }
        public DateTimeOffset ComputedAt { get; init; }
    }
}
