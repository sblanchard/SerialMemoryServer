using System.Security.Claims;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using SerialMemory.Core.Interfaces;
using SerialMemory.Infrastructure;

namespace SerialMemory.Api.Dashboard;

/// <summary>
/// Dashboard endpoints for Billing and Subscription management.
/// FIX #10: "Failed to create checkout session (404)" + missing Pro Plus plan.
/// Fixed Stripe price IDs, added Pro Plus and Team plans.
/// </summary>
public static class BillingEndpoints
{
    public static void MapBillingEndpoints(this WebApplication app, bool selfHostedMode)
    {
        var group = app.MapGroup("/billing")
            .WithTags("Billing")
            .RequireAuthorization();

        // =============================================================================
        // GET /billing/plans - Get available plans (FIX #10)
        // =============================================================================
        group.MapGet("/plans", async (
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            CancellationToken ct) =>
        {
            var tenantId = GetTenantId(user, selfHostedMode);

            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.SetInternalAdminWithTenantAsync(tenantId);

            var plans = await conn.QueryAsync<PlanDto>(
                """
                SELECT
                    tp.plan_name AS planName,
                    tp.display_name AS displayName,
                    tp.credits_per_cycle AS creditsPerCycle,
                    tp.cycle_days AS cycleDays,
                    tp.max_memories AS maxMemories,
                    tp.max_entities AS maxEntities,
                    tp.rate_limit_rpm AS rateLimitRpm,
                    tp.rate_limit_rpd AS rateLimitRpd,
                    tp.features,
                    spm.stripe_price_id AS stripePriceId,
                    spm.stripe_product_id AS stripeProductId
                FROM tenant_plans tp
                LEFT JOIN stripe_price_mapping spm ON tp.plan_name = spm.plan_name AND spm.is_active = TRUE
                WHERE tp.is_active = TRUE
                ORDER BY tp.credits_per_cycle ASC
                """);

            // Add hardcoded plans if not in DB (Pro Plus, Team)
            var planList = plans.ToList();
            EnsurePlanExists(planList, "free", "Free Tier", 1000, 60, 10000, GetFreeFeatures());
            EnsurePlanExists(planList, "pro", "Professional", 50000, 300, 100000, GetProFeatures());
            EnsurePlanExists(planList, "pro_plus", "Pro Plus", 200000, 600, 500000, GetProPlusFeatures());
            EnsurePlanExists(planList, "team", "Enterprise Team", 1000000, 2000, 2000000, GetTeamFeatures());
            EnsurePlanExists(planList, "enterprise", "Enterprise", 10000000, 5000, 10000000, GetEnterpriseFeatures());

            // Get current tenant plan
            var currentPlan = await conn.QueryFirstOrDefaultAsync<string>(
                "SELECT plan FROM tenant_settings WHERE tenant_id = @TenantId",
                new { TenantId = tenantId }) ?? "free";

            return Results.Ok(new
            {
                plans = planList,
                currentPlan
            });
        })
        .WithName("GetBillingPlans")
        .WithDescription("Gets available billing plans")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized);

