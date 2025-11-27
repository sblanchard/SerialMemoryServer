using System.Collections.Concurrent;

namespace SerialMemory.Core.Enterprise;

/// <summary>
/// Circuit breaker states for fail-fast behavior.
/// </summary>
public enum CircuitState
{
    Closed,     // Normal operation
    Open,       // Failing, reject requests
    HalfOpen    // Testing if service recovered
}

/// <summary>
/// Circuit breaker for preventing cascade failures.
/// Thread-safe, stateful, with configurable thresholds.
/// </summary>
public sealed class CircuitBreaker
{
    private readonly string _name;
    private readonly int _failureThreshold;
    private readonly TimeSpan _openDuration;
    private readonly TimeSpan _halfOpenTestInterval;

    private CircuitState _state = CircuitState.Closed;
    private int _failureCount;
    private int _successCount;
    private DateTimeOffset _lastFailureTime;
    private DateTimeOffset _lastStateChange;
    private readonly object _lock = new();

    public string Name => _name;
    public CircuitState State => _state;
    public int FailureCount => _failureCount;
    public int SuccessCount => _successCount;
    public DateTimeOffset LastStateChange => _lastStateChange;

    public CircuitBreaker(
        string name,
        int failureThreshold = 5,
        TimeSpan? openDuration = null,
        TimeSpan? halfOpenTestInterval = null)
    {
        _name = name;
        _failureThreshold = failureThreshold;
        _openDuration = openDuration ?? TimeSpan.FromSeconds(30);
        _halfOpenTestInterval = halfOpenTestInterval ?? TimeSpan.FromSeconds(10);
        _lastStateChange = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Check if request should be allowed through.
    /// </summary>
    public bool AllowRequest()
    {
        lock (_lock)
        {
            switch (_state)
            {
                case CircuitState.Closed:
                    return true;

                case CircuitState.Open:
                    if (DateTimeOffset.UtcNow - _lastStateChange >= _openDuration)
                    {
                        TransitionTo(CircuitState.HalfOpen);
                        return true;
                    }
                    return false;

                case CircuitState.HalfOpen:
                    // Allow limited requests to test recovery
                    return DateTimeOffset.UtcNow - _lastFailureTime >= _halfOpenTestInterval;

                default:
                    return false;
            }
        }
    }

    /// <summary>
    /// Record a successful operation.
    /// </summary>
    public void RecordSuccess()
    {
        lock (_lock)
        {
            _successCount++;

            if (_state == CircuitState.HalfOpen)
            {
                // Service recovered, close circuit
                TransitionTo(CircuitState.Closed);
                _failureCount = 0;
            }
        }
    }

    /// <summary>
    /// Record a failed operation.
    /// </summary>
    public void RecordFailure()
    {
        lock (_lock)
        {
            _failureCount++;
            _lastFailureTime = DateTimeOffset.UtcNow;

            if (_state == CircuitState.HalfOpen)
            {
                // Still failing, reopen circuit
                TransitionTo(CircuitState.Open);
            }
            else if (_state == CircuitState.Closed && _failureCount >= _failureThreshold)
            {
                // Threshold exceeded, open circuit
                TransitionTo(CircuitState.Open);
            }
        }
    }

    /// <summary>
    /// Force circuit to specific state (for testing/admin).
    /// </summary>
    public void ForceState(CircuitState state)
    {
        lock (_lock)
        {
            TransitionTo(state);
            if (state == CircuitState.Closed)
            {
                _failureCount = 0;
            }
        }
    }

    /// <summary>
    /// Reset circuit to closed state.
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            _failureCount = 0;
            _successCount = 0;
            TransitionTo(CircuitState.Closed);
        }
    }

    private void TransitionTo(CircuitState newState)
    {
        if (_state != newState)
        {
            _state = newState;
            _lastStateChange = DateTimeOffset.UtcNow;
        }
    }

    public CircuitBreakerSnapshot GetSnapshot()
    {
        lock (_lock)
        {
            return new CircuitBreakerSnapshot
            {
                Name = _name,
                State = _state,
                FailureCount = _failureCount,
                SuccessCount = _successCount,
                FailureThreshold = _failureThreshold,
                LastFailureTime = _lastFailureTime,
                LastStateChange = _lastStateChange,
                OpenDuration = _openDuration,
                IsAllowingRequests = AllowRequest()
            };
        }
    }
}

public sealed class CircuitBreakerSnapshot
{
    public string Name { get; init; } = "";
    public CircuitState State { get; init; }
    public int FailureCount { get; init; }
    public int SuccessCount { get; init; }
    public int FailureThreshold { get; init; }
    public DateTimeOffset LastFailureTime { get; init; }
    public DateTimeOffset LastStateChange { get; init; }
    public TimeSpan OpenDuration { get; init; }
    public bool IsAllowingRequests { get; init; }
}

/// <summary>
/// Registry for managing multiple circuit breakers.
/// </summary>
public sealed class CircuitBreakerRegistry
{
    private readonly ConcurrentDictionary<string, CircuitBreaker> _breakers = new();

    public CircuitBreaker GetOrCreate(
        string name,
        int failureThreshold = 5,
        TimeSpan? openDuration = null)
    {
        return _breakers.GetOrAdd(name, _ => new CircuitBreaker(name, failureThreshold, openDuration));
    }

    public CircuitBreaker? Get(string name)
    {
        return _breakers.TryGetValue(name, out var breaker) ? breaker : null;
    }

    public IReadOnlyDictionary<string, CircuitBreakerSnapshot> GetAllSnapshots()
    {
        return _breakers.ToDictionary(x => x.Key, x => x.Value.GetSnapshot());
    }

    public void ResetAll()
    {
        foreach (var breaker in _breakers.Values)
        {
            breaker.Reset();
        }
    }
}

/// <summary>
/// Execute operations with circuit breaker protection.
/// </summary>
public static class CircuitBreakerExecutor
{
    public static async Task<TResult> ExecuteAsync<TResult>(
        CircuitBreaker breaker,
        Func<Task<TResult>> operation,
        Func<TResult>? fallback = null)
    {
        if (!breaker.AllowRequest())
        {
            if (fallback != null)
            {
                return fallback();
            }
            throw new CircuitBreakerOpenException(breaker.Name);
        }

        try
        {
            var result = await operation();
            breaker.RecordSuccess();
            return result;
        }
        catch
        {
            breaker.RecordFailure();
            if (fallback != null)
            {
                return fallback();
            }
            throw;
        }
    }

    public static async Task ExecuteAsync(
        CircuitBreaker breaker,
        Func<Task> operation,
        Action? fallback = null)
    {
        if (!breaker.AllowRequest())
        {
            if (fallback != null)
            {
                fallback();
                return;
            }
            throw new CircuitBreakerOpenException(breaker.Name);
        }

        try
        {
            await operation();
            breaker.RecordSuccess();
        }
        catch
        {
            breaker.RecordFailure();
            if (fallback != null)
            {
                fallback();
                return;
            }
            throw;
        }
    }
}

public sealed class CircuitBreakerOpenException : Exception
{
    public string BreakerName { get; }

    public CircuitBreakerOpenException(string breakerName)
        : base($"Circuit breaker '{breakerName}' is open")
    {
        BreakerName = breakerName;
    }
}
