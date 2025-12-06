using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using SerialMemory.Core.Deployment;
using SerialMemory.Core.Interfaces;
using SerialMemory.Core.Models;
using SerialMemory.EventSourcing.Store;
using SerialMemory.Infrastructure;
using SerialMemory.Infrastructure.Billing;
using SerialMemory.Infrastructure.Email;
using SerialMemory.Infrastructure.KillSwitch;
using SerialMemory.Infrastructure.Rag;
using SerialMemory.Infrastructure.Reasoning;
using SerialMemory.Infrastructure.Services;
using SerialMemory.ML;
using SerialMemory.Api.Realtime;

namespace SerialMemory.Api.Dashboard;

/// <summary>
/// Shared helper methods for Dashboard endpoints.
/// </summary>
public static class DashboardHelpers
{
    /// <summary>
    /// Default tenant ID used in self-hosted mode.
    /// </summary>
    public static readonly Guid SelfHostedTenantId = Guid.Parse("00000000-0000-0000-0000-000000000000");

    /// <summary>
    /// Extracts the tenant ID from the user's claims or returns the self-hosted default.
    /// </summary>
    public static Guid GetTenantId(ClaimsPrincipal user, bool selfHosted)
    {
        if (selfHosted)
            return SelfHostedTenantId;

        var tenantIdClaim = user.FindFirst("tenant_id")?.Value
            ?? throw new UnauthorizedAccessException("Missing tenant_id claim");

        return Guid.Parse(tenantIdClaim);
    }

