-- =====================================================
-- DETERMINISTIC COGNITIVE MEMORY ENGINE SCHEMA
-- Extensions for offline, replayable, ONNX-based cognition
-- =====================================================

-- Inference session tracking for deterministic replay
CREATE TABLE IF NOT EXISTS inference_sessions (
    session_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    created_at TIMESTAMP WITH TIME ZONE DEFAULT NOW(),
    completed_at TIMESTAMP WITH TIME ZONE,
    seed BIGINT NOT NULL,
    session_hash CHAR(64) NOT NULL,
    input_hash CHAR(64) NOT NULL,
    output_hash CHAR(64),
    config_snapshot JSONB NOT NULL,
    status VARCHAR(32) DEFAULT 'active',
    parent_session_id UUID REFERENCES inference_sessions(session_id),
    replay_source_session_id UUID REFERENCES inference_sessions(session_id),
    metadata JSONB DEFAULT '{}'::JSONB
);

CREATE INDEX idx_inference_sessions_seed ON inference_sessions(seed);
CREATE INDEX idx_inference_sessions_session_hash ON inference_sessions(session_hash);
CREATE INDEX idx_inference_sessions_created_at ON inference_sessions(created_at);
CREATE INDEX idx_inference_sessions_status ON inference_sessions(status);

-- Reasoning step cache for deterministic execution
CREATE TABLE IF NOT EXISTS reasoning_steps (
    step_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    session_id UUID NOT NULL REFERENCES inference_sessions(session_id) ON DELETE CASCADE,
    step_sequence INTEGER NOT NULL,
    step_type VARCHAR(64) NOT NULL,
    input_hash CHAR(64) NOT NULL,
    output_hash CHAR(64) NOT NULL,
    input_data JSONB NOT NULL,
    output_data JSONB NOT NULL,
    embedding_cache_key CHAR(64),
    duration_ms INTEGER NOT NULL,
    created_at TIMESTAMP WITH TIME ZONE DEFAULT NOW(),
    UNIQUE(session_id, step_sequence)
);

CREATE INDEX idx_reasoning_steps_session ON reasoning_steps(session_id);
CREATE INDEX idx_reasoning_steps_input_hash ON reasoning_steps(input_hash);
CREATE INDEX idx_reasoning_steps_type ON reasoning_steps(step_type);

-- Embedding cache for deterministic retrieval
CREATE TABLE IF NOT EXISTS embedding_cache (
    cache_key CHAR(64) NOT NULL,
    content_hash CHAR(64) NOT NULL,
    embedding vector(384) NOT NULL,
    model_version VARCHAR(64) NOT NULL,
    created_at TIMESTAMP WITH TIME ZONE DEFAULT NOW(),
    last_accessed_at TIMESTAMP WITH TIME ZONE DEFAULT NOW(),
    access_count INTEGER DEFAULT 1,
    is_compiled BOOLEAN DEFAULT FALSE,
    PRIMARY KEY (cache_key, model_version)
);

CREATE INDEX idx_embedding_cache_content ON embedding_cache(content_hash);
CREATE INDEX idx_embedding_cache_compiled ON embedding_cache(is_compiled) WHERE is_compiled = TRUE;
CREATE INDEX idx_embedding_cache_access ON embedding_cache(last_accessed_at);

-- Memory clusters for pre-compiled semantic lookup
CREATE TABLE IF NOT EXISTS memory_clusters (
    cluster_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    cluster_name VARCHAR(256),
    centroid vector(384) NOT NULL,
    cluster_radius REAL NOT NULL,
    memory_count INTEGER DEFAULT 0,
    created_at TIMESTAMP WITH TIME ZONE DEFAULT NOW(),
    updated_at TIMESTAMP WITH TIME ZONE DEFAULT NOW(),
    compilation_version INTEGER DEFAULT 1,
    is_stable BOOLEAN DEFAULT FALSE,
    parent_cluster_id UUID REFERENCES memory_clusters(cluster_id),
    metadata JSONB DEFAULT '{}'::JSONB
);

CREATE INDEX idx_memory_clusters_centroid ON memory_clusters USING ivfflat (centroid vector_cosine_ops) WITH (lists = 50);
CREATE INDEX idx_memory_clusters_stable ON memory_clusters(is_stable) WHERE is_stable = TRUE;

