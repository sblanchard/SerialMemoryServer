namespace SerialMemory.Core.Interfaces;

/// <summary>
/// Deterministic inference session with seeded execution.
/// </summary>
public interface IInferenceSession : IAsyncDisposable
{
    Guid SessionId { get; }
    long Seed { get; }
    string SessionHash { get; }
    InferenceSessionStatus Status { get; }
    Guid? ParentSessionId { get; }

    Task<TOutput> ExecuteStepAsync<TInput, TOutput>(
        string stepType,
        TInput input,
        Func<TInput, CancellationToken, Task<TOutput>> executor,
        CancellationToken cancellationToken = default);

    Task<ReasoningStep> GetStepAsync(int stepSequence, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ReasoningStep>> GetAllStepsAsync(CancellationToken cancellationToken = default);
    Task CompleteAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Manages deterministic inference sessions with replay support.
/// </summary>
public interface IInferenceSessionManager
{
    Task<IInferenceSession> CreateSessionAsync(
        long? seed = null,
        InferenceConfig? config = null,
        Guid? parentSessionId = null,
        CancellationToken cancellationToken = default);

    Task<IInferenceSession> ReplaySessionAsync(
        Guid sourceSessionId,
        long? overrideSeed = null,
        CancellationToken cancellationToken = default);

    Task<IInferenceSession?> GetSessionAsync(Guid sessionId, CancellationToken cancellationToken = default);

    Task<ReplayVerificationResult> VerifyReplayAsync(
        Guid originalSessionId,
        Guid replaySessionId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Embedding cache for deterministic retrieval.
/// </summary>
public interface IEmbeddingCache
{
    Task<float[]?> GetAsync(string contentHash, CancellationToken cancellationToken = default);
    Task SetAsync(string contentHash, float[] embedding, string modelVersion, CancellationToken cancellationToken = default);
    Task<bool> ExistsAsync(string contentHash, CancellationToken cancellationToken = default);
    Task MarkCompiledAsync(string contentHash, CancellationToken cancellationToken = default);
    Task<int> GetCompiledCountAsync(CancellationToken cancellationToken = default);
    Task PruneUnusedAsync(TimeSpan maxAge, CancellationToken cancellationToken = default);
}

/// <summary>
/// Memory pre-compilation for fast semantic lookup.
/// </summary>
public interface IMemoryCompiler
{
    Task<CompilationResult> CompileAsync(
        CompilationOptions options,
        IProgress<CompilationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<CompilationResult> IncrementalCompileAsync(
        DateTime since,
        IProgress<CompilationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MemoryCluster>> GetClustersAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Guid>> GetClusterMembersAsync(Guid clusterId, CancellationToken cancellationToken = default);
    Task InvalidateClusterAsync(Guid clusterId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Hybrid retrieval engine combining multiple retrieval strategies.
/// </summary>
public interface IHybridRetrievalEngine
{
    Task<HybridRetrievalResult> RetrieveAsync(
        HybridRetrievalQuery query,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SymbolicRule>> GetActiveRulesAsync(CancellationToken cancellationToken = default);
    Task AddRuleAsync(SymbolicRule rule, CancellationToken cancellationToken = default);
    Task<bool> RemoveRuleAsync(Guid ruleId, CancellationToken cancellationToken = default);
    Task<bool> UpdateRuleAsync(SymbolicRule rule, CancellationToken cancellationToken = default);
}

/// <summary>
/// Context budget optimization for token management.
/// </summary>
public interface IContextBudgetOptimizer
{
    Task<ContextBudget> CreateBudgetAsync(
        int maxTokens,
        int systemReservedTokens,
        PriorityWeights weights,
        Guid? sessionId = null,
        CancellationToken cancellationToken = default);

    Task<PackedContext> PackContextAsync(
        Guid budgetId,
        IEnumerable<MemoryCandidate> candidates,
        CancellationToken cancellationToken = default);

    Task<int> EstimateTokensAsync(string content, CancellationToken cancellationToken = default);
    Task<ContextBudget?> GetBudgetAsync(Guid budgetId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Memory self-healing for contradiction detection and resolution.
/// </summary>
public interface IMemorySelfHealing
{
    Task<SelfHealingResult> RunCycleAsync(
        SelfHealingOptions options,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MemoryContradiction>> DetectContradictionsAsync(
        Guid memoryId,
        CancellationToken cancellationToken = default);

    Task<MergeResult> MergeSimilarMemoriesAsync(
        IEnumerable<Guid> memoryIds,
        float similarityThreshold,
        CancellationToken cancellationToken = default);

    Task<int> ApplyDecayAsync(
        TimeSpan maxAge,
        float decayFactor,
        CancellationToken cancellationToken = default);

    Task<int> ReinforceMemoriesAsync(
        IEnumerable<Guid> memoryIds,
        float reinforcementFactor,
        string reason,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Local encryption for memory at rest.
/// </summary>
public interface ILocalEncryption
{
    Task<EncryptionKey> CreateKeyAsync(string keyName, CancellationToken cancellationToken = default);
    Task<EncryptionKey?> GetActiveKeyAsync(string keyName, CancellationToken cancellationToken = default);
    Task RotateKeyAsync(string keyName, CancellationToken cancellationToken = default);

    Task<EncryptedData> EncryptAsync(byte[] plaintext, Guid keyId, CancellationToken cancellationToken = default);
    Task<byte[]> DecryptAsync(EncryptedData encryptedData, CancellationToken cancellationToken = default);

    Task<Guid> StoreEncryptedMemoryAsync(Guid memoryId, string content, Guid keyId, CancellationToken cancellationToken = default);
    Task<string> RetrieveDecryptedMemoryAsync(Guid memoryId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Dual-pass local reasoning through ONNX.
/// </summary>
public interface IDualPassReasoning
{
    Task<DualPassResult> ReasonAsync(
        DualPassInput input,
        Guid sessionId,
        CancellationToken cancellationToken = default);

    Task<DualPassResult?> GetReasoningResultAsync(
        Guid reasoningId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Time-travel debugging for memory state.
/// </summary>
public interface ITimeTravelDebugger
{
    Task<MemorySnapshot> CreateSnapshotAsync(
        string snapshotType,
        CancellationToken cancellationToken = default);

    Task<MemoryStateAtTime> GetStateAtAsync(
        Guid memoryId,
        DateTime timestamp,
        CancellationToken cancellationToken = default);

    Task<GraphStateAtTime> GetGraphStateAtAsync(
        DateTime timestamp,
        int limit = 1000,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MemorySnapshot>> GetSnapshotsAsync(
        DateTime? since = null,
        DateTime? until = null,
        string? snapshotType = null,
        CancellationToken cancellationToken = default);

    Task<ReasoningReplay> ReplayReasoningAsync(
        Guid sessionId,
        DateTime? untilTimestamp = null,
        CancellationToken cancellationToken = default);
}

// Supporting types

public enum InferenceSessionStatus
{
    Active,
    Completed,
    Failed,
    Replaying
}

public record InferenceConfig(
    string ModelVersion,
    Dictionary<string, object> Parameters);

public record ReasoningStep(
    Guid StepId,
    Guid SessionId,
    int StepSequence,
    string StepType,
    string InputHash,
    string OutputHash,
    object InputData,
    object OutputData,
    string? EmbeddingCacheKey,
    int DurationMs,
    DateTime CreatedAt);

public record ReplayVerificationResult(
    Guid OriginalSessionId,
    Guid ReplaySessionId,
    bool IsDeterministic,
    int StepsReplayed,
    int StepsDiverged,
    IReadOnlyList<DivergencePoint> DivergencePoints,
    string VerificationHash);

public record DivergencePoint(
    int StepSequence,
    string StepType,
    string OriginalOutputHash,
    string ReplayOutputHash,
    object? OriginalOutput,
    object? ReplayOutput);

public record CompilationResult(
    int MemoriesProcessed,
    int ClustersCreated,
    int ClustersUpdated,
    int IndexEntriesCreated,
    TimeSpan Duration,
    int CompilationVersion);

public record CompilationProgress(
    int CurrentStep,
    int TotalSteps,
    string CurrentOperation,
    int ItemsProcessed,
    int TotalItems);

public record CompilationOptions(
    int MinClusterSize,
    float ClusterRadius,
    bool RebuildIndexes,
    int MaxClusters);

public record MemoryCluster(
    Guid ClusterId,
    string? ClusterName,
    float[] Centroid,
    float ClusterRadius,
    int MemoryCount,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    int CompilationVersion,
    bool IsStable,
    Guid? ParentClusterId);

public record HybridRetrievalQuery(
    string QueryText,
    float[]? QueryEmbedding,
    int Limit,
    float SemanticThreshold,
    bool UseSymbolicRules,
    bool UseGraphTraversal,
    bool UseTemporalScoring,
    DateTime? TemporalAnchor,
    Dictionary<string, object>? FilterCriteria);

public record HybridRetrievalResult(
    IReadOnlyList<ScoredMemory> Memories,
    IReadOnlyList<string> AppliedRules,
    IReadOnlyList<Guid> TraversedEntities,
    RetrievalScoreBreakdown ScoreBreakdown,
    TimeSpan Duration);

public record ScoredMemory(
    Guid MemoryId,
    string Content,
    float FinalScore,
    float SemanticScore,
    float SymbolicScore,
    float GraphScore,
    float TemporalScore,
    float ConfidenceScore,
    DateTime CreatedAt,
    IReadOnlyList<string> MatchedRules);

public record RetrievalScoreBreakdown(
    float SemanticWeight,
    float SymbolicWeight,
    float GraphWeight,
    float TemporalWeight,
    float ConfidenceWeight);

public record SymbolicRule(
    Guid RuleId,
    string RuleName,
    string RuleType,
    string ConditionExpression,
    int Priority,
    float Weight,
    bool IsActive,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    Dictionary<string, object>? Metadata);

public record ContextBudget(
    Guid BudgetId,
    Guid? SessionId,
    int MaxTokens,
    int SystemReservedTokens,
    int UsedTokens,
    int AvailableTokens,
    PriorityWeights Weights,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public record PriorityWeights(
    float RecencyWeight,
    float RelevanceWeight,
    float ConfidenceWeight,
    float AffinityWeight,
    float DirectiveWeight);

public record MemoryCandidate(
    Guid MemoryId,
    string Content,
    int TokenCount,
    float RelevanceScore,
    float ConfidenceScore,
    float RecencyScore,
    float AffinityScore,
    float DirectiveScore,
    DateTime CreatedAt);

public record PackedContext(
    Guid BudgetId,
    IReadOnlyList<PackedMemory> IncludedMemories,
    int TotalTokens,
    int MemoriesIncluded,
    int MemoriesExcluded,
    float CoverageScore);

public record PackedMemory(
    Guid MemoryId,
    string Content,
    int TokenCount,
    float PriorityScore,
    int InclusionOrder);

public record SelfHealingOptions(
    bool DetectContradictions,
    bool MergeDuplicates,
    bool ApplyDecay,
    bool ReinforceStable,
    float SimilarityThreshold,
    TimeSpan DecayMaxAge,
    float DecayFactor,
    int MaxOperationsPerCycle);

public record SelfHealingResult(
    int ContradictionsDetected,
    int MemoriesMerged,
    int MemoriesDecayed,
    int MemoriesReinforced,
    TimeSpan Duration,
    IReadOnlyList<SelfHealingOperation> Operations);

public record SelfHealingOperation(
    string OperationType,
    IReadOnlyList<Guid> TargetMemoryIds,
    Guid? ResultMemoryId,
    string Result,
    int DurationMs);

public record MemoryContradiction(
    Guid ContradictionId,
    Guid MemoryIdA,
    Guid MemoryIdB,
    string ContradictionType,
    float Confidence,
    DateTime DetectedAt,
    DateTime? ResolvedAt,
    string? ResolutionType,
    Guid? ResolutionMemoryId,
    string DetectionMethod,
    Dictionary<string, object> Evidence);

public record MergeResult(
    Guid TargetMemoryId,
    IReadOnlyList<Guid> SourceMemoryIds,
    float SimilarityScore,
    string MergeType);

public record EncryptionKey(
    Guid KeyId,
    string KeyName,
    string KeyAlgorithm,
    int KeyVersion,
    DateTime CreatedAt,
    DateTime? RotatedAt,
    bool IsActive);

public record EncryptedData(
    byte[] Ciphertext,
    byte[] Iv,
    Guid KeyId,
    string Algorithm,
    string ContentHash);

public record DualPassInput(
    string Query,
    IReadOnlyList<ScoredMemory> RetrievedMemories,
    Dictionary<string, object>? Context);

public record DualPassResult(
    Guid ReasoningId,
    Guid SessionId,
    object DraftOutput,
    object Critique,
    object FinalOutput,
    float ConfidenceBefore,
    float ConfidenceAfter,
    IReadOnlyList<string> ImprovementsMade,
    int Pass1DurationMs,
    int Pass2DurationMs);

public record MemorySnapshot(
    Guid SnapshotId,
    DateTime SnapshotTimestamp,
    string SnapshotType,
    int MemoryCount,
    int EntityCount,
    int RelationshipCount,
    int ClusterCount,
    string CheckpointHash,
    DateTime CreatedAt,
    Dictionary<string, object>? Metadata);

public record MemoryStateAtTime(
    Guid MemoryId,
    DateTime StateAt,
    string Content,
    float[]? Embedding,
    float Confidence,
    string Layer,
    bool IsActive,
    Guid? SourceEventId);

public record GraphStateAtTime(
    DateTime Timestamp,
    IReadOnlyList<MemoryStateAtTime> Memories,
    int TotalCount);

public record ReasoningReplay(
    Guid SessionId,
    DateTime ReplayUntil,
    IReadOnlyList<ReasoningStep> Steps,
    Dictionary<string, object> FinalState);
