-- ============================================================================
-- Fix Embedding Dimensions for OpenAI text-embedding-3-small
-- Migration: fix_embedding_dimensions.sql
-- ============================================================================
-- Problem: Database has vector(768) but OpenAI text-embedding-3-small produces 1536 dimensions
-- Solution: Update the memories table to use vector(1536)
--
-- IMPORTANT: This migration will NULL out existing embeddings if they have wrong dimensions.
-- After running this, use the reembed_memories tool to regenerate embeddings.
-- ============================================================================

-- Check current dimension and update if needed
DO $$
DECLARE
    current_dimension integer;
    target_dimension integer := 1536;  -- OpenAI text-embedding-3-small dimension
    memory_count integer;
BEGIN
    -- Get current vector dimension from the embedding column
    SELECT atttypmod - 4  -- pgvector stores dimension as atttypmod - 4
    INTO current_dimension
    FROM pg_attribute a
    JOIN pg_class c ON a.attrelid = c.oid
    JOIN pg_namespace n ON c.relnamespace = n.oid
    WHERE c.relname = 'memories'
      AND a.attname = 'embedding'
      AND n.nspname = 'public';

    IF current_dimension IS NULL THEN
        RAISE NOTICE 'No embedding column found in memories table';
        RETURN;
    END IF;

    RAISE NOTICE 'Current embedding dimension: %, Target: %', current_dimension, target_dimension;

    IF current_dimension = target_dimension THEN
        RAISE NOTICE 'Embedding dimension already correct (%), no migration needed', target_dimension;
        RETURN;
    END IF;

    -- Count memories with embeddings
    SELECT COUNT(*) INTO memory_count FROM memories WHERE embedding IS NOT NULL;
    RAISE NOTICE 'Found % memories with embeddings that will need re-embedding', memory_count;

    -- Step 1: Drop the old embedding column
    ALTER TABLE memories DROP COLUMN IF EXISTS embedding;

    -- Step 2: Add new embedding column with correct dimension
    ALTER TABLE memories ADD COLUMN embedding vector(1536);

    -- Step 3: Recreate the embedding index
    DROP INDEX IF EXISTS idx_memories_embedding;
    CREATE INDEX idx_memories_embedding ON memories
        USING ivfflat (embedding vector_cosine_ops) WITH (lists = 100);

    RAISE NOTICE 'Migration complete. Run reembed_memories to regenerate % embeddings', memory_count;
END $$;

-- Also update the event sourcing memories table if it exists
DO $$
DECLARE
    current_dimension integer;
BEGIN
    -- Check if memory_events table exists and has embedding column
    SELECT atttypmod - 4
    INTO current_dimension
    FROM pg_attribute a
    JOIN pg_class c ON a.attrelid = c.oid
    JOIN pg_namespace n ON c.relnamespace = n.oid
    WHERE c.relname = 'memory_events'
      AND a.attname = 'embedding'
      AND n.nspname = 'public';

    IF current_dimension IS NOT NULL AND current_dimension != 1536 THEN
        RAISE NOTICE 'Updating memory_events embedding dimension from % to 1536', current_dimension;
        ALTER TABLE memory_events DROP COLUMN IF EXISTS embedding;
        ALTER TABLE memory_events ADD COLUMN embedding vector(1536);
    END IF;
END $$;

-- Update shadow memories table if it exists
DO $$
DECLARE
    current_dimension integer;
BEGIN
    SELECT atttypmod - 4
    INTO current_dimension
    FROM pg_attribute a
    JOIN pg_class c ON a.attrelid = c.oid
    JOIN pg_namespace n ON c.relnamespace = n.oid
    WHERE c.relname = 'shadow_memories'
      AND a.attname = 'embedding'
      AND n.nspname = 'public';

    IF current_dimension IS NOT NULL AND current_dimension != 1536 THEN
        RAISE NOTICE 'Updating shadow_memories embedding dimension from % to 1536', current_dimension;
        ALTER TABLE shadow_memories DROP COLUMN IF EXISTS embedding;
        ALTER TABLE shadow_memories ADD COLUMN embedding vector(1536);
    END IF;
END $$;

-- Update embedding_cache table if it exists
DO $$
DECLARE
    current_dimension integer;
BEGIN
    SELECT atttypmod - 4
    INTO current_dimension
    FROM pg_attribute a
    JOIN pg_class c ON a.attrelid = c.oid
    JOIN pg_namespace n ON c.relnamespace = n.oid
    WHERE c.relname = 'embedding_cache'
      AND a.attname = 'embedding'
      AND n.nspname = 'public';

    IF current_dimension IS NOT NULL AND current_dimension != 1536 THEN
        RAISE NOTICE 'Updating embedding_cache embedding dimension from % to 1536', current_dimension;
        ALTER TABLE embedding_cache DROP COLUMN IF EXISTS embedding;
        ALTER TABLE embedding_cache ADD COLUMN embedding vector(1536);
    END IF;
END $$;

-- ============================================================================
-- Migration complete - run reembed_memories tool to regenerate embeddings
-- ============================================================================
