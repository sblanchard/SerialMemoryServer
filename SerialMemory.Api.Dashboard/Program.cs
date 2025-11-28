using System.Diagnostics;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using SerialMemory.Core.Interfaces;
using SerialMemory.Core.Models;
using SerialMemory.Core.Telemetry;
using SerialMemory.Infrastructure;
using SerialMemory.Infrastructure.Email;

var builder = WebApplication.CreateBuilder(args);

// Configuration
var postgresHost = builder.Configuration["POSTGRES_HOST"] ?? "localhost";
var postgresPort = builder.Configuration["POSTGRES_PORT"] ?? "5432";
var postgresUser = builder.Configuration["POSTGRES_USER"] ?? "postgres";
var postgresPassword = builder.Configuration["POSTGRES_PASSWORD"] ?? "postgres";
var postgresDb = builder.Configuration["POSTGRES_DB"] ?? "contextdb";
var connectionString = $"Host={postgresHost};Port={postgresPort};Database={postgresDb};Username={postgresUser};Password={postgresPassword};Timeout=10;Command Timeout=10;Pooling=true;Minimum Pool Size=1;Maximum Pool Size=20";

var jwtSecret = builder.Configuration["JWT_SECRET"] ?? "default-development-secret-32chars!!";
var jwtIssuer = builder.Configuration["JWT_ISSUER"] ?? "serialmemory";
var jwtAudience = builder.Configuration["JWT_AUDIENCE"] ?? "serialmemory-api";
var selfHostedMode = builder.Configuration["SERIALMEMORY_MODE"]?.ToLowerInvariant() == "self-hosted";

// Add services
builder.Services.AddSingleton<ITenantDashboardService>(sp =>
    new TenantDashboardService(connectionString, sp.GetRequiredService<ILogger<TenantDashboardService>>()));

builder.Services.AddSingleton<IApiKeyService>(sp =>
    new ApiKeyService(connectionString, sp.GetRequiredService<ILogger<ApiKeyService>>(), jwtSecret, jwtIssuer, jwtAudience));

builder.Services.AddSingleton<IAdminService>(sp =>
    new AdminService(connectionString, sp.GetRequiredService<ILogger<AdminService>>()));

// Email and onboarding services - use NoOp if ACS not configured
// Check multiple config key patterns for backward compatibility
var acsConnectionString = builder.Configuration["Email:AcsConnectionString"]
    ?? builder.Configuration["AzureCommunicationServices:ConnectionString"]
    ?? builder.Configuration["AzureCommunicationServices__ConnectionString"]
    ?? Environment.GetEnvironmentVariable("SERIALMEMORY_ACS_CONNECTION");
if (!string.IsNullOrEmpty(acsConnectionString))
{
    builder.Services.AddSingleton<IEmailService, AcsEmailService>();
}
else
{
    Console.WriteLine("[WARN] ACS not configured - using NoOp email service (emails will be logged but not sent)");
    builder.Services.AddSingleton<IEmailService, NoOpEmailService>();
}
builder.Services.AddSingleton<IDeveloperOnboardingService>(sp =>
    new SerialMemory.Infrastructure.Onboarding.DeveloperOnboardingService(
        builder.Configuration,
        sp.GetRequiredService<IEmailService>(),
        sp.GetRequiredService<ILogger<SerialMemory.Infrastructure.Onboarding.DeveloperOnboardingService>>()));
builder.Services.AddSingleton<IEmailVerificationService, EmailVerificationService>();

// Stripe configuration
var stripeConfig = new StripeConfig
{
    SecretKey = builder.Configuration["STRIPE_SECRET_KEY"] ?? "",
    PublishableKey = builder.Configuration["STRIPE_PUBLISHABLE_KEY"] ?? "",
    WebhookSecret = builder.Configuration["STRIPE_WEBHOOK_SECRET"] ?? "",
    ProPriceId = builder.Configuration["STRIPE_PRO_PRICE_ID"] ?? "",
    EnterprisePriceId = builder.Configuration["STRIPE_ENTERPRISE_PRICE_ID"] ?? "",
    DefaultSuccessUrl = builder.Configuration["STRIPE_SUCCESS_URL"] ?? "/dashboard/billing?success=true",
    DefaultCancelUrl = builder.Configuration["STRIPE_CANCEL_URL"] ?? "/dashboard/billing?canceled=true"
};

builder.Services.AddSingleton<IBillingService>(sp =>
    new StripeBillingService(connectionString, sp.GetRequiredService<ILogger<StripeBillingService>>(), stripeConfig));

