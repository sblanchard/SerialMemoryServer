namespace SerialMemory.Core.Interfaces;

/// <summary>
/// Service for email verification and passwordless login.
/// </summary>
public interface IEmailVerificationService
{
    /// <summary>
    /// Creates a verification token and returns it (caller is responsible for sending email).
    /// </summary>
    Task<string> CreateVerificationTokenAsync(
        Guid tenantId,
        string userId,
        string email,
        string? ipAddress = null,
        string? userAgent = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies an email using the provided token.
    /// </summary>
    Task<EmailVerificationResult> VerifyEmailAsync(
        string token,
        string? ipAddress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a magic link login token and returns it.
    /// Returns empty string if user doesn't exist (for security).
    /// </summary>
    Task<string> CreateLoginTokenAsync(
        string email,
        string? ipAddress = null,
        string? userAgent = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates a magic link login token and returns user info.
    /// </summary>
    Task<LoginResult> ValidateLoginTokenAsync(
        string token,
        string? ipAddress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates verification token and sends verification email.
    /// </summary>
    Task SendVerificationEmailAsync(
        Guid tenantId,
        string userId,
        string email,
        string? ipAddress = null,
        string? userAgent = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates login token and sends magic link email.
    /// Silently succeeds even if user doesn't exist (security).
    /// </summary>
    Task SendLoginLinkAsync(
        string email,
        string? ipAddress = null,
        string? userAgent = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Logs an email send event for audit purposes.
    /// </summary>
    Task LogEmailAsync(
        Guid? tenantId,
        string recipientEmail,
        string emailType,
        string? subject,
        string? operationId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of email verification.
/// </summary>
public sealed record EmailVerificationResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public Guid? TenantId { get; init; }
    public string? UserId { get; init; }
    public string? Email { get; init; }

    // Onboarding result (populated after tenant provisioning)
    public string? ApiKey { get; init; }
    public string? ApiKeyPrefix { get; init; }
    public string? DockerCommand { get; init; }
    public string? McpConfig { get; init; }
}

/// <summary>
/// Result of magic link login validation.
/// </summary>
public sealed record LoginResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public Guid? TenantId { get; init; }
    public string? UserId { get; init; }
    public string? Email { get; init; }
    public string? Role { get; init; }
    public string? TenantName { get; init; }
    public string? TenantSlug { get; init; }
}
