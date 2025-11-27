namespace SerialMemory.Core.Enterprise;

/// <summary>
/// Soft-fail result wrapper. Operations return results instead of throwing.
/// Enables graceful degradation.
/// </summary>
public readonly struct SoftResult<T>
{
    public T? Value { get; }
    public bool IsSuccess { get; }
    public string? Error { get; }
    public Exception? Exception { get; }
    public SoftFailSeverity Severity { get; }
    public string? FallbackUsed { get; }

    private SoftResult(T? value, bool isSuccess, string? error, Exception? exception, SoftFailSeverity severity, string? fallbackUsed)
    {
        Value = value;
        IsSuccess = isSuccess;
        Error = error;
        Exception = exception;
        Severity = severity;
        FallbackUsed = fallbackUsed;
    }

    public static SoftResult<T> Success(T value) =>
        new(value, true, null, null, SoftFailSeverity.None, null);

    public static SoftResult<T> Fail(string error, SoftFailSeverity severity = SoftFailSeverity.Warning) =>
        new(default, false, error, null, severity, null);

    public static SoftResult<T> Fail(Exception ex, SoftFailSeverity severity = SoftFailSeverity.Error) =>
        new(default, false, ex.Message, ex, severity, null);

    public static SoftResult<T> Fallback(T fallbackValue, string fallbackName, string reason) =>
        new(fallbackValue, true, reason, null, SoftFailSeverity.Warning, fallbackName);

    public T GetValueOrDefault(T defaultValue) => IsSuccess && Value is not null ? Value : defaultValue;

    public SoftResult<TNew> Map<TNew>(Func<T, TNew> mapper)
    {
        if (!IsSuccess || Value is null)
            return new SoftResult<TNew>(default, false, Error, Exception, Severity, FallbackUsed);

        return SoftResult<TNew>.Success(mapper(Value));
    }

    public async Task<SoftResult<TNew>> MapAsync<TNew>(Func<T, Task<TNew>> mapper)
    {
        if (!IsSuccess || Value is null)
            return new SoftResult<TNew>(default, false, Error, Exception, Severity, FallbackUsed);

        var result = await mapper(Value);
        return SoftResult<TNew>.Success(result);
    }
}

public enum SoftFailSeverity
{
    None,
    Info,
    Warning,
    Error,
    Critical
}

/// <summary>
/// Soft-fail executor for wrapping operations.
/// </summary>
public static class SoftFail
{
    /// <summary>
    /// Execute operation with soft-fail. Returns result instead of throwing.
    /// </summary>
    public static async Task<SoftResult<T>> TryAsync<T>(
        Func<Task<T>> operation,
        string operationName = "operation")
    {
        try
        {
            var result = await operation();
            return SoftResult<T>.Success(result);
        }
        catch (Exception ex)
        {
            return SoftResult<T>.Fail(ex, ClassifySeverity(ex));
        }
    }

