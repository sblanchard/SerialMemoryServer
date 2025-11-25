using SerialMemory.EventSourcing.Events;
using SerialMemory.EventSourcing.Retrieval;

namespace SerialMemory.EventSourcing.CQRS;

/// <summary>
/// Base interface for queries.
/// Queries read from projections (read models).
/// </summary>
public interface IQuery<TResult> { }

/// <summary>
/// Query handler interface.
/// </summary>
public interface IQueryHandler<in TQuery, TResult> where TQuery : IQuery<TResult>
{
    Task<TResult> HandleAsync(TQuery query, CancellationToken cancellationToken = default);
}

/// <summary>
/// Query dispatcher.
/// </summary>
public interface IQueryDispatcher
{
    Task<TResult> DispatchAsync<TQuery, TResult>(TQuery query, CancellationToken cancellationToken = default)
        where TQuery : IQuery<TResult>;
}

/// <summary>
/// Simple query dispatcher using DI.
/// </summary>
public sealed class QueryDispatcher : IQueryDispatcher
{
    private readonly IServiceProvider _serviceProvider;

    public QueryDispatcher(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public async Task<TResult> DispatchAsync<TQuery, TResult>(TQuery query, CancellationToken cancellationToken = default)
        where TQuery : IQuery<TResult>
    {
        var handler = _serviceProvider.GetService(typeof(IQueryHandler<TQuery, TResult>)) as IQueryHandler<TQuery, TResult>
            ?? throw new InvalidOperationException($"No handler registered for {typeof(TQuery).Name}");

        return await handler.HandleAsync(query, cancellationToken);
    }
}

// ============================================================================
// READ QUERIES
// ============================================================================

/// <summary>
/// Query to search memories with multi-axis scoring.
/// </summary>
public sealed record SearchMemoriesQuery : IQuery<IReadOnlyList<RetrievalResult>>
{
    public required string QueryText { get; init; }
    public string UserId { get; init; } = "default_user";
    public string[] CurrentDirectives { get; init; } = [];
    public int MaxResults { get; init; } = 10;
    public float MinScore { get; init; } = 0.0f;
    public MemoryLayer[]? LayerFilter { get; init; }
    public bool ActiveOnly { get; init; } = true;
    public RetrievalWeights? Weights { get; init; }
    public bool HybridSearch { get; init; } = true;
}

/// <summary>
/// Query to get memory by ID.
/// </summary>
public sealed record GetMemoryByIdQuery : IQuery<RetrievalResult?>
{
    public required Guid MemoryId { get; init; }
    public bool VerifyIntegrity { get; init; } = true;
}

/// <summary>
/// Query to get related memories via causal graph.
/// </summary>
public sealed record GetRelatedMemoriesQuery : IQuery<IReadOnlyList<RetrievalResult>>
{
    public required Guid MemoryId { get; init; }
    public int MaxDepth { get; init; } = 2;
}

/// <summary>
/// Query to find duplicate memories.
/// </summary>
public sealed record FindDuplicatesQuery : IQuery<IReadOnlyList<(Guid MemoryA, Guid MemoryB, float Similarity)>>
{
    public float SimilarityThreshold { get; init; } = 0.95f;
}

/// <summary>
/// Query for memory statistics by layer.
/// </summary>
public sealed record GetLayerStatisticsQuery : IQuery<IReadOnlyList<LayerStatistics>>;

public sealed record LayerStatistics
{
    public MemoryLayer Layer { get; init; }
    public int TotalCount { get; init; }
    public int ActiveCount { get; init; }
    public float AvgConfidence { get; init; }
    public float AvgAccessCount { get; init; }
    public DateTimeOffset? OldestMemory { get; init; }
    public DateTimeOffset? NewestMemory { get; init; }
}

/// <summary>
/// Query for recent memories.
/// </summary>
public sealed record GetRecentMemoriesQuery : IQuery<IReadOnlyList<RetrievalResult>>
{
    public int Limit { get; init; } = 20;
    public MemoryLayer[]? LayerFilter { get; init; }
}

/// <summary>
/// Query for cognitive stage logs.
/// </summary>
public sealed record GetCognitiveStageLogsQuery : IQuery<IReadOnlyList<CognitiveStageLog>>
{
    public Guid? SessionId { get; init; }
    public CognitiveStage? Stage { get; init; }
    public DateTimeOffset? FromDate { get; init; }
    public DateTimeOffset? ToDate { get; init; }
    public int Limit { get; init; } = 100;
}
