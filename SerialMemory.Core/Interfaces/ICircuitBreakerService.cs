namespace SerialMemory.Core.Interfaces;

/// <summary>
/// Circuit breaker service for graceful degradation.
/// Implements the circuit breaker pattern to prevent cascading failures.
/// </summary>
public interface ICircuitBreakerService
{
    /// <summary>
    /// Gets the current state of a circuit.
    /// </summary>
    Task<CircuitState> GetStateAsync(string circuitName, CancellationToken ct = default);

    /// <summary>
    /// Attempts to execute an operation through the circuit breaker.
    /// Returns false if circuit is open, true if operation can proceed.
    /// </summary>
    Task<bool> TryExecuteAsync(string circuitName, CancellationToken ct = default);

    /// <summary>
    /// Records a successful operation.
    /// </summary>
    Task RecordSuccessAsync(string circuitName, CancellationToken ct = default);

    /// <summary>
    /// Records a failed operation.
    /// </summary>
    Task RecordFailureAsync(string circuitName, CancellationToken ct = default);

    /// <summary>
    /// Manually opens a circuit (for emergency shutoff).
    /// </summary>
    Task OpenCircuitAsync(string circuitName, TimeSpan duration, string reason, CancellationToken ct = default);

    /// <summary>
    /// Manually closes a circuit.
    /// </summary>
    Task CloseCircuitAsync(string circuitName, CancellationToken ct = default);

    /// <summary>
    /// Gets statistics for all circuits.
    /// </summary>
    Task<IReadOnlyList<CircuitStatistics>> GetAllStatisticsAsync(CancellationToken ct = default);
}

/// <summary>
/// Circuit breaker state.
/// </summary>
public enum CircuitState
{
    /// <summary>
    /// Circuit is closed, operations proceed normally.
    /// </summary>
    Closed,

    /// <summary>
    /// Circuit is open, operations are blocked.
    /// </summary>
    Open,

    /// <summary>
    /// Circuit is half-open, testing if service recovered.
    /// </summary>
    HalfOpen
}

/// <summary>
/// Statistics for a circuit breaker.
/// </summary>
public sealed record CircuitStatistics
{
    public required string CircuitName { get; init; }
    public required CircuitState State { get; init; }
    public required int SuccessCount { get; init; }
    public required int FailureCount { get; init; }
    public required int ConsecutiveFailures { get; init; }
    public required double FailureRate { get; init; }
    public DateTimeOffset? LastFailure { get; init; }
    public DateTimeOffset? LastSuccess { get; init; }
    public DateTimeOffset? OpenedAt { get; init; }
    public DateTimeOffset? ClosesAt { get; init; }
    public string? OpenReason { get; init; }
}

/// <summary>
/// Configuration for a circuit breaker.
/// </summary>
public sealed record CircuitBreakerConfig
{
    /// <summary>
    /// Number of failures before opening the circuit.
    /// </summary>
    public int FailureThreshold { get; init; } = 5;

    /// <summary>
    /// Time window for counting failures.
    /// </summary>
    public TimeSpan FailureWindow { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Time to keep circuit open before testing.
    /// </summary>
    public TimeSpan OpenDuration { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Number of successes needed to close half-open circuit.
    /// </summary>
    public int SuccessThreshold { get; init; } = 3;

    public static CircuitBreakerConfig Default => new();

    public static CircuitBreakerConfig Strict => new()
    {
        FailureThreshold = 3,
        FailureWindow = TimeSpan.FromSeconds(30),
        OpenDuration = TimeSpan.FromMinutes(1),
        SuccessThreshold = 5
    };

    public static CircuitBreakerConfig Lenient => new()
    {
        FailureThreshold = 10,
        FailureWindow = TimeSpan.FromMinutes(5),
        OpenDuration = TimeSpan.FromSeconds(15),
        SuccessThreshold = 2
    };
}
