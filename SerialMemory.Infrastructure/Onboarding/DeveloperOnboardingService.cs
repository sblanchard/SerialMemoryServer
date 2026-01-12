using System.Text;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using SerialMemory.Core.Interfaces;
using SerialMemory.Core.Models;

namespace SerialMemory.Infrastructure.Onboarding;

/// <summary>
/// Service for developer onboarding flow.
/// Handles tenant provisioning, API key generation (sk_live_), and welcome emails.
/// </summary>
public sealed class DeveloperOnboardingService(
    IConfiguration configuration,
    IEmailService emailService,
    ILogger<DeveloperOnboardingService> logger)
    : IDeveloperOnboardingService
{
    private readonly string _connectionString = configuration.GetConnectionString("Postgres")
                                                ?? BuildConnectionString();

    private readonly string _serverUrl = configuration["App:ServerUrl"]
                                         ?? Environment.GetEnvironmentVariable("SERIALMEMORY_SERVER_URL")
                                         ?? "https://serialmemory.serialcoder.com";

    private static string BuildConnectionString()
        => SerialMemory.Infrastructure.Configuration.ConnectionStringFactory.BuildConnectionString();

    public async Task<OnboardingResult> ProvisionTenantAsync(
        string email,
        string? tenantName = null,
        string? ipAddress = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedEmail = email.ToLowerInvariant().Trim();
        var effectiveTenantName = tenantName ?? ExtractTenantName(normalizedEmail);

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);

        try
        {
            // Check if tenant already exists for this email
            var existingTenant = await conn.QueryFirstOrDefaultAsync<Guid?>(
                "SELECT tenant_id FROM tenant_users WHERE user_id = @Email LIMIT 1",
                new { Email = normalizedEmail },
                tx);

            if (existingTenant.HasValue)
            {
                // Update email_verified = TRUE for existing user (the whole point of verification!)
                await conn.ExecuteAsync(
                    """
                    UPDATE tenant_users
                    SET email_verified = TRUE, email_verified_at = COALESCE(email_verified_at, NOW()), updated_at = NOW()
                    WHERE tenant_id = @TenantId AND user_id = @UserId
                    """,
                    new { TenantId = existingTenant.Value, UserId = normalizedEmail },
                    tx);

                await tx.CommitAsync(cancellationToken);

                // Return existing tenant info
                var existingKey = await GetExistingApiKeyAsync(conn, existingTenant.Value);

                return new OnboardingResult
                {
                    Success = true,
                    TenantId = existingTenant.Value,
                    TenantSlug = await GetTenantSlugAsync(conn, existingTenant.Value) ?? "",
                    UserId = normalizedEmail,
                    ApiKeyId = existingKey?.id ?? Guid.Empty,
                    ApiKeyPrefix = existingKey?.key_prefix ?? "",
                    Error = null
                };
            }

            // Generate slug
            var slug = GenerateSlug(effectiveTenantName);
            var existingSlug = await conn.QueryFirstOrDefaultAsync<Guid?>(
                "SELECT id FROM tenants WHERE slug = @Slug",
                new { Slug = slug },
                tx);

            if (existingSlug.HasValue)
            {
                slug = $"{slug}-{Guid.NewGuid().ToString("N")[..6]}";
            }

            // Create tenant
            var tenantId = Guid.CreateVersion7();
            await conn.ExecuteAsync(
                """
                INSERT INTO tenants (id, name, slug, status, created_at, updated_at)
                VALUES (@Id, @Name, @Slug, 'active', NOW(), NOW())
                """,
                new { Id = tenantId, Name = effectiveTenantName, Slug = slug },
                tx);

            logger.LogInformation("Created tenant {TenantId} ({Slug}) for {Email}", tenantId, slug, normalizedEmail);

            // Create tenant settings (free plan)
            await conn.ExecuteAsync(
                """
                INSERT INTO tenant_settings (tenant_id, plan, max_workspaces, max_users, created_at, updated_at)
                VALUES (@TenantId, 'free', 1, 5, NOW(), NOW())
                """,
                new { TenantId = tenantId },
                tx);

            // Create default workspace
            var workspaceId = Guid.CreateVersion7();
            await conn.ExecuteAsync(
                """
                INSERT INTO tenant_workspaces (id, tenant_id, name, slug, is_default, created_at, updated_at)
                VALUES (@Id, @TenantId, 'Default', 'default', TRUE, NOW(), NOW())
                """,
                new { Id = workspaceId, TenantId = tenantId },
                tx);

            // Link user to tenant as owner
            await conn.ExecuteAsync(
                """
                INSERT INTO tenant_users (tenant_id, user_id, role, email_verified, email_verified_at, created_at, updated_at)
                VALUES (@TenantId, @UserId, 'owner', TRUE, NOW(), NOW(), NOW())
                ON CONFLICT (tenant_id, user_id) DO UPDATE SET email_verified = TRUE, email_verified_at = NOW()
                """,
                new { TenantId = tenantId, UserId = normalizedEmail },
                tx);

            // Create initial billing cycle
            var cycleStart = DateTimeOffset.UtcNow.Date;
            var cycleEnd = cycleStart.AddDays(30);

            await conn.ExecuteAsync(
                """
                INSERT INTO billing_cycles (id, tenant_id, workspace_id, cycle_start, cycle_end, credits_allocated, credits_used, is_current, created_at)
                VALUES (@Id, @TenantId, 'default', @CycleStart, @CycleEnd, 1000, 0, TRUE, NOW())
                """,
                new
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = tenantId.ToString(),
                    CycleStart = cycleStart,
                    CycleEnd = cycleEnd
                },
                tx);

            // Generate API key with sk_live_ prefix
            var (key, hash, prefix) = ApiKeyGenerator.GenerateSkLive();
            var keyId = Guid.CreateVersion7();
            var scopes = new[] { "serialmemory.core" };

            await conn.ExecuteAsync(
                """
                INSERT INTO tenant_api_keys (id, tenant_id, name, key_hash, key_prefix, scopes, created_by, one_time_viewed, created_at)
                VALUES (@Id, @TenantId, @Name, @KeyHash, @KeyPrefix, @Scopes, @CreatedBy, FALSE, NOW())
                """,
                new
                {
                    Id = keyId,
                    TenantId = tenantId,
                    Name = "Default API Key",
                    KeyHash = hash,
                    KeyPrefix = prefix,
                    Scopes = scopes,
                    CreatedBy = normalizedEmail
                },
                tx);

            // Log audit event
            await LogSecurityEventAsync(conn, tx, tenantId, "tenant_provisioned",
                $"New tenant provisioned for {normalizedEmail}", ipAddress);

            await LogSecurityEventAsync(conn, tx, tenantId, "api_key_created",
                $"API key {keyId} created (sk_live_***)", ipAddress);

            await tx.CommitAsync(cancellationToken);

            logger.LogInformation(
                "Tenant {TenantId} provisioned successfully for {Email} with API key {KeyId}",
                tenantId, normalizedEmail, keyId);

            // Generate Docker command and MCP config
            var dockerCommand = GenerateDockerCommand(key);
            var mcpConfig = GenerateMcpConfig(key);

            // Send welcome email with API key (key shown in email once)
            try
            {
                await SendWelcomeEmailAsync(normalizedEmail, key, dockerCommand, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to send welcome email to {Email}", normalizedEmail);
                // Don't fail provisioning due to email failure
            }

            return new OnboardingResult
            {
                Success = true,
                TenantId = tenantId,
                TenantSlug = slug,
                WorkspaceId = workspaceId,
                UserId = normalizedEmail,
                ApiKeyId = keyId,
                ApiKey = key,
                ApiKeyPrefix = prefix,
                DockerCommand = dockerCommand,
                McpConfig = mcpConfig
            };
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(cancellationToken);
            logger.LogError(ex, "Failed to provision tenant for {Email}", normalizedEmail);
            throw;
        }
    }

    public async Task<OneTimeApiKeyResult?> GetOneTimeApiKeyAsync(
        Guid tenantId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        // Get API key info - only return if not yet viewed
        var keyInfo = await conn.QueryFirstOrDefaultAsync<ApiKeyRow>(
            """
            SELECT id, name, key_prefix, one_time_viewed, created_at
            FROM tenant_api_keys
            WHERE tenant_id = @TenantId
              AND created_by = @UserId
              AND revoked_at IS NULL
            ORDER BY created_at DESC
            LIMIT 1
            """,
            new { TenantId = tenantId, UserId = userId.ToLowerInvariant() });

        if (keyInfo == null)
            return null;

        // If already viewed, return info but not the key
        if (keyInfo.one_time_viewed)
        {
            return new OneTimeApiKeyResult
            {
                KeyId = keyInfo.id,
                Key = "", // Not available
                Prefix = keyInfo.key_prefix,
                Name = keyInfo.name,
                CreatedAt = new DateTimeOffset(keyInfo.created_at, TimeSpan.Zero),
                WasViewed = true,
                DockerCommand = "",
                McpConfig = ""
            };
        }

        // Key hasn't been viewed - we can't return the actual key since we only store hash
        // The key was sent in the welcome email and shown on the redirect page
        return new OneTimeApiKeyResult
        {
            KeyId = keyInfo.id,
            Key = "", // Hash only stored - key was in email/redirect
            Prefix = keyInfo.key_prefix,
            Name = keyInfo.name,
            CreatedAt = new DateTimeOffset(keyInfo.created_at, TimeSpan.Zero),
            WasViewed = false,
            DockerCommand = "",
            McpConfig = ""
        };
    }

    public async Task MarkApiKeyViewedAsync(
        Guid tenantId,
        Guid keyId,
        CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        await conn.ExecuteAsync(
            """
            UPDATE tenant_api_keys
            SET one_time_viewed = TRUE
            WHERE id = @KeyId AND tenant_id = @TenantId
            """,
            new { KeyId = keyId, TenantId = tenantId });

        logger.LogInformation("API key {KeyId} marked as viewed for tenant {TenantId}", keyId, tenantId);
    }

    public async Task<OneTimeApiKeyResult> RegenerateApiKeyAsync(
        Guid tenantId,
        string userId,
        string? ipAddress = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedUserId = userId.ToLowerInvariant();

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);

        try
        {
            // Revoke all existing keys
            await conn.ExecuteAsync(
                """
                UPDATE tenant_api_keys
                SET revoked_at = NOW()
                WHERE tenant_id = @TenantId AND revoked_at IS NULL
                """,
                new { TenantId = tenantId },
                tx);

            // Generate new key
            var (key, hash, prefix) = ApiKeyGenerator.GenerateSkLive();
            var keyId = Guid.CreateVersion7();
            var scopes = new[] { "serialmemory.core" };

            await conn.ExecuteAsync(
                """
                INSERT INTO tenant_api_keys (id, tenant_id, name, key_hash, key_prefix, scopes, created_by, one_time_viewed, created_at)
                VALUES (@Id, @TenantId, @Name, @KeyHash, @KeyPrefix, @Scopes, @CreatedBy, FALSE, NOW())
                """,
                new
                {
                    Id = keyId,
                    TenantId = tenantId,
                    Name = "Regenerated API Key",
                    KeyHash = hash,
                    KeyPrefix = prefix,
                    Scopes = scopes,
                    CreatedBy = normalizedUserId
                },
                tx);

            await LogSecurityEventAsync(conn, tx, tenantId, "api_key_regenerated",
                $"API key regenerated by {normalizedUserId}", ipAddress);

            await tx.CommitAsync(cancellationToken);

            logger.LogInformation("API key regenerated for tenant {TenantId} by {UserId}", tenantId, normalizedUserId);

            var dockerCommand = GenerateDockerCommand(key);
            var mcpConfig = GenerateMcpConfig(key);

            return new OneTimeApiKeyResult
            {
                KeyId = keyId,
                Key = key,
                Prefix = prefix,
                Name = "Regenerated API Key",
                CreatedAt = DateTimeOffset.UtcNow,
                WasViewed = false,
                DockerCommand = dockerCommand,
                McpConfig = mcpConfig
            };
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public string GenerateDockerCommand(string apiKey, string? serverUrl = null)
    {
        var url = serverUrl ?? _serverUrl;
        return $"""
            docker run \
              -e MCP_SERVER_URL={url} \
              -e MCP_API_KEY={apiKey} \
              -p 4317:4317 \
              serialcoder/serialmemory-mcp
            """;
    }

    public string GenerateMcpConfig(string apiKey, string? serverUrl = null)
    {
        var url = serverUrl ?? _serverUrl;
        return $$"""
            {
              "mcpServers": {
                "serialmemory-memory": {
                  "command": "docker",
                  "args": [
                    "run", "--rm", "-i",
                    "-e", "MCP_SERVER_URL={{url}}",
                    "-e", "MCP_API_KEY={{apiKey}}",
                    "serialcoder/serialmemory-mcp"
                  ]
                }
              }
            }
            """;
    }

    private async Task SendWelcomeEmailAsync(
        string email,
        string apiKey,
        string dockerCommand,
        CancellationToken cancellationToken)
    {
        var subject = "Your SerialMemory API Key";
        var htmlBody = $$"""
            <!DOCTYPE html>
            <html>
            <head>
                <style>
                    body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; line-height: 1.6; color: #333; }
                    .container { max-width: 600px; margin: 0 auto; padding: 20px; }
                    .header { background: linear-gradient(135deg, #667eea 0%, #764ba2 100%); color: white; padding: 30px; border-radius: 10px 10px 0 0; }
                    .content { background: #f8f9fa; padding: 30px; border-radius: 0 0 10px 10px; }
                    .code-block { background: #1e1e1e; color: #d4d4d4; padding: 15px; border-radius: 5px; font-family: 'Fira Code', 'Consolas', monospace; font-size: 13px; overflow-x: auto; white-space: pre-wrap; word-break: break-all; }
                    .warning { background: #fff3cd; border-left: 4px solid #ffc107; padding: 15px; margin: 20px 0; }
                    .step { margin: 20px 0; padding: 15px; background: white; border-radius: 5px; box-shadow: 0 1px 3px rgba(0,0,0,0.1); }
                    .step-number { display: inline-block; width: 30px; height: 30px; background: #667eea; color: white; border-radius: 50%; text-align: center; line-height: 30px; margin-right: 10px; }
                </style>
            </head>
            <body>
                <div class="container">
                    <div class="header">
                        <h1>Welcome to SerialMemory</h1>
                        <p>Your AI memory infrastructure is ready</p>
                    </div>
                    <div class="content">
                        <div class="warning">
                            <strong>Important:</strong> This API key will only be shown once. Save it securely now.
                        </div>

                        <h2>Your API Key</h2>
                        <div class="code-block">{{apiKey}}</div>

                        <h2>Quick Start</h2>

                        <div class="step">
                            <span class="step-number">1</span>
                            <strong>Run the Docker MCP Client</strong>
                            <div class="code-block">{{dockerCommand}}</div>
                        </div>

                        <div class="step">
                            <span class="step-number">2</span>
                            <strong>Connect Claude Desktop</strong>
                            <p>Add this to your Claude Desktop config:</p>
                            <div class="code-block">{{GenerateMcpConfig(apiKey)}}</div>
                        </div>

                        <div class="step">
                            <span class="step-number">3</span>
                            <strong>Start Using Memory</strong>
                            <p>Claude will now have access to persistent memory across sessions.</p>
                        </div>

                        <p style="margin-top: 30px; color: #666;">
                            Need help? Visit <a href="https://docs.serialmemory.com">docs.serialmemory.com</a>
                        </p>
                    </div>
                </div>
            </body>
            </html>
            """;

        await emailService.SendAsync(email, subject, htmlBody, cancellationToken);
        logger.LogInformation("Welcome email sent to {Email}", email);
    }

    private static async Task LogSecurityEventAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        Guid tenantId,
        string eventType,
        string message,
        string? ipAddress)
    {
        await conn.ExecuteAsync(
            """
            INSERT INTO security_events (id, tenant_id, event_type, category, severity, message, ip_address, created_at)
            VALUES (@Id, @TenantId, @EventType, 'onboarding', 'info', @Message, @IpAddress::inet, NOW())
            """,
            new
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                EventType = eventType,
                Message = message,
                IpAddress = ipAddress
            },
            tx);
    }

    private static async Task<ApiKeyRow?> GetExistingApiKeyAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        NpgsqlTransaction? tx = null)
    {
        return await conn.QueryFirstOrDefaultAsync<ApiKeyRow>(
            """
            SELECT id, name, key_prefix, one_time_viewed, created_at
            FROM tenant_api_keys
            WHERE tenant_id = @TenantId AND revoked_at IS NULL
            ORDER BY created_at DESC
            LIMIT 1
            """,
            new { TenantId = tenantId },
            tx);
    }

    private static async Task<string?> GetTenantSlugAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        NpgsqlTransaction? tx = null)
    {
        return await conn.QueryFirstOrDefaultAsync<string>(
            "SELECT slug FROM tenants WHERE id = @TenantId",
            new { TenantId = tenantId },
            tx);
    }

    private static string GenerateSlug(string name)
    {
        var slug = name.ToLowerInvariant()
            .Replace(" ", "-")
            .Replace("_", "-")
            .Replace("@", "")
            .Replace(".", "-");

        var sb = new StringBuilder();
        foreach (var c in slug)
        {
            if (char.IsLetterOrDigit(c) || c == '-')
                sb.Append(c);
        }

        slug = sb.ToString();

        while (slug.Contains("--"))
            slug = slug.Replace("--", "-");

        return slug.Trim('-');
    }

    private static string ExtractTenantName(string email)
    {
        var atIndex = email.IndexOf('@');
        if (atIndex > 0)
        {
            var domain = email[(atIndex + 1)..];
            var dotIndex = domain.IndexOf('.');
            if (dotIndex > 0)
            {
                return char.ToUpper(domain[0]) + domain[1..dotIndex];
            }
            return domain;
        }
        return email;
    }

    private sealed class ApiKeyRow
    {
        public Guid id { get; set; }
        public string name { get; set; } = "";
        public string key_prefix { get; set; } = "";
        public bool one_time_viewed { get; set; }
        public DateTime created_at { get; set; }
    }
}
