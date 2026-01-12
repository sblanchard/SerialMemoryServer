namespace SerialMemory.Core.Billing;

/// <summary>
/// Billing states for a tenant subscription.
/// </summary>
public enum BillingState
{
    /// <summary>Free tier or no subscription.</summary>
    Free = 0,

    /// <summary>Trial period (if applicable).</summary>
    Trialing = 1,

    /// <summary>Active paid subscription.</summary>
    Active = 2,

    /// <summary>Payment failed, but still in grace period.</summary>
    PastDue = 3,

    /// <summary>Multiple payment failures, service may be limited.</summary>
    Delinquent = 4,

    /// <summary>Grace period after cancellation, still has access.</summary>
    GracePeriod = 5,

    /// <summary>Subscription cancelled, reverted to free tier.</summary>
    Cancelled = 6,

    /// <summary>Account locked due to non-payment or abuse.</summary>
    Locked = 7
}

/// <summary>
/// Events that trigger billing state transitions.
/// </summary>
public enum BillingEvent
{
    /// <summary>Subscription created successfully.</summary>
    SubscriptionCreated,

    /// <summary>Payment succeeded (invoice.paid).</summary>
    PaymentSucceeded,

    /// <summary>Payment failed (invoice.payment_failed).</summary>
    PaymentFailed,

    /// <summary>Multiple consecutive payment failures.</summary>
    PaymentFailedRepeatedly,

    /// <summary>Subscription cancelled at period end.</summary>
    SubscriptionCancelledAtPeriodEnd,

    /// <summary>Subscription actually ended.</summary>
    SubscriptionEnded,

    /// <summary>Trial period expired without payment.</summary>
    TrialExpired,

    /// <summary>Trial converted to paid subscription.</summary>
    TrialConverted,

    /// <summary>Subscription resumed before period end.</summary>
    SubscriptionResumed,

    /// <summary>Account locked by admin.</summary>
    AccountLocked,

    /// <summary>Account unlocked by admin.</summary>
    AccountUnlocked,

    /// <summary>Grace period expired.</summary>
    GracePeriodExpired
}

/// <summary>
/// Result of a billing state transition.
/// </summary>
public sealed record BillingTransitionResult
{
    public bool Success { get; init; }
    public BillingState FromState { get; init; }
    public BillingState ToState { get; init; }
    public string? ErrorMessage { get; init; }
    public bool ShouldNotifyUser { get; init; }
    public string? NotificationMessage { get; init; }
    public bool ShouldDowngradePlan { get; init; }
    public bool ShouldRestrictAccess { get; init; }
    public bool ShouldGrantAccess { get; init; }
}

/// <summary>
/// State machine for managing tenant billing state transitions.
/// Enforces valid state transitions and determines side effects.
/// </summary>
public sealed class TenantBillingStateMachine
{
    private BillingState _currentState;
    private int _consecutivePaymentFailures;
    private const int MaxPaymentFailuresBeforeDelinquent = 3;
    private const int GracePeriodDays = 7;

    public TenantBillingStateMachine(BillingState initialState = BillingState.Free)
    {
        _currentState = initialState;
        _consecutivePaymentFailures = 0;
    }

    public BillingState CurrentState => _currentState;
    public int ConsecutivePaymentFailures => _consecutivePaymentFailures;

