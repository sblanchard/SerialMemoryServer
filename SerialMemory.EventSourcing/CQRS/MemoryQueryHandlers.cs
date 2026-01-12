using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using SerialMemory.Core.Interfaces;
using SerialMemory.EventSourcing.Events;
using SerialMemory.EventSourcing.Retrieval;

namespace SerialMemory.EventSourcing.CQRS;

/// <summary>
/// Handler for SearchMemoriesQuery.
/// </summary>
public sealed class SearchMemoriesQueryHandler : IQueryHandler<SearchMemoriesQuery, IReadOnlyList<RetrievalResult>>
{
    private readonly IRetrievalEngine _retrievalEngine;

    public SearchMemoriesQueryHandler(IRetrievalEngine retrievalEngine)
    {
        _retrievalEngine = retrievalEngine;
    }

    public async Task<IReadOnlyList<RetrievalResult>> HandleAsync(
        SearchMemoriesQuery query,
        CancellationToken cancellationToken = default)
    {
        return await _retrievalEngine.SearchAsync(new RetrievalQuery
        {
            QueryText = query.QueryText,
            UserId = query.UserId,
            CurrentDirectives = query.CurrentDirectives,
            MaxResults = query.MaxResults,
            MinScore = query.MinScore,
            LayerFilter = query.LayerFilter,
            ActiveOnly = query.ActiveOnly,
            Weights = query.Weights ?? RetrievalWeights.Default,
            HybridSearch = query.HybridSearch
        }, cancellationToken);
    }
}

/// <summary>
/// Handler for GetMemoryByIdQuery.
/// </summary>
public sealed class GetMemoryByIdQueryHandler : IQueryHandler<GetMemoryByIdQuery, RetrievalResult?>
{
    private readonly IRetrievalEngine _retrievalEngine;

    public GetMemoryByIdQueryHandler(IRetrievalEngine retrievalEngine)
    {
        _retrievalEngine = retrievalEngine;
    }

    public async Task<RetrievalResult?> HandleAsync(
        GetMemoryByIdQuery query,
        CancellationToken cancellationToken = default)
    {
        return await _retrievalEngine.GetByIdAsync(query.MemoryId, query.VerifyIntegrity, cancellationToken);
    }
}

/// <summary>
/// Handler for GetRelatedMemoriesQuery.
/// </summary>
public sealed class GetRelatedMemoriesQueryHandler : IQueryHandler<GetRelatedMemoriesQuery, IReadOnlyList<RetrievalResult>>
{
    private readonly IRetrievalEngine _retrievalEngine;

    public GetRelatedMemoriesQueryHandler(IRetrievalEngine retrievalEngine)
    {
        _retrievalEngine = retrievalEngine;
    }

    public async Task<IReadOnlyList<RetrievalResult>> HandleAsync(
        GetRelatedMemoriesQuery query,
        CancellationToken cancellationToken = default)
    {
        return await _retrievalEngine.GetRelatedMemoriesAsync(query.MemoryId, query.MaxDepth, cancellationToken);
    }
}

/// <summary>
/// Handler for FindDuplicatesQuery.
/// </summary>
public sealed class FindDuplicatesQueryHandler : IQueryHandler<FindDuplicatesQuery, IReadOnlyList<(Guid MemoryA, Guid MemoryB, float Similarity)>>
{
    private readonly IRetrievalEngine _retrievalEngine;

    public FindDuplicatesQueryHandler(IRetrievalEngine retrievalEngine)
    {
        _retrievalEngine = retrievalEngine;
    }

    public async Task<IReadOnlyList<(Guid MemoryA, Guid MemoryB, float Similarity)>> HandleAsync(
        FindDuplicatesQuery query,
        CancellationToken cancellationToken = default)
    {
        return await _retrievalEngine.FindDuplicatesAsync(query.SimilarityThreshold, cancellationToken);
    }
}

/// <summary>
/// Handler for GetLayerStatisticsQuery.
/// </summary>
public sealed class GetLayerStatisticsQueryHandler : IQueryHandler<GetLayerStatisticsQuery, IReadOnlyList<LayerStatistics>>
{
    private readonly NpgsqlDataSource _dataSource;

    public GetLayerStatisticsQueryHandler(string connectionString)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        _dataSource = builder.Build();
    }

    public async Task<IReadOnlyList<LayerStatistics>> HandleAsync(
        GetLayerStatisticsQuery query,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        var results = await conn.QueryAsync<dynamic>(new CommandDefinition(@"
            SELECT
                layer,
                COUNT(*) AS total_count,
                COUNT(*) FILTER (WHERE is_active) AS active_count,
                AVG(confidence_score)::DECIMAL(4,3) AS avg_confidence,
                AVG(access_count)::DECIMAL(10,2) AS avg_access_count,
                MIN(created_at) AS oldest_memory,
                MAX(created_at) AS newest_memory
            FROM memory_projections
            GROUP BY layer
            ORDER BY layer",
            cancellationToken: cancellationToken));

        return results.Select(r => new LayerStatistics
        {
            Layer = Enum.Parse<MemoryLayer>((string)r.layer),
            TotalCount = (int)r.total_count,
            ActiveCount = (int)r.active_count,
            AvgConfidence = (float)(decimal)r.avg_confidence,
            AvgAccessCount = (float)(decimal)r.avg_access_count,
            OldestMemory = r.oldest_memory,
            NewestMemory = r.newest_memory
        }).ToList();
    }
}

