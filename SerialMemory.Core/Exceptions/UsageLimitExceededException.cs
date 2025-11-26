using SerialMemory.Core.Interfaces;

namespace SerialMemory.Core.Exceptions;

/// <summary>
/// Exception thrown when usage limits are exceeded.
/// Contains structured error information for machine-readable responses.
/// </summary>
public sealed class UsageLimitExceededException : Exception
{
    public UsageLimitType LimitType { get; }
    public string ErrorCode { get; }
    public DateTimeOffset? RetryAfter { get; }
    public Dictionary<string, object>? Details { get; }

    public UsageLimitExceededException(
        UsageLimitType limitType,
        string message,
        string? errorCode = null,
        DateTimeOffset? retryAfter = null,
        Dictionary<string, object>? details = null)
        : base(message)
    {
        LimitType = limitType;
        ErrorCode = errorCode ?? GetDefaultErrorCode(limitType);
        RetryAfter = retryAfter;
        Details = details;
    }

    /// <summary>
    /// Creates an exception for credit limit exceeded.
    /// </summary>
    public static UsageLimitExceededException CreditLimitExceeded(
        decimal creditsRequired,
        decimal creditsRemaining,
        DateTimeOffset cycleEnd)
    {
        return new UsageLimitExceededException(
            UsageLimitType.CreditLimitExceeded,
            $"Credit limit exceeded. Required: {creditsRequired:F2}, Remaining: {creditsRemaining:F2}",
            "CREDIT_LIMIT_EXCEEDED",
            retryAfter: cycleEnd,
            details: new Dictionary<string, object>
            {
                ["credits_required"] = creditsRequired,
                ["credits_remaining"] = creditsRemaining,
                ["cycle_end"] = cycleEnd.ToString("O")
            });
    }

    /// <summary>
    /// Creates an exception for rate limit exceeded.
    /// </summary>
    public static UsageLimitExceededException RateLimitExceeded(
        int limitPerMinute,
        int currentCount,
        DateTimeOffset retryAfter)
    {
        return new UsageLimitExceededException(
            UsageLimitType.RateLimitExceeded,
            $"Rate limit exceeded. Limit: {limitPerMinute}/min, Current: {currentCount}",
            "RATE_LIMIT_EXCEEDED",
            retryAfter: retryAfter,
            details: new Dictionary<string, object>
            {
                ["limit_per_minute"] = limitPerMinute,
                ["current_count"] = currentCount,
                ["retry_after"] = retryAfter.ToString("O")
            });
    }

    /// <summary>
    /// Creates an exception for max memories exceeded.
    /// </summary>
    public static UsageLimitExceededException MaxMemoriesExceeded(
        int maxMemories,
        int currentCount)
    {
        return new UsageLimitExceededException(
            UsageLimitType.MaxMemoriesExceeded,
            $"Maximum memories limit exceeded. Limit: {maxMemories}, Current: {currentCount}",
            "MAX_MEMORIES_EXCEEDED",
            details: new Dictionary<string, object>
            {
                ["max_memories"] = maxMemories,
                ["current_count"] = currentCount
            });
    }

    /// <summary>
    /// Creates an exception for max entities exceeded.
    /// </summary>
    public static UsageLimitExceededException MaxEntitiesExceeded(
        int maxEntities,
        int currentCount)
    {
        return new UsageLimitExceededException(
            UsageLimitType.MaxEntitiesExceeded,
            $"Maximum entities limit exceeded. Limit: {maxEntities}, Current: {currentCount}",
            "MAX_ENTITIES_EXCEEDED",
            details: new Dictionary<string, object>
            {
                ["max_entities"] = maxEntities,
                ["current_count"] = currentCount
            });
    }

    /// <summary>
    /// Creates an exception for inactive plan.
    /// </summary>
    public static UsageLimitExceededException PlanInactive(string planName)
    {
        return new UsageLimitExceededException(
            UsageLimitType.PlanInactive,
            $"Plan '{planName}' is inactive or suspended",
            "PLAN_INACTIVE",
            details: new Dictionary<string, object>
            {
                ["plan_name"] = planName
            });
    }

    /// <summary>
    /// Converts to a structured error response.
    /// </summary>
    public UsageLimitErrorResponse ToErrorResponse()
    {
        return new UsageLimitErrorResponse
        {
            Error = ErrorCode,
            Message = Message,
            LimitType = LimitType.ToString(),
            RetryAfter = RetryAfter?.ToString("O"),
            Details = Details
        };
    }

    private static string GetDefaultErrorCode(UsageLimitType limitType) => limitType switch
    {
        UsageLimitType.CreditLimitExceeded => "CREDIT_LIMIT_EXCEEDED",
        UsageLimitType.RateLimitExceeded => "RATE_LIMIT_EXCEEDED",
        UsageLimitType.MaxMemoriesExceeded => "MAX_MEMORIES_EXCEEDED",
        UsageLimitType.MaxEntitiesExceeded => "MAX_ENTITIES_EXCEEDED",
        UsageLimitType.PlanInactive => "PLAN_INACTIVE",
        _ => "USAGE_LIMIT_EXCEEDED"
    };
}

/// <summary>
/// Structured error response for usage limit violations.
/// </summary>
public sealed class UsageLimitErrorResponse
{
    public string Error { get; init; } = "";
    public string Message { get; init; } = "";
    public string LimitType { get; init; } = "";
    public string? RetryAfter { get; init; }
    public Dictionary<string, object>? Details { get; init; }
}
