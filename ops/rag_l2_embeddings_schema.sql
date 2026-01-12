-- =============================================================================
-- Personalized RAG L2 Embeddings Schema
-- =============================================================================
-- Creates the L2 embedding index for fast semantic retrieval in RAG pipeline.
-- L2 summaries are the canonical retrieval surface for personalized RAG.
--
-- Features:
--   - Vector embeddings for L2 summaries (pgvector)
--   - Multi-tenant isolation via RLS
--   - IVFFlat index for fast similarity search
--   - Keywords extracted from L2 for hybrid search
-- =============================================================================

BEGIN;

-- =============================================================================
-- STEP 1: Memory L2 Embeddings Table
-- =============================================================================
-- This table stores embeddings specifically for L2 summaries, enabling
-- fast semantic search across summarized memories.

CREATE TABLE IF NOT EXISTS memory_l2_embeddings (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id       UUID NOT NULL,
    memory_id       UUID NOT NULL,
    embedding       vector(1536),           -- OpenAI text-embedding-3-small dimension
    summary         TEXT NOT NULL,          -- Duplicate L2 summary for quick access
    keywords        TEXT[] DEFAULT '{}',    -- Keywords from L2 for hybrid search
    importance      DECIMAL(4,3) DEFAULT 0.5 CHECK (importance >= 0 AND importance <= 1),
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    -- Ensure one embedding per memory per tenant
    CONSTRAINT unique_memory_l2_embedding UNIQUE (tenant_id, memory_id)
);

-- =============================================================================
-- STEP 2: Indexes
-- =============================================================================

-- Tenant isolation index (fast tenant filtering)
CREATE INDEX IF NOT EXISTS idx_l2_embeddings_tenant
    ON memory_l2_embeddings(tenant_id);

-- Memory lookup index
CREATE INDEX IF NOT EXISTS idx_l2_embeddings_memory
    ON memory_l2_embeddings(memory_id);

-- Combined tenant + created_at for recent queries
CREATE INDEX IF NOT EXISTS idx_l2_embeddings_tenant_created
    ON memory_l2_embeddings(tenant_id, created_at DESC);

-- Vector index for similarity search (IVFFlat for large datasets)
-- Using cosine distance as it works best with normalized embeddings
DO $$
BEGIN
    -- IVFFlat requires at least lists*10 rows for optimal performance
    -- We start with lists=100, suitable for up to ~1M embeddings
    CREATE INDEX IF NOT EXISTS idx_l2_embeddings_vector
        ON memory_l2_embeddings
        USING ivfflat (embedding vector_cosine_ops)
        WITH (lists = 100);
EXCEPTION WHEN others THEN
    RAISE NOTICE 'Vector index creation deferred (table may be empty): %', SQLERRM;
END $$;

-- GIN index for keyword array search (for hybrid retrieval)
CREATE INDEX IF NOT EXISTS idx_l2_embeddings_keywords
    ON memory_l2_embeddings USING GIN (keywords);

-- Full-text search on summary
ALTER TABLE memory_l2_embeddings
    ADD COLUMN IF NOT EXISTS summary_tsvector tsvector
    GENERATED ALWAYS AS (to_tsvector('english', summary)) STORED;

CREATE INDEX IF NOT EXISTS idx_l2_embeddings_summary_fts
    ON memory_l2_embeddings USING GIN (summary_tsvector);

-- =============================================================================
-- STEP 3: Enable RLS
-- =============================================================================

ALTER TABLE memory_l2_embeddings ENABLE ROW LEVEL SECURITY;

-- =============================================================================
-- STEP 4: RLS Policies
-- =============================================================================

-- Unified policy for tenant isolation with internal_admin bypass
DROP POLICY IF EXISTS unified_memory_l2_embeddings ON memory_l2_embeddings;
CREATE POLICY unified_memory_l2_embeddings ON memory_l2_embeddings
    FOR ALL
    USING (
        COALESCE(current_setting('app.role', true), '') = 'internal_admin'
        OR tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
    )
    WITH CHECK (
        COALESCE(current_setting('app.role', true), '') = 'internal_admin'
        OR tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
    );

-- =============================================================================
-- STEP 5: RAG Query Results Table (for caching and analytics)
-- =============================================================================

CREATE TABLE IF NOT EXISTS rag_query_results (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id       UUID NOT NULL,
    user_id         TEXT NOT NULL,
    query           TEXT NOT NULL,
    query_embedding vector(1536),
    answer          TEXT NOT NULL,
    memory_ids      UUID[] NOT NULL,        -- Retrieved memory IDs
    reasoning_trace TEXT,                   -- Optional reasoning explanation
    latency_ms      INTEGER NOT NULL,
    model_name      TEXT NOT NULL,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    metadata        JSONB DEFAULT '{}'
);

-- Indexes for RAG results
CREATE INDEX IF NOT EXISTS idx_rag_results_tenant
    ON rag_query_results(tenant_id, created_at DESC);

CREATE INDEX IF NOT EXISTS idx_rag_results_user
    ON rag_query_results(tenant_id, user_id, created_at DESC);

-- RLS for RAG results
ALTER TABLE rag_query_results ENABLE ROW LEVEL SECURITY;

