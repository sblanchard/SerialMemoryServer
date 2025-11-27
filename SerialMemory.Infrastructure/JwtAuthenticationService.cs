using System.Security.Cryptography;
using Dapper;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using SerialMemory.Core.Auth;

namespace SerialMemory.Infrastructure;

/// <summary>
/// JWT token validation service using configured keys.
/// Supports both symmetric (HMAC) and asymmetric (RSA/ECDSA) signing.
/// </summary>
public sealed class JwtAuthenticationService : IJwtAuthenticationService, IDisposable
{
    private readonly JsonWebTokenHandler _tokenHandler;
    private readonly TokenValidationParameters _validationParameters;
    private readonly NpgsqlDataSource _dataSource;

    // JWT claim names
    private const string TenantIdClaim = "tenant_id";
    private const string WorkspaceIdClaim = "workspace_id";
    private const string RoleClaim = "role";
    private const string ScopeClaim = "scope";

    /// <summary>
    /// Creates a new JWT authentication service.
    /// </summary>
    /// <param name="connectionString">PostgreSQL connection string for API key validation.</param>
    /// <param name="options">JWT configuration options.</param>
    public JwtAuthenticationService(string connectionString, JwtAuthenticationOptions options)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionString);
        ArgumentNullException.ThrowIfNull(options);

        _tokenHandler = new JsonWebTokenHandler();

        // Build data source for API key lookups
        var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
        _dataSource = dataSourceBuilder.Build();

        // Build validation parameters (only if JWT key is configured)
        var securityKey = BuildSecurityKey(options);
        _validationParameters = securityKey != null
            ? new TokenValidationParameters
            {
                ValidateIssuer = !string.IsNullOrEmpty(options.Issuer),
                ValidIssuer = options.Issuer,
                ValidateAudience = !string.IsNullOrEmpty(options.Audience),
                ValidAudience = options.Audience,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ClockSkew = TimeSpan.FromMinutes(1),
                IssuerSigningKey = securityKey
            }
            : new TokenValidationParameters(); // API key-only mode
    }

    /// <summary>
    /// Validates a JWT token and extracts authentication claims.
    /// </summary>
    public async Task<AuthenticationResult> ValidateTokenAsync(string token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
            return AuthenticationResult.MissingToken();

        try
        {
            // Validate the token
            var result = await _tokenHandler.ValidateTokenAsync(token, _validationParameters);

            if (!result.IsValid)
            {
                return result.Exception switch
                {
                    SecurityTokenExpiredException => AuthenticationResult.ExpiredToken(),
                    _ => AuthenticationResult.InvalidToken(result.Exception?.Message ?? "Token validation failed")
                };
            }

            // Extract claims
            var claims = result.ClaimsIdentity;

            // Required: tenant_id
            var tenantIdStr = claims.FindFirst(TenantIdClaim)?.Value;
            if (string.IsNullOrEmpty(tenantIdStr) || !Guid.TryParse(tenantIdStr, out var tenantId))
            {
                return AuthenticationResult.InvalidToken("Missing or invalid tenant_id claim");
            }

            // Required: subject (user_id)
            var userId = claims.FindFirst("sub")?.Value;
            if (string.IsNullOrEmpty(userId))
            {
                return AuthenticationResult.InvalidToken("Missing sub claim");
            }

            // Required: role
            var role = claims.FindFirst(RoleClaim)?.Value ?? Roles.Member;
            if (!Roles.IsValid(role))
            {
                return AuthenticationResult.InvalidToken($"Invalid role: {role}");
            }

            // Optional: workspace_id
            var workspaceId = claims.FindFirst(WorkspaceIdClaim)?.Value;

            // Optional: scopes (space-separated or array)
            var scopesClaim = claims.FindFirst(ScopeClaim)?.Value ?? "";
            var scopes = ParseScopes(scopesClaim);

            // Check tenant is active
            var tenantStatus = await GetTenantStatusAsync(tenantId, cancellationToken);
            if (tenantStatus == "suspended")
            {
                return AuthenticationResult.TenantSuspended();
            }
            if (tenantStatus != "active")
            {
                return AuthenticationResult.InvalidToken($"Tenant status: {tenantStatus}");
            }

            // Get expiration time
            DateTimeOffset? expiresAt = null;
            if (result.SecurityToken is JsonWebToken jwt)
            {
                expiresAt = jwt.ValidTo;
            }

            return AuthenticationResult.Success(
                tenantId: tenantId,
                userId: userId,
                role: role,
                scopes: scopes,
                workspaceId: workspaceId,
                expiresAt: expiresAt);
        }
        catch (Exception ex)
        {
            return AuthenticationResult.InvalidToken(ex.Message);
        }
    }

    /// <summary>
    /// Validates an API key and returns authentication result.
    /// </summary>
    public async Task<AuthenticationResult> ValidateApiKeyAsync(string apiKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return AuthenticationResult.MissingToken();

        try
        {
            // Hash the API key for lookup
            var keyHash = HashApiKey(apiKey);
            var keyPrefix = apiKey.Length >= 8 ? apiKey[..8] : apiKey;

            SerialMemory.Core.Services.DebugFileLogger.Log("Auth", $"ValidateApiKeyAsync: keyPrefix={keyPrefix}, keyHash={keyHash[..16]}...");

            // Look up the API key (use FirstOrDefault to handle duplicates)
            await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
            const string sql = """

                                               SELECT ak.tenant_id, ak.scopes, ak.created_by, ak.expires_at, ak.revoked_at, t.status
                                               FROM tenant_api_keys ak
                                               JOIN tenants t ON ak.tenant_id = t.id
                                               WHERE ak.key_hash = @KeyHash AND ak.key_prefix = @KeyPrefix
                                               AND ak.revoked_at IS NULL
                                               LIMIT 1
                               """;

            var result = await conn.QuerySingleOrDefaultAsync<ApiKeyRecord>(sql, new { KeyHash = keyHash, KeyPrefix = keyPrefix });

            SerialMemory.Core.Services.DebugFileLogger.Log("Auth", $"Query result: {(result == null ? "null" : $"tenant_id={result.tenant_id}, status={result.status}")}");

            if (result == null)
            {
                return AuthenticationResult.InvalidToken("Invalid API key");
            }

            if (result.revoked_at.HasValue)
            {
                return AuthenticationResult.InvalidToken("API key has been revoked");
            }

            if (result.expires_at.HasValue && result.expires_at.Value < DateTimeOffset.UtcNow)
            {
                return AuthenticationResult.ExpiredToken();
            }

            if (result.status == "suspended")
            {
                return AuthenticationResult.TenantSuspended();
            }

            if (result.status != "active")
            {
                return AuthenticationResult.InvalidToken($"Tenant status: {result.status}");
            }

            var scopes = result.scopes?.ToHashSet() ?? new HashSet<string>();

            return AuthenticationResult.Success(
                tenantId: result.tenant_id,
                userId: result.created_by,
                role: Roles.Service,
                scopes: scopes,
                workspaceId: null,
                expiresAt: result.expires_at);
        }
        catch (Exception ex)
        {
            return AuthenticationResult.InvalidToken(ex.Message);
        }
    }

    /// <summary>
    /// Builds the security key from configuration.
    /// Returns null if no key is configured (API key-only mode).
    /// </summary>
    private static SecurityKey? BuildSecurityKey(JwtAuthenticationOptions options)
    {
        if (!string.IsNullOrEmpty(options.SymmetricKey))
        {
            // HMAC signing key
            var keyBytes = Convert.FromBase64String(options.SymmetricKey);
            return new SymmetricSecurityKey(keyBytes);
        }

        if (!string.IsNullOrEmpty(options.RsaPublicKey))
        {
            // RSA public key for verification
            var rsa = RSA.Create();
            rsa.ImportFromPem(options.RsaPublicKey);
            return new RsaSecurityKey(rsa);
        }

        if (!string.IsNullOrEmpty(options.EcdsaPublicKey))
        {
            // ECDSA public key for verification
            var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(options.EcdsaPublicKey);
            return new ECDsaSecurityKey(ecdsa);
        }

        // No JWT key configured - API key-only mode
        return null;
    }

    /// <summary>
    /// Parses space-separated scope string into set.
    /// </summary>
    private static HashSet<string> ParseScopes(string scopesClaim)
    {
        if (string.IsNullOrWhiteSpace(scopesClaim))
            return new HashSet<string>();

        return scopesClaim
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet();
    }

    /// <summary>
    /// Hashes an API key for storage/lookup.
    /// </summary>
    private static string HashApiKey(string apiKey)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(apiKey);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Gets the status of a tenant from the database.
    /// </summary>
    private async Task<string?> GetTenantStatusAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
            return await conn.QuerySingleOrDefaultAsync<string>(
                "SELECT status FROM tenants WHERE id = @TenantId",
                new { TenantId = tenantId });
        }
        catch
        {
            // If tenant table doesn't exist (self-hosted), assume active
            return "active";
        }
    }

    public void Dispose()
    {
        _dataSource.Dispose();
    }

    // Record for API key lookup result
    private sealed class ApiKeyRecord
    {
        public Guid tenant_id { get; set; }
        public string[]? scopes { get; set; }
        public string created_by { get; set; } = "";
        public DateTimeOffset? expires_at { get; set; }
        public DateTimeOffset? revoked_at { get; set; }
        public string status { get; set; } = "";
    }
}

