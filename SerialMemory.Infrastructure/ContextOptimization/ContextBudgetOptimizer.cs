using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using SerialMemory.Core.Interfaces;

namespace SerialMemory.Infrastructure.ContextOptimization;

public sealed class ContextBudgetOptimizer(
    NpgsqlDataSource dataSource,
    ILogger<ContextBudgetOptimizer> logger)
    : IContextBudgetOptimizer
{
    // Approximate tokens per character ratio (conservative estimate)
    private const float TokensPerCharacter = 0.3f;

    // Token overhead for memory formatting
    private const int MemoryFormatOverhead = 20;

    public async Task<ContextBudget> CreateBudgetAsync(
        int maxTokens,
        int systemReservedTokens,
        PriorityWeights weights,
        Guid? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        var budgetId = Guid.CreateVersion7();

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        const string sql = """

                                       INSERT INTO context_budgets (
                                           budget_id, session_id, max_tokens, system_reserved_tokens,
                                           used_tokens, memory_allocation, priority_weights
                                       ) VALUES (
                                           @BudgetId, @SessionId, @MaxTokens, @SystemReservedTokens,
                                           0, '{}'::jsonb, @PriorityWeights::jsonb
                                       )
                           """;

        await connection.ExecuteAsync(sql, new
        {
            BudgetId = budgetId,
            SessionId = sessionId,
            MaxTokens = maxTokens,
            SystemReservedTokens = systemReservedTokens,
            PriorityWeights = JsonSerializer.Serialize(weights)
        });

        logger.LogDebug(
            "Created context budget {BudgetId}: {MaxTokens} max, {Reserved} reserved",
            budgetId, maxTokens, systemReservedTokens);

        return new ContextBudget(
            budgetId,
            sessionId,
            maxTokens,
            systemReservedTokens,
            0,
            maxTokens - systemReservedTokens,
            weights,
            DateTime.UtcNow,
            DateTime.UtcNow);
    }

    public async Task<PackedContext> PackContextAsync(
        Guid budgetId,
        IEnumerable<MemoryCandidate> candidates,
        CancellationToken cancellationToken = default)
    {
        var budget = await GetBudgetAsync(budgetId, cancellationToken);
        if (budget == null)
        {
            throw new InvalidOperationException($"Budget {budgetId} not found");
        }

        var candidateList = candidates.ToList();

        // Calculate priority scores for each candidate
        var scoredCandidates = candidateList
            .Select(c => (
                Candidate: c,
                PriorityScore: ComputePriorityScore(c, budget.Weights)))
            .OrderByDescending(x => x.PriorityScore)
            .ToList();

        // Greedy knapsack packing
        var availableTokens = budget.AvailableTokens;
        var includedMemories = new List<PackedMemory>();
        var inclusionOrder = 0;
        var totalTokensUsed = 0;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        foreach (var (candidate, priorityScore) in scoredCandidates)
        {
            if (candidate.TokenCount > availableTokens)
            {
                continue;
            }

            includedMemories.Add(new PackedMemory(
                candidate.MemoryId,
                candidate.Content,
                candidate.TokenCount,
                priorityScore,
                ++inclusionOrder));

            availableTokens -= candidate.TokenCount;
            totalTokensUsed += candidate.TokenCount;

            // Store in priority queue
            const string insertQueueSql = """

                                                          INSERT INTO memory_priority_queue (
                                                              budget_id, memory_id, priority_score, token_count,
                                                              is_included, inclusion_order
                                                          ) VALUES (
                                                              @BudgetId, @MemoryId, @PriorityScore, @TokenCount,
                                                              TRUE, @InclusionOrder
                                                          )
                                                          ON CONFLICT (budget_id, memory_id) DO UPDATE SET
                                                              priority_score = EXCLUDED.priority_score,
                                                              is_included = EXCLUDED.is_included,
                                                              inclusion_order = EXCLUDED.inclusion_order
                                          """;

            await connection.ExecuteAsync(insertQueueSql, new
            {
                BudgetId = budgetId,
                candidate.MemoryId,
                PriorityScore = priorityScore,
                candidate.TokenCount,
                InclusionOrder = inclusionOrder
            });
        }

        // Store excluded memories too (for debugging)
        var excludedCount = candidateList.Count - includedMemories.Count;
        foreach (var (candidate, priorityScore) in scoredCandidates.Where(x =>
            !includedMemories.Any(m => m.MemoryId == x.Candidate.MemoryId)))
        {
            const string insertExcludedSql = """

                                                             INSERT INTO memory_priority_queue (
                                                                 budget_id, memory_id, priority_score, token_count,
                                                                 is_included, inclusion_order
                                                             ) VALUES (
                                                                 @BudgetId, @MemoryId, @PriorityScore, @TokenCount,
                                                                 FALSE, NULL
                                                             )
                                                             ON CONFLICT (budget_id, memory_id) DO UPDATE SET
                                                                 priority_score = EXCLUDED.priority_score,
                                                                 is_included = FALSE,
                                                                 inclusion_order = NULL
                                             """;

            await connection.ExecuteAsync(insertExcludedSql, new
            {
                BudgetId = budgetId,
                candidate.MemoryId,
                PriorityScore = priorityScore,
                candidate.TokenCount
            });
        }

        // Update budget usage
        const string updateBudgetSql = """

                                                   UPDATE context_budgets
                                                   SET used_tokens = @UsedTokens,
                                                       memory_allocation = @Allocation::jsonb,
                                                       updated_at = NOW()
                                                   WHERE budget_id = @BudgetId
                                       """;

        var allocation = new Dictionary<string, object>
        {
            ["included_count"] = includedMemories.Count,
            ["excluded_count"] = excludedCount,
            ["total_candidates"] = candidateList.Count
        };

        await connection.ExecuteAsync(updateBudgetSql, new
        {
            BudgetId = budgetId,
            UsedTokens = totalTokensUsed,
            Allocation = JsonSerializer.Serialize(allocation)
        });

        // Calculate coverage score (how much priority was captured)
        var totalPossiblePriority = scoredCandidates.Sum(x => x.PriorityScore);
        var capturedPriority = includedMemories.Sum(m => m.PriorityScore);
        var coverageScore = totalPossiblePriority > 0 ? capturedPriority / totalPossiblePriority : 1f;

        logger.LogDebug(
            "Packed context for budget {BudgetId}: {Included}/{Total} memories, {Tokens} tokens, {Coverage:P2} coverage",
            budgetId, includedMemories.Count, candidateList.Count, totalTokensUsed, coverageScore);

        return new PackedContext(
            budgetId,
            includedMemories,
            totalTokensUsed,
            includedMemories.Count,
            excludedCount,
            coverageScore);
    }

    public Task<int> EstimateTokensAsync(string content, CancellationToken cancellationToken = default)
    {
        // Simple token estimation based on character count
        // More accurate estimation would use the actual tokenizer
        var charCount = content.Length;
        var estimatedTokens = (int)(charCount * TokensPerCharacter) + MemoryFormatOverhead;

        return Task.FromResult(estimatedTokens);
    }

    public async Task<ContextBudget?> GetBudgetAsync(
        Guid budgetId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        const string sql = """

                                       SELECT budget_id, session_id, max_tokens, system_reserved_tokens,
                                              used_tokens, priority_weights::text, created_at, updated_at
                                       FROM context_budgets
                                       WHERE budget_id = @BudgetId
                           """;

        var row = await connection.QuerySingleOrDefaultAsync<BudgetRow>(sql, new { BudgetId = budgetId });

        if (row == null)
        {
            return null;
        }

        var weights = JsonSerializer.Deserialize<PriorityWeights>(row.priority_weights)!;

        return new ContextBudget(
            row.budget_id,
            row.session_id,
            row.max_tokens,
            row.system_reserved_tokens,
            row.used_tokens,
            row.max_tokens - row.system_reserved_tokens - row.used_tokens,
            weights,
            row.created_at,
            row.updated_at);
    }

    private static float ComputePriorityScore(MemoryCandidate candidate, PriorityWeights weights)
    {
        return candidate.RecencyScore * weights.RecencyWeight
             + candidate.RelevanceScore * weights.RelevanceWeight
             + candidate.ConfidenceScore * weights.ConfidenceWeight
             + candidate.AffinityScore * weights.AffinityWeight
             + candidate.DirectiveScore * weights.DirectiveWeight;
    }

    private record BudgetRow(
        Guid budget_id,
        Guid? session_id,
        int max_tokens,
        int system_reserved_tokens,
        int used_tokens,
        string priority_weights,
        DateTime created_at,
        DateTime updated_at);
}

