using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using SerialMemory.Core.Interfaces;

namespace SerialMemory.Infrastructure.Email;

/// <summary>
/// Service for email verification and passwordless login tokens.
/// </summary>
public sealed class EmailVerificationService(
    IConfiguration configuration,
    IEmailService emailService,
    IDeveloperOnboardingService? onboardingService,
    ILogger<EmailVerificationService> logger)
    : IEmailVerificationService
{
    private readonly string _connectionString = configuration.GetConnectionString("Postgres")
                                                ?? BuildConnectionString();

    private readonly string _baseUrl = configuration["App:BaseUrl"]
                                       ?? Environment.GetEnvironmentVariable("SERIALMEMORY_BASE_URL")
                                       ?? "https://serialmemory.serialcoder.com";

    private static string BuildConnectionString()
    {
        var host = Environment.GetEnvironmentVariable("POSTGRES_HOST") ?? "localhost";
        var port = Environment.GetEnvironmentVariable("POSTGRES_PORT") ?? "5434";
        var user = Environment.GetEnvironmentVariable("POSTGRES_USER") ?? "postgres";
        var password = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD") ?? "postgres";
        var database = Environment.GetEnvironmentVariable("POSTGRES_DB") ?? "contextdb";
        return $"Host={host};Port={port};Username={user};Password={password};Database={database}";
    }

    public async Task<string> CreateVerificationTokenAsync(
        Guid tenantId,
        string userId,
        string email,
        string? ipAddress = null,
        string? userAgent = null,
        CancellationToken cancellationToken = default)
    {
        var token = GenerateSecureToken();
        var tokenHash = ComputeHash(token);
        var expiresAt = DateTimeOffset.UtcNow.AddHours(24);

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        // Invalidate any existing verification tokens for this email
        await conn.ExecuteAsync(
            """
            UPDATE email_verification_tokens
            SET used_at = NOW()
            WHERE email = @Email AND token_type = 'verification' AND used_at IS NULL
            """,
            new { Email = email });

        // Create new token
        await conn.ExecuteAsync(
            """
            INSERT INTO email_verification_tokens
                (id, tenant_id, user_id, email, token_hash, token_type, expires_at, ip_address, user_agent)
            VALUES
                (@Id, @TenantId, @UserId, @Email, @TokenHash, 'verification', @ExpiresAt, @IpAddress::inet, @UserAgent)
            """,
            new
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                UserId = userId,
                Email = email,
                TokenHash = tokenHash,
                ExpiresAt = expiresAt,
                IpAddress = ipAddress,
                UserAgent = userAgent
            });

        logger.LogInformation("Created email verification token for {Email}", email);
        return token;
    }

    public async Task<EmailVerificationResult> VerifyEmailAsync(
        string token,
        string? ipAddress = null,
        CancellationToken cancellationToken = default)
    {
        var tokenHash = ComputeHash(token);

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);

        try
        {
            // Find and validate token
            var tokenRecord = await conn.QueryFirstOrDefaultAsync<TokenRecord>(
                """
                SELECT id, tenant_id, user_id, email, expires_at, used_at
                FROM email_verification_tokens
                WHERE token_hash = @TokenHash AND token_type = 'verification'
                """,
                new { TokenHash = tokenHash },
                tx);

            if (tokenRecord == null)
            {
                return new EmailVerificationResult { Success = false, Error = "Invalid verification link" };
            }

            if (tokenRecord.used_at.HasValue)
            {
                return new EmailVerificationResult { Success = false, Error = "This link has already been used" };
            }

            if (tokenRecord.expires_at < DateTimeOffset.UtcNow)
            {
                return new EmailVerificationResult { Success = false, Error = "This link has expired" };
            }

            // Mark token as used
            await conn.ExecuteAsync(
                "UPDATE email_verification_tokens SET used_at = NOW() WHERE id = @Id",
                new { Id = tokenRecord.id },
                tx);

            // Update user's email verification status
            await conn.ExecuteAsync(
                """
                UPDATE tenant_users
                SET email_verified = TRUE, email_verified_at = NOW()
                WHERE tenant_id = @TenantId AND user_id = @UserId
                """,
                new { TenantId = tokenRecord.tenant_id, UserId = tokenRecord.user_id },
                tx);

            await tx.CommitAsync(cancellationToken);

            logger.LogInformation("Email verified for {Email}", tokenRecord.email);

            // Provision tenant if onboarding service is available
            OnboardingResult? onboardingResult = null;
            if (onboardingService != null)
            {
                try
                {
                    onboardingResult = await onboardingService.ProvisionTenantAsync(
                        tokenRecord.email,
                        tenantName: null,
                        ipAddress,
                        cancellationToken);

                    if (onboardingResult.Success)
                    {
                        logger.LogInformation(
                            "Tenant {TenantId} provisioned for {Email} during email verification",
                            onboardingResult.TenantId, tokenRecord.email);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to provision tenant for {Email}", tokenRecord.email);
                    // Don't fail verification due to provisioning failure
                }
            }

            return new EmailVerificationResult
            {
                Success = true,
                TenantId = onboardingResult?.TenantId ?? tokenRecord.tenant_id,
                UserId = tokenRecord.user_id,
                Email = tokenRecord.email,
                ApiKey = onboardingResult?.ApiKey,
                ApiKeyPrefix = onboardingResult?.ApiKeyPrefix,
                DockerCommand = onboardingResult?.DockerCommand,
                McpConfig = onboardingResult?.McpConfig
            };
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<string> CreateLoginTokenAsync(
        string email,
        string? ipAddress = null,
        string? userAgent = null,
        CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        // Check rate limiting - max 5 login requests per email per hour
        var recentAttempts = await conn.QueryFirstOrDefaultAsync<int>(
            """
            SELECT COUNT(*) FROM email_verification_tokens
            WHERE email = @Email AND token_type = 'login' AND created_at > NOW() - INTERVAL '1 hour'
            """,
            new { Email = email });

        if (recentAttempts >= 5)
        {
            logger.LogWarning("Rate limit exceeded for login token requests: {Email}", email);
            throw new InvalidOperationException("Too many login requests. Please try again later.");
        }

        // Find the user
        var user = await conn.QueryFirstOrDefaultAsync<UserRecord>(
            """
            SELECT tu.tenant_id, tu.user_id, t.name as tenant_name
            FROM tenant_users tu
            JOIN tenants t ON tu.tenant_id = t.id
            WHERE tu.user_id = @Email AND t.status = 'active'
            ORDER BY tu.created_at DESC
            LIMIT 1
            """,
            new { Email = email.ToLowerInvariant() });

        if (user == null)
        {
            // Don't reveal if user exists - still return success but don't send email
            logger.LogWarning("Login token requested for non-existent user: {Email}", email);
            return string.Empty;
        }

        var token = GenerateSecureToken();
        var tokenHash = ComputeHash(token);
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(15);

        // Invalidate any existing login tokens for this email
        await conn.ExecuteAsync(
            """
            UPDATE email_verification_tokens
            SET used_at = NOW()
            WHERE email = @Email AND token_type = 'login' AND used_at IS NULL
            """,
            new { Email = email });

        // Create new token
        await conn.ExecuteAsync(
            """
            INSERT INTO email_verification_tokens
                (id, tenant_id, user_id, email, token_hash, token_type, expires_at, ip_address, user_agent)
            VALUES
                (@Id, @TenantId, @UserId, @Email, @TokenHash, 'login', @ExpiresAt, @IpAddress::inet, @UserAgent)
            """,
            new
            {
                Id = Guid.CreateVersion7(),
                TenantId = user.tenant_id,
                UserId = user.user_id,
                Email = email,
                TokenHash = tokenHash,
                ExpiresAt = expiresAt,
                IpAddress = ipAddress,
                UserAgent = userAgent
            });

        logger.LogInformation("Created login token for {Email}", email);
        return token;
    }

    public async Task<LoginResult> ValidateLoginTokenAsync(
        string token,
        string? ipAddress = null,
        CancellationToken cancellationToken = default)
    {
        var tokenHash = ComputeHash(token);

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);

        try
        {
            // Record login attempt
            await conn.ExecuteAsync(
                """
                INSERT INTO login_attempts (id, email, ip_address, success, created_at)
                SELECT @Id, email, @IpAddress::inet, FALSE, NOW()
                FROM email_verification_tokens WHERE token_hash = @TokenHash
                """,
                new { Id = Guid.CreateVersion7(), TokenHash = tokenHash, IpAddress = ipAddress },
                tx);

            // Find and validate token
            var tokenRecord = await conn.QueryFirstOrDefaultAsync<TokenRecord>(
                """
                SELECT id, tenant_id, user_id, email, expires_at, used_at
                FROM email_verification_tokens
                WHERE token_hash = @TokenHash AND token_type = 'login'
                """,
                new { TokenHash = tokenHash },
                tx);

            if (tokenRecord == null)
            {
                await tx.CommitAsync(cancellationToken);
                return new LoginResult { Success = false, Error = "Invalid login link" };
            }

            if (tokenRecord.used_at.HasValue)
            {
                await tx.CommitAsync(cancellationToken);
                return new LoginResult { Success = false, Error = "This link has already been used" };
            }

            if (tokenRecord.expires_at < DateTimeOffset.UtcNow)
            {
                await tx.CommitAsync(cancellationToken);
                return new LoginResult { Success = false, Error = "This link has expired" };
            }

            // Mark token as used
            await conn.ExecuteAsync(
                "UPDATE email_verification_tokens SET used_at = NOW() WHERE id = @Id",
                new { Id = tokenRecord.id },
                tx);

            // Update login attempt to success
            await conn.ExecuteAsync(
                """
                UPDATE login_attempts SET success = TRUE
                WHERE email = @Email AND ip_address = @IpAddress::inet
                ORDER BY created_at DESC LIMIT 1
                """,
                new { Email = tokenRecord.email, IpAddress = ipAddress },
                tx);

            // Get user info for JWT
            var userInfo = await conn.QueryFirstOrDefaultAsync<UserInfo>(
                """
                SELECT tu.role, t.name as tenant_name, t.slug as tenant_slug
                FROM tenant_users tu
                JOIN tenants t ON tu.tenant_id = t.id
                WHERE tu.tenant_id = @TenantId AND tu.user_id = @UserId
                """,
                new { TenantId = tokenRecord.tenant_id, UserId = tokenRecord.user_id },
                tx);

            await tx.CommitAsync(cancellationToken);

            logger.LogInformation("Successful login via magic link for {Email}", tokenRecord.email);

            return new LoginResult
            {
                Success = true,
                TenantId = tokenRecord.tenant_id,
                UserId = tokenRecord.user_id,
                Email = tokenRecord.email,
                Role = userInfo?.role ?? "member",
                TenantName = userInfo?.tenant_name,
                TenantSlug = userInfo?.tenant_slug
            };
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task SendVerificationEmailAsync(
        Guid tenantId,
        string userId,
        string email,
        string? ipAddress = null,
        string? userAgent = null,
        CancellationToken cancellationToken = default)
    {
        var token = await CreateVerificationTokenAsync(tenantId, userId, email, ipAddress, userAgent, cancellationToken);
        var verificationUrl = $"{_baseUrl}/verify-email?token={Uri.EscapeDataString(token)}";

        await emailService.SendVerificationEmailAsync(email, verificationUrl, cancellationToken);
        await LogEmailAsync(tenantId, email, "verification", "Verify your email - SerialMemory", null, cancellationToken);
    }

    public async Task SendLoginLinkAsync(
        string email,
        string? ipAddress = null,
        string? userAgent = null,
        CancellationToken cancellationToken = default)
    {
        var token = await CreateLoginTokenAsync(email, ipAddress, userAgent, cancellationToken);

        if (string.IsNullOrEmpty(token))
        {
            // User doesn't exist - don't reveal this
            return;
        }

        var loginUrl = $"{_baseUrl}/auth/magic-link?token={Uri.EscapeDataString(token)}";
        await emailService.SendLoginLinkAsync(email, loginUrl, cancellationToken);

        // Get tenant ID for logging
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        var tenantId = await conn.QueryFirstOrDefaultAsync<Guid?>(
            "SELECT tenant_id FROM tenant_users WHERE user_id = @Email ORDER BY created_at DESC LIMIT 1",
            new { Email = email.ToLowerInvariant() });

        await LogEmailAsync(tenantId, email, "login_link", "Your login link - SerialMemory", null, cancellationToken);
    }

    public async Task LogEmailAsync(
        Guid? tenantId,
        string recipientEmail,
        string emailType,
        string? subject,
        string? operationId,
        CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        await conn.ExecuteAsync(
            """
            INSERT INTO email_audit_log (id, tenant_id, recipient_email, email_type, subject, operation_id, status, created_at)
            VALUES (@Id, @TenantId, @RecipientEmail, @EmailType, @Subject, @OperationId, 'sent', NOW())
            """,
            new
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                RecipientEmail = recipientEmail,
                EmailType = emailType,
                Subject = subject,
                OperationId = operationId
            });
    }

    private static string GenerateSecureToken()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").TrimEnd('=');
    }

    private static string ComputeHash(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    // DTOs
    private sealed class TokenRecord
    {
        public Guid id { get; set; }
        public Guid tenant_id { get; set; }
        public string user_id { get; set; } = "";
        public string email { get; set; } = "";
        public DateTimeOffset expires_at { get; set; }
        public DateTimeOffset? used_at { get; set; }
    }

    private sealed class UserRecord
    {
        public Guid tenant_id { get; set; }
        public string user_id { get; set; } = "";
        public string tenant_name { get; set; } = "";
    }

    private sealed class UserInfo
    {
        public string role { get; set; } = "";
        public string tenant_name { get; set; } = "";
        public string tenant_slug { get; set; } = "";
    }
}