        // =============================================================================
        // POST /billing/create-checkout-session - Create Stripe checkout session (FIX #10)
        // =============================================================================
        group.MapPost("/create-checkout-session", async (
            [FromBody] CheckoutSessionRequest request,
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            [FromServices] IBillingService? billingService,
            IConfiguration configuration,
            CancellationToken ct) =>
        {
            var tenantId = GetTenantId(user, selfHostedMode);
            var userId = GetUserId(user);

            // Validate plan
            var validPlans = new[] { "pro", "pro_plus", "team", "enterprise" };
            if (!validPlans.Contains(request.Plan?.ToLowerInvariant()))
                return Results.BadRequest(new { error = "invalid_plan", message = $"Invalid plan: {request.Plan}" });

            // Check if SaaS mode is enabled
            var saasMode = configuration.GetValue<bool>("SaasMode", false);
            var stripeSecretKey = configuration["Stripe:SecretKey"];

            if (!saasMode || string.IsNullOrEmpty(stripeSecretKey))
            {
                return Results.BadRequest(new
                {
                    error = "saas_not_enabled",
                    message = "Billing is not available in self-hosted mode"
                });
            }

            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.SetInternalAdminWithTenantAsync(tenantId);

            // Get Stripe price ID for the plan
            var priceMapping = await conn.QueryFirstOrDefaultAsync<StripePriceMappingDto>(
                """
                SELECT stripe_price_id, stripe_product_id
                FROM stripe_price_mapping
                WHERE plan_name = @Plan AND is_active = TRUE
                """,
                new { Plan = request.Plan?.ToLowerInvariant() });

            // Fall back to hardcoded price IDs if not in DB
            var stripePriceId = priceMapping?.stripe_price_id ?? GetFallbackPriceId(request.Plan);

            if (string.IsNullOrEmpty(stripePriceId))
            {
                return Results.BadRequest(new
                {
                    error = "plan_not_configured",
                    message = $"Plan {request.Plan} is not configured for billing"
                });
            }

            // Use billing service if available
            if (billingService != null)
            {
                try
                {
                    var checkoutRequest = new SerialMemory.Core.Models.CreateCheckoutRequest
                    {
                        TenantId = tenantId.ToString(),
                        PlanName = request.Plan!,
                        SuccessUrl = request.SuccessUrl,
                        CancelUrl = request.CancelUrl
                    };
                    var session = await billingService.CreateCheckoutSessionAsync(checkoutRequest, ct);

                    return Results.Ok(new
                    {
                        sessionId = session.SessionId,
                        url = session.CheckoutUrl,
                        plan = request.Plan
                    });
                }
                catch (Exception ex)
                {
                    return Results.Problem(detail: ex.Message, title: "Failed to create checkout session");
                }
            }

            // Manual Stripe integration fallback
            var stripe = new Stripe.StripeClient(stripeSecretKey);

            // Get tenant email
            var tenantEmail = await conn.QueryFirstOrDefaultAsync<string>(
                """
                SELECT tu.user_id
                FROM tenant_users tu
                WHERE tu.tenant_id = @TenantId AND tu.role = 'owner'
                LIMIT 1
                """,
                new { TenantId = tenantId });

            try
            {
                var sessionOptions = new Stripe.Checkout.SessionCreateOptions
                {
                    Mode = "subscription",
                    CustomerEmail = tenantEmail,
                    LineItems = new List<Stripe.Checkout.SessionLineItemOptions>
                    {
                        new()
                        {
                            Price = stripePriceId,
                            Quantity = 1
                        }
                    },
                    SuccessUrl = request.SuccessUrl ?? $"{configuration["DashboardUrl"]}/billing/success?session_id={{CHECKOUT_SESSION_ID}}",
                    CancelUrl = request.CancelUrl ?? $"{configuration["DashboardUrl"]}/billing/cancel",
                    Metadata = new Dictionary<string, string>
                    {
                        ["tenant_id"] = tenantId.ToString(),
                        ["plan"] = request.Plan!
                    }
                };

                var service = new Stripe.Checkout.SessionService(stripe);
                var session = await service.CreateAsync(sessionOptions, cancellationToken: ct);

                return Results.Ok(new
                {
                    sessionId = session.Id,
                    url = session.Url,
                    plan = request.Plan
                });
            }
            catch (Stripe.StripeException ex)
            {
                return Results.Problem(detail: ex.Message, title: "Stripe error", statusCode: 400);
            }
        })
        .WithName("CreateCheckoutSession")
        .WithDescription("Creates a Stripe checkout session for plan upgrade")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized);

        // =============================================================================
        // GET /billing/subscription - Get current subscription details
        // =============================================================================
        group.MapGet("/subscription", async (
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            CancellationToken ct) =>
        {
            var tenantId = GetTenantId(user, selfHostedMode);

            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.SetInternalAdminWithTenantAsync(tenantId);

            var subscription = await conn.QueryFirstOrDefaultAsync<SubscriptionDto>(
                """
                SELECT
                    ts.plan,
                    ts.stripe_customer_id AS stripeCustomerId,
                    ts.stripe_subscription_id AS stripeSubscriptionId,
                    ts.subscription_status AS status,
                    ts.current_period_start AS periodStart,
                    ts.current_period_end AS periodEnd,
                    ts.cancel_at_period_end AS cancelAtPeriodEnd,
                    tp.display_name AS planDisplayName,
                    tp.credits_per_cycle AS creditsPerCycle,
                    tp.features
                FROM tenant_settings ts
                LEFT JOIN tenant_plans tp ON ts.plan = tp.plan_name
                WHERE ts.tenant_id = @TenantId
                """,
                new { TenantId = tenantId });

            // Get current cycle usage
            var usage = await conn.QueryFirstOrDefaultAsync<UsageDto>(
                """
                SELECT credits_allocated, credits_used, cycle_start, cycle_end
                FROM billing_cycles
                WHERE tenant_id = @TenantId AND is_current = TRUE
                """,
                new { TenantId = tenantId.ToString() });

            return Results.Ok(new
            {
                subscription = subscription ?? new SubscriptionDto { plan = "free", planDisplayName = "Free Tier" },
                usage = usage ?? new UsageDto(),
                creditsRemaining = (usage?.credits_allocated ?? 1000) - (usage?.credits_used ?? 0)
            });
        })
        .WithName("GetSubscription")
        .WithDescription("Gets current subscription details")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized);

        // =============================================================================
        // POST /billing/portal-session - Create Stripe customer portal session
        // =============================================================================
        group.MapPost("/portal-session", async (
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            [FromServices] IBillingService? billingService,
            IConfiguration configuration,
            CancellationToken ct) =>
        {
            var tenantId = GetTenantId(user, selfHostedMode);

            var stripeSecretKey = configuration["Stripe:SecretKey"];
            if (string.IsNullOrEmpty(stripeSecretKey))
            {
                return Results.BadRequest(new { error = "stripe_not_configured" });
            }

            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.SetInternalAdminWithTenantAsync(tenantId);

            // Get Stripe customer ID
            var customerId = await conn.QueryFirstOrDefaultAsync<string>(
                "SELECT stripe_customer_id FROM tenant_settings WHERE tenant_id = @TenantId",
                new { TenantId = tenantId });

            if (string.IsNullOrEmpty(customerId))
            {
                return Results.BadRequest(new { error = "no_subscription", message = "No active subscription found" });
            }

            try
            {
                var stripe = new Stripe.StripeClient(stripeSecretKey);
                var service = new Stripe.BillingPortal.SessionService(stripe);

                var session = await service.CreateAsync(new Stripe.BillingPortal.SessionCreateOptions
                {
                    Customer = customerId,
                    ReturnUrl = $"{configuration["DashboardUrl"]}/billing"
                }, cancellationToken: ct);

                return Results.Ok(new { url = session.Url });
            }
            catch (Stripe.StripeException ex)
            {
                return Results.Problem(detail: ex.Message, title: "Stripe error");
            }
        })
        .WithName("CreatePortalSession")
        .WithDescription("Creates a Stripe customer portal session")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized);

        // =============================================================================
        // GET /billing/invoices - Get invoice history
        // =============================================================================
        group.MapGet("/invoices", async (
            int? limit,
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            CancellationToken ct) =>
        {
            var tenantId = GetTenantId(user, selfHostedMode);

            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.SetInternalAdminWithTenantAsync(tenantId);

            var invoices = await conn.QueryAsync<InvoiceDto>(
                """
                SELECT id, invoice_number, amount, currency, status, created_at, paid_at, stripe_invoice_id
                FROM billing_invoices
                WHERE tenant_id = @TenantId
                ORDER BY created_at DESC
                LIMIT @Limit
                """,
                new { TenantId = tenantId, Limit = limit ?? 20 });

            return Results.Ok(new { invoices = invoices.ToList() });
        })
        .WithName("GetInvoices")
        .WithDescription("Gets invoice history")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized);
    }

    // Helper methods for plan features
    private static void EnsurePlanExists(List<PlanDto> plans, string planName, string displayName,
        int credits, int rpm, int rpd, List<string> features)
    {
        if (!plans.Any(p => p.planName == planName))
        {
            plans.Add(new PlanDto
            {
                planName = planName,
                displayName = displayName,
                creditsPerCycle = credits,
                cycleDays = 30,
                rateLimitRpm = rpm,
                rateLimitRpd = rpd,
                features = features
            });
        }
    }

    private static List<string> GetFreeFeatures() => new()
    {
        "1,000 monthly credits",
        "Basic semantic search",
        "1 workspace",
        "Community support"
    };

    private static List<string> GetProFeatures() => new()
    {
        "50,000 monthly credits",
        "Advanced semantic search",
        "5 workspaces",
        "Priority email support",
        "API access"
    };

    private static List<string> GetProPlusFeatures() => new()
    {
        "200,000 monthly credits",
        "Advanced semantic search",
        "10 workspaces",
        "Priority support",
        "Custom integrations",
        "SSO",
        "Dedicated embeddings"
    };

    private static List<string> GetTeamFeatures() => new()
    {
        "1,000,000 monthly credits",
        "Unlimited workspaces",
        "Dedicated support",
        "Custom integrations",
        "SSO",
        "Dedicated infrastructure",
        "SLA 99.9%"
    };

    private static List<string> GetEnterpriseFeatures() => new()
    {
        "Custom credit allocation",
        "Dedicated infrastructure",
        "24/7 premium support",
        "Custom SLA",
        "On-premise deployment option",
        "Advanced security features"
    };

    private static string? GetFallbackPriceId(string? plan) => plan?.ToLowerInvariant() switch
    {
        "pro" => Environment.GetEnvironmentVariable("STRIPE_PRICE_PRO"),
        "pro_plus" => Environment.GetEnvironmentVariable("STRIPE_PRICE_PRO_PLUS"),
        "team" => Environment.GetEnvironmentVariable("STRIPE_PRICE_TEAM"),
        "enterprise" => Environment.GetEnvironmentVariable("STRIPE_PRICE_ENTERPRISE"),
        _ => null
    };

    private static Guid GetTenantId(ClaimsPrincipal user, bool selfHosted)
    {
        if (selfHosted)
            return Guid.Parse("00000000-0000-0000-0000-000000000000");

        var tenantIdClaim = user.FindFirst("tenant_id")?.Value
            ?? throw new UnauthorizedAccessException("Missing tenant_id claim");

        return Guid.Parse(tenantIdClaim);
    }

    private static string GetUserId(ClaimsPrincipal user)
    {
        return user.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? user.FindFirst("sub")?.Value
            ?? "system";
    }
}

