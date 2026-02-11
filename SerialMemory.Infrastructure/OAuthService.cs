using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

namespace SerialMemory.Infrastructure;

/// <summary>
/// OAuth 2.0 service for ChatGPT and other OAuth clients.
/// Supports authorization code flow with PKCE.
/// </summary>
public sealed class OAuthService : IDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly string _issuer;
    private readonly string _audience;
    private readonly byte[] _signingKey;
    private readonly ILogger<OAuthService> _logger;

    private const int AuthCodeExpirationMinutes = 10;
    private const int AccessTokenExpirationMinutes = 60;
    private const int RefreshTokenExpirationDays = 30;
    private const int MinimumJwtSecretLength = 32;

    public OAuthService(
        string connectionString,
        string jwtSecret,
        ILogger<OAuthService>? logger = null,
        string? issuer = null,
        string? audience = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionString);
        ArgumentException.ThrowIfNullOrEmpty(jwtSecret);

        if (jwtSecret.Length < MinimumJwtSecretLength)
            throw new ArgumentException($"JWT secret must be at least {MinimumJwtSecretLength} characters for security", nameof(jwtSecret));

        var builder = new NpgsqlDataSourceBuilder(connectionString);
        _dataSource = builder.Build();

        _logger = logger ?? NullLogger<OAuthService>.Instance;
        _issuer = issuer ?? "serialmemory";
        _audience = audience ?? "serialmemory-api";
        _signingKey = Encoding.UTF8.GetBytes(jwtSecret);
    }

    /// <summary>
    /// Generates a new OAuth client secret for an API key.
    /// </summary>
    public async Task<OAuthCredentials> EnableOAuthAsync(
        Guid apiKeyId,
        string[] redirectUris,
        CancellationToken ct = default)
    {
        var clientSecret = GenerateSecureToken(48);
        var secretHash = HashSecret(clientSecret);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        // Verify the API key exists
        var exists = await conn.ExecuteScalarAsync<bool>(
            "SELECT EXISTS(SELECT 1 FROM tenant_api_keys WHERE id = @Id)",
            new { Id = apiKeyId });

        if (!exists)
            throw new InvalidOperationException("API key not found");

        await conn.ExecuteAsync(
            """
            UPDATE tenant_api_keys
            SET oauth_enabled = TRUE,
                client_secret_hash = @SecretHash,
                redirect_uris = @RedirectUris
            WHERE id = @Id
            """,
            new { Id = apiKeyId, SecretHash = secretHash, RedirectUris = redirectUris });

        // Use API key ID as OAuth client_id (unique identifier)
        return new OAuthCredentials
        {
            ClientId = apiKeyId.ToString(),
            ClientSecret = clientSecret,
            RedirectUris = redirectUris
        };
    }

    /// <summary>
    /// Disables OAuth for an API key.
    /// </summary>
    public async Task DisableOAuthAsync(Guid apiKeyId, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        await conn.ExecuteAsync(
            """
            UPDATE tenant_api_keys
            SET oauth_enabled = FALSE,
                client_secret_hash = NULL,
                redirect_uris = ARRAY[]::TEXT[]
            WHERE id = @Id
            """,
            new { Id = apiKeyId });

        // Revoke all refresh tokens for this client
        await conn.ExecuteAsync(
            "UPDATE oauth_refresh_tokens SET revoked_at = NOW() WHERE client_id = @Id",
            new { Id = apiKeyId });
    }

    /// <summary>
    /// Validates that an OAuth client_id exists (without verifying secret).
    /// Used for the authorize endpoint to prevent client_id enumeration.
    /// </summary>
    public async Task<bool> ClientExistsAsync(string clientId, CancellationToken ct = default)
    {
        if (!Guid.TryParse(clientId, out var apiKeyId))
            return false;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        return await conn.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS(
                SELECT 1 FROM tenant_api_keys ak
                JOIN tenants t ON ak.tenant_id = t.id
                WHERE ak.id = @ApiKeyId
                  AND ak.oauth_enabled = TRUE
                  AND ak.revoked_at IS NULL
                  AND t.status = 'active'
            )
            """,
            new { ApiKeyId = apiKeyId });
    }

    /// <summary>
    /// Validates OAuth client credentials.
    /// </summary>
    public async Task<OAuthClientInfo?> ValidateClientAsync(
        string clientId,
        string clientSecret,
        CancellationToken ct = default)
    {
        // Client ID is the API key GUID
        if (!Guid.TryParse(clientId, out var apiKeyId))
            return null;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        var client = await conn.QuerySingleOrDefaultAsync<OAuthClientRecord>(
            """
            SELECT ak.id, ak.tenant_id, ak.scopes, ak.client_secret_hash, ak.redirect_uris,
                   t.status as tenant_status
            FROM tenant_api_keys ak
            JOIN tenants t ON ak.tenant_id = t.id
            WHERE ak.id = @ApiKeyId
              AND ak.oauth_enabled = TRUE
              AND ak.revoked_at IS NULL
            """,
            new { ApiKeyId = apiKeyId });

        if (client == null)
            return null;

        // Verify secret
        var providedHash = HashSecret(clientSecret);
        if (!SecureCompare(client.client_secret_hash, providedHash))
            return null;

        if (client.tenant_status != "active")
            return null;

        return new OAuthClientInfo
        {
            ApiKeyId = client.id,
            TenantId = client.tenant_id,
            Scopes = client.scopes ?? ["serialmemory.core"],
            RedirectUris = client.redirect_uris ?? []
        };
    }

    /// <summary>
    /// Creates an authorization code for the authorization code flow.
    /// </summary>
    public async Task<string> CreateAuthorizationCodeAsync(
        OAuthClientInfo client,
        string redirectUri,
        string[] requestedScopes,
        string? state,
        string? codeChallenge,
        string? codeChallengeMethod,
        CancellationToken ct = default)
    {
        var code = GenerateSecureToken(32);
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(AuthCodeExpirationMinutes);

        // Filter requested scopes to only those the client has
        var grantedScopes = requestedScopes
            .Where(s => client.Scopes.Contains(s))
            .ToArray();

        if (grantedScopes.Length == 0)
            grantedScopes = client.Scopes;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        await conn.ExecuteAsync(
            """
            INSERT INTO oauth_authorization_codes
                (code, client_id, tenant_id, redirect_uri, scope, state, code_challenge, code_challenge_method, expires_at)
            VALUES
                (@Code, @ClientId, @TenantId, @RedirectUri, @Scope, @State, @CodeChallenge, @CodeChallengeMethod, @ExpiresAt)
            """,
            new
            {
                Code = code,
                ClientId = client.ApiKeyId,
                TenantId = client.TenantId,
                RedirectUri = redirectUri,
                Scope = grantedScopes,
                State = state,
                CodeChallenge = codeChallenge,
                CodeChallengeMethod = codeChallengeMethod,
                ExpiresAt = expiresAt
            });

        return code;
    }

    /// <summary>
    /// Exchanges an authorization code for tokens.
    /// Returns (response, errorCode) tuple for debugging.
    /// </summary>
    public async Task<OAuthTokenResponse?> ExchangeCodeAsync(
        string code,
        string clientId,
        string clientSecret,
        string redirectUri,
        string? codeVerifier,
        CancellationToken ct = default)
    {
        // Validate client credentials
        var client = await ValidateClientAsync(clientId, clientSecret, ct);
        if (client == null)
        {
            _logger.LogWarning("OAuth ExchangeCode failed: invalid client credentials for {ClientId}", clientId);
            return null;
        }

        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        // Get and validate authorization code
        var authCode = await conn.QuerySingleOrDefaultAsync<AuthCodeRecord>(
            """
            SELECT code, client_id, tenant_id, redirect_uri, scope, code_challenge, code_challenge_method, expires_at, used_at
            FROM oauth_authorization_codes
            WHERE code = @Code
            """,
            new { Code = code });

        if (authCode == null)
        {
            _logger.LogWarning("OAuth ExchangeCode failed: code not found for {ClientId}", clientId);
            return null;
        }

        // Validate code hasn't been used
        if (authCode.used_at.HasValue)
        {
            _logger.LogWarning("OAuth ExchangeCode failed: code already used for {ClientId}", clientId);
            return null;
        }

        // Validate code hasn't expired
        if (authCode.expires_at < DateTimeOffset.UtcNow)
        {
            _logger.LogWarning("OAuth ExchangeCode failed: code expired for {ClientId}", clientId);
            return null;
        }

        // Validate redirect URI matches (case-insensitive comparison for URLs)
        if (!string.Equals(authCode.redirect_uri, redirectUri, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("OAuth ExchangeCode failed: redirect_uri mismatch for {ClientId}. Expected={Expected}, Got={Got}",
                clientId, authCode.redirect_uri, redirectUri);
            return null;
        }

        // Validate client matches
        if (authCode.client_id != client.ApiKeyId)
        {
            _logger.LogWarning("OAuth ExchangeCode failed: client_id mismatch for {ClientId}", clientId);
            return null;
        }

        // Validate PKCE if code challenge was provided
        if (!string.IsNullOrEmpty(authCode.code_challenge))
        {
            if (string.IsNullOrEmpty(codeVerifier))
            {
                _logger.LogWarning("OAuth ExchangeCode failed: PKCE code_verifier required but not provided for {ClientId}", clientId);
                return null;
            }

            if (authCode.code_challenge_method != "S256")
            {
                _logger.LogWarning("OAuth ExchangeCode failed: unsupported code_challenge_method '{Method}' for {ClientId}", authCode.code_challenge_method, clientId);
                return null;
            }

            var expectedChallenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));

            if (expectedChallenge != authCode.code_challenge)
            {
                _logger.LogWarning("OAuth ExchangeCode failed: PKCE code_verifier mismatch for {ClientId}", clientId);
                return null;
            }
        }

        // Mark code as used
        await conn.ExecuteAsync(
            "UPDATE oauth_authorization_codes SET used_at = NOW() WHERE code = @Code",
            new { Code = code });

        // Generate tokens
        var accessToken = GenerateAccessToken(client.ApiKeyId, client.TenantId, authCode.scope);
        var refreshToken = await CreateRefreshTokenAsync(client.ApiKeyId, client.TenantId, authCode.scope, ct);

        return new OAuthTokenResponse
        {
            AccessToken = accessToken,
            TokenType = "Bearer",
            ExpiresIn = AccessTokenExpirationMinutes * 60,
            RefreshToken = refreshToken,
            Scope = string.Join(" ", authCode.scope)
        };
    }

    /// <summary>
    /// Exchanges client credentials for an access token (no user interaction).
    /// Used by ChatGPT for service-to-service auth.
    /// </summary>
    public async Task<OAuthTokenResponse?> ClientCredentialsGrantAsync(
        string clientId,
        string clientSecret,
        string[]? requestedScopes,
        CancellationToken ct = default)
    {
        var client = await ValidateClientAsync(clientId, clientSecret, ct);
        if (client == null)
            return null;

        // Filter requested scopes
        var grantedScopes = requestedScopes?.Where(s => client.Scopes.Contains(s)).ToArray()
            ?? client.Scopes;

        if (grantedScopes.Length == 0)
            grantedScopes = client.Scopes;

        var accessToken = GenerateAccessToken(client.ApiKeyId, client.TenantId, grantedScopes);

        return new OAuthTokenResponse
        {
            AccessToken = accessToken,
            TokenType = "Bearer",
            ExpiresIn = AccessTokenExpirationMinutes * 60,
            Scope = string.Join(" ", grantedScopes)
        };
    }

    /// <summary>
    /// Refreshes an access token using a refresh token.
    /// </summary>
    public async Task<OAuthTokenResponse?> RefreshTokenAsync(
        string refreshToken,
        string clientId,
        string clientSecret,
        CancellationToken ct = default)
    {
        var client = await ValidateClientAsync(clientId, clientSecret, ct);
        if (client == null)
            return null;

        var tokenHash = HashSecret(refreshToken);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        var storedToken = await conn.QuerySingleOrDefaultAsync<RefreshTokenRecord>(
            """
            SELECT id, client_id, tenant_id, scope, expires_at, revoked_at
            FROM oauth_refresh_tokens
            WHERE token_hash = @TokenHash
            """,
            new { TokenHash = tokenHash });

        if (storedToken == null)
            return null;

        if (storedToken.revoked_at.HasValue)
            return null;

        if (storedToken.expires_at < DateTimeOffset.UtcNow)
            return null;

        if (storedToken.client_id != client.ApiKeyId)
            return null;

        // Generate new access token
        var accessToken = GenerateAccessToken(storedToken.client_id, storedToken.tenant_id, storedToken.scope);

        return new OAuthTokenResponse
        {
            AccessToken = accessToken,
            TokenType = "Bearer",
            ExpiresIn = AccessTokenExpirationMinutes * 60,
            RefreshToken = refreshToken, // Return same refresh token
            Scope = string.Join(" ", storedToken.scope)
        };
    }

    /// <summary>
    /// Revokes a refresh token.
    /// </summary>
    public async Task RevokeRefreshTokenAsync(string refreshToken, CancellationToken ct = default)
    {
        var tokenHash = HashSecret(refreshToken);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        await conn.ExecuteAsync(
            "UPDATE oauth_refresh_tokens SET revoked_at = NOW() WHERE token_hash = @TokenHash",
            new { TokenHash = tokenHash });
    }

    private string GenerateAccessToken(Guid clientId, Guid tenantId, string[] scopes)
    {
        var handler = new JsonWebTokenHandler();
        var key = new SymmetricSecurityKey(_signingKey);
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new System.Security.Claims.ClaimsIdentity(
            [
                new("sub", $"oauth-client:{clientId}"),
                new("client_id", clientId.ToString()),
                new("tenant_id", tenantId.ToString()),
                new("scope", string.Join(" ", scopes)),
                new("role", "service")
            ]),
            Expires = DateTime.UtcNow.AddMinutes(AccessTokenExpirationMinutes),
            Issuer = _issuer,
            Audience = _audience,
            SigningCredentials = credentials
        };

        return handler.CreateToken(descriptor);
    }

    private async Task<string> CreateRefreshTokenAsync(
        Guid clientId,
        Guid tenantId,
        string[] scopes,
        CancellationToken ct)
    {
        var token = GenerateSecureToken(48);
        var tokenHash = HashSecret(token);
        var expiresAt = DateTimeOffset.UtcNow.AddDays(RefreshTokenExpirationDays);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        await conn.ExecuteAsync(
            """
            INSERT INTO oauth_refresh_tokens (id, token_hash, client_id, tenant_id, scope, expires_at)
            VALUES (@Id, @TokenHash, @ClientId, @TenantId, @Scope, @ExpiresAt)
            """,
            new
            {
                Id = Guid.CreateVersion7(),
                TokenHash = tokenHash,
                ClientId = clientId,
                TenantId = tenantId,
                Scope = scopes,
                ExpiresAt = expiresAt
            });

        return token;
    }

    private static string GenerateSecureToken(int length)
    {
        var bytes = RandomNumberGenerator.GetBytes(length);
        return Base64UrlEncode(bytes);
    }

    private static string HashSecret(string secret)
    {
        var bytes = Encoding.UTF8.GetBytes(secret);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string Base64UrlEncode(byte[] data)
    {
        return Convert.ToBase64String(data)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static bool SecureCompare(string? a, string? b)
    {
        if (a == null || b == null) return false;
        if (a.Length != b.Length) return false;

        var result = 0;
        for (var i = 0; i < a.Length; i++)
        {
            result |= a[i] ^ b[i];
        }
        return result == 0;
    }

    public void Dispose()
    {
        _dataSource.Dispose();
    }

    // Internal record types
    private sealed class OAuthClientRecord
    {
        public Guid id { get; set; }
        public Guid tenant_id { get; set; }
        public string[]? scopes { get; set; }
        public string? client_secret_hash { get; set; }
        public string[]? redirect_uris { get; set; }
        public string tenant_status { get; set; } = "";
    }

    private sealed class AuthCodeRecord
    {
        public string code { get; set; } = "";
        public Guid client_id { get; set; }
        public Guid tenant_id { get; set; }
        public string redirect_uri { get; set; } = "";
        public string[] scope { get; set; } = [];
        public string? code_challenge { get; set; }
        public string? code_challenge_method { get; set; }
        public DateTimeOffset expires_at { get; set; }
        public DateTimeOffset? used_at { get; set; }
    }

    private sealed class RefreshTokenRecord
    {
        public Guid id { get; set; }
        public Guid client_id { get; set; }
        public Guid tenant_id { get; set; }
        public string[] scope { get; set; } = [];
        public DateTimeOffset expires_at { get; set; }
        public DateTimeOffset? revoked_at { get; set; }
    }
}

/// <summary>
/// OAuth client credentials returned when enabling OAuth.
/// </summary>
public sealed class OAuthCredentials
{
    public required string ClientId { get; init; }
    public required string ClientSecret { get; init; }
    public required string[] RedirectUris { get; init; }
}

/// <summary>
/// Validated OAuth client information.
/// </summary>
public sealed class OAuthClientInfo
{
    public Guid ApiKeyId { get; init; }
    public Guid TenantId { get; init; }
    public string[] Scopes { get; init; } = [];
    public string[] RedirectUris { get; init; } = [];
}

/// <summary>
/// OAuth token response.
/// </summary>
public sealed class OAuthTokenResponse
{
    public required string AccessToken { get; init; }
    public required string TokenType { get; init; }
    public required int ExpiresIn { get; init; }
    public string? RefreshToken { get; init; }
    public string? Scope { get; init; }
}