builder.Services.AddAuthentication(options =>
    {
        // Default to JWT, but allow API key to authenticate as well
        options.DefaultAuthenticateScheme = "MultiScheme";
        options.DefaultChallengeScheme = "MultiScheme";
    })
    .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtIssuer,
            ValidAudience = jwtAudience,
            IssuerSigningKey = new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(jwtSecret))
        };
    })
    .AddApiKey()
    .AddPolicyScheme("MultiScheme", "JWT or API Key", options =>
    {
        options.ForwardDefaultSelector = context =>
        {
            // Check for API key in header or Bearer token starting with "sm_"
            var apiKeyHeader = context.Request.Headers["X-API-Key"].FirstOrDefault();
            var authHeader = context.Request.Headers.Authorization.FirstOrDefault();

            if (!string.IsNullOrEmpty(apiKeyHeader))
            {
                return ApiKeyAuthenticationHandler.SchemeName;
            }

            if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Bearer sm_", StringComparison.OrdinalIgnoreCase))
            {
                return ApiKeyAuthenticationHandler.SchemeName;
            }

            // Default to JWT
            return JwtBearerDefaults.AuthenticationScheme;
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("Owner", policy => policy.RequireClaim("role", "owner"));
    options.AddPolicy("Admin", policy => policy.RequireClaim("role", "owner", "admin"));
    options.AddPolicy("Member", policy => policy.RequireClaim("role", "owner", "admin", "member"));
});

builder.Services.AddOpenApi();
builder.Services.AddEndpointsApiExplorer();

// Add production-grade telemetry with Prometheus exporter
builder.Services.AddSerialMemoryTelemetry("SerialMemory.Api.Dashboard", "1.0.0");

var app = builder.Build();

// Validate database connection at startup
try
{
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    await using var testConn = new Npgsql.NpgsqlConnection(connectionString);
    await testConn.OpenAsync(cts.Token);
    Console.WriteLine($"[OK] Database connection verified: {postgresHost}:{postgresPort}/{postgresDb}");
}
catch (Exception ex)
{
    Console.WriteLine($"[ERROR] Database connection failed: {ex.Message}");
    Console.WriteLine($"[ERROR] Connection string: Host={postgresHost};Port={postgresPort};Database={postgresDb}");
}

// Configure pipeline
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// Add metrics middleware early in pipeline
app.UseSerialMemoryMetrics();

// Map Prometheus /metrics endpoint
app.MapSerialMemoryMetrics();

app.UseAuthentication();
app.UseAuthorization();

// Helper to extract tenant info from claims
static (Guid TenantId, string UserId, string WorkspaceId) GetTenantContext(ClaimsPrincipal user, bool selfHosted)
{
    if (selfHosted)
    {
        return (Guid.Parse("00000000-0000-0000-0000-000000000000"), "self-hosted", "default");
    }

    var tenantIdClaim = user.FindFirst("tenant_id")?.Value
        ?? throw new UnauthorizedAccessException("Missing tenant_id claim");

    if (!Guid.TryParse(tenantIdClaim, out var tenantId))
        throw new UnauthorizedAccessException("Invalid tenant_id claim");

    var userId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value
        ?? user.FindFirst("sub")?.Value
        ?? throw new UnauthorizedAccessException("Missing user identifier");

    var workspaceId = user.FindFirst("workspace_id")?.Value ?? "default";

    return (tenantId, userId, workspaceId);
}

// =============================================================================
// GET /me - Current user info
// =============================================================================
app.MapGet("/me", async (
    ClaimsPrincipal user,
    ITenantDashboardService dashboardService,
    CancellationToken ct) =>
{
    var (tenantId, userId, _) = GetTenantContext(user, selfHostedMode);
    var result = await dashboardService.GetCurrentUserAsync(tenantId, userId, ct);
    return Results.Ok(result);
})
.WithName("GetCurrentUser")
.WithDescription("Gets current user information within the tenant context")
.RequireAuthorization()
.Produces<UserInfoResult>(StatusCodes.Status200OK)
.Produces(StatusCodes.Status401Unauthorized);

// =============================================================================
// GET /tenant/usage - Current cycle usage
// =============================================================================
app.MapGet("/tenant/usage", async (
    ClaimsPrincipal user,
    ITenantDashboardService dashboardService,
    CancellationToken ct) =>
{
    var (tenantId, _, workspaceId) = GetTenantContext(user, selfHostedMode);
    var result = await dashboardService.GetTenantUsageAsync(tenantId, workspaceId, ct);
    return Results.Ok(result);
})
.WithName("GetTenantUsage")
.WithDescription("Gets current billing cycle usage statistics")
.RequireAuthorization()
.Produces<TenantUsageResult>(StatusCodes.Status200OK)
.Produces(StatusCodes.Status401Unauthorized);

// =============================================================================
// GET /tenant/plan - Plan details
// =============================================================================
app.MapGet("/tenant/plan", async (
    ClaimsPrincipal user,
    ITenantDashboardService dashboardService,
    CancellationToken ct) =>
{
    var (tenantId, _, _) = GetTenantContext(user, selfHostedMode);
    var result = await dashboardService.GetTenantPlanAsync(tenantId, ct);
    return Results.Ok(result);
})
.WithName("GetTenantPlan")
.WithDescription("Gets tenant plan details and current limits status")
.RequireAuthorization()
.Produces<TenantPlanResult>(StatusCodes.Status200OK)
.Produces(StatusCodes.Status401Unauthorized);

// =============================================================================
// POST /tenant/export - Request data export
// =============================================================================
app.MapPost("/tenant/export", async (
    ClaimsPrincipal user,
    [FromBody] ExportRequestOptions? options,
    [FromServices] ITenantDashboardService dashboardService,
    CancellationToken ct) =>
{
    var (tenantId, userId, _) = GetTenantContext(user, selfHostedMode);
    options ??= new ExportRequestOptions();

    try
    {
        var result = await dashboardService.RequestExportAsync(tenantId, userId, options, ct);
        return Results.Accepted($"/tenant/export/{result.ExportId}", result);
    }
    catch (InvalidOperationException ex)
    {
        return Results.Conflict(new { error = "export_in_progress", message = ex.Message });
    }
})
.WithName("RequestExport")
.WithDescription("Requests a data export for the tenant")
.RequireAuthorization("Member")
.Produces<ExportRequestResult>(StatusCodes.Status202Accepted)
.Produces(StatusCodes.Status401Unauthorized)
.Produces(StatusCodes.Status409Conflict);

// =============================================================================
// GET /tenant/export/{id} - Export status
// =============================================================================
app.MapGet("/tenant/export/{id:guid}", async (
    Guid id,
    ClaimsPrincipal user,
    ITenantDashboardService dashboardService,
    CancellationToken ct) =>
{
    var (tenantId, _, _) = GetTenantContext(user, selfHostedMode);
    var result = await dashboardService.GetExportStatusAsync(tenantId, id, ct);

    if (result == null)
        return Results.NotFound(new { error = "not_found", message = "Export request not found" });

    return Results.Ok(result);
})
.WithName("GetExportStatus")
.WithDescription("Gets the status of an export request")
.RequireAuthorization()
.Produces<ExportStatusResult>(StatusCodes.Status200OK)
.Produces(StatusCodes.Status401Unauthorized)
.Produces(StatusCodes.Status404NotFound);

// =============================================================================
// DELETE /tenant - Request tenant deletion (owner only)
// =============================================================================
app.MapDelete("/tenant", async (
    ClaimsPrincipal user,
    [FromBody] DeleteTenantRequest request,
    [FromServices] ITenantDashboardService dashboardService,
    CancellationToken ct) =>
{
    var (tenantId, userId, _) = GetTenantContext(user, selfHostedMode);

    if (selfHostedMode)
        return Results.BadRequest(new { error = "not_allowed", message = "Deletion not allowed in self-hosted mode" });

    try
    {
        var result = await dashboardService.RequestDeletionAsync(tenantId, userId, request.ConfirmationPhrase, ct);
        return Results.Accepted($"/tenant/deletion", result);
    }
    catch (UnauthorizedAccessException)
    {
        return Results.Forbid();
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = "invalid_confirmation", message = ex.Message });
    }
})
.WithName("RequestDeletion")
.WithDescription("Requests tenant deletion (owner only, 30-day grace period)")
.RequireAuthorization("Owner")
.Produces<DeletionRequestResult>(StatusCodes.Status202Accepted)
.Produces(StatusCodes.Status400BadRequest)
.Produces(StatusCodes.Status401Unauthorized)
.Produces(StatusCodes.Status403Forbidden);

