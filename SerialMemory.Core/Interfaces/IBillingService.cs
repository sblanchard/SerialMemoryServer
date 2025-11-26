using SerialMemory.Core.Models;

namespace SerialMemory.Core.Interfaces;

/// <summary>
/// Service for managing billing and payment processing via Stripe.
/// </summary>
public interface IBillingService
{
    /// <summary>
    /// Creates a Stripe checkout session for upgrading to a paid plan.
    /// </summary>
    Task<CheckoutSessionResult> CreateCheckoutSessionAsync(
        CreateCheckoutRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a Stripe customer portal session for managing billing.
    /// </summary>
    Task<PortalSessionResult> CreatePortalSessionAsync(
        CreatePortalRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Processes a Stripe webhook event.
    /// Handles idempotency and event deduplication.
    /// </summary>
    Task<WebhookProcessResult> ProcessWebhookAsync(
        string payload,
        string signature,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the billing summary for a tenant.
    /// </summary>
    Task<BillingSummary?> GetBillingSummaryAsync(
        string tenantId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets payment history for a tenant.
    /// </summary>
    Task<IReadOnlyList<PaymentRecord>> GetPaymentHistoryAsync(
        string tenantId,
        int limit = 10,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels a subscription at the end of the current billing period.
    /// </summary>
    Task<WebhookProcessResult> CancelSubscriptionAsync(
        string tenantId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resumes a canceled subscription (if still within billing period).
    /// </summary>
    Task<WebhookProcessResult> ResumeSubscriptionAsync(
        string tenantId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets Stripe price mappings for all plans.
    /// </summary>
    Task<IReadOnlyList<StripePriceMapping>> GetPriceMappingsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the Stripe price mapping for a plan.
    /// </summary>
    Task SetPriceMappingAsync(
        string planName,
        string stripePriceId,
        string stripeProductId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Mock billing service for development/testing.
/// </summary>
public interface IMockableBillingService : IBillingService
{
    /// <summary>
    /// Simulates a subscription created event.
    /// </summary>
    Task SimulateSubscriptionCreatedAsync(
        string tenantId,
        string planName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Simulates a payment failed event.
    /// </summary>
    Task SimulatePaymentFailedAsync(
        string tenantId,
        string reason,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Simulates a subscription cancelled event.
    /// </summary>
    Task SimulateSubscriptionCancelledAsync(
        string tenantId,
        CancellationToken cancellationToken = default);
}
