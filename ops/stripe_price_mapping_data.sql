-- Stripe Price Mapping Data
-- Insert your Stripe price IDs for each plan.
-- Run this after creating your products and prices in Stripe Dashboard.
--
-- To get your price IDs:
--   1. Go to Stripe Dashboard > Products
--   2. Click on each product and copy the price ID (starts with price_)
--   3. Replace the placeholders below with your actual price IDs
--
-- IMPORTANT: These are example price IDs. Replace with your actual Stripe price IDs!

-- First ensure the tenant_plans table has the required plans
INSERT INTO tenant_plans (id, plan_name, display_name, credits_per_cycle, max_memories, max_entities, is_active, created_at)
VALUES
    (gen_random_uuid(), 'free', 'Free', 1000, 10000, 5000, true, NOW()),
    (gen_random_uuid(), 'starter', 'Starter', 10000, 100000, 50000, true, NOW()),
    (gen_random_uuid(), 'pro', 'Pro', 100000, 1000000, 500000, true, NOW()),
    (gen_random_uuid(), 'enterprise', 'Enterprise', 999999999, 999999999, 999999999, true, NOW())
ON CONFLICT (plan_name) DO UPDATE SET
    display_name = EXCLUDED.display_name,
    credits_per_cycle = EXCLUDED.credits_per_cycle,
    max_memories = EXCLUDED.max_memories,
    max_entities = EXCLUDED.max_entities,
    is_active = true,
    updated_at = NOW();

-- Insert Stripe price mappings
-- REPLACE THESE WITH YOUR ACTUAL STRIPE PRICE IDs!
-- The product IDs and price IDs shown are examples only.

-- Starter Plan ($9/month)
-- Create in Stripe: Product name "SerialMemory Starter"
INSERT INTO stripe_price_mapping (id, plan_name, stripe_price_id, stripe_product_id, is_active, created_at)
VALUES (
    gen_random_uuid(),
    'starter',
    'price_starter_monthly', -- Replace with your actual price ID
    'prod_starter',          -- Replace with your actual product ID
    true,
    NOW()
)
ON CONFLICT (plan_name) DO UPDATE SET
    stripe_price_id = EXCLUDED.stripe_price_id,
    stripe_product_id = EXCLUDED.stripe_product_id,
    is_active = true,
    updated_at = NOW();

-- Pro Plan ($29/month)
-- Create in Stripe: Product name "SerialMemory Pro"
INSERT INTO stripe_price_mapping (id, plan_name, stripe_price_id, stripe_product_id, is_active, created_at)
VALUES (
    gen_random_uuid(),
    'pro',
    'price_pro_monthly',     -- Replace with your actual price ID
    'prod_pro',              -- Replace with your actual product ID
    true,
    NOW()
)
ON CONFLICT (plan_name) DO UPDATE SET
    stripe_price_id = EXCLUDED.stripe_price_id,
    stripe_product_id = EXCLUDED.stripe_product_id,
    is_active = true,
    updated_at = NOW();

-- Enterprise Plan ($299/month)
-- Create in Stripe: Product name "SerialMemory Enterprise"
INSERT INTO stripe_price_mapping (id, plan_name, stripe_price_id, stripe_product_id, is_active, created_at)
VALUES (
    gen_random_uuid(),
    'enterprise',
    'price_enterprise_monthly', -- Replace with your actual price ID
    'prod_enterprise',          -- Replace with your actual product ID
    true,
    NOW()
)
ON CONFLICT (plan_name) DO UPDATE SET
    stripe_price_id = EXCLUDED.stripe_price_id,
    stripe_product_id = EXCLUDED.stripe_product_id,
    is_active = true,
    updated_at = NOW();

-- Verify the mappings
SELECT
    tp.plan_name,
    tp.display_name,
    tp.credits_per_cycle,
    spm.stripe_price_id,
    spm.stripe_product_id,
    spm.is_active
FROM tenant_plans tp
LEFT JOIN stripe_price_mapping spm ON tp.plan_name = spm.plan_name
ORDER BY tp.credits_per_cycle;