// =============================================================================
// GET /tenant/deletion - Deletion status
// =============================================================================
app.MapGet("/tenant/deletion", async (
    ClaimsPrincipal user,
    ITenantDashboardService dashboardService,
    CancellationToken ct) =>
{
    var (tenantId, _, _) = GetTenantContext(user, selfHostedMode);
    var result = await dashboardService.GetDeletionStatusAsync(tenantId, ct);

    if (result == null)
        return Results.NotFound(new { error = "not_found", message = "No deletion request found" });

    return Results.Ok(result);
})
.WithName("GetDeletionStatus")
.WithDescription("Gets the status of a deletion request")
.RequireAuthorization()
.Produces<DeletionStatusResult>(StatusCodes.Status200OK)
.Produces(StatusCodes.Status401Unauthorized)
.Produces(StatusCodes.Status404NotFound);

// =============================================================================
// POST /tenant/deletion/cancel - Cancel deletion
// =============================================================================
app.MapPost("/tenant/deletion/cancel", async (
    ClaimsPrincipal user,
    ITenantDashboardService dashboardService,
    CancellationToken ct) =>
{
    var (tenantId, userId, _) = GetTenantContext(user, selfHostedMode);

    try
    {
        var cancelled = await dashboardService.CancelDeletionAsync(tenantId, userId, ct);

        if (!cancelled)
            return Results.NotFound(new { error = "not_found", message = "No pending deletion request found" });

        return Results.Ok(new { message = "Deletion cancelled successfully" });
    }
    catch (UnauthorizedAccessException)
    {
        return Results.Forbid();
    }
})
.WithName("CancelDeletion")
.WithDescription("Cancels a pending deletion request")
.RequireAuthorization("Admin")
.Produces(StatusCodes.Status200OK)
.Produces(StatusCodes.Status401Unauthorized)
.Produces(StatusCodes.Status403Forbidden)
.Produces(StatusCodes.Status404NotFound);

