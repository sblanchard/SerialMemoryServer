using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace SerialMemory.Api.Endpoints;

/// <summary>
/// Auth endpoints that forward to Dashboard API.
/// These provide a unified API surface at /api/auth/* for external consumers.
/// </summary>
public static class AuthEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static void MapAuthEndpoints(this WebApplication app)
    {
        var dashboardApiUrl = Environment.GetEnvironmentVariable("DASHBOARD_API_URL")
            ?? "http://dashboard-api:8080";

        // POST /api/auth/register - Forward to Dashboard API /signup
        app.MapPost("/api/auth/register", async (
            HttpContext context,
            IHttpClientFactory httpClientFactory,
            ILogger<Program> logger) =>
        {
            return await ForwardToDashboard(
                context,
                httpClientFactory,
                logger,
                dashboardApiUrl,
                "/signup",
                "POST");
        })
        .AllowAnonymous()
        .WithName("Register")
        .WithDescription("Register a new tenant (forwards to Dashboard API)");

        // POST /api/auth/signup - Alias for register
        app.MapPost("/api/auth/signup", async (
            HttpContext context,
            IHttpClientFactory httpClientFactory,
            ILogger<Program> logger) =>
        {
            return await ForwardToDashboard(
                context,
                httpClientFactory,
                logger,
                dashboardApiUrl,
                "/signup",
                "POST");
        })
        .AllowAnonymous()
        .WithName("Signup")
        .WithDescription("Register a new tenant (alias for /api/auth/register)");

        // POST /api/auth/verify-email - Forward to Dashboard API
        app.MapPost("/api/auth/verify-email", async (
            HttpContext context,
            IHttpClientFactory httpClientFactory,
            ILogger<Program> logger) =>
        {
            return await ForwardToDashboard(
                context,
                httpClientFactory,
                logger,
                dashboardApiUrl,
                "/auth/verify-email",
                "POST");
        })
        .AllowAnonymous()
        .WithName("VerifyEmail")
        .WithDescription("Verify email address");

        // POST /api/auth/resend-verification - Forward to Dashboard API
        app.MapPost("/api/auth/resend-verification", async (
            HttpContext context,
            IHttpClientFactory httpClientFactory,
            ILogger<Program> logger) =>
        {
            return await ForwardToDashboard(
                context,
                httpClientFactory,
                logger,
                dashboardApiUrl,
                "/auth/resend-verification",
                "POST");
        })
        .AllowAnonymous()
        .WithName("ResendVerification")
        .WithDescription("Resend verification email");

        // POST /api/auth/magic-link - Request magic link login
        app.MapPost("/api/auth/magic-link", async (
            HttpContext context,
            IHttpClientFactory httpClientFactory,
            ILogger<Program> logger) =>
        {
            return await ForwardToDashboard(
                context,
                httpClientFactory,
                logger,
                dashboardApiUrl,
                "/auth/magic-link",
                "POST");
        })
        .AllowAnonymous()
        .WithName("RequestMagicLink")
        .WithDescription("Request a magic link for passwordless login");

        // POST /api/auth/magic-link/verify - Verify magic link token
        app.MapPost("/api/auth/magic-link/verify", async (
            HttpContext context,
            IHttpClientFactory httpClientFactory,
            ILogger<Program> logger) =>
        {
            return await ForwardToDashboard(
                context,
                httpClientFactory,
                logger,
                dashboardApiUrl,
                "/auth/magic-link/verify",
                "POST");
        })
        .AllowAnonymous()
        .WithName("VerifyMagicLink")
        .WithDescription("Verify magic link token and get API key");

        // GET /api/auth/whoami - Get current user info (requires auth)
        app.MapGet("/api/auth/whoami", (
            HttpContext context,
            Core.Interfaces.ITenantContext tenantContext) =>
        {
            if (string.IsNullOrEmpty(tenantContext.TenantId))
            {
                return Results.Unauthorized();
            }

            return Results.Ok(new
            {
                tenantId = tenantContext.TenantId,
                userId = tenantContext.UserId,
                workspaceId = tenantContext.WorkspaceId,
                role = tenantContext.UserRole ?? "user",
                isLabMode = tenantContext.IsLabMode,
                isRootAdmin = tenantContext.IsRootAdmin,
                scopes = tenantContext.Scopes
            });
        })
        .WithName("WhoAmI")
        .WithDescription("Get current authenticated user info");
    }

    private static async Task<IResult> ForwardToDashboard(
        HttpContext context,
        IHttpClientFactory httpClientFactory,
        ILogger logger,
        string dashboardApiUrl,
        string targetPath,
        string method)
    {
        try
        {
            var client = httpClientFactory.CreateClient();
            client.BaseAddress = new Uri(dashboardApiUrl);
            client.Timeout = TimeSpan.FromSeconds(30);

            // Read request body
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync();

            // Forward request
            HttpResponseMessage response;
            var targetUrl = targetPath + context.Request.QueryString;

            if (method == "POST")
            {
                var content = new StringContent(body, Encoding.UTF8, "application/json");
                response = await client.PostAsync(targetUrl, content);
            }
            else if (method == "GET")
            {
                response = await client.GetAsync(targetUrl);
            }
            else
            {
                return Results.StatusCode(405);
            }

            // Return response
            var responseBody = await response.Content.ReadAsStringAsync();
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "application/json";

            logger.LogDebug(
                "Auth forwarded {Method} {Path} -> {DashboardUrl}{Target} = {StatusCode}",
                method, context.Request.Path, dashboardApiUrl, targetPath, (int)response.StatusCode);

            return Results.Content(responseBody, contentType, Encoding.UTF8, (int)response.StatusCode);
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Failed to forward auth request to Dashboard API at {Url}", dashboardApiUrl);
            return Results.Problem(
                "Authentication service temporarily unavailable",
                statusCode: 503);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error forwarding auth request");
            return Results.Problem("Internal server error", statusCode: 500);
        }
    }
}
