using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace SerialMemory.Web.Services;

/// <summary>
/// Service for generating and validating short-lived internal JWT tokens.
/// These tokens are used for dashboard-to-API communication and should NOT
/// be used for external API access (MCP, Docker agents, etc.).
/// </summary>
public sealed class InternalTokenService
{
    private readonly byte[] _signingKey;
    private readonly JwtSecurityTokenHandler _tokenHandler;
    private readonly ILogger<InternalTokenService> _logger;
    private readonly string _issuer;
    private readonly string _audience;

    private static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(5);

    public InternalTokenService(ILogger<InternalTokenService> logger, IConfiguration configuration)
    {
        _logger = logger;
        _tokenHandler = new JwtSecurityTokenHandler
        {
            // Disable claim type mapping - keep "role" as "role", not mapped to ClaimTypes.Role URI
            MapInboundClaims = false
        };

        // Use same issuer/audience as dashboard-api expects
        _issuer = configuration["JWT_ISSUER"] ?? "serialmemory";
        _audience = configuration["JWT_AUDIENCE"] ?? "serialmemory-api";

        // Get signing key - must match dashboard-api's JWT_SECRET
        var jwtSecret = configuration["JWT_SECRET"];
        if (!string.IsNullOrEmpty(jwtSecret))
        {
            _signingKey = System.Text.Encoding.UTF8.GetBytes(jwtSecret);
        }
        else
        {
            // Fallback to INTERNAL_TOKEN_KEY for backwards compatibility
            var keyBase64 = configuration["INTERNAL_TOKEN_KEY"];
            if (string.IsNullOrEmpty(keyBase64))
            {
                // Use default development key matching dashboard-api default
                _signingKey = System.Text.Encoding.UTF8.GetBytes("default-development-secret-32chars!!");
                _logger.LogWarning(
                    "JWT_SECRET not configured. Using default development key.");
            }
            else
            {
                _signingKey = Convert.FromBase64String(keyBase64);
            }
        }
    }