/// <summary>
/// Token-aware memory packer with advanced algorithms.
/// </summary>
public sealed class AdvancedContextPacker(
    IContextBudgetOptimizer budgetOptimizer,
    ILogger<AdvancedContextPacker> logger)
{
    /// <summary>
    /// Packs memories using dynamic programming for optimal coverage.
    /// </summary>
    public async Task<PackedContext> PackOptimalAsync(
        Guid budgetId,
        IEnumerable<MemoryCandidate> candidates,
        CancellationToken cancellationToken = default)
    {
        var budget = await budgetOptimizer.GetBudgetAsync(budgetId, cancellationToken);
        if (budget == null)
        {
            throw new InvalidOperationException($"Budget {budgetId} not found");
        }

        var candidateList = candidates.ToList();
        var n = candidateList.Count;
        var capacity = budget.AvailableTokens;

        if (n == 0 || capacity <= 0)
        {
            return new PackedContext(budgetId, new List<PackedMemory>(), 0, 0, 0, 0f);
        }

        // For large inputs, use greedy approximation
        if (n > 1000 || capacity > 100000)
        {
            logger.LogDebug("Using greedy approximation for large input: {Count} candidates, {Capacity} capacity", n, capacity);
            return await budgetOptimizer.PackContextAsync(budgetId, candidates, cancellationToken);
        }

        // Dynamic programming 0/1 knapsack
        // dp[j] = best priority achievable with j tokens
        var dp = new float[capacity + 1];
        var selected = new List<int>[capacity + 1];

        for (int j = 0; j <= capacity; j++)
        {
            selected[j] = new List<int>();
        }

        // Compute priority scores
        var scores = candidateList
            .Select((c, idx) => (
                Index: idx,
                Candidate: c,
                Priority: ComputePriorityScore(c, budget.Weights)))
            .ToList();

        // DP iteration
        for (int i = 0; i < n; i++)
        {
            var (idx, candidate, priority) = scores[i];
            var tokens = candidate.TokenCount;

            // Iterate backwards to avoid using same item twice
            for (int j = capacity; j >= tokens; j--)
            {
                var withItem = dp[j - tokens] + priority;
                if (withItem > dp[j])
                {
                    dp[j] = withItem;
                    selected[j] = new List<int>(selected[j - tokens]) { idx };
                }
            }
        }

        // Find best solution
        var bestCapacity = capacity;
        for (int j = capacity - 1; j >= 0; j--)
        {
            if (dp[j] > dp[bestCapacity])
            {
                bestCapacity = j;
            }
        }

        // Build result
        var includedMemories = new List<PackedMemory>();
        var inclusionOrder = 0;
        var totalTokens = 0;

        foreach (var idx in selected[bestCapacity].OrderByDescending(i => scores[i].Priority))
        {
            var candidate = candidateList[idx];
            includedMemories.Add(new PackedMemory(
                candidate.MemoryId,
                candidate.Content,
                candidate.TokenCount,
                scores[idx].Priority,
                ++inclusionOrder));
            totalTokens += candidate.TokenCount;
        }

        var totalPriority = scores.Sum(s => s.Priority);
        var capturedPriority = includedMemories.Sum(m => m.PriorityScore);
        var coverage = totalPriority > 0 ? capturedPriority / totalPriority : 1f;

        logger.LogDebug(
            "Optimal packing: {Included}/{Total} memories, {Tokens} tokens, {Coverage:P2} coverage",
            includedMemories.Count, n, totalTokens, coverage);

        return new PackedContext(
            budgetId,
            includedMemories,
            totalTokens,
            includedMemories.Count,
            n - includedMemories.Count,
            coverage);
    }

    /// <summary>
    /// Packs with guaranteed system space and tiered priorities.
    /// </summary>
    public async Task<TieredPackedContext> PackTieredAsync(
        Guid budgetId,
        TieredCandidates tieredCandidates,
        CancellationToken cancellationToken = default)
    {
        var budget = await budgetOptimizer.GetBudgetAsync(budgetId, cancellationToken);
        if (budget == null)
        {
            throw new InvalidOperationException($"Budget {budgetId} not found");
        }

        var results = new Dictionary<string, List<PackedMemory>>();
        var remainingTokens = budget.AvailableTokens;

        // Pack each tier in priority order
        foreach (var tier in tieredCandidates.Tiers.OrderByDescending(t => t.Priority))
        {
            var tierBudget = Math.Min(tier.MaxTokens, remainingTokens);

            if (tierBudget <= 0)
            {
                results[tier.TierName] = new List<PackedMemory>();
                continue;
            }

            var tierResults = PackGreedy(tier.Candidates.ToList(), tierBudget, budget.Weights);
            results[tier.TierName] = tierResults;

            var tierTokens = tierResults.Sum(m => m.TokenCount);
            remainingTokens -= tierTokens;
        }

        var totalTokens = results.Values.SelectMany(m => m).Sum(m => m.TokenCount);
        var totalIncluded = results.Values.SelectMany(m => m).Count();
        var totalExcluded = tieredCandidates.Tiers.Sum(t => t.Candidates.Count()) - totalIncluded;

        return new TieredPackedContext(
            budgetId,
            results,
            totalTokens,
            totalIncluded,
            totalExcluded);
    }

    private static List<PackedMemory> PackGreedy(
        List<MemoryCandidate> candidates,
        int budget,
        PriorityWeights weights)
    {
        var scored = candidates
            .Select(c => (Candidate: c, Priority: ComputePriorityScore(c, weights)))
            .OrderByDescending(x => x.Priority)
            .ToList();

        var results = new List<PackedMemory>();
        var remaining = budget;
        var order = 0;

        foreach (var (candidate, priority) in scored)
        {
            if (candidate.TokenCount > remaining) continue;

            results.Add(new PackedMemory(
                candidate.MemoryId,
                candidate.Content,
                candidate.TokenCount,
                priority,
                ++order));

            remaining -= candidate.TokenCount;
        }

        return results;
    }

    private static float ComputePriorityScore(MemoryCandidate candidate, PriorityWeights weights)
    {
        return candidate.RecencyScore * weights.RecencyWeight
             + candidate.RelevanceScore * weights.RelevanceWeight
             + candidate.ConfidenceScore * weights.ConfidenceWeight
             + candidate.AffinityScore * weights.AffinityWeight
             + candidate.DirectiveScore * weights.DirectiveWeight;
    }
}

public record TieredCandidates(IReadOnlyList<CandidateTier> Tiers);

public record CandidateTier(
    string TierName,
    int Priority,
    int MaxTokens,
    IEnumerable<MemoryCandidate> Candidates);

public record TieredPackedContext(
    Guid BudgetId,
    IReadOnlyDictionary<string, List<PackedMemory>> TierResults,
    int TotalTokens,
    int TotalIncluded,
    int TotalExcluded);