    /// <summary>
    /// Extracts full tenant context including user ID and workspace.
    /// </summary>
    public static (Guid TenantId, string UserId, string WorkspaceId) GetTenantContext(ClaimsPrincipal user, bool selfHosted)
    {
        if (selfHosted)
            return (SelfHostedTenantId, "self-hosted", "default");

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
}

/// <summary>
/// Extension methods to add Dashboard API services and endpoints to the main API.
/// This consolidates what was previously in SerialMemory.Api.Dashboard into the main API.
/// </summary>
public static class DashboardExtensions
{
    /// <summary>
    /// Adds Dashboard-specific services that may not be registered in the main API.
    /// Call this after other service registrations.
    /// </summary>
    public static IServiceCollection AddDashboardServices(this IServiceCollection services, IConfiguration configuration, string connectionString)
    {
        var jwtSecret = configuration["JWT_SECRET"] ?? Environment.GetEnvironmentVariable("JWT_SECRET") ?? "dev-secret-32chars!!";
        var jwtIssuer = configuration["JWT_ISSUER"] ?? Environment.GetEnvironmentVariable("JWT_ISSUER") ?? "serialmemory";
        var jwtAudience = configuration["JWT_AUDIENCE"] ?? Environment.GetEnvironmentVariable("JWT_AUDIENCE") ?? "serialmemory-api";

        // Tenant Dashboard Service - provides /me, /tenant/usage, /tenant/plan endpoints
        services.AddSingleton<ITenantDashboardService>(sp =>
            new TenantDashboardService(connectionString, sp.GetRequiredService<ILogger<TenantDashboardService>>()));

        // Admin Service - provides /admin/* endpoints
        services.AddSingleton<IAdminService>(sp =>
            new AdminService(connectionString, sp.GetRequiredService<ILogger<AdminService>>()));

        // Internal connection factory for RLS bypass (used by Dashboard endpoints)
        var dataSource = services.BuildServiceProvider().GetService<NpgsqlDataSource>();
        if (dataSource != null)
        {
            services.AddSingleton<IInternalDbConnectionFactory>(sp =>
                new InternalDbConnectionFactory(
                    sp.GetRequiredService<NpgsqlDataSource>(),
                    sp.GetRequiredService<ILogger<InternalDbConnectionFactory>>()));
        }

        // Event writer for event sourcing
        services.AddSingleton<IEventWriter>(sp =>
            new EventWriter(
                sp.GetRequiredService<NpgsqlDataSource>(),
                sp.GetRequiredService<ILogger<EventWriter>>()));

        // Kill switch service
        services.AddSingleton<IKillSwitchService>(sp => new KillSwitchService(
            sp.GetRequiredService<NpgsqlDataSource>(),
            sp.GetRequiredService<IDeploymentContext>(),
            sp.GetRequiredService<ILogger<KillSwitchService>>()));

        // Email verification and onboarding services
        services.AddSingleton<IDeveloperOnboardingService>(sp =>
            new Infrastructure.Onboarding.DeveloperOnboardingService(
                configuration,
                sp.GetRequiredService<IEmailService>(),
                sp.GetRequiredService<ILogger<Infrastructure.Onboarding.DeveloperOnboardingService>>()));
        services.AddSingleton<IEmailVerificationService, EmailVerificationService>();

        // L2 Embedding service for RAG indexing
        services.AddSingleton<IL2EmbeddingService>(sp =>
            new L2EmbeddingService(
                sp.GetRequiredService<NpgsqlDataSource>(),
                sp.GetRequiredService<IEmbeddingService>(),
                sp.GetRequiredService<ILogger<L2EmbeddingService>>()));

        // Personalized RAG service
        services.AddSingleton<IPersonalizedRagService>(sp =>
            new PersonalizedRagService(
                sp.GetRequiredService<NpgsqlDataSource>(),
                sp.GetRequiredService<IEmbeddingService>(),
                sp.GetRequiredService<ILlmService>(),
                sp.GetRequiredService<ILogger<PersonalizedRagService>>()));

        // Reasoning hub broadcaster for SignalR events
        services.AddSingleton<ReasoningHubBroadcaster>();

        // Reasoning run service (orchestrates and persists reasoning)
        services.AddSingleton<IReasoningRunService>(sp =>
        {
            var service = new ReasoningRunService(
                sp.GetRequiredService<IInternalDbConnectionFactory>(),
                sp.GetRequiredService<IMultiModelReasoningService>(),
                sp.GetRequiredService<ILogger<ReasoningRunService>>());

            // Wire up SignalR broadcasting
            var broadcaster = sp.GetRequiredService<ReasoningHubBroadcaster>();
            service.OnTraceAdded += async (executionId, trace) =>
            {
                await broadcaster.BroadcastNewStepAddedAsync(executionId, trace.ExecutionId != Guid.Empty ? trace.ExecutionId : executionId, trace);
            };
            service.OnExecutionCompleted += async (executionId, execution) =>
            {
                await broadcaster.BroadcastExecutionCompletedAsync(executionId, execution.TenantId, execution);
            };

            return service;
        });

        // Conflict scanner service
        services.AddSingleton<IConflictScanner>(sp =>
            new ConflictScannerService(
                sp.GetRequiredService<NpgsqlDataSource>(),
                sp.GetRequiredService<ILogger<ConflictScannerService>>()));

        // Admin status service
        var selfHostedMode = configuration["SERIALMEMORY_MODE"]?.ToLowerInvariant() == "self-hosted";
        services.AddSingleton<IAdminStatusService>(sp =>
            new AdminStatusService(
                sp.GetRequiredService<NpgsqlDataSource>(),
                sp.GetRequiredService<ILogger<AdminStatusService>>(),
                selfHostedMode));

        return services;
    }