-- Cluster membership for fast lookup
CREATE TABLE IF NOT EXISTS memory_cluster_members (
    memory_id UUID NOT NULL,
    cluster_id UUID NOT NULL REFERENCES memory_clusters(cluster_id) ON DELETE CASCADE,
    distance_to_centroid REAL NOT NULL,
    membership_score REAL NOT NULL,
    assigned_at TIMESTAMP WITH TIME ZONE DEFAULT NOW(),
    PRIMARY KEY(memory_id, cluster_id)
);

CREATE INDEX idx_cluster_members_cluster ON memory_cluster_members(cluster_id);
CREATE INDEX idx_cluster_members_distance ON memory_cluster_members(distance_to_centroid);

-- Semantic lookup tables for pre-compiled retrieval
CREATE TABLE IF NOT EXISTS semantic_index (
    index_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    index_type VARCHAR(32) NOT NULL,
    key_hash CHAR(64) NOT NULL,
    key_tokens TEXT[] NOT NULL,
    target_memory_ids UUID[] NOT NULL,
    relevance_scores REAL[] NOT NULL,
    created_at TIMESTAMP WITH TIME ZONE DEFAULT NOW(),
    compilation_version INTEGER DEFAULT 1,
    UNIQUE(index_type, key_hash)
);

CREATE INDEX idx_semantic_index_type ON semantic_index(index_type);
CREATE INDEX idx_semantic_index_key ON semantic_index(key_hash);
CREATE INDEX idx_semantic_index_tokens ON semantic_index USING GIN(key_tokens);

-- Symbolic rules for hybrid retrieval
CREATE TABLE IF NOT EXISTS symbolic_rules (
    rule_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    rule_name VARCHAR(256) NOT NULL,
    rule_type VARCHAR(64) NOT NULL,
    condition_expression TEXT NOT NULL,
    priority INTEGER DEFAULT 0,
    weight REAL DEFAULT 1.0,
    is_active BOOLEAN DEFAULT TRUE,
    created_at TIMESTAMP WITH TIME ZONE DEFAULT NOW(),
    updated_at TIMESTAMP WITH TIME ZONE DEFAULT NOW(),
    metadata JSONB DEFAULT '{}'::JSONB,
    UNIQUE(rule_name)
);

CREATE INDEX idx_symbolic_rules_type ON symbolic_rules(rule_type);
CREATE INDEX idx_symbolic_rules_active ON symbolic_rules(is_active) WHERE is_active = TRUE;
CREATE INDEX idx_symbolic_rules_priority ON symbolic_rules(priority DESC);

-- Context budget tracking
CREATE TABLE IF NOT EXISTS context_budgets (
    budget_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    session_id UUID REFERENCES inference_sessions(session_id),
    max_tokens INTEGER NOT NULL,
    system_reserved_tokens INTEGER NOT NULL DEFAULT 1000,
    used_tokens INTEGER DEFAULT 0,
    memory_allocation JSONB NOT NULL,
    priority_weights JSONB NOT NULL,
    created_at TIMESTAMP WITH TIME ZONE DEFAULT NOW(),
    updated_at TIMESTAMP WITH TIME ZONE DEFAULT NOW()
);

CREATE INDEX idx_context_budgets_session ON context_budgets(session_id);

-- Memory priority queue for context packing
CREATE TABLE IF NOT EXISTS memory_priority_queue (
    queue_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    budget_id UUID NOT NULL REFERENCES context_budgets(budget_id) ON DELETE CASCADE,
    memory_id UUID NOT NULL,
    priority_score REAL NOT NULL,
    token_count INTEGER NOT NULL,
    is_included BOOLEAN DEFAULT FALSE,
    inclusion_order INTEGER,
    created_at TIMESTAMP WITH TIME ZONE DEFAULT NOW(),
    UNIQUE(budget_id, memory_id)
);

CREATE INDEX idx_priority_queue_budget ON memory_priority_queue(budget_id);
CREATE INDEX idx_priority_queue_score ON memory_priority_queue(priority_score DESC);
CREATE INDEX idx_priority_queue_included ON memory_priority_queue(is_included) WHERE is_included = TRUE;