// =============================================================================
// POST /signup - Create new tenant (no auth required)
// =============================================================================
app.MapPost("/signup", async (
    [FromBody] SignupRequest request,
    [FromServices] IApiKeyService apiKeyService,
    [FromServices] IEmailVerificationService emailService,
    HttpContext httpContext,
    CancellationToken ct) =>
{
    if (selfHostedMode)
        return Results.BadRequest(new { error = "not_allowed", message = "Signup not available in self-hosted mode" });

    try
    {
        var result = await apiKeyService.SignupAsync(request, ct);
        Metrics.TenantSignupTotal.Add(1);

        // Send verification email (non-blocking - don't fail signup if email fails)
        try
        {
            var ipAddress = httpContext.Connection.RemoteIpAddress?.ToString();
            var userAgent = httpContext.Request.Headers.UserAgent.FirstOrDefault();
            await emailService.SendVerificationEmailAsync(
                result.TenantId,
                result.UserId,
                request.Email,
                ipAddress,
                userAgent,
                ct);
        }
        catch (Exception ex)
        {
            // Log but don't fail signup
            Console.WriteLine($"[WARN] Failed to send verification email to {request.Email}: {ex.Message}");
        }

        return Results.Created($"/tenant/{result.TenantId}", result);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = "validation_error", message = ex.Message });
    }
    catch (InvalidOperationException ex)
    {
        return Results.Conflict(new { error = "duplicate", message = ex.Message });
    }
})
.WithName("Signup")
.WithDescription("Creates a new tenant with initial API key and sends verification email")
.AllowAnonymous()
.Produces<SignupResult>(StatusCodes.Status201Created)
.Produces(StatusCodes.Status400BadRequest)
.Produces(StatusCodes.Status409Conflict);

// =============================================================================
// POST /auth/verify-email - Verify email address
// =============================================================================
app.MapPost("/auth/verify-email", async (
    [FromBody] VerifyEmailRequest request,
    [FromServices] IEmailVerificationService emailService,
    HttpContext httpContext,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Token))
        return Results.BadRequest(new { error = "validation_error", message = "Token is required" });

    var ipAddress = httpContext.Connection.RemoteIpAddress?.ToString();
    var result = await emailService.VerifyEmailAsync(request.Token, ipAddress, ct);

    if (!result.Success)
        return Results.BadRequest(new { error = "verification_failed", message = result.Error });

    return Results.Ok(new
    {
        success = true,
        message = "Email verified successfully",
        tenantId = result.TenantId,
        userId = result.UserId
    });
})
.WithName("VerifyEmail")
.WithDescription("Verifies a user's email address using the token from the verification email")
.AllowAnonymous()
.Produces(StatusCodes.Status200OK)
.Produces(StatusCodes.Status400BadRequest);

// =============================================================================
// POST /auth/resend-verification - Resend verification email
// =============================================================================
app.MapPost("/auth/resend-verification", async (
    [FromBody] ResendVerificationRequest request,
    [FromServices] IEmailVerificationService emailService,
    HttpContext httpContext,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Email))
        return Results.BadRequest(new { error = "validation_error", message = "Email is required" });

    // Always return success to prevent email enumeration
    try
    {
        var ipAddress = httpContext.Connection.RemoteIpAddress?.ToString();
        var userAgent = httpContext.Request.Headers.UserAgent.FirstOrDefault();

        // The service will handle checking if user exists
        await emailService.SendLoginLinkAsync(request.Email, ipAddress, userAgent, ct);
    }
    catch
    {
        // Swallow - don't reveal if email exists
    }

    return Results.Ok(new { message = "If that email exists, a verification link has been sent" });
})
.WithName("ResendVerification")
.WithDescription("Resends the verification email if the user exists")
.AllowAnonymous()
.Produces(StatusCodes.Status200OK)
.Produces(StatusCodes.Status400BadRequest);