    /// <summary>
    /// Maps all Dashboard API endpoints to the application.
    /// </summary>
    public static WebApplication MapDashboardEndpoints(this WebApplication app)
    {
        var selfHostedMode = Environment.GetEnvironmentVariable("SERIALMEMORY_MODE")?.ToLowerInvariant() == "self-hosted";

        // Core tenant endpoints
        MapTenantEndpoints(app, selfHostedMode);

        // Auth endpoints (signup, verify-email, magic-link)
        MapDashboardAuthEndpoints(app, selfHostedMode);

        // Billing endpoints
        MapBillingEndpoints(app, selfHostedMode);

        // Admin endpoints
        MapSystemAdminEndpoints(app, selfHostedMode);

        // Dashboard feature endpoints
        app.MapControlRoomEndpoints(selfHostedMode);
        app.MapRagEndpoints(selfHostedMode);
        app.MapReasoningEndpoints(selfHostedMode);
        app.MapTracesEndpoints(selfHostedMode);
        app.MapConflictsEndpoints(selfHostedMode);
        app.MapMutationsEndpoints(selfHostedMode);
        app.MapMindHealthEndpoints(selfHostedMode);
        app.MapPrivacyEndpoints(selfHostedMode);
        app.MapTimelineEndpoints(selfHostedMode);
        app.MapBranchesEndpoints(selfHostedMode);
        app.MapApiKeysEndpoints(selfHostedMode);
        app.MapAdminEndpoints(selfHostedMode);

        return app;
    }

    private static (Guid TenantId, string UserId, string WorkspaceId) GetTenantContext(ClaimsPrincipal user, bool selfHosted)
        => DashboardHelpers.GetTenantContext(user, selfHosted);

    private static void MapTenantEndpoints(WebApplication app, bool selfHostedMode)
    {
        // GET /me - Current user info
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
        .RequireAuthorization();

        // GET /tenant/usage
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
        .RequireAuthorization();

        // GET /tenant/plan
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
        .RequireAuthorization();

        // POST /tenant/export
        app.MapPost("/tenant/export", async (
            ClaimsPrincipal user,
            [FromBody] ExportRequestOptions? options,
            ITenantDashboardService dashboardService,
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
        .RequireAuthorization("Member");

        // GET /tenant/export/{id}
        app.MapGet("/tenant/export/{id:guid}", async (
            Guid id,
            ClaimsPrincipal user,
            ITenantDashboardService dashboardService,
            CancellationToken ct) =>
        {
            var (tenantId, _, _) = GetTenantContext(user, selfHostedMode);
            var result = await dashboardService.GetExportStatusAsync(tenantId, id, ct);
            return result == null
                ? Results.NotFound(new { error = "not_found", message = "Export request not found" })
                : Results.Ok(result);
        })
        .WithName("GetExportStatus")
        .RequireAuthorization();

        // DELETE /tenant
        app.MapDelete("/tenant", async (
            ClaimsPrincipal user,
            [FromBody] DeleteTenantRequest request,
            ITenantDashboardService dashboardService,
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
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = "invalid_confirmation", message = ex.Message }); }
        })
        .WithName("RequestDeletion")
        .RequireAuthorization("Owner");

        // GET /tenant/deletion
        app.MapGet("/tenant/deletion", async (
            ClaimsPrincipal user,
            ITenantDashboardService dashboardService,
            CancellationToken ct) =>
        {
            var (tenantId, _, _) = GetTenantContext(user, selfHostedMode);
            var result = await dashboardService.GetDeletionStatusAsync(tenantId, ct);
            return result == null
                ? Results.NotFound(new { error = "not_found", message = "No deletion request found" })
                : Results.Ok(result);
        })
        .WithName("GetDeletionStatus")
        .RequireAuthorization();

        // POST /tenant/deletion/cancel
        app.MapPost("/tenant/deletion/cancel", async (
            ClaimsPrincipal user,
            ITenantDashboardService dashboardService,
            CancellationToken ct) =>
        {
            var (tenantId, userId, _) = GetTenantContext(user, selfHostedMode);
            try
            {
                var cancelled = await dashboardService.CancelDeletionAsync(tenantId, userId, ct);
                return !cancelled
                    ? Results.NotFound(new { error = "not_found", message = "No pending deletion request found" })
                    : Results.Ok(new { message = "Deletion cancelled successfully" });
            }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
        })
        .WithName("CancelDeletion")
        .RequireAuthorization("Admin");

        // GET /tenant/limits
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
        .RequireAuthorization();
    }

