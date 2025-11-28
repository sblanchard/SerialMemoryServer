-- Email Verification and Passwordless Login Schema
-- Run after multi_tenant_schema.sql

-- Email verification tokens table
CREATE TABLE IF NOT EXISTS email_verification_tokens (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    user_id VARCHAR(255) NOT NULL,
    email VARCHAR(255) NOT NULL,
    token_hash VARCHAR(64) NOT NULL, -- SHA-256 hash of token
    token_type VARCHAR(50) NOT NULL DEFAULT 'verification', -- 'verification', 'login', 'password_reset'
    expires_at TIMESTAMPTZ NOT NULL,
    used_at TIMESTAMPTZ,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    ip_address INET,
    user_agent TEXT,
    CONSTRAINT unique_active_token UNIQUE (email, token_type, used_at) -- Only one active token per type
);

-- Indexes for fast lookup
CREATE INDEX IF NOT EXISTS idx_email_verification_tokens_hash ON email_verification_tokens(token_hash);
CREATE INDEX IF NOT EXISTS idx_email_verification_tokens_email ON email_verification_tokens(email, token_type);
CREATE INDEX IF NOT EXISTS idx_email_verification_tokens_expires ON email_verification_tokens(expires_at) WHERE used_at IS NULL;
CREATE INDEX IF NOT EXISTS idx_email_verification_tokens_tenant ON email_verification_tokens(tenant_id);

-- User email verification status
ALTER TABLE tenant_users ADD COLUMN IF NOT EXISTS email_verified BOOLEAN DEFAULT FALSE;
ALTER TABLE tenant_users ADD COLUMN IF NOT EXISTS email_verified_at TIMESTAMPTZ;

-- Email audit log for security
CREATE TABLE IF NOT EXISTS email_audit_log (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID REFERENCES tenants(id) ON DELETE SET NULL,
    recipient_email VARCHAR(255) NOT NULL,
    email_type VARCHAR(50) NOT NULL, -- 'verification', 'login_link', 'api_key_created', 'security_alert'
    subject TEXT,
    status VARCHAR(20) NOT NULL DEFAULT 'sent', -- 'sent', 'delivered', 'bounced', 'failed'
    operation_id VARCHAR(255), -- Azure Communication Services operation ID
    error_message TEXT,
    ip_address INET,
    user_agent TEXT,
    metadata JSONB,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Indexes for audit queries
CREATE INDEX IF NOT EXISTS idx_email_audit_recipient ON email_audit_log(recipient_email);
CREATE INDEX IF NOT EXISTS idx_email_audit_tenant ON email_audit_log(tenant_id);
CREATE INDEX IF NOT EXISTS idx_email_audit_type ON email_audit_log(email_type);
CREATE INDEX IF NOT EXISTS idx_email_audit_created ON email_audit_log(created_at);
CREATE INDEX IF NOT EXISTS idx_email_audit_status ON email_audit_log(status) WHERE status != 'sent';

-- Magic link login attempts (rate limiting)
CREATE TABLE IF NOT EXISTS login_attempts (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    email VARCHAR(255) NOT NULL,
    ip_address INET NOT NULL,
    success BOOLEAN NOT NULL DEFAULT FALSE,
    failure_reason VARCHAR(100),
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_login_attempts_email ON login_attempts(email, created_at);
CREATE INDEX IF NOT EXISTS idx_login_attempts_ip ON login_attempts(ip_address, created_at);

-- Function to clean up expired tokens
CREATE OR REPLACE FUNCTION cleanup_expired_email_tokens()
RETURNS INTEGER AS $$
DECLARE
    deleted_count INTEGER;
BEGIN
    DELETE FROM email_verification_tokens
    WHERE expires_at < NOW() - INTERVAL '7 days'
       OR (used_at IS NOT NULL AND used_at < NOW() - INTERVAL '30 days');
    GET DIAGNOSTICS deleted_count = ROW_COUNT;
    RETURN deleted_count;
END;
$$ LANGUAGE plpgsql;

-- RLS policies for email tables
ALTER TABLE email_verification_tokens ENABLE ROW LEVEL SECURITY;
ALTER TABLE email_audit_log ENABLE ROW LEVEL SECURITY;
ALTER TABLE login_attempts ENABLE ROW LEVEL SECURITY;

-- Email verification tokens: tenant isolation
CREATE POLICY email_tokens_tenant_isolation ON email_verification_tokens
    USING (tenant_id = current_setting('app.tenant_id', true)::uuid);

-- Email audit log: tenant can only see their own logs
CREATE POLICY email_audit_tenant_isolation ON email_audit_log
    USING (tenant_id IS NULL OR tenant_id = current_setting('app.tenant_id', true)::uuid);

-- Login attempts: no RLS (admin only table)
CREATE POLICY login_attempts_admin_only ON login_attempts
    USING (current_setting('app.is_admin', true)::boolean = true);

COMMENT ON TABLE email_verification_tokens IS 'Stores hashed tokens for email verification and passwordless login';
COMMENT ON TABLE email_audit_log IS 'Audit trail for all emails sent through the system';
COMMENT ON TABLE login_attempts IS 'Tracks login attempts for rate limiting and security monitoring';
