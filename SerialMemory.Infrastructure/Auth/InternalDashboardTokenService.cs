using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace SerialMemory.Infrastructure.Auth;

/// <summary>
/// Service for generating short-lived internal JWTs for dashboard authentication.
/// These tokens are NOT the same as external API keys and have shorter lifetimes.
/// </summary>
public sealed class InternalDashboardTokenService(
    string secretKey,
    string issuer,
    string audience,
    ILogger<InternalDashboardTokenService> logger)
    : IInternalDashboardTokenService
{
    // Internal tokens are medium-lived (30 minutes by default)
    private static readonly TimeSpan DefaultTokenLifetime = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Generates an internal dashboard token from a validated external auth (cookie or API key).
    /// </summary>
    public InternalDashboardToken GenerateToken(DashboardTokenRequest request)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secretKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var expiresAt = DateTimeOffset.UtcNow.Add(request.Lifetime ?? DefaultTokenLifetime);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, request.UserId),
            new(JwtRegisteredClaimNames.Jti, Guid.CreateVersion7().ToString()),
            new("tenant_id", request.TenantId),
            new("workspace_id", request.WorkspaceId),
            new("role", request.Role),
            new("token_type", "internal_dashboard"),
            new("iat", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64)
        };

        // Add optional claims
        if (!string.IsNullOrEmpty(request.Email))
        {
            claims.Add(new Claim(JwtRegisteredClaimNames.Email, request.Email));
        }

        // Add scopes as multiple claims
        foreach (var scope in request.Scopes)
        {
            claims.Add(new Claim("scope", scope));
        }

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            expires: expiresAt.UtcDateTime,
            signingCredentials: credentials);

        var tokenString = new JwtSecurityTokenHandler().WriteToken(token);

        logger.LogDebug(
            "Generated internal dashboard token for user {UserId} in tenant {TenantId}, expires at {ExpiresAt}",
            request.UserId, request.TenantId, expiresAt);

        return new InternalDashboardToken
        {
            Token = tokenString,
            ExpiresAt = expiresAt,
            TenantId = request.TenantId,
            UserId = request.UserId,
            WorkspaceId = request.WorkspaceId,
            Role = request.Role
        };
    }

    /// <summary>
    /// Validates an internal dashboard token.
    /// </summary>
    public InternalDashboardTokenValidation ValidateToken(string token)
    {
        try
        {
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secretKey));
            var handler = new JwtSecurityTokenHandler();

            var validationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = issuer,
                ValidAudience = audience,
                IssuerSigningKey = key,
                ClockSkew = TimeSpan.FromSeconds(30) // Allow 30 seconds clock skew
            };

            var principal = handler.ValidateToken(token, validationParameters, out var validatedToken);

            // Verify this is an internal dashboard token
            var tokenTypeClaim = principal.FindFirst("token_type")?.Value;
            if (tokenTypeClaim != "internal_dashboard")
            {
                logger.LogWarning("Token validation failed: not an internal dashboard token");
                return InternalDashboardTokenValidation.Invalid("Not an internal dashboard token");
            }

            var tenantId = principal.FindFirst("tenant_id")?.Value;
            var userId = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
            var workspaceId = principal.FindFirst("workspace_id")?.Value ?? "default";
            var role = principal.FindFirst("role")?.Value ?? "member";

            if (string.IsNullOrEmpty(tenantId) || string.IsNullOrEmpty(userId))
            {
                logger.LogWarning("Token validation failed: missing required claims");
                return InternalDashboardTokenValidation.Invalid("Missing required claims");
            }

            var scopes = principal.FindAll("scope").Select(c => c.Value).ToList();

            return new InternalDashboardTokenValidation
            {
                IsValid = true,
                TenantId = tenantId,
                UserId = userId,
                WorkspaceId = workspaceId,
                Role = role,
                Scopes = scopes,
                ExpiresAt = ((JwtSecurityToken)validatedToken).ValidTo
            };
        }
        catch (SecurityTokenExpiredException)
        {
            logger.LogDebug("Token validation failed: token expired");
            return InternalDashboardTokenValidation.Invalid("Token expired");
        }
        catch (SecurityTokenException ex)
        {
            logger.LogDebug("Token validation failed: {Message}", ex.Message);
            return InternalDashboardTokenValidation.Invalid("Invalid token");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error validating token");
            return InternalDashboardTokenValidation.Invalid("Token validation error");
        }
    }

    /// <summary>
    /// Refreshes a valid token if it's close to expiration.
    /// </summary>
    public InternalDashboardToken? RefreshToken(string token, TimeSpan refreshThreshold)
    {
        var validation = ValidateToken(token);
        if (!validation.IsValid)
        {
            return null;
        }

        // Check if token is close to expiration
        var timeRemaining = validation.ExpiresAt - DateTimeOffset.UtcNow;
        if (timeRemaining > refreshThreshold)
        {
            // Token still has plenty of time, no need to refresh
            return null;
        }

        // Generate a new token
        return GenerateToken(new DashboardTokenRequest
        {
            TenantId = validation.TenantId!,
            UserId = validation.UserId!,
            WorkspaceId = validation.WorkspaceId!,
            Role = validation.Role!,
            Scopes = validation.Scopes
        });
    }
}

/// <summary>
/// Interface for internal dashboard token service.
/// </summary>
public interface IInternalDashboardTokenService
{
    /// <summary>
    /// Generates an internal dashboard token.
    /// </summary>
    InternalDashboardToken GenerateToken(DashboardTokenRequest request);

    /// <summary>
    /// Validates an internal dashboard token.
    /// </summary>
    InternalDashboardTokenValidation ValidateToken(string token);

    /// <summary>
    /// Refreshes a token if close to expiration.
    /// </summary>
    InternalDashboardToken? RefreshToken(string token, TimeSpan refreshThreshold);
}

/// <summary>
/// Request for generating a dashboard token.
/// </summary>
public sealed record DashboardTokenRequest
{
    public required string TenantId { get; init; }
    public required string UserId { get; init; }
    public required string WorkspaceId { get; init; }
    public required string Role { get; init; }
    public string? Email { get; init; }
    public IReadOnlyList<string> Scopes { get; init; } = Array.Empty<string>();
    public TimeSpan? Lifetime { get; init; }
}

/// <summary>
/// Generated internal dashboard token.
/// </summary>
public sealed record InternalDashboardToken
{
    public required string Token { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public required string TenantId { get; init; }
    public required string UserId { get; init; }
    public required string WorkspaceId { get; init; }
    public required string Role { get; init; }

    /// <summary>
    /// Seconds until expiration.
    /// </summary>
    public int ExpiresInSeconds => (int)(ExpiresAt - DateTimeOffset.UtcNow).TotalSeconds;
}

/// <summary>
/// Result of validating an internal dashboard token.
/// </summary>
public sealed record InternalDashboardTokenValidation
{
    public required bool IsValid { get; init; }
    public string? Error { get; init; }
    public string? TenantId { get; init; }
    public string? UserId { get; init; }
    public string? WorkspaceId { get; init; }
    public string? Role { get; init; }
    public IReadOnlyList<string> Scopes { get; init; } = Array.Empty<string>();
    public DateTimeOffset ExpiresAt { get; init; }

    public static InternalDashboardTokenValidation Invalid(string error) => new()
    {
        IsValid = false,
        Error = error
    };
}
