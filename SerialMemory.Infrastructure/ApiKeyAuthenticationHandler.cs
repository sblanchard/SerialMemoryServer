using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SerialMemory.Core.Interfaces;

namespace SerialMemory.Infrastructure;

/// <summary>
/// Authentication handler that validates API keys.
/// Supports both X-API-Key header and Authorization: Bearer sm_* format.
/// </summary>
public sealed class ApiKeyAuthenticationHandler : AuthenticationHandler<ApiKeyAuthenticationOptions>
{
    private readonly IApiKeyService _apiKeyService;

    public const string SchemeName = "ApiKey";
    public const string ApiKeyHeaderName = "X-API-Key";

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<ApiKeyAuthenticationOptions> options,
        ILoggerFactory loggerFactory,
        UrlEncoder encoder,
        IApiKeyService apiKeyService)
        : base(options, loggerFactory, encoder)
    {
        _apiKeyService = apiKeyService;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Try X-API-Key header first
        string? apiKey = null;
        if (Request.Headers.TryGetValue(ApiKeyHeaderName, out var headerValue))
        {
            apiKey = headerValue.FirstOrDefault();
        }

        // Fall back to Authorization: Bearer sm_* format
        if (string.IsNullOrEmpty(apiKey))
        {
            var authHeader = Request.Headers.Authorization.FirstOrDefault();
            if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Bearer sm_", StringComparison.OrdinalIgnoreCase))
            {
                apiKey = authHeader["Bearer ".Length..];
            }
        }

        // No API key found, skip this handler
        if (string.IsNullOrEmpty(apiKey))
        {
            return AuthenticateResult.NoResult();
        }

        try
        {
            var validationResult = await _apiKeyService.ValidateApiKeyAsync(apiKey);

            if (validationResult == null)
            {
                return AuthenticateResult.Fail("Invalid API key");
            }

            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, validationResult.CreatedBy),
                new("sub", validationResult.CreatedBy),
                new("tenant_id", validationResult.TenantId.ToString()),
                new("key_id", validationResult.KeyId.ToString()),
                new("auth_method", "api_key")
            };

            // Add tenant info if available
            if (!string.IsNullOrEmpty(validationResult.TenantSlug))
            {
                claims.Add(new Claim("tenant_slug", validationResult.TenantSlug));
            }

            // Add scopes as claims
            foreach (var scope in validationResult.Scopes)
            {
                claims.Add(new Claim("scope", scope));
            }

            // Map scopes to role claims for authorization policies
            // API keys with "admin" scope get admin role, otherwise member
            if (validationResult.Scopes.Contains("admin"))
            {
                claims.Add(new Claim("role", "admin"));
            }
            else if (validationResult.Scopes.Contains("write"))
            {
                claims.Add(new Claim("role", "member"));
            }
            else
            {
                claims.Add(new Claim("role", "reader"));
            }

            var identity = new ClaimsIdentity(claims, SchemeName);
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, SchemeName);

            Logger.LogDebug(
                "API key authenticated for tenant {TenantId}, user {UserId}",
                validationResult.TenantId, validationResult.CreatedBy);

            return AuthenticateResult.Success(ticket);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error validating API key");
            return AuthenticateResult.Fail("Error validating API key");
        }
    }
}

/// <summary>
/// Options for API key authentication.
/// </summary>
public sealed class ApiKeyAuthenticationOptions : AuthenticationSchemeOptions
{
    /// <summary>
    /// If true, API key authentication is required (no fallback to JWT).
    /// Default is false - falls back to JWT if no API key provided.
    /// </summary>
    public bool Required { get; set; } = false;
}

/// <summary>
/// Extensions for configuring API key authentication.
/// </summary>
public static class ApiKeyAuthenticationExtensions
{
    /// <summary>
    /// Adds API key authentication to the authentication builder.
    /// </summary>
    public static AuthenticationBuilder AddApiKey(
        this AuthenticationBuilder builder,
        Action<ApiKeyAuthenticationOptions>? configure = null)
    {
        return builder.AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(
            ApiKeyAuthenticationHandler.SchemeName,
            configure ?? (_ => { }));
    }
}
