using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Dapper;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using SerialMemory.Core.Deployment;
using SerialMemory.Core.Interfaces;
using SerialMemory.Core.Models;

namespace SerialMemory.Infrastructure;

/// <summary>
/// Service for managing API keys, tenant signup, and limits.
/// </summary>
public sealed class ApiKeyService : IApiKeyService
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<ApiKeyService> _logger;
    private readonly string _jwtSecret;
    private readonly string _jwtIssuer;
    private readonly string _jwtAudience;
    private readonly TimeSpan _tokenLifetime;
    private readonly IDeploymentContext? _deploymentContext;

    public ApiKeyService(
        NpgsqlDataSource dataSource,
        ILogger<ApiKeyService> logger,
        string jwtSecret,
        string jwtIssuer = "serialmemory",
        string jwtAudience = "serialmemory-api",
        TimeSpan? tokenLifetime = null,
        IDeploymentContext? deploymentContext = null)
    {
        _dataSource = dataSource;
        _logger = logger;
        _jwtSecret = jwtSecret;
        _jwtIssuer = jwtIssuer;
        _jwtAudience = jwtAudience;
        _tokenLifetime = tokenLifetime ?? TimeSpan.FromHours(24);
        _deploymentContext = deploymentContext;
    }

    public ApiKeyService(
        string connectionString,
        ILogger<ApiKeyService> logger,
        string jwtSecret,
        string jwtIssuer = "serialmemory",
        string jwtAudience = "serialmemory-api",
        IDeploymentContext? deploymentContext = null)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        _dataSource = builder.Build();
        _logger = logger;
        _jwtSecret = jwtSecret;
        _jwtIssuer = jwtIssuer;
        _jwtAudience = jwtAudience;
        _tokenLifetime = TimeSpan.FromHours(24);
        _deploymentContext = deploymentContext;
    }

    public async Task<SignupResult> SignupAsync(
        SignupRequest request,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);

        try
        {
            // CRITICAL: Set internal_admin role to bypass RLS for signup operations.
            // Use false to set for session - ensures RLS bypass works across statements
            await conn.ExecuteAsync(
                "SELECT set_config('app.role', 'internal_admin', false)",
                transaction: tx);

            // Generate slug if not provided
            var slug = request.Slug ?? GenerateSlug(request.TenantName);

            // Check if slug is already taken
            var existingTenant = await conn.QueryFirstOrDefaultAsync<Guid?>(
                "SELECT id FROM tenants WHERE slug = @Slug",
                new { Slug = slug },
                tx);

            if (existingTenant.HasValue)
            {
                // Append random suffix to make it unique
                slug = $"{slug}-{Guid.NewGuid().ToString("N")[..6]}";
            }

            // Create tenant
            var tenantId = Guid.CreateVersion7();
            var plan = request.Plan ?? "free";

            await conn.ExecuteAsync(
                """
                INSERT INTO tenants (id, name, slug, status, created_at, updated_at)
                                  VALUES (@Id, @Name, @Slug, 'active', NOW(), NOW())
                """,
                new { Id = tenantId, Name = request.TenantName, Slug = slug },
                tx);

            // Create tenant settings
            await conn.ExecuteAsync(
                """
                INSERT INTO tenant_settings (tenant_id, plan, max_workspaces, max_users, created_at, updated_at)
                                  VALUES (@TenantId, @Plan, 1, 5, NOW(), NOW())
                """,
                new { TenantId = tenantId, Plan = plan },
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

            // Determine role: member for PublicSaaS, owner only for first SelfHosted tenant
            var userId = request.Email.ToLowerInvariant();
            var isSelfHosted = _deploymentContext?.IsSelfHosted ?? false;
            var tenantCount = await conn.QuerySingleAsync<int>("SELECT COUNT(*) FROM tenants", transaction: tx);
            
            // Only assign owner if: SelfHosted mode AND this is the first tenant ever
            var assignedRole = (isSelfHosted && tenantCount == 1) ? "owner" : "member";
            
            await conn.ExecuteAsync(
                """
                INSERT INTO tenant_users (tenant_id, user_id, role, email_verified, created_at, updated_at)
                VALUES (@TenantId, @UserId, @Role, FALSE, NOW(), NOW())
                """,
                new { TenantId = tenantId, UserId = userId, Role = assignedRole },
                tx);

            // Create initial billing cycle
            var cycleStart = DateTimeOffset.UtcNow.Date;
            var cycleEnd = cycleStart.AddDays(30);
            var creditsPerCycle = GetCreditsForPlan(plan);

            await conn.ExecuteAsync(
                """
                INSERT INTO billing_cycles (id, tenant_id, workspace_id, cycle_start, cycle_end, credits_allocated, credits_used, is_current, created_at)
                                  VALUES (@Id, @TenantId, @WorkspaceId, @CycleStart, @CycleEnd, @Credits, 0, TRUE, NOW())
                """,
                new
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = tenantId.ToString(),
                    WorkspaceId = "default",
                    CycleStart = cycleStart,
                    CycleEnd = cycleEnd,
                    Credits = creditsPerCycle
                },
                tx);

            // Create initial API key
            var (key, hash, prefix) = ApiKeyGenerator.Generate("live");
            var keyId = Guid.CreateVersion7();
            var scopes = new[] { "serialmemory.core" };

            await conn.ExecuteAsync(
                """
                INSERT INTO tenant_api_keys (id, tenant_id, name, key_hash, key_prefix, scopes, created_by, created_at)
                                  VALUES (@Id, @TenantId, @Name, @KeyHash, @KeyPrefix, @Scopes, @CreatedBy, NOW())
                """,
                new
                {
                    Id = keyId,
                    TenantId = tenantId,
                    Name = "Default API Key",
                    KeyHash = hash,
                    KeyPrefix = prefix,
                    Scopes = scopes,
                    CreatedBy = userId
                },
                tx);

            await tx.CommitAsync(cancellationToken);

            // Generate JWT token
            var (token, expiresAt) = GenerateJwtToken(tenantId, userId, assignedRole, scopes);

            _logger.LogInformation(
                "New tenant signed up: {TenantId} ({Slug}) by {UserId}",
                tenantId, slug, userId);

            return new SignupResult
            {
                TenantId = tenantId,
                TenantName = request.TenantName,
                Slug = slug,
                UserId = userId,
                Role = assignedRole,
                Plan = plan,
                ApiKey = new ApiKeyCreateResult
                {
                    Id = keyId,
                    Name = "Default API Key",
                    Key = key,
                    Prefix = prefix,
                    Scopes = scopes,
                    ExpiresAt = null,
                    CreatedAt = DateTimeOffset.UtcNow
                },
                Token = token,
                TokenExpiresAt = expiresAt
            };
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<ApiKeyCreateResult> CreateApiKeyAsync(
        Guid tenantId,
        string userId,
        CreateApiKeyRequest request,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        // Set internal admin role to bypass RLS for API key creation
        await conn.SetInternalAdminWithTenantAsync(tenantId);

        // Check for duplicate name
        var existingKey = await conn.QueryFirstOrDefaultAsync<Guid?>(
            """
            SELECT id FROM tenant_api_keys
                          WHERE tenant_id = @TenantId AND name = @Name AND revoked_at IS NULL
            """,
            new { TenantId = tenantId, Name = request.Name });

        if (existingKey.HasValue)
        {
            throw new InvalidOperationException($"An API key named '{request.Name}' already exists");
        }

        // Get user's role to determine allowed scopes
        var userRole = await conn.QueryFirstOrDefaultAsync<string>(
            "SELECT role FROM tenant_users WHERE tenant_id = @TenantId AND user_id = @UserId",
            new { TenantId = tenantId, UserId = userId });

        var allowedScopes = GetScopesForRole(userRole ?? "member");
        var requestedScopes = request.Scopes ?? new[] { "serialmemory.core" };

        // Filter scopes to only those the user is allowed to grant
        var grantedScopes = requestedScopes.Intersect(allowedScopes).ToArray();
        if (grantedScopes.Length == 0)
        {
            grantedScopes = new[] { "serialmemory.core" };
        }

        // Generate new key
        var (key, hash, prefix) = ApiKeyGenerator.Generate("live");
        var keyId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;

        await conn.ExecuteAsync(
            """
            INSERT INTO tenant_api_keys (id, tenant_id, name, key_hash, key_prefix, scopes, created_by, expires_at, created_at)
                          VALUES (@Id, @TenantId, @Name, @KeyHash, @KeyPrefix, @Scopes, @CreatedBy, @ExpiresAt, NOW())
            """,
            new
            {
                Id = keyId,
                TenantId = tenantId,
                Name = request.Name,
                KeyHash = hash,
                KeyPrefix = prefix,
                Scopes = grantedScopes,
                CreatedBy = userId,
                ExpiresAt = request.ExpiresAt
            });

        _logger.LogInformation(
            "API key created: {KeyId} for tenant {TenantId} by {UserId}",
            keyId, tenantId, userId);

        return new ApiKeyCreateResult
        {
            Id = keyId,
            Name = request.Name,
            Key = key,
            Prefix = prefix,
            Scopes = grantedScopes,
            ExpiresAt = request.ExpiresAt,
            CreatedAt = now
        };
    }

    public async Task<IReadOnlyList<ApiKeyInfo>> ListApiKeysAsync(
        Guid tenantId,
        bool includeRevoked = false,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        var whereClause = includeRevoked
            ? "tenant_id = @TenantId"
            : "tenant_id = @TenantId AND revoked_at IS NULL";

        var keys = await conn.QueryAsync<ApiKeyDto>(
            $"""
             SELECT id, name, key_prefix, scopes, created_by, last_used_at, expires_at, revoked_at, created_at
                            FROM tenant_api_keys
                            WHERE {whereClause}
                            ORDER BY created_at DESC
             """,
            new { TenantId = tenantId });

        return keys.Select(k => new ApiKeyInfo
        {
            Id = k.id,
            Name = k.name,
            Prefix = k.key_prefix,
            Scopes = k.scopes ?? Array.Empty<string>(),
            CreatedBy = k.created_by,
            LastUsedAt = k.last_used_at,
            ExpiresAt = k.expires_at,
            RevokedAt = k.revoked_at,
            CreatedAt = k.created_at
        }).ToList();
    }

    public async Task<ApiKeyInfo?> GetApiKeyAsync(
        Guid tenantId,
        Guid keyId,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        var key = await conn.QueryFirstOrDefaultAsync<ApiKeyDto>(
            """
            SELECT id, name, key_prefix, scopes, created_by, last_used_at, expires_at, revoked_at, created_at
                          FROM tenant_api_keys
                          WHERE id = @KeyId AND tenant_id = @TenantId
            """,
            new { KeyId = keyId, TenantId = tenantId });

        if (key == null)
            return null;

        return new ApiKeyInfo
        {
            Id = key.id,
            Name = key.name,
            Prefix = key.key_prefix,
            Scopes = key.scopes ?? Array.Empty<string>(),
            CreatedBy = key.created_by,
            LastUsedAt = key.last_used_at,
            ExpiresAt = key.expires_at,
            RevokedAt = key.revoked_at,
            CreatedAt = key.created_at
        };
    }

    public async Task<bool> RevokeApiKeyAsync(
        Guid tenantId,
        Guid keyId,
        string revokedBy,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        var affected = await conn.ExecuteAsync(
            """
            UPDATE tenant_api_keys
                          SET revoked_at = NOW()
                          WHERE id = @KeyId AND tenant_id = @TenantId AND revoked_at IS NULL
            """,
            new { KeyId = keyId, TenantId = tenantId });

        if (affected > 0)
        {
            _logger.LogInformation(
                "API key revoked: {KeyId} for tenant {TenantId} by {RevokedBy}",
                keyId, tenantId, revokedBy);
            return true;
        }

        return false;
    }

    public async Task<ApiKeyValidationResult?> ValidateApiKeyAsync(
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return null;

        var hash = ApiKeyGenerator.ComputeHash(apiKey);
        var prefix = ApiKeyGenerator.ExtractPrefix(apiKey);

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        // CRITICAL: Set internal_admin role to bypass RLS for API key lookup.
        // We don't know the tenant yet - we're discovering it from the API key.
        await conn.ExecuteAsync("SELECT set_config('app.role', 'internal_admin', false)");

        // Find key by hash and verify it's valid
        var key = await conn.QueryFirstOrDefaultAsync<ApiKeyValidationDto>(
            """
            SELECT k.id, k.tenant_id, k.scopes, k.created_by, k.expires_at, k.revoked_at,
                                 t.slug as tenant_slug, t.name as tenant_name
                          FROM tenant_api_keys k
                          JOIN tenants t ON k.tenant_id = t.id
                          WHERE k.key_hash = @Hash AND k.key_prefix = @Prefix
                            AND t.status = 'active'
            """,
            new { Hash = hash, Prefix = prefix });

        if (key == null)
            return null;

        // Check if revoked
        if (key.revoked_at.HasValue)
        {
            _logger.LogWarning("Attempt to use revoked API key: {KeyPrefix}***", prefix);
            return null;
        }

        // Check if expired
        if (key.expires_at.HasValue && key.expires_at.Value < DateTimeOffset.UtcNow)
        {
            _logger.LogWarning("Attempt to use expired API key: {KeyPrefix}***", prefix);
            return null;
        }

        // Update last_used_at (await to ensure connection is properly released)
        await conn.ExecuteAsync(
            "UPDATE tenant_api_keys SET last_used_at = NOW() WHERE id = @KeyId",
            new { KeyId = key.id });

        return new ApiKeyValidationResult
        {
            KeyId = key.id,
            TenantId = key.tenant_id,
            Scopes = key.scopes ?? Array.Empty<string>(),
            CreatedBy = key.created_by,
            TenantSlug = key.tenant_slug,
            TenantName = key.tenant_name
        };
    }

    public async Task<TenantLimitsResult> GetTenantLimitsAsync(
        Guid tenantId,
        string workspaceId,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        // Get tenant settings and plan
        var settings = await conn.QueryFirstOrDefaultAsync<TenantSettingsDto>(
            @"SELECT plan, features FROM tenant_settings WHERE tenant_id = @TenantId",
            new { TenantId = tenantId });

        var planName = settings?.plan ?? "free";

        // Get plan details
        var plan = await conn.QueryFirstOrDefaultAsync<TenantPlanDto>(
            """
            SELECT plan_name, display_name, credits_per_cycle, cycle_days, max_memories, max_entities
                          FROM tenant_plans WHERE plan_name = @PlanName AND is_active = TRUE
            """,
            new { PlanName = planName });

        // Get current billing cycle
        var cycle = await conn.QueryFirstOrDefaultAsync<BillingCycleDto>(
            """
            SELECT credits_allocated, credits_used, cycle_start, cycle_end
                          FROM billing_cycles
                          WHERE tenant_id = @TenantId AND workspace_id = @WorkspaceId AND is_current = TRUE
            """,
            new { TenantId = tenantId.ToString(), WorkspaceId = workspaceId });

        // Get current counts
        var memoriesCount = await conn.QueryFirstOrDefaultAsync<int>(
            "SELECT COUNT(*) FROM memories WHERE tenant_id = @TenantId",
            new { TenantId = tenantId });

        var entitiesCount = await conn.QueryFirstOrDefaultAsync<int>(
            "SELECT COUNT(*) FROM entities WHERE tenant_id = @TenantId",
            new { TenantId = tenantId });

        // Calculate limits
        var monthlyCredits = plan?.credits_per_cycle ?? 1000m;
        var creditsUsed = cycle?.credits_used ?? 0m;
        var creditsRemaining = Math.Max(0, monthlyCredits - creditsUsed);
        var cycleEnd = cycle?.cycle_end ?? DateTimeOffset.UtcNow.AddDays(30);
        var daysUntilReset = Math.Max(0, (int)Math.Ceiling((cycleEnd - DateTimeOffset.UtcNow).TotalDays));

        // Rate limits based on plan
        var (rpm, rpd) = GetRateLimitsForPlan(planName);

        // Generate warnings
        var warnings = new List<LimitWarning>();

        var creditPercent = monthlyCredits > 0 ? (creditsUsed / monthlyCredits) * 100 : 0;
        if (creditPercent >= 90)
        {
            warnings.Add(new LimitWarning
            {
                Type = "credits",
                Message = $"You have used {creditPercent:F0}% of your monthly credits",
                PercentUsed = creditPercent,
                Severity = creditPercent >= 100 ? "critical" : "warning"
            });
        }
        else if (creditPercent >= 75)
        {
            warnings.Add(new LimitWarning
            {
                Type = "credits",
                Message = $"You have used {creditPercent:F0}% of your monthly credits",
                PercentUsed = creditPercent,
                Severity = "info"
            });
        }

        if (plan?.max_memories.HasValue == true)
        {
            var memoryPercent = (decimal)memoriesCount / plan.max_memories.Value * 100;
            if (memoryPercent >= 90)
            {
                warnings.Add(new LimitWarning
                {
                    Type = "memories",
                    Message = $"You have used {memoryPercent:F0}% of your memory limit ({memoriesCount:N0}/{plan.max_memories:N0})",
                    PercentUsed = memoryPercent,
                    Severity = memoryPercent >= 100 ? "critical" : "warning"
                });
            }
        }

        return new TenantLimitsResult
        {
            Plan = planName,
            DisplayName = plan?.display_name ?? GetDisplayName(planName),
            MonthlyCredits = monthlyCredits,
            CreditsUsed = creditsUsed,
            CreditsRemaining = creditsRemaining,
            DaysUntilReset = daysUntilReset,
            CycleResetAt = cycleEnd,
            RateLimitRpm = rpm,
            RateLimitRpd = rpd,
            MaxMemories = plan?.max_memories,
            MaxEntities = plan?.max_entities,
            MaxMemorySizeBytes = GetMaxMemorySizeForPlan(planName),
            MaxExportsPerDay = GetMaxExportsForPlan(planName),
            CurrentMemories = memoriesCount,
            CurrentEntities = entitiesCount,
            Warnings = warnings
        };
    }

    private (string Token, DateTimeOffset ExpiresAt) GenerateJwtToken(
        Guid tenantId,
        string userId,
        string role,
        IReadOnlyList<string> scopes)
    {
        var expiresAt = DateTimeOffset.UtcNow.Add(_tokenLifetime);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new("tenant_id", tenantId.ToString()),
            new("role", role)
        };

        foreach (var scope in scopes)
        {
            claims.Add(new Claim("scope", scope));
        }

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtSecret));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _jwtIssuer,
            audience: _jwtAudience,
            claims: claims,
            expires: expiresAt.UtcDateTime,
            signingCredentials: creds);

        return (new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }

    private static string GenerateSlug(string name)
    {
        var slug = name.ToLowerInvariant()
            .Replace(" ", "-")
            .Replace("_", "-");

        // Remove any characters that aren't alphanumeric or hyphens
        var sb = new StringBuilder();
        foreach (var c in slug)
        {
            if (char.IsLetterOrDigit(c) || c == '-')
                sb.Append(c);
        }

        slug = sb.ToString();

        // Remove multiple consecutive hyphens
        while (slug.Contains("--"))
            slug = slug.Replace("--", "-");

        // Trim hyphens from start/end
        return slug.Trim('-');
    }

    private static decimal GetCreditsForPlan(string plan) => plan switch
    {
        "free" => 1000,
        "pro" => 50000,
        "enterprise" => 1000000,
        "self" => 999999999,
        _ => 1000
    };

    private static (int Rpm, int Rpd) GetRateLimitsForPlan(string plan) => plan switch
    {
        "free" => (60, 10000),
        "pro" => (300, 100000),
        "enterprise" => (1000, 1000000),
        "self" => (9999, 99999999),
        _ => (60, 10000)
    };

    private static int GetMaxMemorySizeForPlan(string plan) => plan switch
    {
        "free" => 10000,       // 10KB
        "pro" => 100000,       // 100KB
        "enterprise" => 1000000, // 1MB
        "self" => 10000000,    // 10MB
        _ => 10000
    };

    private static int GetMaxExportsForPlan(string plan) => plan switch
    {
        "free" => 1,
        "pro" => 10,
        "enterprise" => 100,
        "self" => 9999,
        _ => 1
    };

    private static string GetDisplayName(string plan) => plan switch
    {
        "free" => "Free Tier",
        "pro" => "Professional",
        "enterprise" => "Enterprise",
        "self" => "Self-Hosted",
        _ => plan
    };

    private static IReadOnlyList<string> GetScopesForRole(string role) => role switch
    {
        "owner" => new[] { "serialmemory.core", "serialmemory.admin", "serialmemory.export", "serialmemory.delete" },
        "admin" => new[] { "serialmemory.core", "serialmemory.admin", "serialmemory.export" },
        "member" => new[] { "serialmemory.core", "serialmemory.export" },
        "readonly" => new[] { "serialmemory.read" },
        _ => new[] { "serialmemory.read" }
    };

    // DTO classes
    private sealed class ApiKeyDto
    {
        public Guid id { get; set; }
        public string name { get; set; } = "";
        public string key_prefix { get; set; } = "";
        public string[]? scopes { get; set; }
        public string created_by { get; set; } = "";
        public DateTimeOffset? last_used_at { get; set; }
        public DateTimeOffset? expires_at { get; set; }
        public DateTimeOffset? revoked_at { get; set; }
        public DateTimeOffset created_at { get; set; }
    }

    private sealed class ApiKeyValidationDto
    {
        public Guid id { get; set; }
        public Guid tenant_id { get; set; }
        public string[]? scopes { get; set; }
        public string created_by { get; set; } = "";
        public DateTimeOffset? expires_at { get; set; }
        public DateTimeOffset? revoked_at { get; set; }
        public string tenant_slug { get; set; } = "";
        public string tenant_name { get; set; } = "";
    }

    private sealed class TenantSettingsDto
    {
        public string plan { get; set; } = "";
        public string? features { get; set; }
    }

    private sealed class TenantPlanDto
    {
        public string plan_name { get; set; } = "";
        public string display_name { get; set; } = "";
        public decimal credits_per_cycle { get; set; }
        public int cycle_days { get; set; }
        public int? max_memories { get; set; }
        public int? max_entities { get; set; }
    }

    private sealed class BillingCycleDto
    {
        public decimal credits_allocated { get; set; }
        public decimal credits_used { get; set; }
        public DateTimeOffset cycle_start { get; set; }
        public DateTimeOffset cycle_end { get; set; }
    }
}
