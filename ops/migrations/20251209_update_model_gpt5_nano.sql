-- Migration: Update default LLM model to gpt-5-nano
-- Date: 2025-12-09

-- Update existing tenants using the old model
UPDATE tenant_settings
SET openai_model = 'gpt-5-nano',
    updated_at = NOW()
WHERE openai_model = 'gpt-4.1-mini'
   OR openai_model IS NULL;

-- Verify
SELECT openai_model, COUNT(*) as tenant_count
FROM tenant_settings
GROUP BY openai_model;
