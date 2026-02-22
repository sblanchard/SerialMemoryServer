-- Migration: Add structured observation fields to memories table
-- Supports: title, facts, concepts, files_read, files_modified
-- These fields enable progressive disclosure (compact search) and structured filtering.
-- All fields are nullable for backward compatibility.

-- Phase 1: Add new columns
ALTER TABLE memories ADD COLUMN IF NOT EXISTS title TEXT;
ALTER TABLE memories ADD COLUMN IF NOT EXISTS facts JSONB;
ALTER TABLE memories ADD COLUMN IF NOT EXISTS concepts JSONB;
ALTER TABLE memories ADD COLUMN IF NOT EXISTS files_read JSONB;
ALTER TABLE memories ADD COLUMN IF NOT EXISTS files_modified JSONB;

-- Phase 2: Index for concept-based filtering
CREATE INDEX IF NOT EXISTS idx_memories_concepts ON memories USING GIN (concepts);

-- Phase 3: Index for file-based filtering
CREATE INDEX IF NOT EXISTS idx_memories_files_modified ON memories USING GIN (files_modified);

-- Phase 4: Generate titles for existing memories that lack one (first line or first 100 chars)
-- This is idempotent and non-destructive
UPDATE memories
SET title = CASE
    WHEN position(E'\n' in content) > 0 AND position(E'\n' in content) <= 120
        THEN left(content, position(E'\n' in content) - 1)
    ELSE left(content, 100)
END
WHERE title IS NULL;

-- Record migration
CREATE TABLE IF NOT EXISTS schema_migrations (
    name TEXT PRIMARY KEY,
    applied_at TIMESTAMPTZ DEFAULT NOW()
);
INSERT INTO schema_migrations (name) VALUES ('migrate_structured_observations')
ON CONFLICT (name) DO NOTHING;