/// <summary>
/// Configuration options for JWT authentication.
/// </summary>
public sealed class JwtAuthenticationOptions
{
    /// <summary>
    /// Expected token issuer (iss claim).
    /// </summary>
    public string? Issuer { get; set; }

    /// <summary>
    /// Expected token audience (aud claim).
    /// </summary>
    public string? Audience { get; set; }

    /// <summary>
    /// Base64-encoded symmetric key for HMAC signing.
    /// </summary>
    public string? SymmetricKey { get; set; }

    /// <summary>
    /// PEM-encoded RSA public key for signature verification.
    /// </summary>
    public string? RsaPublicKey { get; set; }

    /// <summary>
    /// PEM-encoded ECDSA public key for signature verification.
    /// </summary>
    public string? EcdsaPublicKey { get; set; }

    /// <summary>
    /// Creates options from environment variables.
    /// </summary>
    public static JwtAuthenticationOptions FromEnvironment()
    {
        return new JwtAuthenticationOptions
        {
            Issuer = Environment.GetEnvironmentVariable("JWT_ISSUER"),
            Audience = Environment.GetEnvironmentVariable("JWT_AUDIENCE"),
            SymmetricKey = Environment.GetEnvironmentVariable("JWT_SYMMETRIC_KEY"),
            RsaPublicKey = Environment.GetEnvironmentVariable("JWT_RSA_PUBLIC_KEY"),
            EcdsaPublicKey = Environment.GetEnvironmentVariable("JWT_ECDSA_PUBLIC_KEY")
        };
    }
}
