namespace SerialMemory.Core.Interfaces;

/// <summary>
/// Email service interface for sending transactional emails.
/// </summary>
public interface IEmailService
{
    /// <summary>
    /// Send an email asynchronously.
    /// </summary>
    /// <param name="to">Recipient email address</param>
    /// <param name="subject">Email subject</param>
    /// <param name="htmlBody">HTML body content</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task SendAsync(string to, string subject, string htmlBody, CancellationToken cancellationToken = default);

    /// <summary>
    /// Send email verification link.
    /// </summary>
    /// <param name="apiKey">Optional API key to include in the email (only shown once at signup)</param>
    Task SendVerificationEmailAsync(string to, string verificationUrl, string? apiKey = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Send passwordless login link.
    /// </summary>
    Task SendLoginLinkAsync(string to, string loginUrl, CancellationToken cancellationToken = default);

    /// <summary>
    /// Send API key created notification.
    /// </summary>
    Task SendApiKeyCreatedAsync(string to, string keyName, CancellationToken cancellationToken = default);
}