/// <summary>
/// Handler for GetRecentMemoriesQuery.
/// </summary>
public sealed class GetRecentMemoriesQueryHandler : IQueryHandler<GetRecentMemoriesQuery, IReadOnlyList<RetrievalResult>>
{
    private readonly NpgsqlDataSource _dataSource;

    public GetRecentMemoriesQueryHandler(string connectionString)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        _dataSource = builder.Build();
    }

    public async Task<IReadOnlyList<RetrievalResult>> HandleAsync(
        GetRecentMemoriesQuery query,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        var layerFilter = query.LayerFilter?.Length > 0
            ? "AND layer = ANY(@Layers::memory_layer[])"
            : "";

        var results = await conn.QueryAsync<dynamic>(new CommandDefinition($@"
            SELECT
                memory_id, content, layer, confidence_score, half_life_days,
                last_reinforced_at, user_affinity_score, directive_relevance,
                causal_parents, tags, contradiction_ids, created_at
            FROM memory_projections
            WHERE is_active = TRUE AND is_archived = FALSE
                {layerFilter}
            ORDER BY created_at DESC
            LIMIT @Limit",
            new
            {
                Limit = query.Limit,
                Layers = query.LayerFilter?.Select(l => l.ToString()).ToArray()
            },
            cancellationToken: cancellationToken));

        return results.Select(r =>
        {
            var lastReinforced = (DateTimeOffset)r.last_reinforced_at;
            var daysElapsed = (DateTimeOffset.UtcNow - lastReinforced).TotalDays;
            var decayedConfidence = (float)(decimal)r.confidence_score * (float)Math.Pow(0.5, daysElapsed / (int)r.half_life_days);

            var score = new RetrievalScore
            {
                SemanticScore = 0f,
                RecencyScore = 1f,
                ConfidenceScore = decayedConfidence,
                UserAffinityScore = (float)(decimal)r.user_affinity_score,
                DirectiveMatchScore = (float)(decimal)r.directive_relevance,
                ContradictionCount = ((Guid[])r.contradiction_ids)?.Length ?? 0
            };

            return new RetrievalResult
            {
                MemoryId = r.memory_id,
                Content = r.content,
                Layer = Enum.Parse<MemoryLayer>((string)r.layer),
                Score = score,
                FinalScore = score.ComputeFinalScore(RetrievalWeights.Default),
                CreatedAt = r.created_at,
                CausalParents = r.causal_parents ?? Array.Empty<Guid>(),
                Tags = r.tags ?? Array.Empty<string>()
            };
        }).ToList();
    }
}

/// <summary>
/// Handler for GetCognitiveStageLogsQuery.
/// </summary>
public sealed class GetCognitiveStageLogsQueryHandler : IQueryHandler<GetCognitiveStageLogsQuery, IReadOnlyList<CognitiveStageLog>>
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<GetCognitiveStageLogsQueryHandler> _logger;

    public GetCognitiveStageLogsQueryHandler(string connectionString, ILogger<GetCognitiveStageLogsQueryHandler> logger)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        _dataSource = builder.Build();
        _logger = logger;
    }

    public async Task<IReadOnlyList<CognitiveStageLog>> HandleAsync(
        GetCognitiveStageLogsQuery query,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        var filters = new List<string> { "1=1" };
        var parameters = new DynamicParameters();
        parameters.Add("Limit", query.Limit);

        if (query.SessionId.HasValue)
        {
            filters.Add("session_id = @SessionId");
            parameters.Add("SessionId", query.SessionId.Value);
        }

        if (query.Stage.HasValue)
        {
            filters.Add("stage = @Stage::cognitive_stage");
            parameters.Add("Stage", query.Stage.Value.ToString());
        }

        if (query.FromDate.HasValue)
        {
            filters.Add("started_at >= @FromDate");
            parameters.Add("FromDate", query.FromDate.Value);
        }

        if (query.ToDate.HasValue)
        {
            filters.Add("started_at <= @ToDate");
            parameters.Add("ToDate", query.ToDate.Value);
        }

        var sql = $@"
            SELECT log_id, session_id, stage, started_at, completed_at, duration_ms,
                   input_summary, output_summary, memory_ids_involved, metadata, success, error_message
            FROM cognitive_stage_logs
            WHERE {string.Join(" AND ", filters)}
            ORDER BY started_at DESC
            LIMIT @Limit";

        var results = await conn.QueryAsync<dynamic>(new CommandDefinition(sql, parameters, cancellationToken: cancellationToken));

        return results.Select(r => new CognitiveStageLog
        {
            LogId = r.log_id,
            SessionId = r.session_id,
            Stage = Enum.Parse<CognitiveStage>((string)r.stage),
            StartedAt = r.started_at,
            CompletedAt = r.completed_at,
            InputSummary = r.input_summary,
            OutputSummary = r.output_summary,
            MemoryIdsInvolved = r.memory_ids_involved ?? Array.Empty<Guid>(),
            Success = r.success,
            ErrorMessage = r.error_message
        }).ToList();
    }
}
