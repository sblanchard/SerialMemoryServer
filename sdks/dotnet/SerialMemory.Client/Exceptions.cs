using System.Net;

namespace SerialMemory.Client;

/// <summary>
/// Base exception for SerialMemory client errors
/// </summary>
public class SerialMemoryException : Exception
{
    /// <summary>HTTP status code if applicable</summary>
    public HttpStatusCode? StatusCode { get; }

    public SerialMemoryException(string message) : base(message) { }

    public SerialMemoryException(string message, HttpStatusCode statusCode) : base(message)
    {
        StatusCode = statusCode;
    }

    public SerialMemoryException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Thrown when rate limit is exceeded (HTTP 429).
/// Contains retry-after duration for backoff.
/// </summary>
public class RateLimitExceededException : SerialMemoryException
{
    /// <summary>
    /// Recommended time to wait before retrying
    /// </summary>
    public TimeSpan RetryAfter { get; }

    /// <summary>
    /// Raw error response from server
    /// </summary>
    public string? ErrorResponse { get; }

    public RateLimitExceededException(TimeSpan retryAfter, string? errorResponse = null)
        : base($"Rate limit exceeded. Retry after {retryAfter.TotalSeconds:F0} seconds.", HttpStatusCode.TooManyRequests)
    {
        RetryAfter = retryAfter;
        ErrorResponse = errorResponse;
    }
}

/// <summary>
/// Thrown when usage quota is exceeded (HTTP 402).
/// Indicates plan limits have been reached.
/// </summary>
public class UsageLimitExceededException : SerialMemoryException
{
    /// <summary>
    /// Raw error response from server (may contain plan info)
    /// </summary>
    public string? ErrorResponse { get; }

    public UsageLimitExceededException(string? errorResponse = null)
        : base("Usage limit exceeded. Please upgrade your plan or wait for quota reset.", (HttpStatusCode)402)
    {
        ErrorResponse = errorResponse;
    }
}

/// <summary>
/// Thrown when circuit breaker is open due to repeated failures.
/// Client should wait before retrying.
/// </summary>
public class CircuitBreakerOpenException : SerialMemoryException
{
    /// <summary>
    /// Duration until circuit breaker resets
    /// </summary>
    public TimeSpan BreakDuration { get; }

    public CircuitBreakerOpenException(TimeSpan breakDuration)
        : base($"Circuit breaker is open. Service unavailable for {breakDuration.TotalSeconds:F0} seconds.")
    {
        BreakDuration = breakDuration;
    }
}

/// <summary>
/// Thrown when authentication fails (HTTP 401/403)
/// </summary>
public class AuthenticationException : SerialMemoryException
{
    public AuthenticationException(string message)
        : base(message, HttpStatusCode.Unauthorized) { }
}
