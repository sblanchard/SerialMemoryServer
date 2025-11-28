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

    private const string Issuer = "serialmemory-web";
    private const string Audience = "serialmemory-internal";
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(5);

    public InternalTokenService(ILogger<InternalTokenService> logger, IConfiguration configuration)
    {
        _logger = logger;
        _tokenHandler = new JwtSecurityTokenHandler();

        // Get or generate signing key
        var keyBase64 = configuration["INTERNAL_TOKEN_KEY"];
        if (string.IsNullOrEmpty(keyBase64))
        {
            // Generate a secure key if not configured (development mode)
            // In production, this should be a stable key from configuration
            _signingKey = RandomNumberGenerator.GetBytes(64);
            _logger.LogWarning(
                "INTERNAL_TOKEN_KEY not configured. Using ephemeral key - tokens will not survive restart.");
        }
        else
        {
            _signingKey = Convert.FromBase64String(keyBase64);
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
                new Claim("token_type", "internal")
            }),
            Expires = DateTime.UtcNow.Add(TokenLifetime),
            Issuer = Issuer,
            Audience = Audience,
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
                ValidIssuer = Issuer,
                ValidateAudience = true,
                ValidAudience = Audience,
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

            return new InternalTokenClaims
            {
                UserId = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
                    ?? principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
                    ?? "",
                TenantId = principal.FindFirst("tenant_id")?.Value ?? "",
                WorkspaceId = principal.FindFirst("workspace_id")?.Value ?? "default",
                Role = principal.FindFirst("role")?.Value ?? "",
                Email = principal.FindFirst("email")?.Value
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
}
