-- Context Snapshots and Archive Tables
-- Used by SerialMemory.Worker for context persistence and audit trail

-- Context snapshots - active context values persisted from Redis
CREATE TABLE IF NOT EXISTS context_snapshots (
    key TEXT PRIMARY KEY,
    data JSONB NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Index for efficient lookups
CREATE INDEX IF NOT EXISTS idx_context_snapshots_updated
    ON context_snapshots(updated_at DESC);

-- Context archive - audit trail for deleted contexts
CREATE TABLE IF NOT EXISTS context_archive (
    id UUID PRIMARY KEY,
    key TEXT NOT NULL,
    data JSONB NOT NULL,
    deleted_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    deleted_by TEXT,
    reason TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Indexes for archive queries
CREATE INDEX IF NOT EXISTS idx_context_archive_key
    ON context_archive(key);
CREATE INDEX IF NOT EXISTS idx_context_archive_deleted_at
    ON context_archive(deleted_at DESC);
CREATE INDEX IF NOT EXISTS idx_context_archive_deleted_by
    ON context_archive(deleted_by)
    WHERE deleted_by IS NOT NULL;

-- Comment on tables
COMMENT ON TABLE context_snapshots IS 'Active context values persisted from Redis cache';
COMMENT ON TABLE context_archive IS 'Audit trail of deleted context values';
