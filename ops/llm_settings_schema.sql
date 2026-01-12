-- LLM Provider Settings for SerialMemory
-- Adds tenant-level OpenAI/LLM configuration
-- Run this after multi_tenant_schema.sql

-- =============================================================================
-- ADD LLM SETTINGS TO TENANT_SETTINGS
-- =============================================================================

-- LLM Provider: 'ollama' (default), 'openai', 'azure'
ALTER TABLE tenant_settings
ADD COLUMN IF NOT EXISTS llm_provider TEXT DEFAULT 'ollama'
    CHECK (llm_provider IN ('ollama', 'openai', 'azure'));

-- OpenAI settings (encrypted API key stored in secrets, only key ID stored here)
ALTER TABLE tenant_settings
ADD COLUMN IF NOT EXISTS openai_key_id TEXT;  -- Reference to encrypted secret

ALTER TABLE tenant_settings
ADD COLUMN IF NOT EXISTS openai_model TEXT DEFAULT 'gpt-4.1-mini';

ALTER TABLE tenant_settings
ADD COLUMN IF NOT EXISTS openai_embed_model TEXT DEFAULT 'text-embedding-3-small';

ALTER TABLE tenant_settings
ADD COLUMN IF NOT EXISTS openai_endpoint TEXT;  -- Optional custom endpoint (Azure OpenAI)

-- Embedding provider (can be different from chat provider)
ALTER TABLE tenant_settings
ADD COLUMN IF NOT EXISTS embedding_provider TEXT DEFAULT 'ollama'
    CHECK (embedding_provider IN ('ollama', 'openai', 'azure'));

-- Entity extraction provider
ALTER TABLE tenant_settings
ADD COLUMN IF NOT EXISTS entity_extraction_provider TEXT DEFAULT 'ollama'
    CHECK (entity_extraction_provider IN ('ollama', 'openai', 'azure'));

-- =============================================================================
-- TENANT SECRETS TABLE (for encrypted API keys)
-- =============================================================================

CREATE TABLE IF NOT EXISTS tenant_secrets (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    name TEXT NOT NULL,  -- e.g., 'openai_api_key'
    encrypted_value TEXT NOT NULL,  -- AES-256 encrypted
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    CONSTRAINT tenant_secrets_name_unique UNIQUE (tenant_id, name)
);

CREATE INDEX IF NOT EXISTS idx_tenant_secrets_tenant ON tenant_secrets(tenant_id);

-- Trigger for updated_at
DROP TRIGGER IF EXISTS update_tenant_secrets_updated_at ON tenant_secrets;
CREATE TRIGGER update_tenant_secrets_updated_at
    BEFORE UPDATE ON tenant_secrets
    FOR EACH ROW EXECUTE FUNCTION update_updated_at_column();

-- =============================================================================
-- COMMENTS
-- =============================================================================

COMMENT ON COLUMN tenant_settings.llm_provider IS 'Primary LLM provider for chat: ollama (default), openai, azure';
COMMENT ON COLUMN tenant_settings.openai_key_id IS 'Reference to encrypted OpenAI API key in tenant_secrets';
COMMENT ON COLUMN tenant_settings.openai_model IS 'OpenAI model for chat completions (default: gpt-4.1-mini)';
COMMENT ON COLUMN tenant_settings.openai_embed_model IS 'OpenAI model for embeddings (default: text-embedding-3-small)';
COMMENT ON COLUMN tenant_settings.openai_endpoint IS 'Custom endpoint for Azure OpenAI or compatible APIs';
COMMENT ON COLUMN tenant_settings.embedding_provider IS 'Provider for embeddings: ollama (default), openai, azure';
COMMENT ON COLUMN tenant_settings.entity_extraction_provider IS 'Provider for entity extraction: ollama (default), openai, azure';
COMMENT ON TABLE tenant_secrets IS 'Encrypted secrets storage for tenant API keys';
