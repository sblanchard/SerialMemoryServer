/**
 * Base error class for SerialMemory client errors
 */
export class SerialMemoryError extends Error {
  /** HTTP status code if applicable */
  public readonly statusCode?: number;

  /** Raw response body if available */
  public readonly response?: string;

  constructor(message: string, statusCode?: number, response?: string) {
    super(message);
    this.name = 'SerialMemoryError';
    this.statusCode = statusCode;
    this.response = response;

    // Maintains proper stack trace for where error was thrown (V8 only)
    if (Error.captureStackTrace) {
      Error.captureStackTrace(this, SerialMemoryError);
    }
  }
}

/**
 * Thrown when rate limit is exceeded (HTTP 429).
 * Contains retry-after duration for backoff.
 */
export class RateLimitExceededError extends SerialMemoryError {
  /** Recommended time to wait before retrying (in milliseconds) */
  public readonly retryAfter: number;

  constructor(retryAfter: number, response?: string) {
    super(
      `Rate limit exceeded. Retry after ${Math.round(retryAfter / 1000)} seconds.`,
      429,
      response
    );
    this.name = 'RateLimitExceededError';
    this.retryAfter = retryAfter;
  }
}

/**
 * Thrown when usage quota is exceeded (HTTP 402).
 * Indicates plan limits have been reached.
 */
export class UsageLimitExceededError extends SerialMemoryError {
  /** Current plan name if available */
  public readonly plan?: string;

  /** Next quota reset time if available */
  public readonly nextReset?: Date;

  constructor(response?: string, plan?: string, nextReset?: Date) {
    super(
      'Usage limit exceeded. Please upgrade your plan or wait for quota reset.',
      402,
      response
    );
    this.name = 'UsageLimitExceededError';
    this.plan = plan;
    this.nextReset = nextReset;
  }
}

/**
 * Thrown when circuit breaker is open due to repeated failures.
 * Client should wait before retrying.
 */
export class CircuitBreakerOpenError extends SerialMemoryError {
  /** Duration until circuit breaker resets (in milliseconds) */
  public readonly breakDuration: number;

  constructor(breakDuration: number) {
    super(
      `Circuit breaker is open. Service unavailable for ${Math.round(breakDuration / 1000)} seconds.`
    );
    this.name = 'CircuitBreakerOpenError';
    this.breakDuration = breakDuration;
  }
}

/**
 * Thrown when authentication fails (HTTP 401/403)
 */
export class AuthenticationError extends SerialMemoryError {
  constructor(message: string, statusCode: number = 401) {
    super(message, statusCode);
    this.name = 'AuthenticationError';
  }
}

/**
 * Thrown when a request times out
 */
export class TimeoutError extends SerialMemoryError {
  constructor(timeoutMs: number) {
    super(`Request timed out after ${timeoutMs}ms`);
    this.name = 'TimeoutError';
  }
}
