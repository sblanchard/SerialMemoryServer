namespace SerialMemory.Core.Auth;

/// <summary>
/// JWT token validation service for authentication.
/// </summary>
public interface IJwtAuthenticationService
{
    /// <summary>
    /// Validates a JWT token and extracts authentication claims.
    /// </summary>
    /// <param name="token">The JWT token to validate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Authentication result with claims or error details.</returns>
    Task<AuthenticationResult> ValidateTokenAsync(string token, CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates an API key and returns authentication result.
    /// </summary>
    /// <param name="apiKey">The API key to validate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Authentication result with claims or error details.</returns>
    Task<AuthenticationResult> ValidateApiKeyAsync(string apiKey, CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of authentication attempt.
/// </summary>
public sealed record AuthenticationResult
{
    /// <summary>
    /// Whether the authentication was successful.
    /// </summary>
    public required bool IsValid { get; init; }

    /// <summary>
    /// The tenant ID from the token claims.
    /// </summary>
    public Guid? TenantId { get; init; }

    /// <summary>
    /// The workspace ID from the token claims (optional).
    /// </summary>
    public string? WorkspaceId { get; init; }

    /// <summary>
    /// The user ID (subject) from the token claims.
    /// </summary>
    public string? UserId { get; init; }

    /// <summary>
    /// The user's role within the tenant.
    /// </summary>
    public string? Role { get; init; }

    /// <summary>
    /// The scopes granted by this token.
    /// </summary>
    public IReadOnlySet<string> Scopes { get; init; } = new HashSet<string>();

    /// <summary>
    /// Error code if authentication failed.
    /// </summary>
    public string? ErrorCode { get; init; }

    /// <summary>
    /// Human-readable error message if authentication failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Token expiration time (UTC).
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>
    /// Creates a successful authentication result.
    /// </summary>
    public static AuthenticationResult Success(
        Guid tenantId,
        string userId,
        string role,
        IEnumerable<string> scopes,
        string? workspaceId = null,
        DateTimeOffset? expiresAt = null) => new()
    {
        IsValid = true,
        TenantId = tenantId,
        UserId = userId,
        Role = role,
        Scopes = scopes.ToHashSet(),
        WorkspaceId = workspaceId,
        ExpiresAt = expiresAt
    };

    /// <summary>
    /// Creates a failed authentication result.
    /// </summary>
    public static AuthenticationResult Failure(string errorCode, string errorMessage) => new()
    {
        IsValid = false,
        ErrorCode = errorCode,
        ErrorMessage = errorMessage
    };

    /// <summary>
    /// Creates a result for invalid token.
    /// </summary>
    public static AuthenticationResult InvalidToken(string message = "Invalid or expired token") =>
        Failure("INVALID_TOKEN", message);

    /// <summary>
    /// Creates a result for missing token.
    /// </summary>
    public static AuthenticationResult MissingToken() =>
        Failure("MISSING_TOKEN", "Authentication token is required");

    /// <summary>
    /// Creates a result for expired token.
    /// </summary>
    public static AuthenticationResult ExpiredToken() =>
        Failure("EXPIRED_TOKEN", "Authentication token has expired");

    /// <summary>
    /// Creates a result for insufficient scope.
    /// </summary>
    public static AuthenticationResult InsufficientScope(string requiredScope) =>
        Failure("INSUFFICIENT_SCOPE", $"Required scope '{requiredScope}' not granted");

    /// <summary>
    /// Creates a result for insufficient role.
    /// </summary>
    public static AuthenticationResult InsufficientRole(string requiredRole) =>
        Failure("INSUFFICIENT_ROLE", $"Required role '{requiredRole}' not granted");

    /// <summary>
    /// Creates a result for suspended tenant.
    /// </summary>
    public static AuthenticationResult TenantSuspended() =>
        Failure("TENANT_SUSPENDED", "Tenant account has been suspended");

    /// <summary>
    /// Checks if the result has a specific scope.
    /// </summary>
    public bool HasScope(string scope) => Scopes.Contains(scope);

    /// <summary>
    /// Checks if the result has all specified scopes.
    /// </summary>
    public bool HasAllScopes(params string[] requiredScopes) =>
        requiredScopes.All(s => Scopes.Contains(s));

    /// <summary>
    /// Checks if the result has any of the specified scopes.
    /// </summary>
    public bool HasAnyScope(params string[] requiredScopes) =>
        requiredScopes.Any(s => Scopes.Contains(s));

    /// <summary>
    /// Checks if the role is an admin role.
    /// </summary>
    public bool IsAdmin => Roles.IsAdmin(Role);
}
