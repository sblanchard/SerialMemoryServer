using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using SerialMemory.Core.Interfaces;

namespace SerialMemory.Infrastructure.DeterministicInference;

public sealed class InferenceSessionManager(NpgsqlDataSource dataSource, ILogger<InferenceSessionManager> logger)
    : IInferenceSessionManager
{
    public async Task<IInferenceSession> CreateSessionAsync(
        long? seed = null,
        InferenceConfig? config = null,
        Guid? parentSessionId = null,
        CancellationToken cancellationToken = default)
    {
        var actualSeed = seed ?? GenerateDeterministicSeed();
        var sessionId = Guid.CreateVersion7();
        var configSnapshot = config ?? new InferenceConfig("onnx-minilm-v2", new Dictionary<string, object>());

        var inputHash = ComputeHash($"{actualSeed}:{JsonSerializer.Serialize(configSnapshot)}");
        var sessionHash = ComputeHash($"{sessionId}:{actualSeed}:{inputHash}");

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        const string sql = """

                                       INSERT INTO inference_sessions (
                                           session_id, seed, session_hash, input_hash, config_snapshot,
                                           status, parent_session_id, metadata
                                       ) VALUES (
                                           @SessionId, @Seed, @SessionHash, @InputHash, @ConfigSnapshot::jsonb,
                                           'active', @ParentSessionId, '{}'::jsonb
                                       )
                           """;

        await connection.ExecuteAsync(sql, new
        {
            SessionId = sessionId,
            Seed = actualSeed,
            SessionHash = sessionHash,
            InputHash = inputHash,
            ConfigSnapshot = JsonSerializer.Serialize(configSnapshot),
            ParentSessionId = parentSessionId
        });

        logger.LogInformation("Created inference session {SessionId} with seed {Seed}", sessionId, actualSeed);

        return new InferenceSession(
            sessionId,
            actualSeed,
            sessionHash,
            configSnapshot,
            parentSessionId,
            dataSource,
            logger);
    }

    public async Task<IInferenceSession> ReplaySessionAsync(
        Guid sourceSessionId,
        long? overrideSeed = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        const string getSourceSql = """

                                                SELECT seed, config_snapshot::text as config_snapshot
                                                FROM inference_sessions
                                                WHERE session_id = @SessionId
                                    """;

        var source = await connection.QuerySingleOrDefaultAsync<(long Seed, string ConfigSnapshot)>(
            getSourceSql,
            new { SessionId = sourceSessionId });

        if (source == default)
        {
            throw new InvalidOperationException($"Source session {sourceSessionId} not found");
        }

        var actualSeed = overrideSeed ?? source.Seed;
        var config = JsonSerializer.Deserialize<InferenceConfig>(source.ConfigSnapshot);

        var replaySession = await CreateSessionAsync(actualSeed, config, cancellationToken: cancellationToken);

        const string updateSql = """

                                             UPDATE inference_sessions
                                             SET replay_source_session_id = @SourceSessionId, status = 'replaying'
                                             WHERE session_id = @SessionId
                                 """;

        await connection.ExecuteAsync(updateSql, new
        {
            SourceSessionId = sourceSessionId,
            SessionId = replaySession.SessionId
        });

        logger.LogInformation(
            "Created replay session {ReplaySessionId} from source {SourceSessionId}",
            replaySession.SessionId,
            sourceSessionId);

        return replaySession;
    }

    public async Task<IInferenceSession?> GetSessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        const string sql = """

                                       SELECT session_id, seed, session_hash, config_snapshot::text as config_snapshot,
                                              status, parent_session_id
                                       FROM inference_sessions
                                       WHERE session_id = @SessionId
                           """;

        var row = await connection.QuerySingleOrDefaultAsync<SessionRow>(sql, new { SessionId = sessionId });

        if (row == null)
        {
            return null;
        }

        // Get the current max step sequence to resume from correct position
        const string maxStepSql = """

                                              SELECT COALESCE(MAX(step_sequence), 0)
                                              FROM reasoning_steps
                                              WHERE session_id = @SessionId
                                  """;

        var maxStepSequence = await connection.ExecuteScalarAsync<int>(maxStepSql, new { SessionId = sessionId });

        var config = JsonSerializer.Deserialize<InferenceConfig>(row.config_snapshot);

        return new InferenceSession(
            row.session_id,
            row.seed,
            row.session_hash,
            config!,
            row.parent_session_id,
            dataSource,
            logger,
            Enum.Parse<InferenceSessionStatus>(row.status, true),
            maxStepSequence);
    }

    public async Task<ReplayVerificationResult> VerifyReplayAsync(
        Guid originalSessionId,
        Guid replaySessionId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        const string getStepsSql = """

                                               SELECT step_sequence, step_type, input_hash, output_hash, output_data::text as output_data
                                               FROM reasoning_steps
                                               WHERE session_id = @SessionId
                                               ORDER BY step_sequence
                                   """;

        var originalSteps = (await connection.QueryAsync<StepRow>(
            getStepsSql,
            new { SessionId = originalSessionId })).ToList();

        var replaySteps = (await connection.QueryAsync<StepRow>(
            getStepsSql,
            new { SessionId = replaySessionId })).ToList();

        var divergencePoints = new List<DivergencePoint>();
        var stepsReplayed = Math.Min(originalSteps.Count, replaySteps.Count);
        var stepCountsMatch = originalSteps.Count == replaySteps.Count;

        for (int i = 0; i < stepsReplayed; i++)
        {
            var original = originalSteps[i];
            var replay = replaySteps[i];

            // Check both input and output hashes to ensure full determinism
            if (original.input_hash != replay.input_hash)
            {
                divergencePoints.Add(new DivergencePoint(
                    original.step_sequence,
                    $"{original.step_type}_input_mismatch",
                    original.input_hash,
                    replay.input_hash,
                    $"Input hash mismatch at step {original.step_sequence}",
                    $"Original: {original.input_hash}, Replay: {replay.input_hash}"));
            }

            if (original.output_hash != replay.output_hash)
            {
                divergencePoints.Add(new DivergencePoint(
                    original.step_sequence,
                    original.step_type,
                    original.output_hash,
                    replay.output_hash,
                    JsonSerializer.Deserialize<object>(original.output_data),
                    JsonSerializer.Deserialize<object>(replay.output_data)));
            }
        }

        // Flag step count divergence - replay must have same number of steps as original
        if (!stepCountsMatch)
        {
            var stepCountDiff = replaySteps.Count - originalSteps.Count;
            divergencePoints.Add(new DivergencePoint(
                stepsReplayed,
                "step_count_mismatch",
                originalSteps.Count.ToString(),
                replaySteps.Count.ToString(),
                $"Original session had {originalSteps.Count} steps",
                $"Replay session had {replaySteps.Count} steps (diff: {stepCountDiff:+#;-#;0})"));
        }

        // Deterministic only if: no divergences AND step counts match
        var isDeterministic = divergencePoints.Count == 0 && stepCountsMatch;

        var verificationHash = ComputeHash(
            $"{originalSessionId}:{replaySessionId}:{stepsReplayed}:{divergencePoints.Count}:{stepCountsMatch}");

        const string logReplaySql = """

                                                INSERT INTO replay_executions (
                                                    source_session_id, replay_session_id, completed_at,
                                                    steps_replayed, steps_diverged, divergence_points,
                                                    is_deterministic, verification_hash
                                                ) VALUES (
                                                    @OriginalSessionId, @ReplaySessionId, NOW(),
                                                    @StepsReplayed, @StepsDiverged, @DivergencePoints::jsonb,
                                                    @IsDeterministic, @VerificationHash
                                                )
                                    """;

        await connection.ExecuteAsync(logReplaySql, new
        {
            OriginalSessionId = originalSessionId,
            ReplaySessionId = replaySessionId,
            StepsReplayed = stepsReplayed,
            StepsDiverged = divergencePoints.Count,
            DivergencePoints = JsonSerializer.Serialize(divergencePoints),
            IsDeterministic = isDeterministic,
            VerificationHash = verificationHash
        });

        return new ReplayVerificationResult(
            originalSessionId,
            replaySessionId,
            isDeterministic,
            stepsReplayed,
            divergencePoints.Count,
            divergencePoints,
            verificationHash);
    }

    private static long GenerateDeterministicSeed()
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var random = RandomNumberGenerator.GetInt32(int.MaxValue);
        return timestamp ^ random;
    }

    private static string ComputeHash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private record SessionRow(
        Guid session_id,
        long seed,
        string session_hash,
        string config_snapshot,
        string status,
        Guid? parent_session_id);

    private record StepRow(
        int step_sequence,
        string step_type,
        string input_hash,
        string output_hash,
        string output_data);
}