    private static void MapDashboardAuthEndpoints(WebApplication app, bool selfHostedMode)
    {
        // POST /signup
        app.MapPost("/signup", async (
            [FromBody] SignupRequest request,
            IApiKeyService apiKeyService,
            IEmailVerificationService emailService,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            if (selfHostedMode)
                return Results.BadRequest(new { error = "not_allowed", message = "Signup not available in self-hosted mode" });

            try
            {
                var result = await apiKeyService.SignupAsync(request, ct);
                Core.Telemetry.Metrics.TenantSignupTotal.Add(1);

                try
                {
                    var ipAddress = httpContext.Connection.RemoteIpAddress?.ToString();
                    var userAgent = httpContext.Request.Headers.UserAgent.FirstOrDefault();
                    await emailService.SendVerificationEmailAsync(
                        result.TenantId, result.UserId, request.Email,
                        ipAddress, userAgent, result.ApiKey.Key, ct);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WARN] Failed to send verification email to {request.Email}: {ex.Message}");
                }

                return Results.Created($"/tenant/{result.TenantId}", result);
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = "validation_error", message = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = "duplicate", message = ex.Message }); }
        })
        .WithName("DashboardSignup")
        .AllowAnonymous();

        // POST /auth/verify-email
        app.MapPost("/auth/verify-email", async (
            [FromBody] VerifyEmailRequest request,
            IEmailVerificationService emailService,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Token))
                return Results.BadRequest(new { error = "validation_error", message = "Token is required" });

            var ipAddress = httpContext.Connection.RemoteIpAddress?.ToString();
            var result = await emailService.VerifyEmailAsync(request.Token, ipAddress, ct);

            return !result.Success
                ? Results.BadRequest(new { error = "verification_failed", message = result.Error })
                : Results.Ok(new { success = true, message = "Email verified successfully", tenantId = result.TenantId, userId = result.UserId });
        })
        .WithName("DashboardVerifyEmail")
        .AllowAnonymous();

        // POST /auth/resend-verification
        app.MapPost("/auth/resend-verification", async (
            [FromBody] ResendVerificationRequest request,
            IEmailVerificationService emailService,
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
            catch { /* Swallow - don't reveal if email exists */ }

            return Results.Ok(new { message = "If that email exists, a verification link has been sent" });
        })
        .WithName("DashboardResendVerification")
        .AllowAnonymous();

        // POST /auth/magic-link
        app.MapPost("/auth/magic-link", async (
            [FromBody] MagicLinkRequest request,
            IEmailVerificationService emailService,
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
                return Results.StatusCode(429);
            }
            catch { /* Swallow - don't reveal if email exists */ }

            return Results.Ok(new { message = "If that email exists, a login link has been sent" });
        })
        .WithName("DashboardRequestMagicLink")
        .AllowAnonymous();

        // POST /auth/magic-link/verify
        app.MapPost("/auth/magic-link/verify", async (
            [FromBody] VerifyMagicLinkRequest request,
            IEmailVerificationService emailService,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Token))
                return Results.BadRequest(new { error = "validation_error", message = "Token is required" });

            var ipAddress = httpContext.Connection.RemoteIpAddress?.ToString();
            var result = await emailService.ValidateLoginTokenAsync(request.Token, ipAddress, ct);

            return !result.Success
                ? Results.BadRequest(new { error = "login_failed", message = result.Error })
                : Results.Ok(new
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
        .WithName("DashboardVerifyMagicLink")
        .AllowAnonymous();
    }

    private static void MapBillingEndpoints(WebApplication app, bool selfHostedMode)
    {
        // GET /billing
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
        .WithName("DashboardGetBillingSummary")
        .RequireAuthorization();

        // GET /billing/history
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
        .WithName("DashboardGetPaymentHistory")
        .RequireAuthorization();

        // POST /billing/checkout
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

            return !result.Success
                ? Results.BadRequest(new { error = result.ErrorCode, message = result.ErrorMessage })
                : Results.Ok(new { sessionId = result.SessionId, checkoutUrl = result.CheckoutUrl });
        })
        .WithName("DashboardCreateCheckoutSession")
        .RequireAuthorization("Member");

        // POST /billing/portal
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

            return !result.Success
                ? Results.BadRequest(new { error = result.ErrorCode, message = result.ErrorMessage })
                : Results.Ok(new { portalUrl = result.PortalUrl });
        })
        .WithName("DashboardCreatePortalSession")
        .RequireAuthorization("Member");

        // POST /billing/cancel
        app.MapPost("/billing/cancel", async (
            ClaimsPrincipal user,
            IBillingService billingService,
            CancellationToken ct) =>
        {
            var (tenantId, _, _) = GetTenantContext(user, selfHostedMode);
            if (selfHostedMode)
                return Results.BadRequest(new { error = "not_allowed", message = "Billing not available in self-hosted mode" });

            var result = await billingService.CancelSubscriptionAsync(tenantId.ToString(), ct);
            return !result.Success
                ? Results.BadRequest(new { error = result.ErrorCode, message = result.ErrorMessage })
                : Results.Ok(new { message = "Subscription will be cancelled at the end of the billing period" });
        })
        .WithName("DashboardCancelSubscription")
        .RequireAuthorization("Owner");

        // POST /billing/resume
        app.MapPost("/billing/resume", async (
            ClaimsPrincipal user,
            IBillingService billingService,
            CancellationToken ct) =>
        {
            var (tenantId, _, _) = GetTenantContext(user, selfHostedMode);
            if (selfHostedMode)
                return Results.BadRequest(new { error = "not_allowed", message = "Billing not available in self-hosted mode" });

            var result = await billingService.ResumeSubscriptionAsync(tenantId.ToString(), ct);
            return !result.Success
                ? Results.BadRequest(new { error = result.ErrorCode, message = result.ErrorMessage })
                : Results.Ok(new { message = "Subscription resumed" });
        })
        .WithName("DashboardResumeSubscription")
        .RequireAuthorization("Owner");

        // POST /webhook/stripe
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
                logger.LogError("Stripe webhook processing failed: {Error}", result.ErrorMessage);
                return Results.BadRequest(new { error = result.ErrorCode, message = result.ErrorMessage });
            }

            return Results.Ok(new { received = true, eventType = result.EventType, alreadyProcessed = result.AlreadyProcessed });
        })
        .WithName("StripeWebhook")
        .AllowAnonymous();
    }

    private static void MapSystemAdminEndpoints(WebApplication app, bool selfHostedMode)
    {
        // GET /admin/tenants
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
        .RequireAuthorization("Owner");

        // GET /admin/tenant/{id}/health
        app.MapGet("/admin/tenant/{id:guid}/health", async (
            Guid id,
            IAdminService adminService,
            CancellationToken ct) =>
        {
            var result = await adminService.GetTenantHealthAsync(id, ct);
            return result == null
                ? Results.NotFound(new { error = "not_found", message = "Tenant not found" })
                : Results.Ok(result);
        })
        .WithName("AdminGetTenantHealth")
        .RequireAuthorization("Owner");

        // GET /admin/system/health
        app.MapGet("/admin/system/health", async (
            IAdminService adminService,
            CancellationToken ct) =>
        {
            var result = await adminService.GetSystemHealthAsync(ct);
            return Results.Ok(result);
        })
        .WithName("AdminGetSystemHealth")
        .RequireAuthorization("Owner");
    }
}

// Request DTOs
public sealed record DeleteTenantRequest(string ConfirmationPhrase);
public sealed record CheckoutRequest(string PlanName, string? SuccessUrl = null, string? CancelUrl = null);
public sealed record PortalRequest(string? ReturnUrl = null);
public sealed record VerifyEmailRequest(string Token);
public sealed record ResendVerificationRequest(string Email);
public sealed record MagicLinkRequest(string Email);
public sealed record VerifyMagicLinkRequest(string Token);