    /// <summary>
    /// Attempts to transition the billing state based on an event.
    /// </summary>
    public BillingTransitionResult ProcessEvent(BillingEvent billingEvent)
    {
        var fromState = _currentState;
        var result = (_currentState, billingEvent) switch
        {
            // From Free state
            (BillingState.Free, BillingEvent.SubscriptionCreated) => TransitionTo(BillingState.Active, "Subscription activated"),
            (BillingState.Free, BillingEvent.TrialConverted) => TransitionTo(BillingState.Active, "Trial converted to paid subscription"),

            // From Trialing state
            (BillingState.Trialing, BillingEvent.PaymentSucceeded) => TransitionTo(BillingState.Active, "Payment received, trial converted"),
            (BillingState.Trialing, BillingEvent.TrialConverted) => TransitionTo(BillingState.Active, "Trial converted to paid subscription"),
            (BillingState.Trialing, BillingEvent.TrialExpired) => TransitionTo(BillingState.Free, "Trial expired", shouldNotify: true, shouldDowngrade: true),
            (BillingState.Trialing, BillingEvent.PaymentFailed) => TransitionTo(BillingState.Free, "Payment failed during trial", shouldNotify: true, shouldDowngrade: true),

            // From Active state
            (BillingState.Active, BillingEvent.PaymentSucceeded) => TransitionTo(BillingState.Active, "Payment received"),
            (BillingState.Active, BillingEvent.PaymentFailed) => HandlePaymentFailure(),
            (BillingState.Active, BillingEvent.SubscriptionCancelledAtPeriodEnd) => TransitionTo(BillingState.GracePeriod, "Subscription will end at period end", shouldNotify: true),
            (BillingState.Active, BillingEvent.SubscriptionEnded) => TransitionTo(BillingState.Free, "Subscription ended", shouldNotify: true, shouldDowngrade: true),
            (BillingState.Active, BillingEvent.AccountLocked) => TransitionTo(BillingState.Locked, "Account locked", shouldRestrict: true),

            // From PastDue state
            (BillingState.PastDue, BillingEvent.PaymentSucceeded) => RecoverFromPastDue(),
            (BillingState.PastDue, BillingEvent.PaymentFailed) => HandlePaymentFailure(),
            (BillingState.PastDue, BillingEvent.PaymentFailedRepeatedly) => TransitionTo(BillingState.Delinquent, "Multiple payment failures", shouldNotify: true, shouldRestrict: true),
            (BillingState.PastDue, BillingEvent.SubscriptionEnded) => TransitionTo(BillingState.Free, "Subscription ended due to non-payment", shouldNotify: true, shouldDowngrade: true),
            (BillingState.PastDue, BillingEvent.AccountLocked) => TransitionTo(BillingState.Locked, "Account locked due to payment issues", shouldRestrict: true),

            // From Delinquent state
            (BillingState.Delinquent, BillingEvent.PaymentSucceeded) => RecoverFromPastDue(),
            (BillingState.Delinquent, BillingEvent.SubscriptionEnded) => TransitionTo(BillingState.Free, "Subscription terminated", shouldNotify: true, shouldDowngrade: true),
            (BillingState.Delinquent, BillingEvent.AccountLocked) => TransitionTo(BillingState.Locked, "Account locked", shouldRestrict: true),

            // From GracePeriod state
            (BillingState.GracePeriod, BillingEvent.SubscriptionResumed) => TransitionTo(BillingState.Active, "Subscription resumed", shouldNotify: true, shouldGrant: true),
            (BillingState.GracePeriod, BillingEvent.SubscriptionEnded) => TransitionTo(BillingState.Free, "Grace period ended", shouldNotify: true, shouldDowngrade: true),
            (BillingState.GracePeriod, BillingEvent.GracePeriodExpired) => TransitionTo(BillingState.Free, "Grace period expired", shouldNotify: true, shouldDowngrade: true),
            (BillingState.GracePeriod, BillingEvent.PaymentSucceeded) => TransitionTo(BillingState.Active, "Payment received during grace period", shouldGrant: true),

            // From Cancelled state
            (BillingState.Cancelled, BillingEvent.SubscriptionCreated) => TransitionTo(BillingState.Active, "Re-subscribed", shouldGrant: true),

            // From Locked state
            (BillingState.Locked, BillingEvent.AccountUnlocked) => TransitionTo(BillingState.Free, "Account unlocked", shouldGrant: true),

            // Default: invalid transition
            _ => InvalidTransition(billingEvent)
        };

        return result with { FromState = fromState };
    }