-- Memory contradictions for self-healing
CREATE TABLE IF NOT EXISTS memory_contradictions (
    contradiction_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    memory_id_a UUID NOT NULL,
    memory_id_b UUID NOT NULL,
    contradiction_type VARCHAR(64) NOT NULL,
    confidence REAL NOT NULL,
    detected_at TIMESTAMP WITH TIME ZONE DEFAULT NOW(),
    resolved_at TIMESTAMP WITH TIME ZONE,
    resolution_type VARCHAR(64),
    resolution_memory_id UUID,
    detection_method VARCHAR(64) NOT NULL,
    evidence JSONB NOT NULL,
    UNIQUE(memory_id_a, memory_id_b)
);

CREATE INDEX idx_contradictions_memory_a ON memory_contradictions(memory_id_a);
CREATE INDEX idx_contradictions_memory_b ON memory_contradictions(memory_id_b);
CREATE INDEX idx_contradictions_unresolved ON memory_contradictions(resolved_at) WHERE resolved_at IS NULL;

-- Memory merges for semantic deduplication
CREATE TABLE IF NOT EXISTS memory_merges (
    merge_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    source_memory_ids UUID[] NOT NULL,
    target_memory_id UUID NOT NULL,
    merge_type VARCHAR(64) NOT NULL,
    similarity_score REAL NOT NULL,
    merged_at TIMESTAMP WITH TIME ZONE DEFAULT NOW(),
    merged_by VARCHAR(64) NOT NULL,
    metadata JSONB DEFAULT '{}'::JSONB
);

CREATE INDEX idx_memory_merges_target ON memory_merges(target_memory_id);
CREATE INDEX idx_memory_merges_sources ON memory_merges USING GIN(source_memory_ids);
CREATE INDEX idx_memory_merges_date ON memory_merges(merged_at);

-- Memory decay tracking
CREATE TABLE IF NOT EXISTS memory_decay_log (
    decay_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    memory_id UUID NOT NULL,
    previous_confidence REAL NOT NULL,
    new_confidence REAL NOT NULL,
    decay_factor REAL NOT NULL,
    decay_reason VARCHAR(64) NOT NULL,
    decayed_at TIMESTAMP WITH TIME ZONE DEFAULT NOW()
);

CREATE INDEX idx_decay_log_memory ON memory_decay_log(memory_id);
CREATE INDEX idx_decay_log_date ON memory_decay_log(decayed_at);

-- Memory reinforcements
CREATE TABLE IF NOT EXISTS memory_reinforcements (
    reinforcement_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    memory_id UUID NOT NULL,
    previous_confidence REAL NOT NULL,
    new_confidence REAL NOT NULL,
    reinforcement_factor REAL NOT NULL,
    reinforcement_reason VARCHAR(64) NOT NULL,
    source_session_id UUID REFERENCES inference_sessions(session_id),
    reinforced_at TIMESTAMP WITH TIME ZONE DEFAULT NOW()
);

CREATE INDEX idx_reinforcement_memory ON memory_reinforcements(memory_id);
CREATE INDEX idx_reinforcement_date ON memory_reinforcements(reinforced_at);

-- Encrypted memory storage
CREATE TABLE IF NOT EXISTS encrypted_memories (
    memory_id UUID PRIMARY KEY,
    encrypted_content BYTEA NOT NULL,
    encryption_key_id UUID NOT NULL,
    encryption_algorithm VARCHAR(32) NOT NULL,
    iv BYTEA NOT NULL,
    content_hash CHAR(64) NOT NULL,
    created_at TIMESTAMP WITH TIME ZONE DEFAULT NOW()
);

CREATE INDEX idx_encrypted_memories_key ON encrypted_memories(encryption_key_id);

-- Local key storage (keys encrypted with master key)
CREATE TABLE IF NOT EXISTS encryption_keys (
    key_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    key_name VARCHAR(256) NOT NULL,
    encrypted_key BYTEA NOT NULL,
    key_algorithm VARCHAR(32) NOT NULL,
    key_version INTEGER DEFAULT 1,
    created_at TIMESTAMP WITH TIME ZONE DEFAULT NOW(),
    rotated_at TIMESTAMP WITH TIME ZONE,
    is_active BOOLEAN DEFAULT TRUE,
    metadata JSONB DEFAULT '{}'::JSONB,
    UNIQUE(key_name, key_version)
);

