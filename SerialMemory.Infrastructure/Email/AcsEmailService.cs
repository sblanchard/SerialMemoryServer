using Azure;
using Azure.Communication.Email;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SerialMemory.Core.Interfaces;

namespace SerialMemory.Infrastructure.Email;

/// <summary>
/// Azure Communication Services email implementation.
/// </summary>
public sealed class AcsEmailService : IEmailService
{
    private readonly EmailClient _emailClient;
    private readonly string _senderAddress;
    private readonly string _senderDisplayName;
    private readonly ILogger<AcsEmailService> _logger;

    public AcsEmailService(IConfiguration configuration, ILogger<AcsEmailService> logger)
    {
        _logger = logger;

        // Load from config or environment variables
        var connectionString = configuration["Email:AcsConnectionString"]
            ?? Environment.GetEnvironmentVariable("SERIALMEMORY_ACS_CONNECTION")
            ?? throw new InvalidOperationException(
                "Azure Communication Services connection string not configured. " +
                "Set Email:AcsConnectionString in appsettings.json or SERIALMEMORY_ACS_CONNECTION environment variable.");

        _senderAddress = configuration["Email:SenderAddress"]
            ?? Environment.GetEnvironmentVariable("SERIALMEMORY_EMAIL_SENDER")
            ?? throw new InvalidOperationException(
                "Email sender address not configured. " +
                "Set Email:SenderAddress in appsettings.json or SERIALMEMORY_EMAIL_SENDER environment variable.");

        _senderDisplayName = configuration["Email:SenderDisplayName"]
            ?? Environment.GetEnvironmentVariable("SERIALMEMORY_EMAIL_NAME")
            ?? "SerialMemory";

        _emailClient = new EmailClient(connectionString);
        _logger.LogInformation("AcsEmailService initialized with sender: {SenderAddress}", _senderAddress);
    }

    public async Task SendAsync(string to, string subject, string htmlBody, CancellationToken cancellationToken = default)
    {
        try
        {
            var emailMessage = new EmailMessage(
                senderAddress: _senderAddress,
                content: new EmailContent(subject)
                {
                    Html = htmlBody
                },
                recipients: new EmailRecipients(new List<EmailAddress>
                {
                    new(to)
                }));

            var operation = await _emailClient.SendAsync(WaitUntil.Completed, emailMessage, cancellationToken);

            if (operation.HasValue && operation.Value.Status == EmailSendStatus.Succeeded)
            {
                _logger.LogInformation("Email sent successfully to {Recipient}, OperationId: {OperationId}",
                    to, operation.Id);
            }
            else
            {
                _logger.LogWarning("Email send status unknown for {Recipient}", to);
            }
        }
        catch (RequestFailedException ex)
        {
            _logger.LogError(ex, "Failed to send email to {Recipient}: {ErrorCode}", to, ex.ErrorCode);
            throw;
        }
    }

    public async Task SendVerificationEmailAsync(string to, string verificationUrl, CancellationToken cancellationToken = default)
    {
        var subject = "Verify your email - SerialMemory";
        var htmlBody = $$"""
            <!DOCTYPE html>
            <html>
            <head>
                <meta charset="utf-8">
                <style>
                    body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; line-height: 1.6; color: #333; }
                    .container { max-width: 600px; margin: 0 auto; padding: 20px; }
                    .button { display: inline-block; padding: 12px 24px; background-color: #4F46E5; color: white; text-decoration: none; border-radius: 6px; font-weight: 500; }
                    .footer { margin-top: 30px; font-size: 12px; color: #666; }
                </style>
            </head>
            <body>
                <div class="container">
                    <h2>Verify your email</h2>
                    <p>Thanks for signing up for SerialMemory! Please verify your email address by clicking the button below:</p>
                    <p style="margin: 30px 0;">
                        <a href="{{verificationUrl}}" class="button">Verify Email</a>
                    </p>
                    <p>Or copy and paste this link into your browser:</p>
                    <p style="word-break: break-all; color: #4F46E5;">{{verificationUrl}}</p>
                    <p>This link will expire in 24 hours.</p>
                    <div class="footer">
                        <p>If you didn't create an account, you can safely ignore this email.</p>
                        <p>&copy; SerialMemory - Temporal Knowledge Graph Memory System</p>
                    </div>
                </div>
            </body>
            </html>
            """;

        await SendAsync(to, subject, htmlBody, cancellationToken);
    }

    public async Task SendLoginLinkAsync(string to, string loginUrl, CancellationToken cancellationToken = default)
    {
        var subject = "Your login link - SerialMemory";
        var htmlBody = $$"""
            <!DOCTYPE html>
            <html>
            <head>
                <meta charset="utf-8">
                <style>
                    body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; line-height: 1.6; color: #333; }
                    .container { max-width: 600px; margin: 0 auto; padding: 20px; }
                    .button { display: inline-block; padding: 12px 24px; background-color: #4F46E5; color: white; text-decoration: none; border-radius: 6px; font-weight: 500; }
                    .footer { margin-top: 30px; font-size: 12px; color: #666; }
                    .warning { background-color: #FEF3C7; border-left: 4px solid #F59E0B; padding: 12px; margin: 20px 0; }
                </style>
            </head>
            <body>
                <div class="container">
                    <h2>Sign in to SerialMemory</h2>
                    <p>Click the button below to sign in to your account:</p>
                    <p style="margin: 30px 0;">
                        <a href="{{loginUrl}}" class="button">Sign In</a>
                    </p>
                    <p>Or copy and paste this link into your browser:</p>
                    <p style="word-break: break-all; color: #4F46E5;">{{loginUrl}}</p>
                    <div class="warning">
                        <strong>Security note:</strong> This link will expire in 15 minutes and can only be used once.
                    </div>
                    <div class="footer">
                        <p>If you didn't request this link, you can safely ignore this email.</p>
                        <p>&copy; SerialMemory - Temporal Knowledge Graph Memory System</p>
                    </div>
                </div>
            </body>
            </html>
            """;

        await SendAsync(to, subject, htmlBody, cancellationToken);
    }

    public async Task SendApiKeyCreatedAsync(string to, string keyName, CancellationToken cancellationToken = default)
    {
        var subject = "New API key created - SerialMemory";
        var createdAt = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
        var htmlBody = $$"""
            <!DOCTYPE html>
            <html>
            <head>
                <meta charset="utf-8">
                <style>
                    body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; line-height: 1.6; color: #333; }
                    .container { max-width: 600px; margin: 0 auto; padding: 20px; }
                    .key-name { background-color: #F3F4F6; padding: 8px 12px; border-radius: 4px; font-family: monospace; }
                    .footer { margin-top: 30px; font-size: 12px; color: #666; }
                    .warning { background-color: #FEF3C7; border-left: 4px solid #F59E0B; padding: 12px; margin: 20px 0; }
                </style>
            </head>
            <body>
                <div class="container">
                    <h2>New API Key Created</h2>
                    <p>A new API key has been created for your account:</p>
                    <p><strong>Key name:</strong> <span class="key-name">{{keyName}}</span></p>
                    <p>Created at: {{createdAt}} UTC</p>
                    <div class="warning">
                        <strong>Security note:</strong> If you didn't create this API key, please review your account security immediately and revoke any unauthorized keys.
                    </div>
                    <div class="footer">
                        <p>&copy; SerialMemory - Temporal Knowledge Graph Memory System</p>
                    </div>
                </div>
            </body>
            </html>
            """;

        await SendAsync(to, subject, htmlBody, cancellationToken);
    }
}
