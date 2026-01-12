using System.Security.Claims;
using SerialMemory.Core.Deployment;

namespace SerialMemory.Web.Services;

/// <summary>
/// Middleware that automatically manages internal JWT tokens for authenticated users.
/// Generates tokens on first request and refreshes them before expiration.
/// Also enforces route-level access control for PublicSaaS mode.
/// </summary>
public sealed class InternalTokenMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<InternalTokenMiddleware> _logger;
    private readonly IDeploymentContext _deploymentContext;

    private const string InternalTokenSessionKey = "internal_token";
    private const string InternalTokenTenantKey = "internal_token_tenant";
    private const string InternalTokenUserKey = "internal_token_user";

    // Routes restricted to root admin only in PublicSaaS mode
    private static readonly string[] RestrictedRoutes =
    [
        "/dashboard/controlroom",
        "/dashboard/control-room",
        "/dashboard/selfhost",
        "/dashboard/power",
        "/dashboard/performance",
        "/api/power",
        "/api/mutations",
        "/api/selfhost",
        "/api/performance"
    ];

    public InternalTokenMiddleware(
        RequestDelegate next,
        ILogger<InternalTokenMiddleware> logger,
        IDeploymentContext deploymentContext)
    {
        _next = next;
        _logger = logger;
        _deploymentContext = deploymentContext;
    }

    public async Task InvokeAsync(HttpContext context, InternalTokenService tokenService)
    {
        // Route protection for PublicSaaS mode
        if (_deploymentContext.IsSaaS && context.User.Identity?.IsAuthenticated == true)
        {
            var path = context.Request.Path.Value?.ToLowerInvariant() ?? "";
            var isRootAdmin = context.User.HasClaim("is_root_admin", "true");

            // Check if accessing restricted route without root admin privileges
            if (!isRootAdmin && RestrictedRoutes.Any(r => path.StartsWith(r, StringComparison.OrdinalIgnoreCase)))
            {
                _logger.LogWarning(
                    "Access denied to restricted route {Path} for non-root user {UserId}",
                    path,
                    context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value);

                context.Response.StatusCode = 403;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = "Forbidden",
                    message = "This feature is not available in your current plan."
                });
                return;
            }
        }

        if (!context.User.Identity?.IsAuthenticated ?? true)
        {
            await _next(context);
            return;
        }

        var tenantId = context.User.FindFirst("tenant_id")?.Value;
        var userId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        if (string.IsNullOrEmpty(tenantId) || string.IsNullOrEmpty(userId))
        {
            await _next(context);
            return;
        }

        var existingToken = context.Session.GetString(InternalTokenSessionKey);
        var sessionTenantId = context.Session.GetString(InternalTokenTenantKey);
        var sessionUserId = context.Session.GetString(InternalTokenUserKey);

        // Security: Verify session tenant matches cookie tenant
        if (!string.IsNullOrEmpty(existingToken))
        {
            if (sessionTenantId != tenantId || sessionUserId != userId)
            {
                _logger.LogWarning(
                    "Session/cookie tenant mismatch detected. Clearing session for user {UserId}",
                    userId);
                context.Session.Clear();
                existingToken = null;
            }
        }

        string? token = existingToken;

        if (string.IsNullOrEmpty(token))
        {
            // Generate new token
            token = await GenerateTokenForUser(context, tokenService, userId, tenantId);
        }
        else if (await tokenService.IsExpiringSoonAsync(token))
        {
            // Refresh expiring token
            var refreshedToken = await tokenService.RefreshAsync(token);
            if (refreshedToken != null)
            {
                token = refreshedToken;
                context.Session.SetString(InternalTokenSessionKey, token);
                _logger.LogDebug("Refreshed internal token for user {UserId}", userId);
            }
            else
            {
                // Token invalid, generate new one
                token = await GenerateTokenForUser(context, tokenService, userId, tenantId);
            }
        }

        // Store token in HttpContext items for use by API proxy
        context.Items["InternalToken"] = token;

        await _next(context);
    }

    private async Task<string> GenerateTokenForUser(
        HttpContext context,
        InternalTokenService tokenService,
        string userId,
        string tenantId)
    {
        var workspaceId = context.User.FindFirst("workspace_id")?.Value ?? "default";
        var role = context.User.FindFirst("role")?.Value ?? "member";
        var email = context.User.FindFirst(ClaimTypes.Email)?.Value;

        // Preserve is_root_admin from cookie claims (set during login)
        var isRootAdmin = context.User.HasClaim("is_root_admin", "true");

        var roles = new[] { role };
        var token = await tokenService.CreateForUserAsync(userId, tenantId, workspaceId, roles, email, isRootAdmin);

        context.Session.SetString(InternalTokenSessionKey, token);
        context.Session.SetString(InternalTokenTenantKey, tenantId);
        context.Session.SetString(InternalTokenUserKey, userId);

        _logger.LogDebug(
            "Generated new internal token for user {UserId} in tenant {TenantId}, isRootAdmin={IsRootAdmin}",
            userId, tenantId, isRootAdmin);

        return token;
    }
}

/// <summary>
/// Extension methods for registering InternalTokenMiddleware.
/// </summary>
public static class InternalTokenMiddlewareExtensions
{
    /// <summary>
    /// Adds the internal token middleware to the application pipeline.
    /// Must be placed after UseSession and UseAuthentication.
    /// </summary>
    public static IApplicationBuilder UseInternalTokenMiddleware(this IApplicationBuilder app)
    {
        return app.UseMiddleware<InternalTokenMiddleware>();
    }
}