CREATE INDEX idx_encryption_keys_active ON encryption_keys(is_active) WHERE is_active = TRUE;

-- Dual-pass reasoning logs
CREATE TABLE IF NOT EXISTS dual_pass_reasoning (
    reasoning_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    session_id UUID NOT NULL REFERENCES inference_sessions(session_id) ON DELETE CASCADE,
    pass_number INTEGER NOT NULL CHECK (pass_number IN (1, 2)),
    input_hash CHAR(64) NOT NULL,
    output_hash CHAR(64) NOT NULL,
    input_context JSONB NOT NULL,
    draft_output JSONB,
    critique JSONB,
    final_output JSONB,
    confidence_before REAL,
    confidence_after REAL,
    improvements_made JSONB,
    duration_ms INTEGER NOT NULL,
    created_at TIMESTAMP WITH TIME ZONE DEFAULT NOW()
);

CREATE INDEX idx_dual_pass_session ON dual_pass_reasoning(session_id);
CREATE INDEX idx_dual_pass_number ON dual_pass_reasoning(pass_number);

-- Time-travel snapshots
CREATE TABLE IF NOT EXISTS memory_snapshots (
    snapshot_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    snapshot_timestamp TIMESTAMP WITH TIME ZONE NOT NULL,
    snapshot_type VARCHAR(32) NOT NULL,
    memory_count INTEGER NOT NULL,
    entity_count INTEGER NOT NULL,
    relationship_count INTEGER NOT NULL,
    cluster_count INTEGER NOT NULL,
    snapshot_data JSONB,
    checkpoint_hash CHAR(64) NOT NULL,
    created_at TIMESTAMP WITH TIME ZONE DEFAULT NOW(),
    metadata JSONB DEFAULT '{}'::JSONB
);

CREATE INDEX idx_snapshots_timestamp ON memory_snapshots(snapshot_timestamp);
CREATE INDEX idx_snapshots_type ON memory_snapshots(snapshot_type);
CREATE INDEX idx_snapshots_hash ON memory_snapshots(checkpoint_hash);

-- State at timestamp queries (materialized view for point-in-time)
CREATE TABLE IF NOT EXISTS memory_state_timeline (
    timeline_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    memory_id UUID NOT NULL,
    state_at TIMESTAMP WITH TIME ZONE NOT NULL,
    content TEXT NOT NULL,
    embedding vector(384),
    confidence REAL NOT NULL,
    layer VARCHAR(32) NOT NULL,
    is_active BOOLEAN NOT NULL,
    source_event_id UUID,
    UNIQUE(memory_id, state_at)
);

CREATE INDEX idx_timeline_memory ON memory_state_timeline(memory_id);
CREATE INDEX idx_timeline_timestamp ON memory_state_timeline(state_at);
CREATE INDEX idx_timeline_layer ON memory_state_timeline(layer);

-- Replay execution log
CREATE TABLE IF NOT EXISTS replay_executions (
    execution_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    source_session_id UUID NOT NULL REFERENCES inference_sessions(session_id),
    replay_session_id UUID NOT NULL REFERENCES inference_sessions(session_id),
    started_at TIMESTAMP WITH TIME ZONE DEFAULT NOW(),
    completed_at TIMESTAMP WITH TIME ZONE,
    steps_replayed INTEGER DEFAULT 0,
    steps_diverged INTEGER DEFAULT 0,
    divergence_points JSONB DEFAULT '[]'::JSONB,
    is_deterministic BOOLEAN,
    verification_hash CHAR(64),
    metadata JSONB DEFAULT '{}'::JSONB
);

CREATE INDEX idx_replay_source ON replay_executions(source_session_id);
CREATE INDEX idx_replay_target ON replay_executions(replay_session_id);

-- Compilation tasks tracking
CREATE TABLE IF NOT EXISTS compilation_tasks (
    task_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    task_type VARCHAR(64) NOT NULL,
    status VARCHAR(32) DEFAULT 'pending',
    priority INTEGER DEFAULT 0,
    started_at TIMESTAMP WITH TIME ZONE,
    completed_at TIMESTAMP WITH TIME ZONE,
    items_processed INTEGER DEFAULT 0,
    items_total INTEGER DEFAULT 0,
    error_message TEXT,
    metadata JSONB DEFAULT '{}'::JSONB,
    created_at TIMESTAMP WITH TIME ZONE DEFAULT NOW()
);

