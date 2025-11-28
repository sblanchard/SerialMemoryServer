-- Developer Onboarding Schema Updates
-- Adds one_time_viewed tracking for API keys

-- Add one_time_viewed column to tenant_api_keys if not exists
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                   WHERE table_name = 'tenant_api_keys' AND column_name = 'one_time_viewed') THEN
        ALTER TABLE tenant_api_keys ADD COLUMN one_time_viewed BOOLEAN NOT NULL DEFAULT FALSE;
    END IF;
END $$;

-- Index for finding unviewed keys
CREATE INDEX IF NOT EXISTS idx_tenant_api_keys_one_time_viewed
ON tenant_api_keys(tenant_id, one_time_viewed)
WHERE one_time_viewed = FALSE AND revoked_at IS NULL;

-- Onboarding audit table
CREATE TABLE IF NOT EXISTS onboarding_events (
    id UUID PRIMARY KEY,
    tenant_id UUID REFERENCES tenants(id) ON DELETE CASCADE,
    user_email TEXT NOT NULL,
    event_type TEXT NOT NULL,
    event_data JSONB DEFAULT '{}',
    ip_address INET,
    user_agent TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_onboarding_events_tenant ON onboarding_events(tenant_id);
CREATE INDEX IF NOT EXISTS idx_onboarding_events_email ON onboarding_events(user_email);
CREATE INDEX IF NOT EXISTS idx_onboarding_events_created ON onboarding_events(created_at DESC);

-- Enable RLS
ALTER TABLE onboarding_events ENABLE ROW LEVEL SECURITY;

-- Grant permissions
GRANT ALL ON onboarding_events TO postgres;
