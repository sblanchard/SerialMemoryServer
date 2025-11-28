using System.Net.Http.Headers;
using System.Security.Claims;

namespace SerialMemory.Web.Services;

/// <summary>
/// Service that provides properly authenticated HttpClient for API calls.
/// Ensures all API requests include tenant context and internal authentication.
/// </summary>
public sealed class ApiClientService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly InternalTokenService _internalTokenService;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<ApiClientService> _logger;

    public ApiClientService(
        IHttpClientFactory httpClientFactory,
        InternalTokenService internalTokenService,
        IHttpContextAccessor httpContextAccessor,
        ILogger<ApiClientService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _internalTokenService = internalTokenService;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    /// <summary>
    /// Creates an HttpClient configured with proper tenant context for API calls.
    /// Uses internal tokens + tenant headers for server-side authentication.
    /// </summary>
    /// <param name="clientName">Named HttpClient to use (default: "Api")</param>
    public HttpClient CreateClient(string clientName = "Api")
    {
        var httpContext = _httpContextAccessor.HttpContext
            ?? throw new InvalidOperationException("No HttpContext available");

        var client = _httpClientFactory.CreateClient(clientName);

        // Get tenant context from cookie claims (primary authority)
        var tenantId = httpContext.User.FindFirst("tenant_id")?.Value;
        var userId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        if (string.IsNullOrEmpty(tenantId) || string.IsNullOrEmpty(userId))
        {
            _logger.LogWarning("Cannot create authenticated API client: missing tenant/user claims");
            return client; // Return unauthenticated client - will likely fail with 401/403
        }

        // Get or generate internal token
        var internalToken = httpContext.Session.GetString("internal_token");
        var sessionTenantId = httpContext.Session.GetString("internal_token_tenant");

        // Verify session matches cookie (defense in depth)
        if (!string.IsNullOrEmpty(sessionTenantId) && sessionTenantId != tenantId)
        {
            _logger.LogError(
                "SECURITY: Session tenant mismatch in ApiClientService. Cookie={CookieTenant}, Session={SessionTenant}",
                tenantId, sessionTenantId);
            httpContext.Session.Clear();
            internalToken = null;
        }

        // Validate existing token or generate new one
        if (!string.IsNullOrEmpty(internalToken))
        {
            var tokenClaims = _internalTokenService.ValidateToken(internalToken);
            if (tokenClaims == null || tokenClaims.TenantId != tenantId)
            {
                _logger.LogDebug("Internal token expired or invalid, regenerating");
                internalToken = null;
            }
        }

        if (string.IsNullOrEmpty(internalToken))
        {
            // Generate new internal token
            var role = httpContext.User.FindFirst("role")?.Value ?? "user";
            var email = httpContext.User.FindFirst(ClaimTypes.Email)?.Value;

            internalToken = _internalTokenService.GenerateToken(new InternalTokenClaims
            {
                TenantId = tenantId,
                UserId = userId,
                WorkspaceId = "default",
                Role = role,
                Email = email
            });

            // Store in session
            httpContext.Session.SetString("internal_token", internalToken);
            httpContext.Session.SetString("internal_token_tenant", tenantId);
            httpContext.Session.SetString("internal_token_user", userId);
        }

        // Add internal token as Bearer auth
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", internalToken);

        // Add tenant context headers (for API middleware)
        // Remove any existing headers first to avoid duplicates
        client.DefaultRequestHeaders.Remove("X-Tenant-Id");
        client.DefaultRequestHeaders.Remove("X-User-Id");
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenantId);
        client.DefaultRequestHeaders.Add("X-User-Id", userId);

        _logger.LogDebug(
            "Created authenticated API client for tenant {TenantId}, user {UserId}",
            tenantId, userId);

        return client;
    }

    /// <summary>
    /// Gets the current user's tenant ID from cookie claims.
    /// </summary>
    public string? GetCurrentTenantId()
    {
        return _httpContextAccessor.HttpContext?.User.FindFirst("tenant_id")?.Value;
    }

    /// <summary>
    /// Gets the current user's ID from cookie claims.
    /// </summary>
    public string? GetCurrentUserId()
    {
        return _httpContextAccessor.HttpContext?.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
    }
}
