using SerialMemory.Core.Interfaces;

namespace SerialMemory.Infrastructure.DependencyInjection;

/// <summary>
/// No-op implementations for disabled engines.
/// These provide safe fallbacks when features are disabled.
/// </summary>

public sealed class NoOpEmbeddingCache : IEmbeddingCache
{
    public Task<float[]?> GetAsync(string contentHash, CancellationToken cancellationToken = default)
        => Task.FromResult<float[]?>(null);

    public Task SetAsync(string contentHash, float[] embedding, string modelVersion, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task<bool> ExistsAsync(string contentHash, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    public Task MarkCompiledAsync(string contentHash, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task<int> GetCompiledCountAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(0);

    public Task PruneUnusedAsync(TimeSpan maxAge, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}

public sealed class NoOpInferenceSessionManager : IInferenceSessionManager
{
    public Task<IInferenceSession> CreateSessionAsync(
        long? seed = null,
        InferenceConfig? config = null,
        Guid? parentSessionId = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IInferenceSession>(new NoOpInferenceSession());

    public Task<IInferenceSession> ReplaySessionAsync(
        Guid sourceSessionId,
        long? overrideSeed = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IInferenceSession>(new NoOpInferenceSession());

    public Task<IInferenceSession?> GetSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
        => Task.FromResult<IInferenceSession?>(null);

    public Task<ReplayVerificationResult> VerifyReplayAsync(
        Guid originalSessionId,
        Guid replaySessionId,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new ReplayVerificationResult(
            originalSessionId, replaySessionId, true, 0, 0, Array.Empty<DivergencePoint>(), "noop"));
}

public sealed class NoOpInferenceSession : IInferenceSession
{
    public Guid SessionId => Guid.Empty;
    public long Seed => 0;
    public string SessionHash => "noop";
    public InferenceSessionStatus Status => InferenceSessionStatus.Completed;
    public Guid? ParentSessionId => null;

    public Task<TOutput> ExecuteStepAsync<TInput, TOutput>(
        string stepType,
        TInput input,
        Func<TInput, CancellationToken, Task<TOutput>> executor,
        CancellationToken cancellationToken = default)
        => executor(input, cancellationToken);

    public Task<ReasoningStep> GetStepAsync(int stepSequence, CancellationToken cancellationToken = default)
        => Task.FromResult(new ReasoningStep(Guid.Empty, Guid.Empty, 0, "noop", "", "", new { }, new { }, null, 0, DateTime.UtcNow));

    public Task<IReadOnlyList<ReasoningStep>> GetAllStepsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<ReasoningStep>>(Array.Empty<ReasoningStep>());

    public Task CompleteAsync(CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class NoOpMemoryCompiler : IMemoryCompiler
{
    public Task<CompilationResult> CompileAsync(
        CompilationOptions options,
        IProgress<CompilationProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new CompilationResult(0, 0, 0, 0, TimeSpan.Zero, 0));

    public Task<CompilationResult> IncrementalCompileAsync(
        DateTime since,
        IProgress<CompilationProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new CompilationResult(0, 0, 0, 0, TimeSpan.Zero, 0));

    public Task<IReadOnlyList<MemoryCluster>> GetClustersAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<MemoryCluster>>(Array.Empty<MemoryCluster>());

    public Task<IReadOnlyList<Guid>> GetClusterMembersAsync(Guid clusterId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<Guid>>(Array.Empty<Guid>());

    public Task InvalidateClusterAsync(Guid clusterId, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}

public sealed class NoOpContextBudgetOptimizer : IContextBudgetOptimizer
{
    public Task<ContextBudget> CreateBudgetAsync(
        int maxTokens,
        int systemReservedTokens,
        PriorityWeights weights,
        Guid? sessionId = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new ContextBudget(
            Guid.CreateVersion7(), sessionId, maxTokens, systemReservedTokens, 0,
            maxTokens - systemReservedTokens, weights, DateTime.UtcNow, DateTime.UtcNow));

    public Task<PackedContext> PackContextAsync(
        Guid budgetId,
        IEnumerable<MemoryCandidate> candidates,
        CancellationToken cancellationToken = default)
    {
        var list = candidates.ToList();
        return Task.FromResult(new PackedContext(
            budgetId,
            list.Select((c, i) => new PackedMemory(c.MemoryId, c.Content, c.TokenCount, c.RelevanceScore, i)).ToList(),
            list.Sum(c => c.TokenCount),
            list.Count,
            0,
            1.0f));
    }

    public Task<int> EstimateTokensAsync(string content, CancellationToken cancellationToken = default)
        => Task.FromResult((int)(content.Length * 0.3f) + 20);

    public Task<ContextBudget?> GetBudgetAsync(Guid budgetId, CancellationToken cancellationToken = default)
        => Task.FromResult<ContextBudget?>(null);
}

public sealed class NoOpLocalEncryption : ILocalEncryption
{
    public Task<EncryptionKey> CreateKeyAsync(string keyName, CancellationToken cancellationToken = default)
        => Task.FromResult(new EncryptionKey(Guid.Empty, keyName, "none", 0, DateTime.UtcNow, null, false));

    public Task<EncryptionKey?> GetActiveKeyAsync(string keyName, CancellationToken cancellationToken = default)
        => Task.FromResult<EncryptionKey?>(null);

    public Task RotateKeyAsync(string keyName, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task<EncryptedData> EncryptAsync(byte[] plaintext, Guid keyId, CancellationToken cancellationToken = default)
        => Task.FromResult(new EncryptedData(plaintext, Array.Empty<byte>(), Guid.Empty, "none", ""));

    public Task<byte[]> DecryptAsync(EncryptedData encryptedData, CancellationToken cancellationToken = default)
        => Task.FromResult(encryptedData.Ciphertext);

    public Task<Guid> StoreEncryptedMemoryAsync(Guid memoryId, string content, Guid keyId, CancellationToken cancellationToken = default)
        => Task.FromResult(memoryId);

    public Task<string> RetrieveDecryptedMemoryAsync(Guid memoryId, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("LocalEncryption feature is disabled");
}

public sealed class NoOpDualPassReasoning : IDualPassReasoning
{
    public Task<DualPassResult> ReasonAsync(
        DualPassInput input,
        Guid sessionId,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new DualPassResult(
            Guid.Empty, sessionId, new { }, new { }, new { }, 0f, 0f, Array.Empty<string>(), 0, 0));

    public Task<DualPassResult?> GetReasoningResultAsync(Guid reasoningId, CancellationToken cancellationToken = default)
        => Task.FromResult<DualPassResult?>(null);
}

public sealed class NoOpGraphRecrawlService : IGraphRecrawlService
{
    public Task<Guid> CreateRecrawlJobAsync(Guid tenantId, int batchSize = 100, CancellationToken cancellationToken = default)
        => Task.FromResult(Guid.Empty);

    public Task<RecrawlJobResult> ProcessRecrawlJobAsync(Guid jobId, CancellationToken cancellationToken = default)
        => Task.FromResult(new RecrawlJobResult(jobId, "disabled", 0, 0, 0));

    public Task<RecrawlStatistics> GetStatisticsAsync(Guid tenantId, CancellationToken cancellationToken = default)
        => Task.FromResult(new RecrawlStatistics(0, 0, 0, 0, 0, 0));

    public Task<int> RetryFailedMemoriesAsync(Guid tenantId, int maxRetries = 1, CancellationToken cancellationToken = default)
        => Task.FromResult(0);
}

public sealed class NoOpTimeTravelDebugger : ITimeTravelDebugger
{
    public Task<MemorySnapshot> CreateSnapshotAsync(string snapshotType, CancellationToken cancellationToken = default)
        => Task.FromResult(new MemorySnapshot(
            Guid.Empty, DateTime.UtcNow, snapshotType, 0, 0, 0, 0, "noop", DateTime.UtcNow, null));

    public Task<MemoryStateAtTime> GetStateAtAsync(Guid memoryId, DateTime timestamp, CancellationToken cancellationToken = default)
        => Task.FromResult(new MemoryStateAtTime(memoryId, timestamp, "", null, 0f, "L0_RAW", false, null));

    public Task<GraphStateAtTime> GetGraphStateAtAsync(DateTime timestamp, int limit = 1000, CancellationToken cancellationToken = default)
        => Task.FromResult(new GraphStateAtTime(timestamp, Array.Empty<MemoryStateAtTime>(), 0));

    public Task<IReadOnlyList<MemorySnapshot>> GetSnapshotsAsync(
        DateTime? since = null,
        DateTime? until = null,
        string? snapshotType = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<MemorySnapshot>>(Array.Empty<MemorySnapshot>());

    public Task<ReasoningReplay> ReplayReasoningAsync(Guid sessionId, DateTime? untilTimestamp = null, CancellationToken cancellationToken = default)
        => Task.FromResult(new ReasoningReplay(sessionId, untilTimestamp ?? DateTime.UtcNow, Array.Empty<ReasoningStep>(), new Dictionary<string, object>()));
}