CREATE INDEX idx_compilation_status ON compilation_tasks(status);
CREATE INDEX idx_compilation_type ON compilation_tasks(task_type);
CREATE INDEX idx_compilation_priority ON compilation_tasks(priority DESC);

-- Maintenance task logs (extension)
CREATE TABLE IF NOT EXISTS self_healing_logs (
    log_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    operation_type VARCHAR(64) NOT NULL,
    target_memory_ids UUID[] NOT NULL,
    result_memory_id UUID,
    operation_result VARCHAR(32) NOT NULL,
    details JSONB NOT NULL,
    duration_ms INTEGER NOT NULL,
    executed_at TIMESTAMP WITH TIME ZONE DEFAULT NOW(),
    worker_id VARCHAR(64) NOT NULL,
    is_idempotent BOOLEAN DEFAULT TRUE
);

CREATE INDEX idx_self_healing_type ON self_healing_logs(operation_type);
CREATE INDEX idx_self_healing_date ON self_healing_logs(executed_at);
CREATE INDEX idx_self_healing_result ON self_healing_logs(operation_result);

-- Graph traversal cache
CREATE TABLE IF NOT EXISTS graph_traversal_cache (
    cache_key CHAR(64) PRIMARY KEY,
    start_entity_id UUID NOT NULL,
    max_hops INTEGER NOT NULL,
    traversal_result JSONB NOT NULL,
    entity_ids UUID[] NOT NULL,
    relationship_ids UUID[] NOT NULL,
    created_at TIMESTAMP WITH TIME ZONE DEFAULT NOW(),
    expires_at TIMESTAMP WITH TIME ZONE NOT NULL,
    access_count INTEGER DEFAULT 1
);

CREATE INDEX idx_graph_cache_entity ON graph_traversal_cache(start_entity_id);
CREATE INDEX idx_graph_cache_expires ON graph_traversal_cache(expires_at);

-- Temporal relevance scoring
CREATE TABLE IF NOT EXISTS temporal_relevance_scores (
    score_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    memory_id UUID NOT NULL,
    query_timestamp TIMESTAMP WITH TIME ZONE NOT NULL,
    base_score REAL NOT NULL,
    recency_factor REAL NOT NULL,
    decay_adjusted_score REAL NOT NULL,
    final_score REAL NOT NULL,
    scoring_params JSONB NOT NULL,
    computed_at TIMESTAMP WITH TIME ZONE DEFAULT NOW()
);

CREATE INDEX idx_temporal_scores_memory ON temporal_relevance_scores(memory_id);
CREATE INDEX idx_temporal_scores_timestamp ON temporal_relevance_scores(query_timestamp);

-- Functions for deterministic operations

-- Function to compute deterministic hash
CREATE OR REPLACE FUNCTION compute_deterministic_hash(input_text TEXT, seed BIGINT)
RETURNS CHAR(64) AS $$
DECLARE
    salted_input TEXT;
BEGIN
    salted_input := input_text || '::' || seed::TEXT;
    RETURN encode(sha256(salted_input::BYTEA), 'hex');
END;
$$ LANGUAGE plpgsql IMMUTABLE;

-- Function to get memory state at timestamp
CREATE OR REPLACE FUNCTION get_memory_state_at(
    p_memory_id UUID,
    p_timestamp TIMESTAMP WITH TIME ZONE
) RETURNS TABLE (
    memory_id UUID,
    content TEXT,
    embedding vector(384),
    confidence REAL,
    layer VARCHAR(32),
    is_active BOOLEAN
) AS $$
BEGIN
    RETURN QUERY
    SELECT
        mst.memory_id,
        mst.content,
        mst.embedding,
        mst.confidence,
        mst.layer,
        mst.is_active
    FROM memory_state_timeline mst
    WHERE mst.memory_id = p_memory_id
      AND mst.state_at <= p_timestamp
    ORDER BY mst.state_at DESC
    LIMIT 1;
END;
$$ LANGUAGE plpgsql STABLE;

