using SerialMemory.Core.Interfaces;

namespace SerialMemory.Api.Auth;

/// <summary>
/// Middleware that authenticates requests via X-Api-Key header.
/// Sets up tenant context for all subsequent middleware and handlers.
/// </summary>
public sealed class ApiKeyAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ApiKeyAuthMiddleware> _logger;
    private readonly HashSet<string> _allowAnonymousPaths;

    public ApiKeyAuthMiddleware(RequestDelegate next, ILogger<ApiKeyAuthMiddleware> logger)
    {
        _next = next;
        _logger = logger;

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
        var apiKey = context.Request.Headers["X-Api-Key"].FirstOrDefault()
            ?? context.Request.Headers["Authorization"].FirstOrDefault()?.Replace("Bearer ", "");

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
