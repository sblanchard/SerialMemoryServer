using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;
using SerialMemory.Core.Interfaces;

namespace SerialMemory.Api.Auth;

/// <summary>
/// Middleware that authenticates requests via X-Api-Key header or internal JWT tokens.
/// Sets up tenant context for all subsequent middleware and handlers.
///
/// Authentication modes:
/// 1. External API Key - Used by MCP clients, Docker agents, remote tools
/// 2. Internal JWT Token - Used by dashboard web UI (short-lived, contains tenant context)
/// 3. Service API Key + Trusted Headers - Dashboard proxy with X-Tenant-Id/X-User-Id headers
/// </summary>
public sealed class ApiKeyAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ApiKeyAuthMiddleware> _logger;
    private readonly HashSet<string> _allowAnonymousPaths;
    private readonly byte[]? _internalTokenKey;
    private readonly string? _serviceApiKey;

    private const string InternalTokenIssuer = "serialmemory-web";
    private const string InternalTokenAudience = "serialmemory-internal";

    public ApiKeyAuthMiddleware(RequestDelegate next, ILogger<ApiKeyAuthMiddleware> logger, IConfiguration configuration)
    {
        _next = next;
        _logger = logger;

        // Load internal token signing key (must match SerialMemory.Web)
        var keyBase64 = configuration["INTERNAL_TOKEN_KEY"];
        if (!string.IsNullOrEmpty(keyBase64))
        {
            _internalTokenKey = Convert.FromBase64String(keyBase64);
        }

        // Load service API key for trusted dashboard communication
        _serviceApiKey = configuration["SERVICE_API_KEY"];

        // Paths that don't require authentication
        _allowAnonymousPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "/health",
            "/healthz",
            "/ready",
            "/metrics",
            "/api/auth/login",
            "/api/auth/signup",
            "/api/auth/refresh",
            "/api/tenants/signup",
            "/onboarding",
            "/swagger",
            "/"
        };
    }

    public async Task InvokeAsync(
        HttpContext context,
        IApiKeyService apiKeyService,
        IMutableTenantContext tenantContext)
    {
        var path = context.Request.Path.Value ?? "";

        // Skip authentication for allowed paths
        if (IsAllowedAnonymous(path))
        {
            await _next(context);
            return;
        }

        // Get API key from header
        var xApiKey = context.Request.Headers["X-Api-Key"].FirstOrDefault();
        var bearerToken = context.Request.Headers["Authorization"].FirstOrDefault()?.Replace("Bearer ", "");

        // Check if this is a trusted dashboard request (service API key + tenant headers)
        if (!string.IsNullOrEmpty(_serviceApiKey) && xApiKey == _serviceApiKey)
        {
            if (await TryAuthenticateInternalRequest(context, tenantContext, bearerToken))
            {
                try
                {
                    await _next(context);
                    return;
                }
                finally
                {
                    tenantContext.Clear();
                }
            }

            // SECURITY: Service API key MUST have valid tenant headers or internal token
            // DO NOT fall through to regular API key auth - that would use the SERVICE_API_KEY's
            // registered tenant instead of the actual user's tenant!
            _logger.LogError(
                "SECURITY: Service API key used without valid tenant context. " +
                "Missing X-Tenant-Id/X-User-Id headers or invalid internal token.");
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "Service API key requires valid tenant context",
                code = "MISSING_TENANT_CONTEXT"
            });
            return;
        }

        var apiKey = xApiKey ?? bearerToken;

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning("Request to {Path} rejected: No API key provided", path);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "API key required", code = "MISSING_API_KEY" });
            return;
        }

        // Validate API key
        var validationResult = await apiKeyService.ValidateApiKeyAsync(apiKey, context.RequestAborted);

        if (validationResult == null)
        {
            _logger.LogWarning("Request to {Path} rejected: Invalid API key", path);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Invalid or expired API key", code = "INVALID_API_KEY" });
            return;
        }

        // Determine if this is a lab mode key (prefix starts with "lab_")
        var isLabMode = apiKey.StartsWith("lab_", StringComparison.OrdinalIgnoreCase);

        // Get default workspace for tenant
        var workspaceId = await GetDefaultWorkspaceAsync(validationResult.TenantId, apiKeyService, context.RequestAborted);

        // Set tenant context
        tenantContext.SetContext(
            tenantId: validationResult.TenantId.ToString(),
            workspaceId: workspaceId,
            userId: validationResult.CreatedBy,
            sessionId: null,
            isLabMode: isLabMode,
            scopes: validationResult.Scopes);

        _logger.LogDebug(
            "Authenticated request to {Path} for tenant {TenantId} (lab={IsLab})",
            path, validationResult.TenantId, isLabMode);

        // Store validation result in HttpContext for downstream use
        context.Items["TenantId"] = validationResult.TenantId;
        context.Items["TenantSlug"] = validationResult.TenantSlug;
        context.Items["Scopes"] = validationResult.Scopes;
        context.Items["IsLabMode"] = isLabMode;

        try
        {
            await _next(context);
        }
        finally
        {
            // Clear context after request completes
            tenantContext.Clear();
        }
    }

    /// <summary>
    /// Authenticates internal dashboard requests using short-lived JWT tokens
    /// and/or trusted tenant headers.
    /// </summary>
    private async Task<bool> TryAuthenticateInternalRequest(
        HttpContext context,
        IMutableTenantContext tenantContext,
        string? bearerToken)
    {
        // Try to validate internal JWT token
        if (!string.IsNullOrEmpty(bearerToken) && _internalTokenKey != null)
        {
            var claims = ValidateInternalToken(bearerToken);
            if (claims != null)
            {
                // Verify headers match token claims (defense in depth)
                var headerTenantId = context.Request.Headers["X-Tenant-Id"].FirstOrDefault();
                var headerUserId = context.Request.Headers["X-User-Id"].FirstOrDefault();

                if (!string.IsNullOrEmpty(headerTenantId) && headerTenantId != claims.TenantId)
                {
                    _logger.LogError(
                        "SECURITY: Header/token tenant mismatch! Header={HeaderTenant}, Token={TokenTenant}",
                        headerTenantId, claims.TenantId);
                    return false;
                }

                if (!string.IsNullOrEmpty(headerUserId) && headerUserId != claims.UserId)
                {
                    _logger.LogError(
                        "SECURITY: Header/token user mismatch! Header={HeaderUser}, Token={TokenUser}",
                        headerUserId, claims.UserId);
                    return false;
                }

                tenantContext.SetContext(
                    tenantId: claims.TenantId,
                    workspaceId: claims.WorkspaceId,
                    userId: claims.UserId,
                    sessionId: null,
                    isLabMode: false,
                    scopes: new[] { "dashboard" });

                context.Items["TenantId"] = Guid.TryParse(claims.TenantId, out var tid) ? tid : Guid.Empty;
                context.Items["IsInternalAuth"] = true;

                _logger.LogDebug(
                    "Authenticated internal request for tenant {TenantId}, user {UserId}",
                    claims.TenantId, claims.UserId);

                return true;
            }
        }

        // Fallback: trust headers if service key is valid (for legacy compatibility)
        var tenantId = context.Request.Headers["X-Tenant-Id"].FirstOrDefault();
        var userId = context.Request.Headers["X-User-Id"].FirstOrDefault();

        if (!string.IsNullOrEmpty(tenantId) && !string.IsNullOrEmpty(userId))
        {
            _logger.LogWarning(
                "Using header-based auth without internal token for tenant {TenantId}. " +
                "Consider updating dashboard to use internal tokens.",
                tenantId);

            tenantContext.SetContext(
                tenantId: tenantId,
                workspaceId: "default",
                userId: userId,
                sessionId: null,
                isLabMode: false,
                scopes: new[] { "dashboard" });

            context.Items["TenantId"] = Guid.TryParse(tenantId, out var tid) ? tid : Guid.Empty;
            context.Items["IsInternalAuth"] = true;

            return true;
        }

        return false;
    }

    /// <summary>
    /// Validates an internal JWT token and extracts claims.
    /// </summary>
    private InternalTokenClaims? ValidateInternalToken(string token)
    {
        if (_internalTokenKey == null)
            return null;

        try
        {
            var tokenHandler = new JwtSecurityTokenHandler();
            var securityKey = new SymmetricSecurityKey(_internalTokenKey);
            var validationParameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = securityKey,
                ValidateIssuer = true,
                ValidIssuer = InternalTokenIssuer,
                ValidateAudience = true,
                ValidAudience = InternalTokenAudience,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(30)
            };

            var principal = tokenHandler.ValidateToken(token, validationParameters, out var validatedToken);

            // Verify token type
            var tokenTypeClaim = principal.FindFirst("token_type")?.Value;
            if (tokenTypeClaim != "internal")
            {
                _logger.LogWarning("Token validation failed: Invalid token_type claim");
                return null;
            }

            return new InternalTokenClaims
            {
                UserId = principal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                    ?? principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
                    ?? "",
                TenantId = principal.FindFirst("tenant_id")?.Value ?? "",
                WorkspaceId = principal.FindFirst("workspace_id")?.Value ?? "default",
                Role = principal.FindFirst("role")?.Value ?? ""
            };
        }
        catch (SecurityTokenException ex)
        {
            _logger.LogDebug(ex, "Internal token validation failed");
            return null;
        }
    }

    private sealed class InternalTokenClaims
    {
        public required string TenantId { get; init; }
        public required string UserId { get; init; }
        public string WorkspaceId { get; init; } = "default";
        public required string Role { get; init; }
    }

    private bool IsAllowedAnonymous(string path)
    {
        // Exact matches
        if (_allowAnonymousPaths.Contains(path))
            return true;

        // Prefix matches
        if (path.StartsWith("/swagger", StringComparison.OrdinalIgnoreCase))
            return true;

        if (path.StartsWith("/onboarding", StringComparison.OrdinalIgnoreCase))
            return true;

        if (path.StartsWith("/api/auth/", StringComparison.OrdinalIgnoreCase))
            return true;

        // Static files
        if (path.StartsWith("/css", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/js", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/images", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/favicon", StringComparison.OrdinalIgnoreCase))
            return true;

        // SignalR hubs (allow anonymous for initial connection, auth handled per-hub)
        if (path.StartsWith("/hubs/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/hub/", StringComparison.OrdinalIgnoreCase))
            return true;

        // Scale prediction endpoints (operational, for self-host control room)
        if (path.StartsWith("/api/scale/", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private static async Task<string> GetDefaultWorkspaceAsync(
        Guid tenantId,
        IApiKeyService apiKeyService,
        CancellationToken cancellationToken)
    {
        // For now, return "default" - in the future, look up from tenant_workspaces
        // This could be extended to support X-Workspace-Id header for multi-workspace scenarios
        return await Task.FromResult("default");
    }
}

/// <summary>
/// Extension methods for registering the API key authentication middleware.
/// </summary>
public static class ApiKeyAuthMiddlewareExtensions
{
    /// <summary>
    /// Adds API key authentication middleware to the pipeline.
    /// </summary>
    public static IApplicationBuilder UseApiKeyAuth(this IApplicationBuilder app)
    {
        return app.UseMiddleware<ApiKeyAuthMiddleware>();
    }
}
