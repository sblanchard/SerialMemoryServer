-- Migration script for changing embedding dimension
-- Usage: Update EMBEDDING_DIM variable to your target dimension, then run this script
--
-- Common dimensions:
-- - 384: all-MiniLM-L6-v2, all-MiniLM-L12-v2, bge-small-en-v1.5, e5-small-v2
-- - 768: all-mpnet-base-v2, bge-base-en-v1.5, e5-base-v2, gte-base
-- - 1024: e5-large-v2, bge-large-en-v1.5, gte-large
-- - 4096: e5-mistral-7b-instruct (requires significant resources)
--
-- IMPORTANT: After running this migration, you must re-embed all existing memories
-- using the re-embedding tool.

-- Set your target embedding dimension here
\set EMBEDDING_DIM 768

-- Step 1: Create backup of current embeddings (optional but recommended)
CREATE TABLE IF NOT EXISTS memories_embedding_backup AS
SELECT id, embedding, created_at FROM memories WHERE embedding IS NOT NULL;

-- Step 2: Drop the IVFFlat index (required before altering column)
DROP INDEX IF EXISTS idx_memories_embedding;

-- Step 3: Alter the embedding column to new dimension
-- First set all embeddings to NULL (they need to be regenerated anyway)
UPDATE memories SET embedding = NULL;

-- Step 4: Alter the column type
ALTER TABLE memories
ALTER COLUMN embedding TYPE vector(:EMBEDDING_DIM);

-- Step 5: Recreate the IVFFlat index with appropriate list count
-- Note: lists = sqrt(row_count) is a good starting point
-- For < 10K rows use 100, for 10K-100K use 300, for > 100K use 1000
CREATE INDEX idx_memories_embedding
ON memories USING ivfflat (embedding vector_cosine_ops)
WITH (lists = 100);

-- Step 6: Update the embedding_cache table if it exists (from deterministic engine)
DO $$
BEGIN
    IF EXISTS (SELECT FROM information_schema.tables WHERE table_name = 'embedding_cache') THEN
        DROP INDEX IF EXISTS idx_embedding_cache_vector;
        ALTER TABLE embedding_cache
        ALTER COLUMN embedding TYPE vector(:EMBEDDING_DIM);
        CREATE INDEX idx_embedding_cache_vector
        ON embedding_cache USING ivfflat (embedding vector_cosine_ops)
        WITH (lists = 100);
    END IF;
END $$;

-- Step 7: Record the migration in a tracking table
CREATE TABLE IF NOT EXISTS schema_migrations (
    id SERIAL PRIMARY KEY,
    migration_name TEXT NOT NULL,
    embedding_dimension INT NOT NULL,
    applied_at TIMESTAMP DEFAULT NOW()
);

INSERT INTO schema_migrations (migration_name, embedding_dimension)
VALUES ('embedding_dimension_change', :EMBEDDING_DIM);

-- Done! Now run the re-embedding tool to regenerate all embeddings.