// DTOs for Billing endpoints
public sealed class PlanDto
{
    public string planName { get; set; } = "";
    public string displayName { get; set; } = "";
    public decimal creditsPerCycle { get; set; }
    public int cycleDays { get; set; }
    public int? maxMemories { get; set; }
    public int? maxEntities { get; set; }
    public int rateLimitRpm { get; set; }
    public int rateLimitRpd { get; set; }
    public object? features { get; set; }
    public string? stripePriceId { get; set; }
    public string? stripeProductId { get; set; }
}

public sealed class StripePriceMappingDto
{
    public string? stripe_price_id { get; set; }
    public string? stripe_product_id { get; set; }
}

public sealed class SubscriptionDto
{
    public string plan { get; set; } = "free";
    public string? stripeCustomerId { get; set; }
    public string? stripeSubscriptionId { get; set; }
    public string? status { get; set; }
    public DateTimeOffset? periodStart { get; set; }
    public DateTimeOffset? periodEnd { get; set; }
    public bool? cancelAtPeriodEnd { get; set; }
    public string planDisplayName { get; set; } = "";
    public decimal creditsPerCycle { get; set; }
    public object? features { get; set; }
}

public sealed class UsageDto
{
    public decimal credits_allocated { get; set; }
    public decimal credits_used { get; set; }
    public DateTimeOffset? cycle_start { get; set; }
    public DateTimeOffset? cycle_end { get; set; }
}

public sealed class InvoiceDto
{
    public Guid id { get; set; }
    public string? invoice_number { get; set; }
    public decimal amount { get; set; }
    public string currency { get; set; } = "usd";
    public string status { get; set; } = "";
    public DateTimeOffset created_at { get; set; }
    public DateTimeOffset? paid_at { get; set; }
    public string? stripe_invoice_id { get; set; }
}

public sealed record CheckoutSessionRequest(
    string? Plan,
    string? SuccessUrl = null,
    string? CancelUrl = null
);
