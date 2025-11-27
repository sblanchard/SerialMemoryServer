-- Stripe Billing Schema Migration
-- Adds Stripe-specific columns and tables for payment processing

-- Add Stripe columns to tenant_subscriptions
ALTER TABLE tenant_subscriptions
ADD COLUMN IF NOT EXISTS stripe_customer_id TEXT,
ADD COLUMN IF NOT EXISTS stripe_subscription_id TEXT,
ADD COLUMN IF NOT EXISTS stripe_price_id TEXT,
ADD COLUMN IF NOT EXISTS payment_method_last4 TEXT,
ADD COLUMN IF NOT EXISTS payment_method_brand TEXT;

-- Create unique indexes for Stripe IDs (for webhook idempotency)
CREATE UNIQUE INDEX IF NOT EXISTS idx_tenant_subscriptions_stripe_customer
    ON tenant_subscriptions(stripe_customer_id)
    WHERE stripe_customer_id IS NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS idx_tenant_subscriptions_stripe_subscription
    ON tenant_subscriptions(stripe_subscription_id)
    WHERE stripe_subscription_id IS NOT NULL;

-- Stripe webhook events table (for idempotency and debugging)
CREATE TABLE IF NOT EXISTS stripe_webhook_events (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    stripe_event_id TEXT NOT NULL UNIQUE,
    event_type TEXT NOT NULL,
    tenant_id TEXT,
    stripe_customer_id TEXT,
    stripe_subscription_id TEXT,
    payload JSONB NOT NULL,
    processed BOOLEAN NOT NULL DEFAULT FALSE,
    processed_at TIMESTAMPTZ,
    error_message TEXT,
    retry_count INTEGER NOT NULL DEFAULT 0,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_stripe_webhook_events_type ON stripe_webhook_events(event_type);
CREATE INDEX IF NOT EXISTS idx_stripe_webhook_events_customer ON stripe_webhook_events(stripe_customer_id);
CREATE INDEX IF NOT EXISTS idx_stripe_webhook_events_processed ON stripe_webhook_events(processed, created_at);

-- Payment history table
CREATE TABLE IF NOT EXISTS payment_history (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id TEXT NOT NULL,
    stripe_customer_id TEXT NOT NULL,
    stripe_invoice_id TEXT NOT NULL UNIQUE,
    stripe_payment_intent_id TEXT,
    amount_cents INTEGER NOT NULL,
    currency TEXT NOT NULL DEFAULT 'usd',
    status TEXT NOT NULL CHECK (status IN ('pending', 'paid', 'failed', 'refunded', 'void')),
    plan_name TEXT,
    billing_period_start TIMESTAMPTZ,
    billing_period_end TIMESTAMPTZ,
    failure_reason TEXT,
    failure_code TEXT,
    invoice_pdf_url TEXT,
    hosted_invoice_url TEXT,
    metadata JSONB,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_payment_history_tenant ON payment_history(tenant_id);
CREATE INDEX IF NOT EXISTS idx_payment_history_customer ON payment_history(stripe_customer_id);
CREATE INDEX IF NOT EXISTS idx_payment_history_status ON payment_history(status);
CREATE INDEX IF NOT EXISTS idx_payment_history_created ON payment_history(created_at DESC);

-- Stripe price mapping (maps our plans to Stripe price IDs)
CREATE TABLE IF NOT EXISTS stripe_price_mapping (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    plan_name TEXT NOT NULL UNIQUE REFERENCES tenant_plans(plan_name),
    stripe_price_id TEXT NOT NULL,
    stripe_product_id TEXT NOT NULL,
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Function to handle payment failure (downgrade to free)
CREATE OR REPLACE FUNCTION handle_payment_failure(
    p_tenant_id TEXT,
    p_stripe_customer_id TEXT,
    p_failure_reason TEXT DEFAULT NULL
)
RETURNS VOID AS $$
DECLARE
    v_free_plan_id UUID;
BEGIN
    -- Get free plan ID
    SELECT id INTO v_free_plan_id FROM tenant_plans WHERE plan_name = 'free' AND is_active = TRUE;

    IF v_free_plan_id IS NULL THEN
        RAISE EXCEPTION 'Free plan not found';
    END IF;

    -- Update subscription status and plan
    UPDATE tenant_subscriptions
    SET
        status = 'past_due',
        updated_at = NOW(),
        metadata = COALESCE(metadata, '{}'::jsonb) ||
            jsonb_build_object(
                'payment_failure_reason', p_failure_reason,
                'payment_failure_at', NOW()::text
            )
    WHERE tenant_id = p_tenant_id OR stripe_customer_id = p_stripe_customer_id;

    -- Log the event
    PERFORM append_audit_log(
        p_tenant_id,
        'default',
        NULL,
        'payment_failed',
        'Payment failed - subscription marked as past due',
        jsonb_build_object('failure_reason', p_failure_reason)
    );
END;
$$ LANGUAGE plpgsql;

-- Function to handle subscription cancellation (downgrade to free)
CREATE OR REPLACE FUNCTION handle_subscription_cancelled(
    p_tenant_id TEXT,
    p_stripe_customer_id TEXT
)
RETURNS VOID AS $$
DECLARE
    v_free_plan_id UUID;
BEGIN
    -- Get free plan ID
    SELECT id INTO v_free_plan_id FROM tenant_plans WHERE plan_name = 'free' AND is_active = TRUE;

    IF v_free_plan_id IS NULL THEN
        RAISE EXCEPTION 'Free plan not found';
    END IF;

    -- Downgrade to free plan
    UPDATE tenant_subscriptions
    SET
        plan_id = v_free_plan_id,
        status = 'active',
        stripe_subscription_id = NULL,
        stripe_price_id = NULL,
        cancelled_at = NOW(),
        updated_at = NOW(),
        metadata = COALESCE(metadata, '{}'::jsonb) ||
            jsonb_build_object('downgraded_to_free_at', NOW()::text)
    WHERE tenant_id = p_tenant_id OR stripe_customer_id = p_stripe_customer_id;

    -- Log the event
    PERFORM append_audit_log(
        p_tenant_id,
        'default',
        NULL,
        'subscription_cancelled',
        'Subscription cancelled - downgraded to free plan',
        NULL
    );
END;
$$ LANGUAGE plpgsql;

-- Function to handle successful subscription update
-- Also updates credit limits from plan_limits table
CREATE OR REPLACE FUNCTION handle_subscription_updated(
    p_tenant_id TEXT,
    p_stripe_customer_id TEXT,
    p_stripe_subscription_id TEXT,
    p_stripe_price_id TEXT,
    p_status TEXT
)
RETURNS VOID AS $$
DECLARE
    v_plan_id UUID;
    v_plan_name TEXT;
    v_tenant_uuid UUID;
BEGIN
    -- Get plan from price mapping
    SELECT tp.id, spm.plan_name INTO v_plan_id, v_plan_name
    FROM stripe_price_mapping spm
    JOIN tenant_plans tp ON tp.plan_name = spm.plan_name
    WHERE spm.stripe_price_id = p_stripe_price_id AND spm.is_active = TRUE;

    IF v_plan_id IS NULL THEN
        RAISE EXCEPTION 'No plan found for Stripe price ID: %', p_stripe_price_id;
    END IF;

    -- Convert tenant_id to UUID
    v_tenant_uuid := p_tenant_id::UUID;

    -- Update subscription
    UPDATE tenant_subscriptions
    SET
        plan_id = v_plan_id,
        stripe_subscription_id = p_stripe_subscription_id,
        stripe_price_id = p_stripe_price_id,
        status = CASE
            WHEN p_status = 'active' THEN 'active'
            WHEN p_status = 'past_due' THEN 'past_due'
            WHEN p_status = 'canceled' THEN 'cancelled'
            ELSE 'active'
        END,
        updated_at = NOW()
    WHERE tenant_id = p_tenant_id OR stripe_customer_id = p_stripe_customer_id;

    -- Update credit limits from plan_limits table
    -- This syncs the tenant_plans table with new credit allocation
    PERFORM update_tenant_plan(v_tenant_uuid, v_plan_name, p_stripe_price_id, p_stripe_subscription_id);

    -- Log the event
    PERFORM append_audit_log(
        p_tenant_id,
        'default',
        NULL,
        'subscription_updated',
        format('Subscription updated to %s plan with credit limit update', v_plan_name),
        jsonb_build_object('plan_name', v_plan_name, 'stripe_price_id', p_stripe_price_id)
    );
END;
$$ LANGUAGE plpgsql;

-- Trigger to update payment_history updated_at
CREATE OR REPLACE TRIGGER update_payment_history_updated_at
    BEFORE UPDATE ON payment_history
    FOR EACH ROW
    EXECUTE FUNCTION update_updated_at_column();

-- Trigger to update stripe_price_mapping updated_at
CREATE OR REPLACE TRIGGER update_stripe_price_mapping_updated_at
    BEFORE UPDATE ON stripe_price_mapping
    FOR EACH ROW
    EXECUTE FUNCTION update_updated_at_column();
