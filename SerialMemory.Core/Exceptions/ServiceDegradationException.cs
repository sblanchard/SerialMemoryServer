namespace SerialMemory.Core.Exceptions;

/// <summary>
/// Exception thrown when a service is degraded and cannot complete the operation.
/// Used for graceful degradation with deterministic error responses.
/// </summary>
public class ServiceDegradationException(
    DegradationType type,
    string serviceName,
    string message,
    string errorCode = "SERVICE_DEGRADED",
    bool isRetryable = true,
    TimeSpan? retryAfter = null,
    Dictionary<string, object>? details = null,
    Exception? innerException = null)
    : Exception(message, innerException)
{
    public DegradationType DegradationType { get; } = type;
    public string ServiceName { get; } = serviceName;
    public string ErrorCode { get; } = errorCode;
    public bool IsRetryable { get; } = isRetryable;
    public TimeSpan? RetryAfter { get; } = retryAfter;
    public Dictionary<string, object>? Details { get; } = details;

    /// <summary>
    /// Creates a deterministic error response for API consumers.
    /// </summary>
    public DegradationErrorResponse ToErrorResponse() => new()
    {
        Error = ErrorCode,
        Message = Message,
        ServiceName = ServiceName,
        DegradationType = DegradationType.ToString().ToLowerInvariant(),
        IsRetryable = IsRetryable,
        RetryAfter = RetryAfter?.TotalSeconds,
        Details = Details
    };
}

/// <summary>
/// Types of service degradation.
/// </summary>
public enum DegradationType
{
    /// <summary>
    /// Service is completely unavailable.
    /// </summary>
    Unavailable,

    /// <summary>
    /// Service timed out.
    /// </summary>
    Timeout,

    /// <summary>
    /// Service is overloaded and rate-limiting.
    /// </summary>
    Overloaded,

    /// <summary>
    /// Service returned an error but may recover.
    /// </summary>
    TransientError,

    /// <summary>
    /// Service configuration is invalid.
    /// </summary>
    ConfigurationError,

    /// <summary>
    /// Dependency service failed.
    /// </summary>
    DependencyFailure,

    /// <summary>
    /// Circuit breaker is open.
    /// </summary>
    CircuitOpen
}

/// <summary>
/// Deterministic error response for degraded services.
/// </summary>
public sealed record DegradationErrorResponse
{
    public required string Error { get; init; }
    public required string Message { get; init; }
    public required string ServiceName { get; init; }
    public required string DegradationType { get; init; }
    public required bool IsRetryable { get; init; }
    public double? RetryAfter { get; init; }
    public Dictionary<string, object>? Details { get; init; }
}
