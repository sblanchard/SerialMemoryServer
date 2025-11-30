using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using SerialMemory.Core.Interfaces;
using SerialMemory.Core.Models;
using Stripe;
using Stripe.Checkout;

namespace SerialMemory.Infrastructure;

/// <summary>
/// Stripe implementation of IBillingService.
/// Handles checkout sessions, webhooks, and subscription management.
/// </summary>
public sealed class StripeBillingService : IBillingService, IMockableBillingService
{
    private readonly string _connectionString;
    private readonly ILogger<StripeBillingService> _logger;
    private readonly StripeConfig _config;
    private readonly SessionService _checkoutSessionService;
    private readonly CustomerService _customerService;
    private readonly SubscriptionService _subscriptionService;
    private readonly Stripe.BillingPortal.SessionService _portalSessionService;

    public StripeBillingService(
        string connectionString,
        ILogger<StripeBillingService> logger,
        StripeConfig config)
    {
        _connectionString = connectionString;
        _logger = logger;
        _config = config;

        // Configure Stripe
        StripeConfiguration.ApiKey = config.SecretKey;

        _checkoutSessionService = new SessionService();
        _customerService = new CustomerService();
        _subscriptionService = new SubscriptionService();
        _portalSessionService = new Stripe.BillingPortal.SessionService();
    }