// =============================================================================
// POST /auth/magic-link - Request passwordless login link
// =============================================================================
app.MapPost("/auth/magic-link", async (
    [FromBody] MagicLinkRequest request,
    [FromServices] IEmailVerificationService emailService,
    HttpContext httpContext,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Email))
        return Results.BadRequest(new { error = "validation_error", message = "Email is required" });

    try
    {
        var ipAddress = httpContext.Connection.RemoteIpAddress?.ToString();
        var userAgent = httpContext.Request.Headers.UserAgent.FirstOrDefault();
        await emailService.SendLoginLinkAsync(request.Email, ipAddress, userAgent, ct);
    }
    catch (InvalidOperationException ex) when (ex.Message.Contains("Too many"))
    {
        return Results.StatusCode(429); // Too Many Requests
    }
    catch
    {
        // Swallow - don't reveal if email exists
    }

    // Always return success to prevent email enumeration
    return Results.Ok(new { message = "If that email exists, a login link has been sent" });
})
.WithName("RequestMagicLink")
.WithDescription("Requests a passwordless login link via email")
.AllowAnonymous()
.Produces(StatusCodes.Status200OK)
.Produces(StatusCodes.Status400BadRequest)
.Produces(StatusCodes.Status429TooManyRequests);

// =============================================================================
// POST /auth/magic-link/verify - Verify magic link and get token
// =============================================================================
app.MapPost("/auth/magic-link/verify", async (
    [FromBody] VerifyMagicLinkRequest request,
    [FromServices] IEmailVerificationService emailService,
    [FromServices] IApiKeyService apiKeyService,
    HttpContext httpContext,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Token))
        return Results.BadRequest(new { error = "validation_error", message = "Token is required" });

    var ipAddress = httpContext.Connection.RemoteIpAddress?.ToString();
    var result = await emailService.ValidateLoginTokenAsync(request.Token, ipAddress, ct);

    if (!result.Success)
        return Results.BadRequest(new { error = "login_failed", message = result.Error });

    // Generate JWT token for the authenticated user
    // Use the API key service's internal JWT generation by creating a validation result
    return Results.Ok(new
    {
        success = true,
        tenantId = result.TenantId,
        userId = result.UserId,
        email = result.Email,
        role = result.Role,
        tenantName = result.TenantName,
        tenantSlug = result.TenantSlug
    });
})
.WithName("VerifyMagicLink")
.WithDescription("Verifies a magic link token and returns user info")
.AllowAnonymous()
.Produces(StatusCodes.Status200OK)
.Produces(StatusCodes.Status400BadRequest);

// =============================================================================
// POST /api-keys - Create new API key
// =============================================================================
app.MapPost("/api-keys", async (
    [FromBody] CreateApiKeyRequest request,
    ClaimsPrincipal user,
    [FromServices] IApiKeyService apiKeyService,
    CancellationToken ct) =>
{
    var (tenantId, userId, _) = GetTenantContext(user, selfHostedMode);

    try
    {
        var result = await apiKeyService.CreateApiKeyAsync(tenantId, userId, request, ct);
        return Results.Created($"/api-keys/{result.Id}", result);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = "validation_error", message = ex.Message });
    }
    catch (UnauthorizedAccessException)
    {
        return Results.Forbid();
    }
})
.WithName("CreateApiKey")
.WithDescription("Creates a new API key for the tenant")
.RequireAuthorization()
.Produces<ApiKeyCreateResult>(StatusCodes.Status201Created)
.Produces(StatusCodes.Status400BadRequest)
.Produces(StatusCodes.Status401Unauthorized)
.Produces(StatusCodes.Status403Forbidden);

// =============================================================================
// GET /api-keys - List API keys
// =============================================================================
app.MapGet("/api-keys", async (
    bool? includeRevoked,
    ClaimsPrincipal user,
    IApiKeyService apiKeyService,
    CancellationToken ct) =>
{
    var (tenantId, _, _) = GetTenantContext(user, selfHostedMode);
    var result = await apiKeyService.ListApiKeysAsync(tenantId, includeRevoked ?? false, ct);
    return Results.Ok(result);
})
.WithName("ListApiKeys")
.WithDescription("Lists all API keys for the tenant (secrets not included)")
.RequireAuthorization()
.Produces<IReadOnlyList<ApiKeyInfo>>(StatusCodes.Status200OK)
.Produces(StatusCodes.Status401Unauthorized);

