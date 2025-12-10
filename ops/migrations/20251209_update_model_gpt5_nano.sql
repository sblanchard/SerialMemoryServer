-- Migration: Update default LLM model to gpt-5-nano-2025-08-07
-- Date: 2025-12-10

-- Update existing tenants using the old model
UPDATE tenant_settings
SET openai_model = 'gpt-5-nano-2025-08-07',
    updated_at = NOW()
WHERE openai_model = 'gpt-4.1-mini'
   OR openai_model = 'gpt-5-nano'
   OR openai_model IS NULL;

-- Verify
SELECT openai_model, COUNT(*) as tenant_count
FROM tenant_settings
GROUP BY openai_model;
