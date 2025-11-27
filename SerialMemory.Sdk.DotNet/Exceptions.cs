namespace SerialMemory.Sdk;

/// <summary>
/// Base exception for SerialMemory SDK errors.
/// </summary>
public class SerialMemoryException : Exception
{
    public SerialMemoryException(string message) : base(message) { }
    public SerialMemoryException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Thrown when rate limit (429) is exceeded.
/// Check RetryAfter for when to retry.
/// </summary>
public sealed class RateLimitException(TimeSpan retryAfter, string? details = null)
    : SerialMemoryException($"Rate limit exceeded. Retry after {retryAfter.TotalSeconds:F0}s. {details}")
{
    /// <summary>
    /// How long to wait before retrying.
    /// </summary>
    public TimeSpan RetryAfter { get; } = retryAfter;
}

/// <summary>
/// Thrown when usage/credit limit (402) is exceeded.
/// Upgrade plan or wait for cycle reset.
/// </summary>
public sealed class UsageLimitException(string? details = null)
    : SerialMemoryException($"Usage limit exceeded. Upgrade your plan or wait for credit reset. {details}");

/// <summary>
/// Thrown when authentication fails (401/403).
/// Check API key or token validity.
/// </summary>
public sealed class AuthenticationException(string? details = null)
    : SerialMemoryException($"Authentication failed. Check your API key or token. {details}");
