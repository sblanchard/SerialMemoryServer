-- ============================================================================
-- Migration: Add Dedup Performance Indexes
-- Description: Add composite and partial indexes to optimize dedup history queries
-- Date: 2026-02-07
-- ============================================================================

-- Composite index for dedup history queries (tenant + event type + created_at)
-- Optimizes queries like: GET /api/dedup/supersede-history and /api/dedup/merge-history
CREATE INDEX IF NOT EXISTS idx_memory_events_tenant_type_created
    ON memory_events(tenant_id, event_type, created_at DESC);

-- Partial index for supersede queries (more selective than full table)
-- Only indexes rows where event is MemoryInvalidated with supersededById present
CREATE INDEX IF NOT EXISTS idx_memory_events_superseded
    ON memory_events(tenant_id, created_at DESC)
    WHERE event_type::text = 'MemoryInvalidated'
    AND (event_data::jsonb->>'supersededById') IS NOT NULL;

-- Partial index for merge queries
-- Only indexes rows where event is MemoryMerged
CREATE INDEX IF NOT EXISTS idx_memory_events_merged
    ON memory_events(tenant_id, created_at DESC)
    WHERE event_type::text = 'MemoryMerged';

-- Verification query
DO $$
BEGIN
    RAISE NOTICE 'Dedup indexes created successfully';
    RAISE NOTICE 'Composite index: idx_memory_events_tenant_type_created';
    RAISE NOTICE 'Partial index: idx_memory_events_superseded';
    RAISE NOTICE 'Partial index: idx_memory_events_merged';
END $$;
