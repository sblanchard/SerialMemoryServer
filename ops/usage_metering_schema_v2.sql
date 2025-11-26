-- Usage Metering Schema V2 Migration
-- Adds: rate limiting, subscriptions, plan management, audit logging with hash chains
-- Run this AFTER usage_metering_schema.sql

-- Add rate limiting columns to tenant_plans
ALTER TABLE tenant_plans
ADD COLUMN IF NOT EXISTS rate_limit_per_minute INTEGER DEFAULT NULL,
ADD COLUMN IF NOT EXISTS rate_limit_window_seconds INTEGER DEFAULT 60,
ADD COLUMN IF NOT EXISTS credit_carryover_enabled BOOLEAN DEFAULT FALSE,
ADD COLUMN IF NOT EXISTS credit_carryover_max_percent DECIMAL(5, 2) DEFAULT 0,
ADD COLUMN IF NOT EXISTS credit_expiry_days INTEGER DEFAULT NULL;

-- Tenant subscriptions table (tracks plan assignments to tenants)
CREATE TABLE IF NOT EXISTS tenant_subscriptions (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id TEXT NOT NULL,
    workspace_id TEXT NOT NULL,
    plan_id UUID NOT NULL REFERENCES tenant_plans(id),
    status TEXT NOT NULL DEFAULT 'active' CHECK (status IN ('active', 'past_due', 'cancelled', 'suspended', 'pending_downgrade')),
    started_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    cancelled_at TIMESTAMPTZ,
    effective_end_date TIMESTAMPTZ,
    pending_plan_id UUID REFERENCES tenant_plans(id),
    pending_change_date TIMESTAMPTZ,
    metadata JSONB,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    CONSTRAINT tenant_subscriptions_unique UNIQUE (tenant_id, workspace_id)
);

CREATE INDEX IF NOT EXISTS idx_tenant_subscriptions_tenant ON tenant_subscriptions(tenant_id, workspace_id);
CREATE INDEX IF NOT EXISTS idx_tenant_subscriptions_plan ON tenant_subscriptions(plan_id);
CREATE INDEX IF NOT EXISTS idx_tenant_subscriptions_status ON tenant_subscriptions(status);

-- Rate limit tracking table (sliding window counters)
CREATE TABLE IF NOT EXISTS rate_limit_buckets (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id TEXT NOT NULL,
    workspace_id TEXT NOT NULL,
    bucket_minute TIMESTAMPTZ NOT NULL,
    request_count INTEGER NOT NULL DEFAULT 0,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    CONSTRAINT rate_limit_buckets_unique UNIQUE (tenant_id, workspace_id, bucket_minute)
);

CREATE INDEX IF NOT EXISTS idx_rate_limit_buckets_tenant ON rate_limit_buckets(tenant_id, workspace_id, bucket_minute DESC);

-- Audit log table with hash chain
CREATE TABLE IF NOT EXISTS audit_logs (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    sequence_number BIGSERIAL,
    tenant_id TEXT NOT NULL,
    workspace_id TEXT NOT NULL,
    billing_cycle_id UUID REFERENCES billing_cycles(id),
    event_type TEXT NOT NULL,
    description TEXT NOT NULL,
    data JSONB,
    user_id TEXT,
    ip_address INET,
    timestamp TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    previous_hash TEXT NOT NULL DEFAULT '',
    content_hash TEXT NOT NULL,

    CONSTRAINT audit_logs_sequence_unique UNIQUE (tenant_id, workspace_id, sequence_number)
);

CREATE INDEX IF NOT EXISTS idx_audit_logs_tenant ON audit_logs(tenant_id, workspace_id);
CREATE INDEX IF NOT EXISTS idx_audit_logs_billing_cycle ON audit_logs(billing_cycle_id);
CREATE INDEX IF NOT EXISTS idx_audit_logs_timestamp ON audit_logs(tenant_id, workspace_id, timestamp DESC);
CREATE INDEX IF NOT EXISTS idx_audit_logs_sequence ON audit_logs(tenant_id, workspace_id, sequence_number);
CREATE INDEX IF NOT EXISTS idx_audit_logs_event_type ON audit_logs(event_type);