    private BillingTransitionResult TransitionTo(
        BillingState newState,
        string message,
        bool shouldNotify = false,
        bool shouldDowngrade = false,
        bool shouldRestrict = false,
        bool shouldGrant = false)
    {
        _currentState = newState;

        // Reset payment failure counter on successful transitions
        if (newState == BillingState.Active)
        {
            _consecutivePaymentFailures = 0;
        }

        return new BillingTransitionResult
        {
            Success = true,
            ToState = newState,
            ShouldNotifyUser = shouldNotify,
            NotificationMessage = message,
            ShouldDowngradePlan = shouldDowngrade,
            ShouldRestrictAccess = shouldRestrict,
            ShouldGrantAccess = shouldGrant
        };
    }

    private BillingTransitionResult HandlePaymentFailure()
    {
        _consecutivePaymentFailures++;

        if (_consecutivePaymentFailures >= MaxPaymentFailuresBeforeDelinquent)
        {
            return TransitionTo(
                BillingState.Delinquent,
                $"Account delinquent after {_consecutivePaymentFailures} failed payments",
                shouldNotify: true,
                shouldRestrict: true);
        }

        return TransitionTo(
            BillingState.PastDue,
            $"Payment failed (attempt {_consecutivePaymentFailures})",
            shouldNotify: true);
    }

    private BillingTransitionResult RecoverFromPastDue()
    {
        _consecutivePaymentFailures = 0;
        return TransitionTo(
            BillingState.Active,
            "Payment received, account restored",
            shouldNotify: true,
            shouldGrant: true);
    }

    private BillingTransitionResult InvalidTransition(BillingEvent billingEvent)
    {
        return new BillingTransitionResult
        {
            Success = false,
            ToState = _currentState,
            ErrorMessage = $"Invalid transition: cannot process {billingEvent} in state {_currentState}"
        };
    }

    /// <summary>
    /// Gets the billing state from a string status (e.g., from database or Stripe).
    /// </summary>
    public static BillingState ParseState(string? status)
    {
        return status?.ToLowerInvariant() switch
        {
            "free" => BillingState.Free,
            "trialing" => BillingState.Trialing,
            "active" => BillingState.Active,
            "past_due" => BillingState.PastDue,
            "delinquent" => BillingState.Delinquent,
            "grace_period" => BillingState.GracePeriod,
            "cancelled" or "canceled" => BillingState.Cancelled,
            "locked" => BillingState.Locked,
            _ => BillingState.Free
        };
    }

    /// <summary>
    /// Converts a billing state to a database-compatible status string.
    /// </summary>
    public static string ToStatusString(BillingState state)
    {
        return state switch
        {
            BillingState.Free => "free",
            BillingState.Trialing => "trialing",
            BillingState.Active => "active",
            BillingState.PastDue => "past_due",
            BillingState.Delinquent => "delinquent",
            BillingState.GracePeriod => "grace_period",
            BillingState.Cancelled => "cancelled",
            BillingState.Locked => "locked",
            _ => "free"
        };
    }

    /// <summary>
    /// Determines if the current state allows API access.
    /// </summary>
    public bool AllowsApiAccess()
    {
        return _currentState switch
        {
            BillingState.Free => true,
            BillingState.Trialing => true,
            BillingState.Active => true,
            BillingState.PastDue => true, // Reduced limits but still has access
            BillingState.Delinquent => false, // Restricted access
            BillingState.GracePeriod => true,
            BillingState.Cancelled => true, // Reverted to free tier
            BillingState.Locked => false, // No access
            _ => false
        };
    }

    /// <summary>
    /// Gets the credit multiplier for the current state (1.0 = full access, 0 = no credits).
    /// </summary>
    public decimal GetCreditMultiplier()
    {
        return _currentState switch
        {
            BillingState.Free => 1.0m,
            BillingState.Trialing => 1.0m,
            BillingState.Active => 1.0m,
            BillingState.PastDue => 0.5m, // Half credits during past due
            BillingState.Delinquent => 0.1m, // Minimal credits
            BillingState.GracePeriod => 1.0m,
            BillingState.Cancelled => 1.0m, // Free tier credits
            BillingState.Locked => 0.0m, // No credits
            _ => 0.0m
        };
    }
}