public sealed class InferenceSession : IInferenceSession
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger _logger;
    private readonly InferenceConfig _config;
    private int _currentStepSequence;
    private bool _disposed;

    public Guid SessionId { get; }
    public long Seed { get; }
    public string SessionHash { get; }
    public InferenceSessionStatus Status { get; private set; }
    public Guid? ParentSessionId { get; }

    internal InferenceSession(
        Guid sessionId,
        long seed,
        string sessionHash,
        InferenceConfig config,
        Guid? parentSessionId,
        NpgsqlDataSource dataSource,
        ILogger logger,
        InferenceSessionStatus status = InferenceSessionStatus.Active,
        int initialStepSequence = 0)
    {
        SessionId = sessionId;
        Seed = seed;
        SessionHash = sessionHash;
        _config = config;
        ParentSessionId = parentSessionId;
        _dataSource = dataSource;
        _logger = logger;
        Status = status;
        _currentStepSequence = initialStepSequence;
    }

    public async Task<TOutput> ExecuteStepAsync<TInput, TOutput>(
        string stepType,
        TInput input,
        Func<TInput, CancellationToken, Task<TOutput>> executor,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var stepSequence = Interlocked.Increment(ref _currentStepSequence);
        var inputJson = JsonSerializer.Serialize(input);
        var inputHash = ComputeHash(inputJson);

        // Check cache first
        var cached = await GetCachedStepOutputAsync<TOutput>(inputHash, stepType, cancellationToken);
        if (cached != null)
        {
            _logger.LogDebug("Cache hit for step {StepType} in session {SessionId}", stepType, SessionId);
            return cached;
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // Apply seed for determinism
        var seededRandom = new Random((int)(Seed + stepSequence));
        var deterministicContext = new DeterministicContext(seededRandom);

        TOutput output;
        using (deterministicContext.Apply())
        {
            output = await executor(input, cancellationToken);
        }

        stopwatch.Stop();

        var outputJson = JsonSerializer.Serialize(output);
        var outputHash = ComputeHash(outputJson);

        await StoreStepAsync(
            stepSequence,
            stepType,
            inputHash,
            outputHash,
            inputJson,
            outputJson,
            (int)stopwatch.ElapsedMilliseconds,
            cancellationToken);

        return output;
    }

    private async Task<TOutput?> GetCachedStepOutputAsync<TOutput>(
        string inputHash,
        string stepType,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        // Scope cache lookup to current session to ensure deterministic replay.
        // Cross-session caching would break determinism if sessions have different seeds.
        const string sql = """

                                       SELECT output_data::text
                                       FROM reasoning_steps
                                       WHERE session_id = @SessionId AND input_hash = @InputHash AND step_type = @StepType
                                       ORDER BY created_at DESC
                                       LIMIT 1
                           """;

        var outputJson = await connection.QuerySingleOrDefaultAsync<string>(
            sql,
            new { SessionId, InputHash = inputHash, StepType = stepType });

        if (outputJson == null)
        {
            return default;
        }

        return JsonSerializer.Deserialize<TOutput>(outputJson);
    }

    private async Task StoreStepAsync(
        int stepSequence,
        string stepType,
        string inputHash,
        string outputHash,
        string inputData,
        string outputData,
        int durationMs,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        const string sql = """

                                       INSERT INTO reasoning_steps (
                                           session_id, step_sequence, step_type, input_hash, output_hash,
                                           input_data, output_data, duration_ms
                                       ) VALUES (
                                           @SessionId, @StepSequence, @StepType, @InputHash, @OutputHash,
                                           @InputData::jsonb, @OutputData::jsonb, @DurationMs
                                       )
                                       ON CONFLICT (session_id, step_sequence)
                                       DO UPDATE SET
                                           output_hash = EXCLUDED.output_hash,
                                           output_data = EXCLUDED.output_data,
                                           duration_ms = EXCLUDED.duration_ms
                           """;

        await connection.ExecuteAsync(sql, new
        {
            SessionId,
            StepSequence = stepSequence,
            StepType = stepType,
            InputHash = inputHash,
            OutputHash = outputHash,
            InputData = inputData,
            OutputData = outputData,
            DurationMs = durationMs
        });
    }

    public async Task<ReasoningStep> GetStepAsync(int stepSequence, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        const string sql = """

                                       SELECT step_id, session_id, step_sequence, step_type, input_hash, output_hash,
                                              input_data::text as input_data, output_data::text as output_data,
                                              embedding_cache_key, duration_ms, created_at
                                       FROM reasoning_steps
                                       WHERE session_id = @SessionId AND step_sequence = @StepSequence
                           """;

        var row = await connection.QuerySingleOrDefaultAsync<dynamic>(
            sql,
            new { SessionId, StepSequence = stepSequence });

        if (row == null)
        {
            throw new InvalidOperationException($"Step {stepSequence} not found in session {SessionId}");
        }

        return new ReasoningStep(
            row.step_id,
            row.session_id,
            row.step_sequence,
            row.step_type,
            row.input_hash,
            row.output_hash,
            JsonSerializer.Deserialize<object>(row.input_data)!,
            JsonSerializer.Deserialize<object>(row.output_data)!,
            row.embedding_cache_key,
            row.duration_ms,
            row.created_at);
    }

    public async Task<IReadOnlyList<ReasoningStep>> GetAllStepsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        const string sql = """

                                       SELECT step_id, session_id, step_sequence, step_type, input_hash, output_hash,
                                              input_data::text as input_data, output_data::text as output_data,
                                              embedding_cache_key, duration_ms, created_at
                                       FROM reasoning_steps
                                       WHERE session_id = @SessionId
                                       ORDER BY step_sequence
                           """;

        var rows = await connection.QueryAsync<dynamic>(sql, new { SessionId });

        return rows.Select(row => new ReasoningStep(
            row.step_id,
            row.session_id,
            row.step_sequence,
            row.step_type,
            row.input_hash,
            row.output_hash,
            JsonSerializer.Deserialize<object>(row.input_data)!,
            JsonSerializer.Deserialize<object>(row.output_data)!,
            row.embedding_cache_key,
            row.duration_ms,
            row.created_at)).ToList();
    }

    public async Task CompleteAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        // Compute output hash from all steps
        var steps = await GetAllStepsAsync(cancellationToken);
        var outputHash = ComputeHash(string.Join(":", steps.Select(s => s.OutputHash)));

        const string sql = """

                                       UPDATE inference_sessions
                                       SET status = 'completed', completed_at = NOW(), output_hash = @OutputHash
                                       WHERE session_id = @SessionId
                           """;

        await connection.ExecuteAsync(sql, new { SessionId, OutputHash = outputHash });

        Status = InferenceSessionStatus.Completed;
        _logger.LogInformation("Completed inference session {SessionId}", SessionId);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        if (Status == InferenceSessionStatus.Active)
        {
            try
            {
                await CompleteAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to complete session {SessionId} during disposal", SessionId);
            }
        }

        _disposed = true;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(InferenceSession));
        }
    }

    private static string ComputeHash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

internal sealed class DeterministicContext(Random random) : IDisposable
{
    private static readonly AsyncLocal<Random?> _currentRandom = new();
    private readonly Random? _previousRandom = _currentRandom.Value;

    public IDisposable Apply()
    {
        _currentRandom.Value = random;
        return this;
    }

    public static Random? Current => _currentRandom.Value;

    public void Dispose()
    {
        _currentRandom.Value = _previousRandom;
    }
}
