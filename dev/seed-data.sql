-- =============================================================================
-- SerialMemory Development Seed Data
-- =============================================================================
-- This file creates demo data for local development and testing.
-- It runs after all schema migrations during docker-compose initialization.
-- =============================================================================

-- Insert default self-hosted tenant (if not exists)
INSERT INTO tenants (id, name, slug, status)
VALUES ('00000000-0000-0000-0000-000000000000', 'Self-Hosted', 'self-hosted', 'active')
ON CONFLICT (id) DO NOTHING;

-- Insert demo tenant for testing multi-tenancy
INSERT INTO tenants (id, name, slug, status)
VALUES ('11111111-1111-1111-1111-111111111111', 'Demo Tenant', 'demo', 'active')
ON CONFLICT (id) DO NOTHING;

-- Insert default tenant settings
INSERT INTO tenant_settings (tenant_id, retention_days, region, plan, max_workspaces)
VALUES
    ('00000000-0000-0000-0000-000000000000', 365, 'local', 'self-hosted', 100),
    ('11111111-1111-1111-1111-111111111111', 90, 'us-east-1', 'pro', 10)
ON CONFLICT (tenant_id) DO NOTHING;

-- Insert demo users
INSERT INTO tenant_users (tenant_id, user_id, role)
VALUES
    ('00000000-0000-0000-0000-000000000000', 'admin', 'owner'),
    ('00000000-0000-0000-0000-000000000000', 'demo-user', 'member'),
    ('11111111-1111-1111-1111-111111111111', 'demo-admin', 'owner'),
    ('11111111-1111-1111-1111-111111111111', 'demo-member', 'member')
ON CONFLICT (tenant_id, user_id) DO NOTHING;

-- Insert tenant plans for usage metering
INSERT INTO tenant_plans (id, name, credits_per_cycle, rate_limit_rpm, features)
VALUES
    ('plan-free', 'Free', 1000, 60, '{"search": true, "ingest": true, "export": false}'::jsonb),
    ('plan-pro', 'Pro', 50000, 300, '{"search": true, "ingest": true, "export": true, "admin": true}'::jsonb),
    ('plan-enterprise', 'Enterprise', 1000000, 1000, '{"search": true, "ingest": true, "export": true, "admin": true, "sso": true}'::jsonb),
    ('plan-self-hosted', 'Self-Hosted', 999999999, 9999, '{"search": true, "ingest": true, "export": true, "admin": true}'::jsonb)
ON CONFLICT (id) DO NOTHING;

-- Insert tenant subscriptions
INSERT INTO tenant_subscriptions (tenant_id, plan_id, status, current_cycle_start, current_cycle_end)
VALUES
    ('00000000-0000-0000-0000-000000000000', 'plan-self-hosted', 'active',
     DATE_TRUNC('month', NOW()), DATE_TRUNC('month', NOW()) + INTERVAL '1 month'),
    ('11111111-1111-1111-1111-111111111111', 'plan-pro', 'active',
     DATE_TRUNC('month', NOW()), DATE_TRUNC('month', NOW()) + INTERVAL '1 month')
ON CONFLICT (tenant_id) DO UPDATE SET
    plan_id = EXCLUDED.plan_id,
    status = EXCLUDED.status;

-- Insert some demo memories for testing (self-hosted tenant)
-- Note: These won't have embeddings - they need to be re-embedded via the API
INSERT INTO memories (id, tenant_id, content, source, created_at, updated_at)
VALUES
    (gen_random_uuid(), '00000000-0000-0000-0000-000000000000',
     'SerialMemory is a temporal knowledge graph memory system for AI applications. It provides semantic search, entity extraction, and multi-hop reasoning.',
     'seed-data', NOW(), NOW()),
    (gen_random_uuid(), '00000000-0000-0000-0000-000000000000',
     'The system uses PostgreSQL with pgvector for vector storage, enabling fast semantic similarity search across millions of memories.',
     'seed-data', NOW(), NOW()),
    (gen_random_uuid(), '00000000-0000-0000-0000-000000000000',
     'Entity extraction identifies people, organizations, locations, dates, and technical concepts from memory content, building a knowledge graph.',
     'seed-data', NOW(), NOW())
ON CONFLICT DO NOTHING;

-- Insert demo user personas
INSERT INTO user_personas (id, tenant_id, user_id, attribute_type, attribute_key, attribute_value, confidence)
VALUES
    (gen_random_uuid(), '00000000-0000-0000-0000-000000000000', 'demo-user', 'skill', 'programming', 'Python, TypeScript, C#', 0.9),
    (gen_random_uuid(), '00000000-0000-0000-0000-000000000000', 'demo-user', 'preference', 'communication_style', 'concise and technical', 0.85),
    (gen_random_uuid(), '00000000-0000-0000-0000-000000000000', 'demo-user', 'goal', 'current_project', 'Building AI-powered developer tools', 1.0)
ON CONFLICT DO NOTHING;

-- Output summary
DO $$
BEGIN
    RAISE NOTICE '==========================================';
    RAISE NOTICE 'SerialMemory seed data loaded successfully';
    RAISE NOTICE '==========================================';
    RAISE NOTICE 'Demo tenants: self-hosted, demo';
    RAISE NOTICE 'Demo users: admin, demo-user, demo-admin, demo-member';
    RAISE NOTICE '';
    RAISE NOTICE 'Use these credentials for testing:';
    RAISE NOTICE '  Tenant: 00000000-0000-0000-0000-000000000000 (self-hosted)';
    RAISE NOTICE '  User: demo-user';
    RAISE NOTICE '==========================================';
END $$;