    public async Task<CheckoutSessionResult> CreateCheckoutSessionAsync(
        CreateCheckoutRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var priceId = await GetPriceIdForPlanAsync(request.PlanName, cancellationToken);
            if (priceId == null)
            {
                return new CheckoutSessionResult
                {
                    Success = false,
                    ErrorCode = "invalid_plan",
                    ErrorMessage = $"No Stripe price configured for plan: {request.PlanName}"
                };
            }

            // Get or create customer
            var customerId = await GetOrCreateStripeCustomerAsync(request.TenantId, cancellationToken);

            var options = new SessionCreateOptions
            {
                Customer = customerId,
                Mode = "subscription",
                LineItems =
                [
                    new SessionLineItemOptions
                    {
                        Price = priceId,
                        Quantity = 1
                    }
                ],
                SuccessUrl = request.SuccessUrl ?? _config.DefaultSuccessUrl,
                CancelUrl = request.CancelUrl ?? _config.DefaultCancelUrl,
                Metadata = new Dictionary<string, string>
                {
                    ["tenant_id"] = request.TenantId,
                    ["plan_name"] = request.PlanName
                },
                SubscriptionData = new SessionSubscriptionDataOptions
                {
                    Metadata = new Dictionary<string, string>
                    {
                        ["tenant_id"] = request.TenantId,
                        ["plan_name"] = request.PlanName
                    }
                }
            };

            var session = await _checkoutSessionService.CreateAsync(options, cancellationToken: cancellationToken);

            _logger.LogInformation(
                "Created checkout session {SessionId} for tenant {TenantId} plan {PlanName}",
                session.Id, request.TenantId, request.PlanName);

            return new CheckoutSessionResult
            {
                Success = true,
                SessionId = session.Id,
                CheckoutUrl = session.Url
            };
        }
        catch (StripeException ex)
        {
            _logger.LogError(ex, "Stripe error creating checkout session for tenant {TenantId}", request.TenantId);
            return new CheckoutSessionResult
            {
                Success = false,
                ErrorCode = ex.StripeError?.Code ?? "stripe_error",
                ErrorMessage = ex.StripeError?.Message ?? ex.Message
            };
        }
    }

    public async Task<PortalSessionResult> CreatePortalSessionAsync(
        CreatePortalRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var customerId = await GetStripeCustomerIdAsync(request.TenantId, cancellationToken);
            if (customerId == null)
            {
                return new PortalSessionResult
                {
                    Success = false,
                    ErrorCode = "no_customer",
                    ErrorMessage = "No Stripe customer found for this tenant"
                };
            }

            var options = new Stripe.BillingPortal.SessionCreateOptions
            {
                Customer = customerId,
                ReturnUrl = request.ReturnUrl ?? "/dashboard/billing"
            };

            var session = await _portalSessionService.CreateAsync(options, cancellationToken: cancellationToken);

            return new PortalSessionResult
            {
                Success = true,
                PortalUrl = session.Url
            };
        }
        catch (StripeException ex)
        {
            _logger.LogError(ex, "Stripe error creating portal session for tenant {TenantId}", request.TenantId);
            return new PortalSessionResult
            {
                Success = false,
                ErrorCode = ex.StripeError?.Code ?? "stripe_error",
                ErrorMessage = ex.StripeError?.Message ?? ex.Message
            };
        }
    }

    public async Task<WebhookProcessResult> ProcessWebhookAsync(
        string payload,
        string signature,
        CancellationToken cancellationToken = default)
    {
        Event stripeEvent;
        try
        {
            stripeEvent = EventUtility.ConstructEvent(payload, signature, _config.WebhookSecret);
        }
        catch (StripeException ex)
        {
            _logger.LogWarning(ex, "Invalid Stripe webhook signature");
            return new WebhookProcessResult
            {
                Success = false,
                ErrorCode = "invalid_signature",
                ErrorMessage = "Invalid webhook signature"
            };
        }

        // Check for idempotency
        if (await IsEventAlreadyProcessedAsync(stripeEvent.Id, cancellationToken))
        {
            _logger.LogInformation("Webhook event {EventId} already processed, skipping", stripeEvent.Id);
            return new WebhookProcessResult
            {
                Success = true,
                AlreadyProcessed = true,
                EventType = stripeEvent.Type
            };
        }

        // Store the event
        var webhookEvent = await StoreWebhookEventAsync(stripeEvent, payload, cancellationToken);

        try
        {
            var result = stripeEvent.Type switch
            {
                StripeEventTypes.SubscriptionCreated => await HandleSubscriptionCreatedAsync(stripeEvent, cancellationToken),
                StripeEventTypes.SubscriptionUpdated => await HandleSubscriptionUpdatedAsync(stripeEvent, cancellationToken),
                StripeEventTypes.SubscriptionDeleted => await HandleSubscriptionDeletedAsync(stripeEvent, cancellationToken),
                StripeEventTypes.InvoicePaymentFailed => await HandlePaymentFailedAsync(stripeEvent, cancellationToken),
                StripeEventTypes.InvoicePaymentSucceeded => await HandlePaymentSucceededAsync(stripeEvent, cancellationToken),
                StripeEventTypes.CheckoutSessionCompleted => await HandleCheckoutCompletedAsync(stripeEvent, cancellationToken),
                _ => new WebhookProcessResult { Success = true, EventType = stripeEvent.Type }
            };

            // Mark as processed
            await MarkEventProcessedAsync(webhookEvent.Id, null, cancellationToken);

            return result with { EventType = stripeEvent.Type };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing webhook event {EventId} type {EventType}",
                stripeEvent.Id, stripeEvent.Type);

            await MarkEventProcessedAsync(webhookEvent.Id, ex.Message, cancellationToken);

            return new WebhookProcessResult
            {
                Success = false,
                ErrorCode = "processing_error",
                ErrorMessage = ex.Message,
                EventType = stripeEvent.Type
            };
        }
    }

    public async Task<BillingSummary?> GetBillingSummaryAsync(
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        var subscription = await conn.QuerySingleOrDefaultAsync<dynamic>(
            """
            SELECT ts.stripe_customer_id, ts.stripe_subscription_id, ts.stripe_price_id,
                   ts.payment_method_last4, ts.payment_method_brand,
                   tp.plan_name, tp.display_name,
                   bc.cycle_start, bc.cycle_end
            FROM tenant_subscriptions ts
            JOIN tenant_plans tp ON ts.plan_id = tp.id
            LEFT JOIN billing_cycles bc ON bc.tenant_id = ts.tenant_id
                AND bc.workspace_id = ts.workspace_id AND bc.is_current = true
            WHERE ts.tenant_id = @TenantId
            """,
            new { TenantId = tenantId });

        if (subscription == null)
        {
            return null;
        }

        var recentPayments = await GetPaymentHistoryAsync(tenantId, 5, cancellationToken);

        // Get cancellation info from Stripe if we have a subscription
        bool willCancel = false;
        DateTimeOffset? cancelAt = null;
        if (subscription.stripe_subscription_id != null)
        {
            try
            {
                var stripeSub = await _subscriptionService.GetAsync(
                    subscription.stripe_subscription_id,
                    cancellationToken: cancellationToken);
                willCancel = stripeSub.CancelAtPeriodEnd;
                cancelAt = stripeSub.CancelAt;
            }
            catch (StripeException ex)
            {
                _logger.LogWarning(ex, "Could not fetch Stripe subscription details");
            }
        }

        return new BillingSummary
        {
            TenantId = tenantId,
            StripeCustomerId = subscription.stripe_customer_id,
            StripeSubscriptionId = subscription.stripe_subscription_id,
            CurrentPlan = subscription.plan_name ?? "free",
            PaymentMethodLast4 = subscription.payment_method_last4,
            PaymentMethodBrand = subscription.payment_method_brand,
            CurrentPeriodStart = subscription.cycle_start,
            CurrentPeriodEnd = subscription.cycle_end,
            WillCancelAtPeriodEnd = willCancel,
            CancelAt = cancelAt,
            RecentPayments = recentPayments
        };
    }

    public async Task<IReadOnlyList<PaymentRecord>> GetPaymentHistoryAsync(
        string tenantId,
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        var payments = await conn.QueryAsync<PaymentRecord>(
            """
            SELECT id, tenant_id AS TenantId, stripe_customer_id AS StripeCustomerId,
                   stripe_invoice_id AS StripeInvoiceId, stripe_payment_intent_id AS StripePaymentIntentId,
                   amount_cents AS AmountCents, currency AS Currency, status AS Status,
                   plan_name AS PlanName, billing_period_start AS BillingPeriodStart,
                   billing_period_end AS BillingPeriodEnd, failure_reason AS FailureReason,
                   failure_code AS FailureCode, invoice_pdf_url AS InvoicePdfUrl,
                   hosted_invoice_url AS HostedInvoiceUrl, created_at AS CreatedAt, updated_at AS UpdatedAt
            FROM payment_history
            WHERE tenant_id = @TenantId
            ORDER BY created_at DESC
            LIMIT @Limit
            """,
            new { TenantId = tenantId, Limit = limit });

        return payments.ToList();
    }

    public async Task<WebhookProcessResult> CancelSubscriptionAsync(
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        var subscriptionId = await GetStripeSubscriptionIdAsync(tenantId, cancellationToken);
        if (subscriptionId == null)
        {
            return new WebhookProcessResult
            {
                Success = false,
                ErrorCode = "no_subscription",
                ErrorMessage = "No active subscription found"
            };
        }

        try
        {
            var options = new SubscriptionUpdateOptions
            {
                CancelAtPeriodEnd = true
            };

            await _subscriptionService.UpdateAsync(subscriptionId, options, cancellationToken: cancellationToken);

            _logger.LogInformation("Marked subscription {SubscriptionId} for cancellation at period end", subscriptionId);

            return new WebhookProcessResult
            {
                Success = true,
                AffectedTenantId = tenantId
            };
        }
        catch (StripeException ex)
        {
            _logger.LogError(ex, "Failed to cancel subscription for tenant {TenantId}", tenantId);
            return new WebhookProcessResult
            {
                Success = false,
                ErrorCode = ex.StripeError?.Code ?? "stripe_error",
                ErrorMessage = ex.StripeError?.Message ?? ex.Message
            };
        }
    }

    public async Task<WebhookProcessResult> ResumeSubscriptionAsync(
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        var subscriptionId = await GetStripeSubscriptionIdAsync(tenantId, cancellationToken);
        if (subscriptionId == null)
        {
            return new WebhookProcessResult
            {
                Success = false,
                ErrorCode = "no_subscription",
                ErrorMessage = "No subscription found"
            };
        }

        try
        {
            var options = new SubscriptionUpdateOptions
            {
                CancelAtPeriodEnd = false
            };

            await _subscriptionService.UpdateAsync(subscriptionId, options, cancellationToken: cancellationToken);

            _logger.LogInformation("Resumed subscription {SubscriptionId}", subscriptionId);

            return new WebhookProcessResult
            {
                Success = true,
                AffectedTenantId = tenantId
            };
        }
        catch (StripeException ex)
        {
            _logger.LogError(ex, "Failed to resume subscription for tenant {TenantId}", tenantId);
            return new WebhookProcessResult
            {
                Success = false,
                ErrorCode = ex.StripeError?.Code ?? "stripe_error",
                ErrorMessage = ex.StripeError?.Message ?? ex.Message
            };
        }
    }

    public async Task<IReadOnlyList<StripePriceMapping>> GetPriceMappingsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        var mappings = await conn.QueryAsync<StripePriceMapping>(
            """
            SELECT id AS Id, plan_name AS PlanName, stripe_price_id AS StripePriceId,
                   stripe_product_id AS StripeProductId, is_active AS IsActive, created_at AS CreatedAt
            FROM stripe_price_mapping
            ORDER BY plan_name
            """);

        return mappings.ToList();
    }

    public async Task SetPriceMappingAsync(
        string planName,
        string stripePriceId,
        string stripeProductId,
        CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        await conn.ExecuteAsync(
            """
            INSERT INTO stripe_price_mapping (plan_name, stripe_price_id, stripe_product_id)
            VALUES (@PlanName, @StripePriceId, @StripeProductId)
            ON CONFLICT (plan_name) DO UPDATE SET
                stripe_price_id = EXCLUDED.stripe_price_id,
                stripe_product_id = EXCLUDED.stripe_product_id,
                is_active = true,
                updated_at = NOW()
            """,
            new { PlanName = planName, StripePriceId = stripePriceId, StripeProductId = stripeProductId });

        _logger.LogInformation("Set price mapping for plan {PlanName}: {PriceId}", planName, stripePriceId);
    }

    // Mock methods for testing
    public async Task SimulateSubscriptionCreatedAsync(
        string tenantId,
        string planName,
        CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        var planId = await conn.QuerySingleOrDefaultAsync<Guid>(
            "SELECT id FROM tenant_plans WHERE plan_name = @PlanName AND is_active = true",
            new { PlanName = planName });

        if (planId == default)
        {
            throw new ArgumentException($"Plan not found: {planName}");
        }

        await conn.ExecuteAsync(
            """
            UPDATE tenant_subscriptions
            SET plan_id = @PlanId, status = 'active',
                stripe_customer_id = @StripeCustomerId,
                stripe_subscription_id = @StripeSubscriptionId,
                updated_at = NOW()
            WHERE tenant_id = @TenantId
            """,
            new
            {
                TenantId = tenantId,
                PlanId = planId,
                StripeCustomerId = $"cus_mock_{tenantId}",
                StripeSubscriptionId = $"sub_mock_{Guid.NewGuid():N}"
            });

        _logger.LogInformation("Simulated subscription created for tenant {TenantId} plan {PlanName}", tenantId, planName);
    }

    public async Task SimulatePaymentFailedAsync(
        string tenantId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        await conn.ExecuteAsync(
            "SELECT handle_payment_failure(@TenantId, NULL, @Reason)",
            new { TenantId = tenantId, Reason = reason });

        _logger.LogInformation("Simulated payment failure for tenant {TenantId}: {Reason}", tenantId, reason);
    }

    public async Task SimulateSubscriptionCancelledAsync(
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        await conn.ExecuteAsync(
            "SELECT handle_subscription_cancelled(@TenantId, NULL)",
            new { TenantId = tenantId });

        _logger.LogInformation("Simulated subscription cancelled for tenant {TenantId}", tenantId);
    }

    // Private helper methods
    private async Task<string?> GetPriceIdForPlanAsync(string planName, CancellationToken cancellationToken)
    {
        // First check database mapping
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        var priceId = await conn.QuerySingleOrDefaultAsync<string>(
            "SELECT stripe_price_id FROM stripe_price_mapping WHERE plan_name = @PlanName AND is_active = true",
            new { PlanName = planName.ToLowerInvariant() });

        if (!string.IsNullOrEmpty(priceId))
            return priceId;

        // Fall back to config - return null if empty or not configured
        var configPriceId = planName.ToLowerInvariant() switch
        {
            "free" => _config.FreePriceId,
            "pro" => _config.ProPriceId,
            "enterprise" => _config.EnterprisePriceId,
            _ => null
        };

        // Return null if empty (not configured)
        return string.IsNullOrEmpty(configPriceId) ? null : configPriceId;
    }

    private async Task<string> GetOrCreateStripeCustomerAsync(string tenantId, CancellationToken cancellationToken)
    {
        var existingCustomerId = await GetStripeCustomerIdAsync(tenantId, cancellationToken);
        if (existingCustomerId != null)
            return existingCustomerId;

        // Get tenant info for customer creation
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        var tenantInfo = await conn.QuerySingleOrDefaultAsync<dynamic>(
            "SELECT name, slug FROM tenants WHERE id::text = @TenantId",
            new { TenantId = tenantId });

        var customerOptions = new CustomerCreateOptions
        {
            Metadata = new Dictionary<string, string>
            {
                ["tenant_id"] = tenantId
            }
        };

        if (tenantInfo != null)
        {
            customerOptions.Name = tenantInfo.name;
            customerOptions.Description = $"SerialMemory tenant: {tenantInfo.slug}";
        }

        var customer = await _customerService.CreateAsync(customerOptions, cancellationToken: cancellationToken);

        // Store the customer ID
        await conn.ExecuteAsync(
            "UPDATE tenant_subscriptions SET stripe_customer_id = @CustomerId WHERE tenant_id = @TenantId",
            new { TenantId = tenantId, CustomerId = customer.Id });

        _logger.LogInformation("Created Stripe customer {CustomerId} for tenant {TenantId}", customer.Id, tenantId);

        return customer.Id;
    }

    private async Task<string?> GetStripeCustomerIdAsync(string tenantId, CancellationToken cancellationToken)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        return await conn.QuerySingleOrDefaultAsync<string>(
            "SELECT stripe_customer_id FROM tenant_subscriptions WHERE tenant_id = @TenantId",
            new { TenantId = tenantId });
    }

    private async Task<string?> GetStripeSubscriptionIdAsync(string tenantId, CancellationToken cancellationToken)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        return await conn.QuerySingleOrDefaultAsync<string>(
            "SELECT stripe_subscription_id FROM tenant_subscriptions WHERE tenant_id = @TenantId",
            new { TenantId = tenantId });
    }

    private async Task<bool> IsEventAlreadyProcessedAsync(string eventId, CancellationToken cancellationToken)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        var processed = await conn.QuerySingleOrDefaultAsync<bool>(
            "SELECT processed FROM stripe_webhook_events WHERE stripe_event_id = @EventId",
            new { EventId = eventId });

        return processed;
    }

    private async Task<StripeWebhookEvent> StoreWebhookEventAsync(Event stripeEvent, string payload, CancellationToken cancellationToken)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        var id = Guid.CreateVersion7();
        var customerId = GetCustomerIdFromEvent(stripeEvent);
        var subscriptionId = GetSubscriptionIdFromEvent(stripeEvent);
        var tenantId = await GetTenantIdByStripeCustomerAsync(customerId, cancellationToken);

        await conn.ExecuteAsync(
            """
            INSERT INTO stripe_webhook_events
                (id, stripe_event_id, event_type, tenant_id, stripe_customer_id, stripe_subscription_id, payload)
            VALUES
                (@Id, @StripeEventId, @EventType, @TenantId, @CustomerId, @SubscriptionId, @Payload::jsonb)
            ON CONFLICT (stripe_event_id) DO NOTHING
            """,
            new
            {
                Id = id,
                StripeEventId = stripeEvent.Id,
                EventType = stripeEvent.Type,
                TenantId = tenantId,
                CustomerId = customerId,
                SubscriptionId = subscriptionId,
                Payload = payload
            });

        return new StripeWebhookEvent
        {
            Id = id,
            StripeEventId = stripeEvent.Id,
            EventType = stripeEvent.Type,
            TenantId = tenantId,
            StripeCustomerId = customerId,
            StripeSubscriptionId = subscriptionId,
            Payload = payload,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    private async Task MarkEventProcessedAsync(Guid eventId, string? errorMessage, CancellationToken cancellationToken)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        await conn.ExecuteAsync(
            """
            UPDATE stripe_webhook_events
            SET processed = @Processed, processed_at = NOW(), error_message = @ErrorMessage,
                retry_count = retry_count + CASE WHEN @ErrorMessage IS NOT NULL THEN 1 ELSE 0 END
            WHERE id = @Id
            """,
            new { Id = eventId, Processed = errorMessage == null, ErrorMessage = errorMessage });
    }

    private async Task<string?> GetTenantIdByStripeCustomerAsync(string? customerId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(customerId))
            return null;

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        return await conn.QuerySingleOrDefaultAsync<string>(
            "SELECT tenant_id FROM tenant_subscriptions WHERE stripe_customer_id = @CustomerId",
            new { CustomerId = customerId });
    }

    private static string? GetCustomerIdFromEvent(Event stripeEvent)
    {
        return stripeEvent.Data.Object switch
        {
            Subscription sub => sub.CustomerId,
            Invoice inv => inv.CustomerId,
            Customer cust => cust.Id,
            Session session => session.CustomerId,
            _ => null
        };
    }

    private static string? GetSubscriptionIdFromEvent(Event stripeEvent)
    {
        return stripeEvent.Data.Object switch
        {
            Subscription sub => sub.Id,
            Invoice inv => inv.SubscriptionId,
            Session session => session.SubscriptionId,
            _ => null
        };
    }

    // Webhook handlers
    private async Task<WebhookProcessResult> HandleSubscriptionCreatedAsync(Event stripeEvent, CancellationToken cancellationToken)
    {
        var subscription = (Subscription)stripeEvent.Data.Object;
        var tenantId = subscription.Metadata.TryGetValue("tenant_id", out var tid) ? tid : null;

        if (tenantId == null)
        {
            tenantId = await GetTenantIdByStripeCustomerAsync(subscription.CustomerId, cancellationToken);
        }

        if (tenantId == null)
        {
            _logger.LogWarning("Could not find tenant for subscription {SubscriptionId}", subscription.Id);
            return new WebhookProcessResult
            {
                Success = false,
                ErrorCode = "tenant_not_found",
                ErrorMessage = "Could not find tenant for subscription"
            };
        }

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        var priceId = subscription.Items.Data.FirstOrDefault()?.Price?.Id;

        await conn.ExecuteAsync(
            "SELECT handle_subscription_updated(@TenantId, @CustomerId, @SubscriptionId, @PriceId, @Status)",
            new
            {
                TenantId = tenantId,
                CustomerId = subscription.CustomerId,
                SubscriptionId = subscription.Id,
                PriceId = priceId,
                Status = subscription.Status
            });

        _logger.LogInformation(
            "Processed subscription.created for tenant {TenantId} subscription {SubscriptionId}",
            tenantId, subscription.Id);

        return new WebhookProcessResult { Success = true, AffectedTenantId = tenantId };
    }

    private async Task<WebhookProcessResult> HandleSubscriptionUpdatedAsync(Event stripeEvent, CancellationToken cancellationToken)
    {
        var subscription = (Subscription)stripeEvent.Data.Object;
        var tenantId = await GetTenantIdByStripeCustomerAsync(subscription.CustomerId, cancellationToken);

        if (tenantId == null)
        {
            _logger.LogWarning("Could not find tenant for subscription {SubscriptionId}", subscription.Id);
            return new WebhookProcessResult { Success = true }; // Not an error, just not our subscription
        }

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        var priceId = subscription.Items.Data.FirstOrDefault()?.Price?.Id;

        await conn.ExecuteAsync(
            "SELECT handle_subscription_updated(@TenantId, @CustomerId, @SubscriptionId, @PriceId, @Status)",
            new
            {
                TenantId = tenantId,
                CustomerId = subscription.CustomerId,
                SubscriptionId = subscription.Id,
                PriceId = priceId,
                Status = subscription.Status
            });

        // Update payment method info if available
        if (subscription.DefaultPaymentMethod is PaymentMethod pm && pm.Card != null)
        {
            await conn.ExecuteAsync(
                """
                UPDATE tenant_subscriptions
                SET payment_method_last4 = @Last4, payment_method_brand = @Brand
                WHERE tenant_id = @TenantId
                """,
                new { TenantId = tenantId, Last4 = pm.Card.Last4, Brand = pm.Card.Brand });
        }

        _logger.LogInformation(
            "Processed subscription.updated for tenant {TenantId} status {Status}",
            tenantId, subscription.Status);

        return new WebhookProcessResult { Success = true, AffectedTenantId = tenantId };
    }

    private async Task<WebhookProcessResult> HandleSubscriptionDeletedAsync(Event stripeEvent, CancellationToken cancellationToken)
    {
        var subscription = (Subscription)stripeEvent.Data.Object;
        var tenantId = await GetTenantIdByStripeCustomerAsync(subscription.CustomerId, cancellationToken);

        if (tenantId == null)
        {
            return new WebhookProcessResult { Success = true };
        }

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        await conn.ExecuteAsync(
            "SELECT handle_subscription_cancelled(@TenantId, @CustomerId)",
            new { TenantId = tenantId, CustomerId = subscription.CustomerId });

        _logger.LogInformation("Processed subscription.deleted for tenant {TenantId}", tenantId);

        return new WebhookProcessResult { Success = true, AffectedTenantId = tenantId };
    }

    private async Task<WebhookProcessResult> HandlePaymentFailedAsync(Event stripeEvent, CancellationToken cancellationToken)
    {
        var invoice = (Invoice)stripeEvent.Data.Object;
        var tenantId = await GetTenantIdByStripeCustomerAsync(invoice.CustomerId, cancellationToken);

        if (tenantId == null)
        {
            return new WebhookProcessResult { Success = true };
        }

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        // Record payment failure
        await conn.ExecuteAsync(
            """
            INSERT INTO payment_history
                (tenant_id, stripe_customer_id, stripe_invoice_id, stripe_payment_intent_id,
                 amount_cents, currency, status, failure_reason, failure_code,
                 billing_period_start, billing_period_end, hosted_invoice_url)
            VALUES
                (@TenantId, @CustomerId, @InvoiceId, @PaymentIntentId,
                 @Amount, @Currency, 'failed', @FailureReason, @FailureCode,
                 @PeriodStart, @PeriodEnd, @HostedUrl)
            ON CONFLICT (stripe_invoice_id) DO UPDATE SET
                status = 'failed', failure_reason = EXCLUDED.failure_reason, updated_at = NOW()
            """,
            new
            {
                TenantId = tenantId,
                CustomerId = invoice.CustomerId,
                InvoiceId = invoice.Id,
                PaymentIntentId = invoice.PaymentIntentId,
                Amount = invoice.AmountDue,
                Currency = invoice.Currency,
                FailureReason = invoice.LastFinalizationError?.Message,
                FailureCode = invoice.LastFinalizationError?.Code,
                PeriodStart = invoice.PeriodStart,
                PeriodEnd = invoice.PeriodEnd,
                HostedUrl = invoice.HostedInvoiceUrl
            });

        // Handle the failure (mark subscription as past_due)
        await conn.ExecuteAsync(
            "SELECT handle_payment_failure(@TenantId, @CustomerId, @Reason)",
            new
            {
                TenantId = tenantId,
                CustomerId = invoice.CustomerId,
                Reason = invoice.LastFinalizationError?.Message ?? "Payment failed"
            });

        _logger.LogWarning("Processed invoice.payment_failed for tenant {TenantId}", tenantId);

        return new WebhookProcessResult { Success = true, AffectedTenantId = tenantId };
    }

    private async Task<WebhookProcessResult> HandlePaymentSucceededAsync(Event stripeEvent, CancellationToken cancellationToken)
    {
        var invoice = (Invoice)stripeEvent.Data.Object;
        var tenantId = await GetTenantIdByStripeCustomerAsync(invoice.CustomerId, cancellationToken);

        if (tenantId == null)
        {
            return new WebhookProcessResult { Success = true };
        }

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        // Get plan name from subscription
        string? planName = null;
        if (invoice.SubscriptionId != null)
        {
            var priceId = await conn.QuerySingleOrDefaultAsync<string>(
                "SELECT stripe_price_id FROM tenant_subscriptions WHERE stripe_subscription_id = @SubId",
                new { SubId = invoice.SubscriptionId });

            if (priceId != null)
            {
                planName = await conn.QuerySingleOrDefaultAsync<string>(
                    "SELECT plan_name FROM stripe_price_mapping WHERE stripe_price_id = @PriceId",
                    new { PriceId = priceId });
            }
        }

        // Record successful payment
        await conn.ExecuteAsync(
            """
            INSERT INTO payment_history
                (tenant_id, stripe_customer_id, stripe_invoice_id, stripe_payment_intent_id,
                 amount_cents, currency, status, plan_name,
                 billing_period_start, billing_period_end, invoice_pdf_url, hosted_invoice_url)
            VALUES
                (@TenantId, @CustomerId, @InvoiceId, @PaymentIntentId,
                 @Amount, @Currency, 'paid', @PlanName,
                 @PeriodStart, @PeriodEnd, @PdfUrl, @HostedUrl)
            ON CONFLICT (stripe_invoice_id) DO UPDATE SET
                status = 'paid', updated_at = NOW()
            """,
            new
            {
                TenantId = tenantId,
                CustomerId = invoice.CustomerId,
                InvoiceId = invoice.Id,
                PaymentIntentId = invoice.PaymentIntentId,
                Amount = invoice.AmountPaid,
                Currency = invoice.Currency,
                PlanName = planName,
                PeriodStart = invoice.PeriodStart,
                PeriodEnd = invoice.PeriodEnd,
                PdfUrl = invoice.InvoicePdf,
                HostedUrl = invoice.HostedInvoiceUrl
            });

        // Clear any past_due status
        await conn.ExecuteAsync(
            """
            UPDATE tenant_subscriptions
            SET status = 'active', updated_at = NOW()
            WHERE tenant_id = @TenantId AND status = 'past_due'
            """,
            new { TenantId = tenantId });

        // Grant credits for the new billing cycle
        if (planName != null && invoice.PeriodStart != null && invoice.PeriodEnd != null)
        {
            // Get credit allocation for this plan
            var creditsPerCycle = await conn.QuerySingleOrDefaultAsync<decimal?>(
                """
                SELECT credits_per_cycle FROM tenant_plans
                WHERE plan_name = @PlanName AND is_active = true
                """,
                new { PlanName = planName });

            if (creditsPerCycle.HasValue && creditsPerCycle > 0)
            {
                // Create or update billing cycle with new credit allocation
                await conn.ExecuteAsync(
                    """
                    INSERT INTO billing_cycles (id, tenant_id, workspace_id, cycle_start, cycle_end, credits_allocated, credits_used, is_current, created_at)
                    VALUES (@Id, @TenantId, 'default', @CycleStart, @CycleEnd, @CreditsAllocated, 0, true, NOW())
                    ON CONFLICT (tenant_id, workspace_id, cycle_start) DO UPDATE SET
                        credits_allocated = EXCLUDED.credits_allocated,
                        cycle_end = EXCLUDED.cycle_end,
                        is_current = true,
                        updated_at = NOW()
                    """,
                    new
                    {
                        Id = Guid.CreateVersion7(),
                        TenantId = tenantId,
                        CycleStart = invoice.PeriodStart,
                        CycleEnd = invoice.PeriodEnd,
                        CreditsAllocated = creditsPerCycle.Value
                    });

                // Mark previous cycles as not current
                await conn.ExecuteAsync(
                    """
                    UPDATE billing_cycles
                    SET is_current = false, updated_at = NOW()
                    WHERE tenant_id = @TenantId AND workspace_id = 'default'
                      AND cycle_start < @CycleStart AND is_current = true
                    """,
                    new { TenantId = tenantId, CycleStart = invoice.PeriodStart });

                _logger.LogInformation(
                    "Granted {Credits} credits to tenant {TenantId} for billing cycle {Start} - {End}",
                    creditsPerCycle, tenantId, invoice.PeriodStart, invoice.PeriodEnd);
            }
        }

        _logger.LogInformation("Processed invoice.payment_succeeded for tenant {TenantId}", tenantId);

        return new WebhookProcessResult { Success = true, AffectedTenantId = tenantId };
    }

    private async Task<WebhookProcessResult> HandleCheckoutCompletedAsync(Event stripeEvent, CancellationToken cancellationToken)
    {
        var session = (Session)stripeEvent.Data.Object;
        var tenantId = session.Metadata.GetValueOrDefault("tenant_id");

        if (tenantId == null)
        {
            tenantId = await GetTenantIdByStripeCustomerAsync(session.CustomerId, cancellationToken);
        }

        if (tenantId == null)
        {
            _logger.LogWarning("Could not find tenant for checkout session {SessionId}", session.Id);
            return new WebhookProcessResult { Success = true };
        }

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        // Update the customer ID if this was a new customer
        await conn.ExecuteAsync(
            """
            UPDATE tenant_subscriptions
            SET stripe_customer_id = @CustomerId
            WHERE tenant_id = @TenantId AND stripe_customer_id IS NULL
            """,
            new { TenantId = tenantId, CustomerId = session.CustomerId });

        _logger.LogInformation(
            "Processed checkout.session.completed for tenant {TenantId} subscription {SubscriptionId}",
            tenantId, session.SubscriptionId);

        return new WebhookProcessResult { Success = true, AffectedTenantId = tenantId };
    }
}