// =============================================================================
// GET /api-keys/{id} - Get single API key
// =============================================================================
app.MapGet("/api-keys/{id:guid}", async (
    Guid id,
    ClaimsPrincipal user,
    IApiKeyService apiKeyService,
    CancellationToken ct) =>
{
    var (tenantId, _, _) = GetTenantContext(user, selfHostedMode);
    var result = await apiKeyService.GetApiKeyAsync(tenantId, id, ct);

    if (result == null)
        return Results.NotFound(new { error = "not_found", message = "API key not found" });

    return Results.Ok(result);
})
.WithName("GetApiKey")
.WithDescription("Gets details for a specific API key")
.RequireAuthorization()
.Produces<ApiKeyInfo>(StatusCodes.Status200OK)
.Produces(StatusCodes.Status401Unauthorized)
.Produces(StatusCodes.Status404NotFound);

// =============================================================================
// DELETE /api-keys/{id} - Revoke API key
// =============================================================================
app.MapDelete("/api-keys/{id:guid}", async (
    Guid id,
    ClaimsPrincipal user,
    IApiKeyService apiKeyService,
    CancellationToken ct) =>
{
    var (tenantId, userId, _) = GetTenantContext(user, selfHostedMode);
    var revoked = await apiKeyService.RevokeApiKeyAsync(tenantId, id, userId, ct);

    if (!revoked)
        return Results.NotFound(new { error = "not_found", message = "API key not found or already revoked" });

    return Results.Ok(new { message = "API key revoked successfully", keyId = id });
})
.WithName("RevokeApiKey")
.WithDescription("Revokes an API key (soft delete)")
.RequireAuthorization()
.Produces(StatusCodes.Status200OK)
.Produces(StatusCodes.Status401Unauthorized)
.Produces(StatusCodes.Status404NotFound);

// =============================================================================
// GET /tenant/limits - Get tenant limits for SDK error messages
// =============================================================================
app.MapGet("/tenant/limits", async (
    ClaimsPrincipal user,
    IApiKeyService apiKeyService,
    CancellationToken ct) =>
{
    var (tenantId, _, workspaceId) = GetTenantContext(user, selfHostedMode);
    var result = await apiKeyService.GetTenantLimitsAsync(tenantId, workspaceId, ct);
    return Results.Ok(result);
})
.WithName("GetTenantLimits")
.WithDescription("Gets plan limits and current usage for SDK error messages")
.RequireAuthorization()
.Produces<TenantLimitsResult>(StatusCodes.Status200OK)
.Produces(StatusCodes.Status401Unauthorized);

// =============================================================================
// BILLING ENDPOINTS
// =============================================================================

// GET /billing - Get billing summary
app.MapGet("/billing", async (
    ClaimsPrincipal user,
    IBillingService billingService,
    CancellationToken ct) =>
{
    var (tenantId, _, _) = GetTenantContext(user, selfHostedMode);

    if (selfHostedMode)
        return Results.Ok(new { plan = "self-hosted", message = "Billing not applicable in self-hosted mode" });

    var result = await billingService.GetBillingSummaryAsync(tenantId.ToString(), ct);
    return result != null
        ? Results.Ok(result)
        : Results.NotFound(new { error = "not_found", message = "No billing info found" });
})
.WithName("GetBillingSummary")
.WithDescription("Gets billing summary including current plan and payment method")
.RequireAuthorization()
.Produces<BillingSummary>(StatusCodes.Status200OK)
.Produces(StatusCodes.Status401Unauthorized)
.Produces(StatusCodes.Status404NotFound);

// GET /billing/history - Get payment history
app.MapGet("/billing/history", async (
    int? limit,
    ClaimsPrincipal user,
    IBillingService billingService,
    CancellationToken ct) =>
{
    var (tenantId, _, _) = GetTenantContext(user, selfHostedMode);

    if (selfHostedMode)
        return Results.Ok(Array.Empty<object>());

    var result = await billingService.GetPaymentHistoryAsync(tenantId.ToString(), limit ?? 10, ct);
    return Results.Ok(result);
})
.WithName("GetPaymentHistory")
.WithDescription("Gets payment history for the tenant")
.RequireAuthorization()
.Produces<IReadOnlyList<PaymentRecord>>(StatusCodes.Status200OK)
.Produces(StatusCodes.Status401Unauthorized);

