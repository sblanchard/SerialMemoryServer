using Microsoft.Extensions.Logging;
using SerialMemory.Core.Interfaces;

namespace SerialMemory.Infrastructure.Email;

/// <summary>
/// No-op email service for development when ACS is not configured.
/// Logs email operations but doesn't actually send them.
/// </summary>
public sealed class NoOpEmailService(ILogger<NoOpEmailService> logger) : IEmailService
{
    public Task SendAsync(string to, string subject, string htmlBody, CancellationToken cancellationToken = default)
    {
        logger.LogWarning("[DEV MODE] Email not sent (ACS not configured). To: {To}, Subject: {Subject}", to, subject);
        return Task.CompletedTask;
    }

    public Task SendVerificationEmailAsync(string to, string verificationUrl, string? apiKey = null, CancellationToken cancellationToken = default)
    {
        logger.LogWarning("[DEV MODE] Verification email not sent (ACS not configured). To: {To}, URL: {Url}, HasApiKey: {HasApiKey}", to, verificationUrl, !string.IsNullOrEmpty(apiKey));
        return Task.CompletedTask;
    }

    public Task SendLoginLinkAsync(string to, string loginUrl, CancellationToken cancellationToken = default)
    {
        logger.LogWarning("[DEV MODE] Login link email not sent (ACS not configured). To: {To}, URL: {Url}", to, loginUrl);
        return Task.CompletedTask;
    }

    public Task SendApiKeyCreatedAsync(string to, string keyName, CancellationToken cancellationToken = default)
    {
        logger.LogWarning("[DEV MODE] API key notification not sent (ACS not configured). To: {To}, Key: {KeyName}", to, keyName);
        return Task.CompletedTask;
    }
}
