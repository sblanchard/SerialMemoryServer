-- Migration: Add memory_type column to memories table
-- Types: error, decision, pattern, learning, knowledge (default), session_summary, auto_capture

-- Add column with default
ALTER TABLE memories ADD COLUMN IF NOT EXISTS memory_type TEXT NOT NULL DEFAULT 'knowledge';

-- Backfill from existing metadata JSONB (memory_type key takes precedence over type key)
UPDATE memories SET memory_type = metadata->>'memory_type'
WHERE metadata->>'memory_type' IS NOT NULL AND memory_type = 'knowledge';

UPDATE memories SET memory_type = metadata->>'type'
WHERE metadata->>'type' IS NOT NULL AND memory_type = 'knowledge'
  AND metadata->>'memory_type' IS NULL;

-- Composite indexes for filtered queries
CREATE INDEX IF NOT EXISTS idx_memories_type ON memories(tenant_id, workspace_id, memory_type);
CREATE INDEX IF NOT EXISTS idx_memories_type_created ON memories(tenant_id, workspace_id, memory_type, created_at DESC);