// POST /billing/checkout - Create Stripe checkout session
app.MapPost("/billing/checkout", async (
    [FromBody] CheckoutRequest request,
    ClaimsPrincipal user,
    IBillingService billingService,
    CancellationToken ct) =>
{
    var (tenantId, _, _) = GetTenantContext(user, selfHostedMode);

    if (selfHostedMode)
        return Results.BadRequest(new { error = "not_allowed", message = "Billing not available in self-hosted mode" });

    var result = await billingService.CreateCheckoutSessionAsync(new CreateCheckoutRequest
    {
        TenantId = tenantId.ToString(),
        PlanName = request.PlanName,
        SuccessUrl = request.SuccessUrl,
        CancelUrl = request.CancelUrl
    }, ct);

    if (!result.Success)
        return Results.BadRequest(new { error = result.ErrorCode, message = result.ErrorMessage });

    return Results.Ok(new { sessionId = result.SessionId, checkoutUrl = result.CheckoutUrl });
})
.WithName("CreateCheckoutSession")
.WithDescription("Creates a Stripe checkout session for upgrading to a paid plan")
.RequireAuthorization("Admin")
.Produces(StatusCodes.Status200OK)
.Produces(StatusCodes.Status400BadRequest)
.Produces(StatusCodes.Status401Unauthorized);

// POST /billing/portal - Create Stripe customer portal session
app.MapPost("/billing/portal", async (
    [FromBody] PortalRequest? request,
    ClaimsPrincipal user,
    IBillingService billingService,
    CancellationToken ct) =>
{
    var (tenantId, _, _) = GetTenantContext(user, selfHostedMode);

    if (selfHostedMode)
        return Results.BadRequest(new { error = "not_allowed", message = "Billing not available in self-hosted mode" });

    var result = await billingService.CreatePortalSessionAsync(new CreatePortalRequest
    {
        TenantId = tenantId.ToString(),
        ReturnUrl = request?.ReturnUrl
    }, ct);

    if (!result.Success)
        return Results.BadRequest(new { error = result.ErrorCode, message = result.ErrorMessage });

    return Results.Ok(new { portalUrl = result.PortalUrl });
})
.WithName("CreatePortalSession")
.WithDescription("Creates a Stripe customer portal session for managing billing")
.RequireAuthorization("Admin")
.Produces(StatusCodes.Status200OK)
.Produces(StatusCodes.Status400BadRequest)
.Produces(StatusCodes.Status401Unauthorized);

// POST /billing/cancel - Cancel subscription at period end
app.MapPost("/billing/cancel", async (
    ClaimsPrincipal user,
    IBillingService billingService,
    CancellationToken ct) =>
{
    var (tenantId, _, _) = GetTenantContext(user, selfHostedMode);

    if (selfHostedMode)
        return Results.BadRequest(new { error = "not_allowed", message = "Billing not available in self-hosted mode" });

    var result = await billingService.CancelSubscriptionAsync(tenantId.ToString(), ct);

    if (!result.Success)
        return Results.BadRequest(new { error = result.ErrorCode, message = result.ErrorMessage });

    return Results.Ok(new { message = "Subscription will be cancelled at the end of the billing period" });
})
.WithName("CancelSubscription")
.WithDescription("Cancels the subscription at the end of the current billing period")
.RequireAuthorization("Owner")
.Produces(StatusCodes.Status200OK)
.Produces(StatusCodes.Status400BadRequest)
.Produces(StatusCodes.Status401Unauthorized);

// POST /billing/resume - Resume cancelled subscription
app.MapPost("/billing/resume", async (
    ClaimsPrincipal user,
    IBillingService billingService,
    CancellationToken ct) =>
{
    var (tenantId, _, _) = GetTenantContext(user, selfHostedMode);

    if (selfHostedMode)
        return Results.BadRequest(new { error = "not_allowed", message = "Billing not available in self-hosted mode" });

    var result = await billingService.ResumeSubscriptionAsync(tenantId.ToString(), ct);

    if (!result.Success)
        return Results.BadRequest(new { error = result.ErrorCode, message = result.ErrorMessage });

    return Results.Ok(new { message = "Subscription resumed" });
})
.WithName("ResumeSubscription")
.WithDescription("Resumes a cancelled subscription")
.RequireAuthorization("Owner")
.Produces(StatusCodes.Status200OK)
.Produces(StatusCodes.Status400BadRequest)
.Produces(StatusCodes.Status401Unauthorized);

