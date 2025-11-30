using System.Net.Http.Headers;
using System.Security.Claims;

namespace SerialMemory.Web.Services;

/// <summary>
/// Service that provides properly authenticated HttpClient for API calls.
/// Ensures all API requests include tenant context and internal authentication.
/// </summary>
public sealed class ApiClientService(
    IHttpClientFactory httpClientFactory,
    InternalTokenService internalTokenService,
    IHttpContextAccessor httpContextAccessor,
    IConfiguration configuration,
    ILogger<ApiClientService> logger)
{
    private readonly string? _serviceApiKey = configuration["SERVICE_API_KEY"]
                                              ?? Environment.GetEnvironmentVariable("SERVICE_API_KEY");

    /// <summary>
    /// Creates an HttpClient configured with proper tenant context for API calls.
    /// Uses internal tokens + tenant headers for server-side authentication.
    /// </summary>
    /// <param name="clientName">Named HttpClient to use (default: "DashboardApi" for billing, api-keys, etc.)</param>
    public HttpClient CreateClient(string clientName = "DashboardApi")
    {
        // Log at startup to verify _serviceApiKey is loaded
        logger.LogInformation(
            "ApiClientService.CreateClient called with clientName={ClientName}, hasServiceApiKey={HasKey}, keyLength={KeyLength}",
            clientName, !string.IsNullOrEmpty(_serviceApiKey), _serviceApiKey?.Length ?? 0);

        var httpContext = httpContextAccessor.HttpContext
            ?? throw new InvalidOperationException("No HttpContext available");

        var client = httpClientFactory.CreateClient(clientName);

        // Get tenant context from cookie claims (primary authority)
        var tenantId = httpContext.User.FindFirst("tenant_id")?.Value;
        var userId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        if (string.IsNullOrEmpty(tenantId) || string.IsNullOrEmpty(userId))
        {
            logger.LogWarning("Cannot create authenticated API client: missing tenant/user claims");
            return client; // Return unauthenticated client - will likely fail with 401/403
        }

        // Get or generate internal token
        var internalToken = httpContext.Session.GetString("internal_token");
        var sessionTenantId = httpContext.Session.GetString("internal_token_tenant");

        // Verify session matches cookie (defense in depth)
        if (!string.IsNullOrEmpty(sessionTenantId) && sessionTenantId != tenantId)
        {
            logger.LogError(
                "SECURITY: Session tenant mismatch in ApiClientService. Cookie={CookieTenant}, Session={SessionTenant}",
                tenantId, sessionTenantId);
            httpContext.Session.Clear();
            internalToken = null;
        }

        // Valid roles for authorization policies
        var validRoles = new[] { "owner", "admin", "member" };

        // Validate existing token or generate new one
        if (!string.IsNullOrEmpty(internalToken))
        {
            var tokenClaims = internalTokenService.ValidateToken(internalToken);
            if (tokenClaims == null || tokenClaims.TenantId != tenantId)
            {
                logger.LogDebug("Internal token expired or invalid, regenerating");
                internalToken = null;
            }
            else if (!validRoles.Contains(tokenClaims.Role, StringComparer.OrdinalIgnoreCase))
            {
                // Force regeneration if cached token has invalid role (e.g., "user")
                logger.LogWarning(
                    "Cached token has invalid role '{Role}', forcing regeneration",
                    tokenClaims.Role);
                httpContext.Session.Clear();
                internalToken = null;
            }
        }

        if (string.IsNullOrEmpty(internalToken))
        {
            // Generate new internal token
            var cookieRole = httpContext.User.FindFirst("role")?.Value;

            // Use the actual role from cookie - needed for owner/admin access
            var role = cookieRole ?? "member";

            // Preserve is_root_admin from cookie claims (set during login)
            var isRootAdmin = httpContext.User.HasClaim("is_root_admin", "true");

            logger.LogInformation(
                "Generating new internal token for user {UserId} with role '{Role}', isRootAdmin={IsRootAdmin}",
                userId, role, isRootAdmin);

            var email = httpContext.User.FindFirst(ClaimTypes.Email)?.Value;

            // DEBUG: Log all claims to understand what's in the cookie
            logger.LogInformation("TOKEN_DEBUG: Getting email from cookie claims");
            foreach (var claim in httpContext.User.Claims)
            {
                logger.LogInformation("TOKEN_DEBUG: Claim {Type}={Value}", claim.Type, claim.Value);
            }
            logger.LogInformation("TOKEN_DEBUG: Email from ClaimTypes.Email = {Email}", email ?? "(null)");

            internalToken = internalTokenService.GenerateToken(new InternalTokenClaims
            {
                TenantId = tenantId,
                UserId = userId,
                WorkspaceId = "default",
                Role = role,
                Email = email,
                IsRootAdmin = isRootAdmin
            });

            // Verify the generated token has the correct role
            var verifyToken = internalTokenService.ValidateToken(internalToken);
            if (verifyToken?.Role != role)
            {
                logger.LogError(
                    "CRITICAL: Generated token has wrong role! Expected '{Expected}', got '{Actual}'",
                    role, verifyToken?.Role ?? "(null)");
            }

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
        client.DefaultRequestHeaders.Remove("X-Api-Key");
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenantId);
        client.DefaultRequestHeaders.Add("X-User-Id", userId);

        // Add service API key for server-to-server communication with main API
        if (!string.IsNullOrEmpty(_serviceApiKey) && clientName == "Api")
        {
            client.DefaultRequestHeaders.Add("X-Api-Key", _serviceApiKey);
            logger.LogInformation(
                "API_KEY_ADDED: Added X-Api-Key header for {ClientName} client (key length: {KeyLength})",
                clientName, _serviceApiKey.Length);
        }
        else if (clientName == "Api")
        {
            logger.LogError(
                "API_KEY_MISSING: SERVICE_API_KEY is null/empty! Cannot add X-Api-Key header. " +
                "Config value: {ConfigValue}, Env value: {EnvValue}",
                configuration["SERVICE_API_KEY"] ?? "(null)",
                Environment.GetEnvironmentVariable("SERVICE_API_KEY") ?? "(null)");
        }

        logger.LogInformation(
            "API_CLIENT_CREATED: tenant={TenantId}, user={UserId}, hasApiKey={HasApiKey}, clientName={ClientName}",
            tenantId, userId, !string.IsNullOrEmpty(_serviceApiKey), clientName);

        return client;
    }

    /// <summary>
    /// Gets the current user's tenant ID from cookie claims.
    /// </summary>
    public string? GetCurrentTenantId()
    {
        return httpContextAccessor.HttpContext?.User.FindFirst("tenant_id")?.Value;
    }

    /// <summary>
    /// Gets the current user's ID from cookie claims.
    /// </summary>
    public string? GetCurrentUserId()
    {
        return httpContextAccessor.HttpContext?.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
    }
}