    /// <summary>
    /// Execute operation with fallback on failure.
    /// </summary>
    public static async Task<SoftResult<T>> TryWithFallbackAsync<T>(
        Func<Task<T>> operation,
        T fallbackValue,
        string fallbackName = "default",
        string operationName = "operation")
    {
        try
        {
            var result = await operation();
            return SoftResult<T>.Success(result);
        }
        catch (Exception ex)
        {
            return SoftResult<T>.Fallback(fallbackValue, fallbackName, $"{operationName} failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Execute operation with multiple fallback levels.
    /// </summary>
    public static async Task<SoftResult<T>> TryWithCascadingFallbackAsync<T>(
        Func<Task<T>> primary,
        params (string name, Func<Task<T>> operation)[] fallbacks)
    {
        // Try primary
        try
        {
            var result = await primary();
            return SoftResult<T>.Success(result);
        }
        catch { }

        // Try fallbacks in order
        foreach (var (name, operation) in fallbacks)
        {
            try
            {
                var result = await operation();
                return SoftResult<T>.Fallback(result, name, "Primary operation failed, using fallback");
            }
            catch { }
        }

        return SoftResult<T>.Fail("All operations failed including fallbacks", SoftFailSeverity.Error);
    }

    /// <summary>
    /// Execute with timeout and fallback.
    /// </summary>
    public static async Task<SoftResult<T>> TryWithTimeoutAsync<T>(
        Func<Task<T>> operation,
        TimeSpan timeout,
        T? timeoutFallback = default,
        string operationName = "operation")
    {
        using var cts = new CancellationTokenSource(timeout);

        try
        {
            var result = await operation().WaitAsync(cts.Token);
            return SoftResult<T>.Success(result);
        }
        catch (OperationCanceledException)
        {
            if (timeoutFallback is not null)
            {
                return SoftResult<T>.Fallback(timeoutFallback, "timeout", $"{operationName} timed out after {timeout.TotalSeconds}s");
            }
            return SoftResult<T>.Fail($"{operationName} timed out after {timeout.TotalSeconds}s", SoftFailSeverity.Warning);
        }
        catch (Exception ex)
        {
            return SoftResult<T>.Fail(ex, ClassifySeverity(ex));
        }
    }

    /// <summary>
    /// Execute with retry and soft-fail.
    /// </summary>
    public static async Task<SoftResult<T>> TryWithRetryAsync<T>(
        Func<Task<T>> operation,
        int maxRetries = 3,
        TimeSpan? delayBetweenRetries = null,
        string operationName = "operation")
    {
        var delay = delayBetweenRetries ?? TimeSpan.FromMilliseconds(100);
        Exception? lastException = null;

        for (int i = 0; i <= maxRetries; i++)
        {
            try
            {
                var result = await operation();
                return SoftResult<T>.Success(result);
            }
            catch (Exception ex)
            {
                lastException = ex;
                if (i < maxRetries)
                {
                    await Task.Delay(delay * (i + 1)); // Exponential backoff
                }
            }
        }

        return SoftResult<T>.Fail(lastException!, SoftFailSeverity.Error);
    }

    /// <summary>
    /// Combine multiple soft-fail results.
    /// </summary>
    public static SoftResult<(T1, T2)> Combine<T1, T2>(
        SoftResult<T1> result1,
        SoftResult<T2> result2)
    {
        if (!result1.IsSuccess)
            return SoftResult<(T1, T2)>.Fail(result1.Error ?? "First operation failed", result1.Severity);

        if (!result2.IsSuccess)
            return SoftResult<(T1, T2)>.Fail(result2.Error ?? "Second operation failed", result2.Severity);

        return SoftResult<(T1, T2)>.Success((result1.Value!, result2.Value!));
    }

    private static SoftFailSeverity ClassifySeverity(Exception ex) => ex switch
    {
        ArgumentException => SoftFailSeverity.Warning,
        InvalidOperationException => SoftFailSeverity.Warning,
        TimeoutException => SoftFailSeverity.Warning,
        OperationCanceledException => SoftFailSeverity.Info,
        UnauthorizedAccessException => SoftFailSeverity.Error,
        _ => SoftFailSeverity.Error
    };
}

/// <summary>
/// Degradation levels for graceful service degradation.
/// </summary>
public enum DegradationLevel
{
    Full,           // All features available
    Limited,        // Some features disabled
    ReadOnly,       // Only reads allowed
    Minimal,        // Critical paths only
    Offline         // Service unavailable
}

/// <summary>
/// Service degradation manager.
/// </summary>
public sealed class DegradationManager
{
    private DegradationLevel _currentLevel = DegradationLevel.Full;
    private readonly object _lock = new();
    private readonly List<string> _disabledFeatures = [];
    private DateTimeOffset _lastDegradation = DateTimeOffset.MinValue;

    public DegradationLevel CurrentLevel
    {
        get { lock (_lock) return _currentLevel; }
    }

    public IReadOnlyList<string> DisabledFeatures
    {
        get { lock (_lock) return _disabledFeatures.ToList(); }
    }

    public void SetLevel(DegradationLevel level, string reason)
    {
        lock (_lock)
        {
            _currentLevel = level;
            _lastDegradation = DateTimeOffset.UtcNow;

            _disabledFeatures.Clear();
            switch (level)
            {
                case DegradationLevel.Limited:
                    _disabledFeatures.AddRange(["batch_operations", "exports", "analytics"]);
                    break;
                case DegradationLevel.ReadOnly:
                    _disabledFeatures.AddRange(["writes", "deletes", "batch_operations", "exports", "analytics"]);
                    break;
                case DegradationLevel.Minimal:
                    _disabledFeatures.AddRange(["writes", "deletes", "batch_operations", "exports", "analytics", "search", "entity_extraction"]);
                    break;
                case DegradationLevel.Offline:
                    _disabledFeatures.AddRange(["all"]);
                    break;
            }
        }
    }

    public bool IsFeatureEnabled(string feature)
    {
        lock (_lock)
        {
            if (_currentLevel == DegradationLevel.Full) return true;
            if (_currentLevel == DegradationLevel.Offline) return false;
            return !_disabledFeatures.Contains(feature) && !_disabledFeatures.Contains("all");
        }
    }

    public void Restore()
    {
        lock (_lock)
        {
            _currentLevel = DegradationLevel.Full;
            _disabledFeatures.Clear();
        }
    }

    public DegradationSnapshot GetSnapshot()
    {
        lock (_lock)
        {
            return new DegradationSnapshot
            {
                Level = _currentLevel,
                DisabledFeatures = _disabledFeatures.ToList(),
                LastDegradation = _lastDegradation
            };
        }
    }
}

public sealed class DegradationSnapshot
{
    public DegradationLevel Level { get; init; }
    public List<string> DisabledFeatures { get; init; } = [];
    public DateTimeOffset LastDegradation { get; init; }
}