-- Function to get graph state at timestamp
CREATE OR REPLACE FUNCTION get_graph_state_at(
    p_timestamp TIMESTAMP WITH TIME ZONE,
    p_limit INTEGER DEFAULT 1000
) RETURNS TABLE (
    memory_id UUID,
    content TEXT,
    confidence REAL,
    layer VARCHAR(32)
) AS $$
BEGIN
    RETURN QUERY
    WITH latest_states AS (
        SELECT DISTINCT ON (mst.memory_id)
            mst.memory_id,
            mst.content,
            mst.confidence,
            mst.layer,
            mst.is_active
        FROM memory_state_timeline mst
        WHERE mst.state_at <= p_timestamp
        ORDER BY mst.memory_id, mst.state_at DESC
    )
    SELECT
        ls.memory_id,
        ls.content,
        ls.confidence,
        ls.layer
    FROM latest_states ls
    WHERE ls.is_active = TRUE
    LIMIT p_limit;
END;
$$ LANGUAGE plpgsql STABLE;

-- Function to apply exponential decay
CREATE OR REPLACE FUNCTION apply_exponential_decay(
    p_original_confidence REAL,
    p_half_life_days REAL,
    p_days_elapsed REAL
) RETURNS REAL AS $$
BEGIN
    RETURN p_original_confidence * POWER(0.5, p_days_elapsed / p_half_life_days);
END;
$$ LANGUAGE plpgsql IMMUTABLE;

-- Function to compute cluster distance
CREATE OR REPLACE FUNCTION compute_cluster_assignment(
    p_embedding vector(384),
    p_max_distance REAL DEFAULT 0.5
) RETURNS TABLE (
    cluster_id UUID,
    distance REAL,
    membership_score REAL
) AS $$
BEGIN
    RETURN QUERY
    SELECT
        mc.cluster_id,
        (mc.centroid <=> p_embedding)::REAL AS distance,
        (1.0 - (mc.centroid <=> p_embedding))::REAL AS membership_score
    FROM memory_clusters mc
    WHERE mc.is_stable = TRUE
      AND (mc.centroid <=> p_embedding) <= p_max_distance
    ORDER BY mc.centroid <=> p_embedding
    LIMIT 5;
END;
$$ LANGUAGE plpgsql STABLE;

-- Trigger to update memory state timeline on memory events
CREATE OR REPLACE FUNCTION update_memory_timeline()
RETURNS TRIGGER AS $$
BEGIN
    INSERT INTO memory_state_timeline (
        memory_id,
        state_at,
        content,
        embedding,
        confidence,
        layer,
        is_active,
        source_event_id
    ) VALUES (
        NEW.stream_id,
        NEW.created_at,
        (NEW.event_data->>'content')::TEXT,
        NULL, -- Embedding handled separately
        COALESCE((NEW.event_data->>'confidence')::REAL, 1.0),
        COALESCE(NEW.event_data->>'layer', 'L0_RAW'),
        CASE WHEN NEW.event_type IN ('MemoryInvalidated', 'MemoryArchived', 'MemoryExpired') THEN FALSE ELSE TRUE END,
        NEW.event_id
    );
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

-- Comment blocks for documentation
COMMENT ON TABLE inference_sessions IS 'Tracks deterministic inference sessions with seeded execution for replay capability';
COMMENT ON TABLE reasoning_steps IS 'Cached reasoning steps for deterministic execution and debugging';
COMMENT ON TABLE embedding_cache IS 'Pre-computed embeddings for deterministic retrieval';
COMMENT ON TABLE memory_clusters IS 'Semantic clusters for fast pre-compiled lookup';
COMMENT ON TABLE symbolic_rules IS 'Rule-based filters for hybrid retrieval';
COMMENT ON TABLE context_budgets IS 'Token budget tracking for context optimization';
COMMENT ON TABLE memory_contradictions IS 'Detected contradictions between memories for self-healing';
COMMENT ON TABLE encrypted_memories IS 'Encrypted memory content for local security';
COMMENT ON TABLE dual_pass_reasoning IS 'Draft and critique passes for local reasoning';
COMMENT ON TABLE memory_snapshots IS 'Time-travel snapshots for point-in-time debugging';
COMMENT ON TABLE memory_state_timeline IS 'Memory state history for as-of timestamp queries';
COMMENT ON TABLE replay_executions IS 'Replay execution tracking for determinism verification';