-- Credit adjustments table for tracking mid-cycle changes
CREATE TABLE IF NOT EXISTS credit_adjustments (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    billing_cycle_id UUID NOT NULL REFERENCES billing_cycles(id),
    adjustment_type TEXT NOT NULL CHECK (adjustment_type IN ('upgrade_proration', 'downgrade_pending', 'carryover', 'expiry', 'manual', 'refund')),
    amount DECIMAL(12, 2) NOT NULL,
    reason TEXT NOT NULL,
    metadata JSONB,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_credit_adjustments_cycle ON credit_adjustments(billing_cycle_id);

-- Update tenant_plans with new tiers (keeping existing plans)
INSERT INTO tenant_plans (plan_name, display_name, credits_per_cycle, cycle_days, max_memories, max_entities, rate_limit_per_minute, credit_carryover_enabled)
VALUES
    ('free', 'Free Tier', 100.00, 30, 1000, 500, 10, FALSE),
    ('pro', 'Professional', 5000.00, 30, 50000, 25000, 60, TRUE),
    ('enterprise', 'Enterprise', 50000.00, 30, NULL, NULL, NULL, TRUE)
ON CONFLICT (plan_name) DO UPDATE SET
    credits_per_cycle = EXCLUDED.credits_per_cycle,
    max_memories = EXCLUDED.max_memories,
    max_entities = EXCLUDED.max_entities,
    rate_limit_per_minute = EXCLUDED.rate_limit_per_minute,
    credit_carryover_enabled = EXCLUDED.credit_carryover_enabled,
    updated_at = NOW();

-- Update existing plans with rate limits
UPDATE tenant_plans SET
    rate_limit_per_minute = 10,
    credit_carryover_enabled = FALSE,
    credit_carryover_max_percent = 0
WHERE plan_name = 'free' AND rate_limit_per_minute IS NULL;

UPDATE tenant_plans SET
    rate_limit_per_minute = 30,
    credit_carryover_enabled = FALSE
WHERE plan_name = 'starter' AND rate_limit_per_minute IS NULL;

UPDATE tenant_plans SET
    rate_limit_per_minute = 60,
    credit_carryover_enabled = TRUE,
    credit_carryover_max_percent = 20
WHERE plan_name = 'professional' AND rate_limit_per_minute IS NULL;

UPDATE tenant_plans SET
    rate_limit_per_minute = NULL,
    credit_carryover_enabled = TRUE,
    credit_carryover_max_percent = 50
WHERE plan_name IN ('enterprise', 'self') AND rate_limit_per_minute IS NULL;

-- Function to check rate limits
CREATE OR REPLACE FUNCTION check_rate_limit(
    p_tenant_id TEXT,
    p_workspace_id TEXT,
    p_limit INTEGER,
    p_window_seconds INTEGER DEFAULT 60
)
RETURNS TABLE(allowed BOOLEAN, current_count INTEGER, limit_per_window INTEGER, retry_after TIMESTAMPTZ) AS $$
DECLARE
    v_current_minute TIMESTAMPTZ;
    v_count INTEGER;
BEGIN
    -- Truncate to current minute
    v_current_minute := date_trunc('minute', NOW());

    -- Clean up old buckets (older than 5 minutes)
    DELETE FROM rate_limit_buckets
    WHERE tenant_id = p_tenant_id
      AND workspace_id = p_workspace_id
      AND bucket_minute < NOW() - INTERVAL '5 minutes';

    -- Get current count
    SELECT COALESCE(SUM(request_count), 0) INTO v_count
    FROM rate_limit_buckets
    WHERE tenant_id = p_tenant_id
      AND workspace_id = p_workspace_id
      AND bucket_minute >= NOW() - (p_window_seconds || ' seconds')::INTERVAL;

    RETURN QUERY SELECT
        v_count < p_limit,
        v_count,
        p_limit,
        CASE WHEN v_count >= p_limit
            THEN v_current_minute + INTERVAL '1 minute'
            ELSE NULL::TIMESTAMPTZ
        END;
END;
$$ LANGUAGE plpgsql;

-- Function to record a rate limit hit
CREATE OR REPLACE FUNCTION record_rate_limit_hit(
    p_tenant_id TEXT,
    p_workspace_id TEXT
)
RETURNS VOID AS $$
DECLARE
    v_current_minute TIMESTAMPTZ;
BEGIN
    v_current_minute := date_trunc('minute', NOW());

    INSERT INTO rate_limit_buckets (tenant_id, workspace_id, bucket_minute, request_count)
    VALUES (p_tenant_id, p_workspace_id, v_current_minute, 1)
    ON CONFLICT (tenant_id, workspace_id, bucket_minute)
    DO UPDATE SET request_count = rate_limit_buckets.request_count + 1;
END;
$$ LANGUAGE plpgsql;

-- Function to get or create billing cycle with subscription support
CREATE OR REPLACE FUNCTION get_or_create_billing_cycle_v2(
    p_tenant_id TEXT,
    p_workspace_id TEXT
)
RETURNS UUID AS $$
DECLARE
    v_cycle_id UUID;
    v_plan_id UUID;
    v_credits DECIMAL(12, 2);
    v_cycle_days INTEGER;
    v_carryover_credits DECIMAL(12, 2) := 0;
    v_old_cycle_remaining DECIMAL(12, 2);
    v_carryover_enabled BOOLEAN;
    v_carryover_max_percent DECIMAL(5, 2);
BEGIN
    -- Try to get existing current cycle
    SELECT id INTO v_cycle_id
    FROM billing_cycles
    WHERE tenant_id = p_tenant_id
      AND workspace_id = p_workspace_id
      AND is_current = TRUE
      AND cycle_end > NOW();

    IF v_cycle_id IS NOT NULL THEN
        RETURN v_cycle_id;
    END IF;

    -- Get plan from subscription (or default to 'self')
    SELECT tp.id, tp.credits_per_cycle, tp.cycle_days,
           tp.credit_carryover_enabled, tp.credit_carryover_max_percent
    INTO v_plan_id, v_credits, v_cycle_days, v_carryover_enabled, v_carryover_max_percent
    FROM tenant_subscriptions ts
    JOIN tenant_plans tp ON ts.plan_id = tp.id
    WHERE ts.tenant_id = p_tenant_id
      AND ts.workspace_id = p_workspace_id
      AND ts.status = 'active';

    IF v_plan_id IS NULL THEN
        -- Default to 'self' plan for self-hosted
        SELECT id, credits_per_cycle, cycle_days, credit_carryover_enabled, credit_carryover_max_percent
        INTO v_plan_id, v_credits, v_cycle_days, v_carryover_enabled, v_carryover_max_percent
        FROM tenant_plans
        WHERE plan_name = 'self' AND is_active = TRUE
        LIMIT 1;
    END IF;

    -- Handle carryover from expiring cycle
    IF v_carryover_enabled THEN
        SELECT credits_allocated - credits_used INTO v_old_cycle_remaining
        FROM billing_cycles
        WHERE tenant_id = p_tenant_id
          AND workspace_id = p_workspace_id
          AND is_current = TRUE
          AND cycle_end <= NOW();

        IF v_old_cycle_remaining > 0 AND v_carryover_max_percent > 0 THEN
            v_carryover_credits := LEAST(
                v_old_cycle_remaining,
                v_credits * (v_carryover_max_percent / 100)
            );
        END IF;
    END IF;

    -- Close any expired current cycles
    UPDATE billing_cycles
    SET is_current = FALSE, closed_at = NOW()
    WHERE tenant_id = p_tenant_id
      AND workspace_id = p_workspace_id
      AND is_current = TRUE
      AND cycle_end <= NOW();

    -- Create new billing cycle with carryover
    INSERT INTO billing_cycles (tenant_id, workspace_id, plan_id, cycle_start, cycle_end, credits_allocated)
    VALUES (
        p_tenant_id,
        p_workspace_id,
        v_plan_id,
        NOW(),
        NOW() + (v_cycle_days || ' days')::INTERVAL,
        v_credits + v_carryover_credits
    )
    RETURNING id INTO v_cycle_id;

    -- Record carryover if any
    IF v_carryover_credits > 0 THEN
        INSERT INTO credit_adjustments (billing_cycle_id, adjustment_type, amount, reason)
        VALUES (v_cycle_id, 'carryover', v_carryover_credits, 'Credit carryover from previous cycle');
    END IF;

    RETURN v_cycle_id;
END;
$$ LANGUAGE plpgsql;

-- Function to compute audit log hash
CREATE OR REPLACE FUNCTION compute_audit_hash(
    p_tenant_id TEXT,
    p_workspace_id TEXT,
    p_event_type TEXT,
    p_description TEXT,
    p_data JSONB,
    p_timestamp TIMESTAMPTZ,
    p_previous_hash TEXT
)
RETURNS TEXT AS $$
DECLARE
    v_content TEXT;
BEGIN
    v_content := COALESCE(p_tenant_id, '') || '|' ||
                 COALESCE(p_workspace_id, '') || '|' ||
                 COALESCE(p_event_type, '') || '|' ||
                 COALESCE(p_description, '') || '|' ||
                 COALESCE(p_data::TEXT, '') || '|' ||
                 COALESCE(p_timestamp::TEXT, '') || '|' ||
                 COALESCE(p_previous_hash, '');

    RETURN encode(sha256(v_content::bytea), 'hex');
END;
$$ LANGUAGE plpgsql IMMUTABLE;

-- Function to append audit log with hash chain
CREATE OR REPLACE FUNCTION append_audit_log(
    p_tenant_id TEXT,
    p_workspace_id TEXT,
    p_billing_cycle_id UUID,
    p_event_type TEXT,
    p_description TEXT,
    p_data JSONB DEFAULT NULL,
    p_user_id TEXT DEFAULT NULL,
    p_ip_address INET DEFAULT NULL
)
RETURNS audit_logs AS $$
DECLARE
    v_previous_hash TEXT;
    v_content_hash TEXT;
    v_result audit_logs;
    v_timestamp TIMESTAMPTZ := NOW();
BEGIN
    -- Get previous hash
    SELECT content_hash INTO v_previous_hash
    FROM audit_logs
    WHERE tenant_id = p_tenant_id
      AND workspace_id = p_workspace_id
    ORDER BY sequence_number DESC
    LIMIT 1;

    v_previous_hash := COALESCE(v_previous_hash, '');

    -- Compute content hash
    v_content_hash := compute_audit_hash(
        p_tenant_id, p_workspace_id, p_event_type,
        p_description, p_data, v_timestamp, v_previous_hash
    );

    -- Insert new entry
    INSERT INTO audit_logs (
        tenant_id, workspace_id, billing_cycle_id,
        event_type, description, data,
        user_id, ip_address, timestamp,
        previous_hash, content_hash
    )
    VALUES (
        p_tenant_id, p_workspace_id, p_billing_cycle_id,
        p_event_type, p_description, p_data,
        p_user_id, p_ip_address, v_timestamp,
        v_previous_hash, v_content_hash
    )
    RETURNING * INTO v_result;

    RETURN v_result;
END;
$$ LANGUAGE plpgsql;

-- Function to verify audit log integrity
CREATE OR REPLACE FUNCTION verify_audit_log_integrity(
    p_tenant_id TEXT,
    p_workspace_id TEXT,
    p_billing_cycle_id UUID DEFAULT NULL
)
RETURNS TABLE(
    is_valid BOOLEAN,
    total_entries INTEGER,
    verified_entries INTEGER,
    first_issue_sequence BIGINT,
    first_issue_type TEXT,
    first_issue_description TEXT
) AS $$
DECLARE
    v_entry RECORD;
    v_prev_hash TEXT := '';
    v_prev_seq BIGINT := 0;
    v_computed_hash TEXT;
    v_total INTEGER := 0;
    v_verified INTEGER := 0;
    v_first_issue_seq BIGINT := NULL;
    v_first_issue_type TEXT := NULL;
    v_first_issue_desc TEXT := NULL;
BEGIN
    FOR v_entry IN
        SELECT * FROM audit_logs
        WHERE tenant_id = p_tenant_id
          AND workspace_id = p_workspace_id
          AND (p_billing_cycle_id IS NULL OR billing_cycle_id = p_billing_cycle_id)
        ORDER BY sequence_number
    LOOP
        v_total := v_total + 1;

        -- Check sequence continuity
        IF v_prev_seq > 0 AND v_entry.sequence_number != v_prev_seq + 1 THEN
            IF v_first_issue_seq IS NULL THEN
                v_first_issue_seq := v_entry.sequence_number;
                v_first_issue_type := 'sequence_gap';
                v_first_issue_desc := format('Gap between sequence %s and %s', v_prev_seq, v_entry.sequence_number);
            END IF;
        END IF;

        -- Check hash chain
        IF v_entry.previous_hash != v_prev_hash THEN
            IF v_first_issue_seq IS NULL THEN
                v_first_issue_seq := v_entry.sequence_number;
                v_first_issue_type := 'chain_broken';
                v_first_issue_desc := format('Previous hash mismatch at sequence %s', v_entry.sequence_number);
            END IF;
        END IF;

        -- Verify content hash
        v_computed_hash := compute_audit_hash(
            v_entry.tenant_id, v_entry.workspace_id, v_entry.event_type,
            v_entry.description, v_entry.data, v_entry.timestamp, v_entry.previous_hash
        );

        IF v_entry.content_hash != v_computed_hash THEN
            IF v_first_issue_seq IS NULL THEN
                v_first_issue_seq := v_entry.sequence_number;
                v_first_issue_type := 'hash_mismatch';
                v_first_issue_desc := format('Content hash mismatch at sequence %s', v_entry.sequence_number);
            END IF;
        ELSE
            v_verified := v_verified + 1;
        END IF;

        v_prev_hash := v_entry.content_hash;
        v_prev_seq := v_entry.sequence_number;
    END LOOP;

    RETURN QUERY SELECT
        v_first_issue_seq IS NULL,
        v_total,
        v_verified,
        v_first_issue_seq,
        v_first_issue_type,
        v_first_issue_desc;
END;
$$ LANGUAGE plpgsql;

-- Trigger to update updated_at on tenant_subscriptions
DROP TRIGGER IF EXISTS update_tenant_subscriptions_updated_at ON tenant_subscriptions;
CREATE TRIGGER update_tenant_subscriptions_updated_at
    BEFORE UPDATE ON tenant_subscriptions
    FOR EACH ROW
    EXECUTE FUNCTION update_updated_at_column();
