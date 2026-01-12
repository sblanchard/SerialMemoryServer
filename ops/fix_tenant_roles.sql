-- Fix tenant user roles for PublicSaaS mode
-- Only stephan@serialcoder.com should be owner
-- All other users should be member

-- Show current state
SELECT tu.user_id, tu.role, t.name as tenant_name 
FROM tenant_users tu 
JOIN tenants t ON tu.tenant_id = t.id 
ORDER BY tu.created_at;

-- Downgrade all non-root users to member
UPDATE tenant_users 
SET role = 'member', updated_at = NOW()
WHERE user_id != 'stephan@serialcoder.com'
  AND role = 'owner';

-- Ensure root admin is owner
UPDATE tenant_users 
SET role = 'owner', updated_at = NOW()
WHERE user_id = 'stephan@serialcoder.com';

-- Show updated state
SELECT tu.user_id, tu.role, t.name as tenant_name 
FROM tenant_users tu 
JOIN tenants t ON tu.tenant_id = t.id 
ORDER BY tu.created_at;
