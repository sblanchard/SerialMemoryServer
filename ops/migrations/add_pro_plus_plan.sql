-- =============================================================================
-- Migration: Add Pro Plus Plan
-- Version: 2024-11-30
-- Description: Adds the Pro Plus tier between Pro and Enterprise
-- =============================================================================

-- Insert Pro Plus plan into tenant_plans table
INSERT INTO tenant_plans (
    id,
    plan_name,
    display_name,
    credits_per_cycle,
    cycle_days,
    max_memories,
    max_entities,
    rate_limit_per_minute,
    credit_carryover_enabled,
    credit_carryover_max_percent,
    is_active,
    metadata,
    created_at,
    updated_at
)
VALUES (
    gen_random_uuid(),
    'pro_plus',
    'Pro Plus',
    20000.00,           -- 20,000 credits per month
    30,                 -- 30-day cycle
    200000,             -- 200,000 max memories
    100000,             -- 100,000 max entities
    200,                -- 200 requests per minute
    TRUE,               -- Credit carryover enabled
    50.00,              -- 50% max carryover
    TRUE,               -- Active
    jsonb_build_object(
        'price_monthly_eur', 79.00,
        'seats_included', 5,
        'features', jsonb_build_array(
            'Shared workspace',
            'Team collaboration',
            'Role-based access control',
            'Credit pooling',
            'All Pro features'
        ),
        'tier_position', 2  -- Between Pro (1) and Enterprise (3)
    ),
    NOW(),
    NOW()
)
ON CONFLICT (plan_name) DO UPDATE SET
    display_name = EXCLUDED.display_name,
    credits_per_cycle = EXCLUDED.credits_per_cycle,
    max_memories = EXCLUDED.max_memories,
    max_entities = EXCLUDED.max_entities,
    rate_limit_per_minute = EXCLUDED.rate_limit_per_minute,
    credit_carryover_enabled = EXCLUDED.credit_carryover_enabled,
    credit_carryover_max_percent = EXCLUDED.credit_carryover_max_percent,
    is_active = EXCLUDED.is_active,
    metadata = EXCLUDED.metadata,
    updated_at = NOW();

-- Add Stripe price mapping placeholder (to be updated with actual Stripe price ID)
INSERT INTO stripe_price_mapping (
    id,
    plan_name,
    stripe_price_id,
    stripe_product_id,
    is_active,
    created_at
)
VALUES (
    gen_random_uuid(),
    'pro_plus',
    'price_pro_plus_placeholder',  -- Replace with actual Stripe price ID
    'prod_pro_plus_placeholder',   -- Replace with actual Stripe product ID
    FALSE,  -- Inactive until configured with real Stripe IDs
    NOW()
)
ON CONFLICT (plan_name) DO NOTHING;

-- Log the migration
DO $$
BEGIN
    RAISE NOTICE '==========================================';
    RAISE NOTICE 'Pro Plus plan added successfully';
    RAISE NOTICE '==========================================';
    RAISE NOTICE 'Plan: pro_plus';
    RAISE NOTICE 'Price: €79.00/month';
    RAISE NOTICE 'Credits: 20,000/month';
    RAISE NOTICE 'Max Memories: 200,000';
    RAISE NOTICE 'Rate Limit: 200 RPM';
    RAISE NOTICE 'Seats Included: 5';
    RAISE NOTICE '';
    RAISE NOTICE 'IMPORTANT: Update stripe_price_mapping with actual Stripe IDs';
    RAISE NOTICE '==========================================';
END $$;