    /// <summary>
    /// Generates a short-lived internal JWT token for dashboard API calls.
    /// </summary>
    public string GenerateToken(InternalTokenClaims claims)
    {
        var securityKey = new SymmetricSecurityKey(_signingKey);
        var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);

        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(new[]
            {
                new Claim(JwtRegisteredClaimNames.Sub, claims.UserId),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.CreateVersion7().ToString()),
                new Claim("tenant_id", claims.TenantId),
                new Claim("workspace_id", claims.WorkspaceId),
                new Claim("role", claims.Role),
                new Claim("email", claims.Email ?? ""),
                new Claim("token_type", "internal"),
                new Claim("is_root_admin", claims.IsRootAdmin.ToString().ToLowerInvariant())
            }),
            Expires = DateTime.UtcNow.Add(TokenLifetime),
            Issuer = _issuer,
            Audience = _audience,
            SigningCredentials = credentials
        };

        var token = _tokenHandler.CreateToken(tokenDescriptor);
        return _tokenHandler.WriteToken(token);
    }

    /// <summary>
    /// Validates an internal token and returns the claims if valid.
    /// </summary>
    public InternalTokenClaims? ValidateToken(string token)
    {
        try
        {
            var securityKey = new SymmetricSecurityKey(_signingKey);
            var validationParameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = securityKey,
                ValidateIssuer = true,
                ValidIssuer = _issuer,
                ValidateAudience = true,
                ValidAudience = _audience,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(30)
            };

            var principal = _tokenHandler.ValidateToken(token, validationParameters, out var validatedToken);

            // Verify token type
            var tokenTypeClaim = principal.FindFirst("token_type")?.Value;
            if (tokenTypeClaim != "internal")
            {
                _logger.LogWarning("Token validation failed: Invalid token_type claim");
                return null;
            }

            // Try multiple claim types for role (JWT might map "role" to ClaimTypes.Role)
            var role = principal.FindFirst("role")?.Value
                ?? principal.FindFirst(ClaimTypes.Role)?.Value
                ?? principal.FindFirst("http://schemas.microsoft.com/ws/2008/06/identity/claims/role")?.Value
                ?? "";

            return new InternalTokenClaims
            {
                UserId = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
                    ?? principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
                    ?? "",
                TenantId = principal.FindFirst("tenant_id")?.Value ?? "",
                WorkspaceId = principal.FindFirst("workspace_id")?.Value ?? "default",
                Role = role,
                Email = principal.FindFirst("email")?.Value,
                IsRootAdmin = string.Equals(principal.FindFirst("is_root_admin")?.Value, "true", StringComparison.OrdinalIgnoreCase)
            };
        }
        catch (SecurityTokenException ex)
        {
            _logger.LogWarning(ex, "Internal token validation failed");
            return null;
        }
    }

    /// <summary>
    /// Validates that the cookie claims match the internal token claims.
    /// This prevents cross-tenant attacks where an old token might be reused.
    /// </summary>
    public bool ValidateTenantMatch(ClaimsPrincipal cookiePrincipal, InternalTokenClaims tokenClaims)
    {
        var cookieTenantId = cookiePrincipal.FindFirst("tenant_id")?.Value;
        var cookieUserId = cookiePrincipal.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        if (string.IsNullOrEmpty(cookieTenantId) || string.IsNullOrEmpty(cookieUserId))
        {
            _logger.LogWarning("Cookie principal missing tenant_id or user_id claims");
            return false;
        }

        if (cookieTenantId != tokenClaims.TenantId)
        {
            _logger.LogError(
                "SECURITY: Tenant mismatch detected! Cookie tenant={CookieTenant}, Token tenant={TokenTenant}",
                cookieTenantId, tokenClaims.TenantId);
            return false;
        }

        if (cookieUserId != tokenClaims.UserId)
        {
            _logger.LogError(
                "SECURITY: User mismatch detected! Cookie user={CookieUser}, Token user={TokenUser}",
                cookieUserId, tokenClaims.UserId);
            return false;
        }

        return true;
    }

    private static readonly TimeSpan RefreshThreshold = TimeSpan.FromMinutes(1);

    public Task<string> CreateForUserAsync(
        string userId,
        string tenantId,
        string workspaceId,
        IEnumerable<string> roles,
        string? email = null,
        bool isRootAdmin = false)
    {
        var primaryRole = roles.FirstOrDefault() ?? "user";
        var claims = new InternalTokenClaims
        {
            UserId = userId,
            TenantId = tenantId,
            WorkspaceId = workspaceId,
            Role = primaryRole,
            Email = email,
            IsRootAdmin = isRootAdmin
        };

        var token = GenerateToken(claims);
        _logger.LogDebug(
            "Created internal token for user {UserId} in tenant {TenantId}",
            userId, tenantId);

        return Task.FromResult(token);
    }

    public Task<bool> IsExpiringSoonAsync(string token)
    {
        try
        {
            var jwtToken = _tokenHandler.ReadJwtToken(token);
            var expiresAt = jwtToken.ValidTo;
            var timeRemaining = expiresAt - DateTime.UtcNow;

            var isExpiringSoon = timeRemaining <= RefreshThreshold;

            if (isExpiringSoon)
            {
                _logger.LogDebug(
                    "Token expiring soon. Expires at {ExpiresAt}, time remaining: {TimeRemaining}",
                    expiresAt, timeRemaining);
            }

            return Task.FromResult(isExpiringSoon);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check token expiration");
            return Task.FromResult(true);
        }
    }

    public Task<string?> RefreshAsync(string oldToken)
    {
        var claims = ValidateToken(oldToken);
        if (claims == null)
        {
            _logger.LogWarning("Cannot refresh invalid or expired token");
            return Task.FromResult<string?>(null);
        }

        var newToken = GenerateToken(claims);
        _logger.LogDebug(
            "Refreshed token for user {UserId} in tenant {TenantId}",
            claims.UserId, claims.TenantId);

        return Task.FromResult<string?>(newToken);
    }
}

/// <summary>
/// Claims contained in an internal JWT token.
/// </summary>
public sealed class InternalTokenClaims
{
    public required string TenantId { get; init; }
    public required string UserId { get; init; }
    public string WorkspaceId { get; init; } = "default";
    public required string Role { get; init; }
    public string? Email { get; init; }
    public bool IsRootAdmin { get; init; }
}
