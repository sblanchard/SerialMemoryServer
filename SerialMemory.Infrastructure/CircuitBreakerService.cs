using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SerialMemory.Core.Interfaces;

namespace SerialMemory.Infrastructure;

/// <summary>
/// In-memory circuit breaker service with optional Redis backing for distributed scenarios.
/// Implements circuit breaker pattern for graceful degradation.
/// </summary>
public sealed class CircuitBreakerService(
    ILogger<CircuitBreakerService> logger,
    CircuitBreakerConfig? defaultConfig = null)
    : ICircuitBreakerService
{
    private readonly ConcurrentDictionary<string, CircuitBreakerState> _circuits = new();
    private readonly CircuitBreakerConfig _defaultConfig = defaultConfig ?? CircuitBreakerConfig.Default;

    public Task<CircuitState> GetStateAsync(string circuitName, CancellationToken ct = default)
    {
        var state = GetOrCreateCircuit(circuitName);
        return Task.FromResult(CalculateCurrentState(state));
    }

    public async Task<bool> TryExecuteAsync(string circuitName, CancellationToken ct = default)
    {
        var state = GetOrCreateCircuit(circuitName);
        var currentState = CalculateCurrentState(state);

        switch (currentState)
        {
            case CircuitState.Closed:
                return true;

            case CircuitState.Open:
                logger.LogWarning(
                    "Circuit {CircuitName} is OPEN, blocking operation. Closes at {ClosesAt}",
                    circuitName, state.ClosesAt);
                return false;

            case CircuitState.HalfOpen:
                // Allow limited operations through
                if (Interlocked.Increment(ref state.HalfOpenAttempts) <= _defaultConfig.SuccessThreshold)
                {
                    logger.LogDebug("Circuit {CircuitName} is HALF-OPEN, allowing test operation", circuitName);
                    return true;
                }
                return false;

            default:
                return true;
        }
    }

    public Task RecordSuccessAsync(string circuitName, CancellationToken ct = default)
    {
        var state = GetOrCreateCircuit(circuitName);
        var currentState = CalculateCurrentState(state);

        Interlocked.Increment(ref state.SuccessCount);
        state.LastSuccess = DateTimeOffset.UtcNow;

        if (currentState == CircuitState.HalfOpen)
        {
            var successes = Interlocked.Increment(ref state.HalfOpenSuccesses);
            if (successes >= _defaultConfig.SuccessThreshold)
            {
                CloseCircuit(state, circuitName);
            }
        }

        // Reset consecutive failures on success
        Interlocked.Exchange(ref state.ConsecutiveFailures, 0);

        return Task.CompletedTask;
    }

    public Task RecordFailureAsync(string circuitName, CancellationToken ct = default)
    {
        var state = GetOrCreateCircuit(circuitName);
        var currentState = CalculateCurrentState(state);

        Interlocked.Increment(ref state.FailureCount);
        var consecutiveFailures = Interlocked.Increment(ref state.ConsecutiveFailures);
        state.LastFailure = DateTimeOffset.UtcNow;

        // Add to failure window
        state.RecentFailures.Enqueue(DateTimeOffset.UtcNow);

        // Clean old failures outside window
        while (state.RecentFailures.TryPeek(out var oldest) &&
               oldest < DateTimeOffset.UtcNow - _defaultConfig.FailureWindow)
        {
            state.RecentFailures.TryDequeue(out _);
        }

        // Check if we should open the circuit
        if (currentState == CircuitState.Closed &&
            state.RecentFailures.Count >= _defaultConfig.FailureThreshold)
        {
            OpenCircuitInternal(state, circuitName, _defaultConfig.OpenDuration, "Failure threshold exceeded");
        }
        else if (currentState == CircuitState.HalfOpen)
        {
            // Failure in half-open state reopens the circuit
            OpenCircuitInternal(state, circuitName, _defaultConfig.OpenDuration, "Failure during half-open test");
        }

        return Task.CompletedTask;
    }

    public Task OpenCircuitAsync(string circuitName, TimeSpan duration, string reason, CancellationToken ct = default)
    {
        var state = GetOrCreateCircuit(circuitName);
        OpenCircuitInternal(state, circuitName, duration, reason);
        return Task.CompletedTask;
    }

    public Task CloseCircuitAsync(string circuitName, CancellationToken ct = default)
    {
        var state = GetOrCreateCircuit(circuitName);
        CloseCircuit(state, circuitName);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<CircuitStatistics>> GetAllStatisticsAsync(CancellationToken ct = default)
    {
        var stats = _circuits.Select(kvp =>
        {
            var state = kvp.Value;
            var currentState = CalculateCurrentState(state);
            var total = state.SuccessCount + state.FailureCount;

            return new CircuitStatistics
            {
                CircuitName = kvp.Key,
                State = currentState,
                SuccessCount = state.SuccessCount,
                FailureCount = state.FailureCount,
                ConsecutiveFailures = state.ConsecutiveFailures,
                FailureRate = total > 0 ? (double)state.FailureCount / total : 0,
                LastFailure = state.LastFailure,
                LastSuccess = state.LastSuccess,
                OpenedAt = state.OpenedAt,
                ClosesAt = state.ClosesAt,
                OpenReason = state.OpenReason
            };
        }).ToList();

        return Task.FromResult<IReadOnlyList<CircuitStatistics>>(stats);
    }

    private CircuitBreakerState GetOrCreateCircuit(string circuitName)
    {
        return _circuits.GetOrAdd(circuitName, _ => new CircuitBreakerState());
    }

    private CircuitState CalculateCurrentState(CircuitBreakerState state)
    {
        if (!state.IsOpen)
            return CircuitState.Closed;

        if (state.ClosesAt.HasValue && DateTimeOffset.UtcNow >= state.ClosesAt.Value)
            return CircuitState.HalfOpen;

        return CircuitState.Open;
    }

    private void OpenCircuitInternal(CircuitBreakerState state, string circuitName, TimeSpan duration, string reason)
    {
        state.IsOpen = true;
        state.OpenedAt = DateTimeOffset.UtcNow;
        state.ClosesAt = DateTimeOffset.UtcNow.Add(duration);
        state.OpenReason = reason;
        state.HalfOpenAttempts = 0;
        state.HalfOpenSuccesses = 0;

        logger.LogWarning(
            "Circuit {CircuitName} OPENED: {Reason}. Will transition to half-open at {ClosesAt}",
            circuitName, reason, state.ClosesAt);
    }

    private void CloseCircuit(CircuitBreakerState state, string circuitName)
    {
        state.IsOpen = false;
        state.OpenedAt = null;
        state.ClosesAt = null;
        state.OpenReason = null;
        state.HalfOpenAttempts = 0;
        state.HalfOpenSuccesses = 0;

        // Clear failure tracking
        state.ConsecutiveFailures = 0;
        while (state.RecentFailures.TryDequeue(out _)) { }

        logger.LogInformation("Circuit {CircuitName} CLOSED", circuitName);
    }

    private sealed class CircuitBreakerState
    {
        public bool IsOpen;
        public int SuccessCount;
        public int FailureCount;
        public int ConsecutiveFailures;
        public DateTimeOffset? LastFailure;
        public DateTimeOffset? LastSuccess;
        public DateTimeOffset? OpenedAt;
        public DateTimeOffset? ClosesAt;
        public string? OpenReason;
        public int HalfOpenAttempts;
        public int HalfOpenSuccesses;
        public readonly ConcurrentQueue<DateTimeOffset> RecentFailures = new();
    }
}
