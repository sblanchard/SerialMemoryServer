namespace SerialMemory.Core.Models;

/// <summary>
/// Stripe webhook event types we handle.
/// </summary>
public static class StripeEventTypes
{
    public const string SubscriptionCreated = "customer.subscription.created";
    public const string SubscriptionUpdated = "customer.subscription.updated";
    public const string SubscriptionDeleted = "customer.subscription.deleted";
    public const string InvoicePaymentFailed = "invoice.payment_failed";
    public const string InvoicePaymentSucceeded = "invoice.payment_succeeded";
    public const string InvoicePaid = "invoice.paid";
    public const string CustomerCreated = "customer.created";
    public const string CustomerUpdated = "customer.updated";
    public const string CheckoutSessionCompleted = "checkout.session.completed";
}

/// <summary>
/// Represents a Stripe webhook event record.
/// </summary>
public sealed class StripeWebhookEvent
{
    public Guid Id { get; init; }
    public required string StripeEventId { get; init; }
    public required string EventType { get; init; }
    public string? TenantId { get; init; }
    public string? StripeCustomerId { get; init; }
    public string? StripeSubscriptionId { get; init; }
    public required string Payload { get; init; }
    public bool Processed { get; init; }
    public DateTimeOffset? ProcessedAt { get; init; }
    public string? ErrorMessage { get; init; }
    public int RetryCount { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
/// Payment history record.
/// </summary>
public sealed class PaymentRecord
{
    public Guid Id { get; init; }
    public required string TenantId { get; init; }
    public required string StripeCustomerId { get; init; }
    public required string StripeInvoiceId { get; init; }
    public string? StripePaymentIntentId { get; init; }
    public int AmountCents { get; init; }
    public string Currency { get; init; } = "usd";
    public PaymentStatus Status { get; init; }
    public string? PlanName { get; init; }
    public DateTimeOffset? BillingPeriodStart { get; init; }
    public DateTimeOffset? BillingPeriodEnd { get; init; }
    public string? FailureReason { get; init; }
    public string? FailureCode { get; init; }
    public string? InvoicePdfUrl { get; init; }
    public string? HostedInvoiceUrl { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public enum PaymentStatus
{
    Pending,
    Paid,
    Failed,
    Refunded,
    Void
}

/// <summary>
/// Stripe price mapping to internal plans.
/// </summary>
public sealed class StripePriceMapping
{
    public Guid Id { get; init; }
    public required string PlanName { get; init; }
    public required string StripePriceId { get; init; }
    public required string StripeProductId { get; init; }
    public bool IsActive { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
/// Request to create a Stripe checkout session.
/// </summary>
public sealed class CreateCheckoutRequest
{
    public required string TenantId { get; init; }
    public required string PlanName { get; init; }
    public string? SuccessUrl { get; init; }
    public string? CancelUrl { get; init; }
}

/// <summary>
/// Result of creating a checkout session.
/// </summary>
public sealed class CheckoutSessionResult
{
    public bool Success { get; init; }
    public string? SessionId { get; init; }
    public string? CheckoutUrl { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Request to create a Stripe customer portal session.
/// </summary>
public sealed class CreatePortalRequest
{
    public required string TenantId { get; init; }
    public string? ReturnUrl { get; init; }
}

/// <summary>
/// Result of creating a portal session.
/// </summary>
public sealed class PortalSessionResult
{
    public bool Success { get; init; }
    public string? PortalUrl { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Webhook processing result.
/// </summary>
public sealed record WebhookProcessResult
{
    public bool Success { get; init; }
    public bool AlreadyProcessed { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public string? EventType { get; init; }
    public string? AffectedTenantId { get; init; }
}

/// <summary>
/// Billing summary for a tenant.
/// </summary>
public sealed class BillingSummary
{
    public required string TenantId { get; init; }
    public string? StripeCustomerId { get; init; }
    public string? StripeSubscriptionId { get; init; }
    public required string CurrentPlan { get; init; }
    public string? PaymentMethodLast4 { get; init; }
    public string? PaymentMethodBrand { get; init; }
    public DateTimeOffset? CurrentPeriodStart { get; init; }
    public DateTimeOffset? CurrentPeriodEnd { get; init; }
    public DateTimeOffset? CancelAt { get; init; }
    public bool WillCancelAtPeriodEnd { get; init; }
    public IReadOnlyList<PaymentRecord> RecentPayments { get; init; } = [];
}

/// <summary>
/// Configuration for Stripe integration.
/// </summary>
public sealed class StripeConfig
{
    public required string SecretKey { get; init; }
    public required string PublishableKey { get; init; }
    public required string WebhookSecret { get; init; }
    public string? FreePriceId { get; init; }
    public required string ProPriceId { get; init; }
    public required string EnterprisePriceId { get; init; }
    public string DefaultSuccessUrl { get; init; } = "/dashboard/billing?success=true";
    public string DefaultCancelUrl { get; init; } = "/dashboard/billing?canceled=true";
}
