-- Migration: Fix tenant_plans columns for comprehensive fixes
-- The production table may have different column names than expected

-- Add missing columns if they don't exist
DO $$
BEGIN
    -- Add rate_limit_rpm if it doesn't exist
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_name = 'tenant_plans' AND column_name = 'rate_limit_rpm'
    ) THEN
        -- Check if rate_limit_per_minute exists and rename it
        IF EXISTS (
            SELECT 1 FROM information_schema.columns
            WHERE table_name = 'tenant_plans' AND column_name = 'rate_limit_per_minute'
        ) THEN
            ALTER TABLE tenant_plans RENAME COLUMN rate_limit_per_minute TO rate_limit_rpm;
            RAISE NOTICE 'Renamed rate_limit_per_minute to rate_limit_rpm';
        ELSE
            ALTER TABLE tenant_plans ADD COLUMN rate_limit_rpm INTEGER DEFAULT 60;
            RAISE NOTICE 'Added rate_limit_rpm column';
        END IF;
    END IF;

    -- Add rate_limit_rpd if it doesn't exist
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_name = 'tenant_plans' AND column_name = 'rate_limit_rpd'
    ) THEN
        ALTER TABLE tenant_plans ADD COLUMN rate_limit_rpd INTEGER DEFAULT 10000;
        RAISE NOTICE 'Added rate_limit_rpd column';
    END IF;

    -- Add features column if it doesn't exist
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_name = 'tenant_plans' AND column_name = 'features'
    ) THEN
        ALTER TABLE tenant_plans ADD COLUMN features JSONB DEFAULT '[]'::jsonb;
        RAISE NOTICE 'Added features column';
    END IF;
END $$;

-- Update existing plans with default rate limits if needed
UPDATE tenant_plans SET
    rate_limit_rpm = COALESCE(rate_limit_rpm, 60),
    rate_limit_rpd = COALESCE(rate_limit_rpd, 10000)
WHERE rate_limit_rpm IS NULL OR rate_limit_rpd IS NULL;

SELECT 'tenant_plans columns migration complete' as status;