// POST /webhook/stripe - Stripe webhook endpoint (no auth - uses signature verification)
app.MapPost("/webhook/stripe", async (
    HttpRequest request,
    IBillingService billingService,
    ILogger<Program> logger,
    CancellationToken ct) =>
{
    var payload = await new StreamReader(request.Body).ReadToEndAsync(ct);
    var signature = request.Headers["Stripe-Signature"].FirstOrDefault();

    if (string.IsNullOrEmpty(signature))
    {
        logger.LogWarning("Stripe webhook received without signature");
        return Results.BadRequest(new { error = "missing_signature" });
    }

    var result = await billingService.ProcessWebhookAsync(payload, signature, ct);

    if (!result.Success && !result.AlreadyProcessed)
    {
        Metrics.StripeFailuresTotal.Add(1, new TagList { { "type", result.ErrorCode ?? "webhook_error" } });
        logger.LogError("Stripe webhook processing failed: {Error}", result.ErrorMessage);
        return Results.BadRequest(new { error = result.ErrorCode, message = result.ErrorMessage });
    }

    // Track successful payment events
    if (result.EventType?.Contains("payment_intent.succeeded") == true ||
        result.EventType?.Contains("invoice.paid") == true)
    {
        Metrics.StripePaymentsTotal.Add(1);
    }

    return Results.Ok(new { received = true, eventType = result.EventType, alreadyProcessed = result.AlreadyProcessed });
})
.WithName("StripeWebhook")
.WithDescription("Stripe webhook endpoint for subscription events")
.AllowAnonymous()
.Produces(StatusCodes.Status200OK)
.Produces(StatusCodes.Status400BadRequest);

// =============================================================================
// ADMIN ENDPOINTS (Operator Observability)
// =============================================================================

// GET /admin/tenants - List tenants with optional status filter
app.MapGet("/admin/tenants", async (
    string? status,
    int? limit,
    int? offset,
    IAdminService adminService,
    CancellationToken ct) =>
{
    var statusFilter = status?.ToLowerInvariant() switch
    {
        "active" => TenantStatusFilter.Active,
        "pending_deletion" => TenantStatusFilter.PendingDeletion,
        "blocked" => TenantStatusFilter.Blocked,
        _ => TenantStatusFilter.All
    };

    var result = await adminService.ListTenantsAsync(statusFilter, limit ?? 50, offset ?? 0, ct);
    return Results.Ok(result);
})
.WithName("AdminListTenants")
.WithDescription("Lists all tenants with optional status filter (admin only)")
.RequireAuthorization("Owner")
.Produces<TenantListResult>(StatusCodes.Status200OK)
.Produces(StatusCodes.Status401Unauthorized)
.Produces(StatusCodes.Status403Forbidden);

// GET /admin/tenant/{id}/health - Get detailed tenant health
app.MapGet("/admin/tenant/{id:guid}/health", async (
    Guid id,
    IAdminService adminService,
    CancellationToken ct) =>
{
    var result = await adminService.GetTenantHealthAsync(id, ct);

    if (result == null)
        return Results.NotFound(new { error = "not_found", message = "Tenant not found" });

    return Results.Ok(result);
})
.WithName("AdminGetTenantHealth")
.WithDescription("Gets detailed health information for a specific tenant (admin only)")
.RequireAuthorization("Owner")
.Produces<TenantHealthResult>(StatusCodes.Status200OK)
.Produces(StatusCodes.Status401Unauthorized)
.Produces(StatusCodes.Status403Forbidden)
.Produces(StatusCodes.Status404NotFound);

// GET /admin/system/health - Get system-wide health
app.MapGet("/admin/system/health", async (
    IAdminService adminService,
    CancellationToken ct) =>
{
    var result = await adminService.GetSystemHealthAsync(ct);
    return Results.Ok(result);
})
.WithName("AdminGetSystemHealth")
.WithDescription("Gets system-wide health and statistics (admin only)")
.RequireAuthorization("Owner")
.Produces<SystemHealthResult>(StatusCodes.Status200OK)
.Produces(StatusCodes.Status401Unauthorized)
.Produces(StatusCodes.Status403Forbidden);

// =============================================================================
// Health check
// =============================================================================
app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTimeOffset.UtcNow }))
.WithName("HealthCheck")
.ExcludeFromDescription();

app.Run();

// Request DTOs
public sealed record DeleteTenantRequest(string ConfirmationPhrase);
public sealed record CheckoutRequest(string PlanName, string? SuccessUrl = null, string? CancelUrl = null);
public sealed record PortalRequest(string? ReturnUrl = null);

// Auth DTOs
public sealed record VerifyEmailRequest(string Token);
public sealed record ResendVerificationRequest(string Email);
public sealed record MagicLinkRequest(string Email);
public sealed record VerifyMagicLinkRequest(string Token);