DROP POLICY IF EXISTS unified_rag_query_results ON rag_query_results;
CREATE POLICY unified_rag_query_results ON rag_query_results
    FOR ALL
    USING (
        COALESCE(current_setting('app.role', true), '') = 'internal_admin'
        OR tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
    )
    WITH CHECK (
        COALESCE(current_setting('app.role', true), '') = 'internal_admin'
        OR tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
    );

-- =============================================================================
-- STEP 6: Helper Functions
-- =============================================================================

-- Function to upsert L2 embedding
CREATE OR REPLACE FUNCTION upsert_l2_embedding(
    p_tenant_id     UUID,
    p_memory_id     UUID,
    p_embedding     vector(1536),
    p_summary       TEXT,
    p_keywords      TEXT[] DEFAULT '{}',
    p_importance    DECIMAL DEFAULT 0.5
)
RETURNS UUID
SECURITY DEFINER
SET search_path = public
AS $$
DECLARE
    v_id UUID;
BEGIN
    INSERT INTO memory_l2_embeddings (tenant_id, memory_id, embedding, summary, keywords, importance)
    VALUES (p_tenant_id, p_memory_id, p_embedding, p_summary, p_keywords, p_importance)
    ON CONFLICT (tenant_id, memory_id)
    DO UPDATE SET
        embedding = EXCLUDED.embedding,
        summary = EXCLUDED.summary,
        keywords = EXCLUDED.keywords,
        importance = EXCLUDED.importance,
        updated_at = NOW()
    RETURNING id INTO v_id;

    RETURN v_id;
END;
$$ LANGUAGE plpgsql;

-- Function to search L2 embeddings by vector similarity
CREATE OR REPLACE FUNCTION search_l2_embeddings(
    p_tenant_id     UUID,
    p_query_embedding vector(1536),
    p_limit         INTEGER DEFAULT 10,
    p_threshold     DECIMAL DEFAULT 0.5
)
RETURNS TABLE (
    memory_id       UUID,
    summary         TEXT,
    keywords        TEXT[],
    importance      DECIMAL,
    similarity      DECIMAL
)
SECURITY DEFINER
SET search_path = public
AS $$
BEGIN
    RETURN QUERY
    SELECT
        e.memory_id,
        e.summary,
        e.keywords,
        e.importance,
        (1 - (e.embedding <=> p_query_embedding))::DECIMAL AS similarity
    FROM memory_l2_embeddings e
    WHERE e.tenant_id = p_tenant_id
      AND (1 - (e.embedding <=> p_query_embedding)) > p_threshold
    ORDER BY e.embedding <=> p_query_embedding
    LIMIT p_limit;
END;
$$ LANGUAGE plpgsql;

-- Function to get memory layers for RAG enrichment
CREATE OR REPLACE FUNCTION get_memory_layers_for_rag(
    p_tenant_id     UUID,
    p_memory_id     UUID
)
RETURNS TABLE (
    layer           TEXT,
    content_json    JSONB
)
SECURITY DEFINER
SET search_path = public
AS $$
BEGIN
    RETURN QUERY
    SELECT
        ml.layer::TEXT,
        ml.content_json
    FROM memory_layers ml
    WHERE ml.tenant_id = p_tenant_id
      AND ml.memory_id = p_memory_id
      AND ml.is_current = TRUE
      AND ml.layer IN ('L1_CONTEXT', 'L2_SUMMARY', 'L3_KNOWLEDGE', 'L4_HEURISTIC')
    ORDER BY ml.layer;
END;
$$ LANGUAGE plpgsql;

-- =============================================================================
-- STEP 7: Grant Permissions
-- =============================================================================

-- Grant to application user
GRANT SELECT, INSERT, UPDATE, DELETE ON memory_l2_embeddings TO contextdb_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON rag_query_results TO contextdb_app;

-- Grant execute on functions
GRANT EXECUTE ON FUNCTION upsert_l2_embedding(UUID, UUID, vector, TEXT, TEXT[], DECIMAL) TO contextdb_app;
GRANT EXECUTE ON FUNCTION search_l2_embeddings(UUID, vector, INTEGER, DECIMAL) TO contextdb_app;
GRANT EXECUTE ON FUNCTION get_memory_layers_for_rag(UUID, UUID) TO contextdb_app;

COMMIT;

-- =============================================================================
-- VERIFICATION
-- =============================================================================

DO $$
BEGIN
    RAISE NOTICE '===============================================';
    RAISE NOTICE 'RAG L2 Embeddings Schema Applied Successfully';
    RAISE NOTICE '===============================================';
    RAISE NOTICE 'Tables created:';
    RAISE NOTICE '  - memory_l2_embeddings (vector index for L2 summaries)';
    RAISE NOTICE '  - rag_query_results (RAG query caching/analytics)';
    RAISE NOTICE '';
    RAISE NOTICE 'Functions created:';
    RAISE NOTICE '  - upsert_l2_embedding()';
    RAISE NOTICE '  - search_l2_embeddings()';
    RAISE NOTICE '  - get_memory_layers_for_rag()';
    RAISE NOTICE '';
    RAISE NOTICE 'Indexes:';
    RAISE NOTICE '  - IVFFlat vector index on embeddings';
    RAISE NOTICE '  - GIN index on keywords array';
    RAISE NOTICE '  - GIN index on summary full-text search';
    RAISE NOTICE '';
    RAISE NOTICE 'RLS policies applied with internal_admin bypass';
    RAISE NOTICE '===============================================';
END $$;
