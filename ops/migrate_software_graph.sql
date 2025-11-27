-- Software Engineering Graph Schema Migration
-- Extends entity and relationship types for software-aware knowledge graphs
-- BACKWARD COMPATIBLE: existing data preserved, new types added

-- =============================================================================
-- ENTITY TYPES REFERENCE TABLE
-- =============================================================================

CREATE TABLE IF NOT EXISTS entity_types (
    type_name TEXT PRIMARY KEY,
    category TEXT NOT NULL, -- 'generic', 'software', 'infrastructure', 'process'
    description TEXT,
    icon TEXT, -- For UI rendering
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Insert canonical entity types
INSERT INTO entity_types (type_name, category, description, icon) VALUES
    -- Generic NLP types (existing)
    ('PERSON', 'generic', 'Individual human being', 'user'),
    ('ORG', 'generic', 'Organization or company', 'building'),
    ('TEAM', 'generic', 'Team within an organization', 'users'),
    ('DATE', 'generic', 'Date or time reference', 'calendar'),
    ('EVENT', 'generic', 'Event or occurrence', 'calendar-check'),
    ('PRODUCT', 'generic', 'Product or offering', 'package'),

    -- Software engineering types
    ('REPO', 'software', 'Code repository (GitHub, GitLab, etc.)', 'git-branch'),
    ('PROJECT', 'software', 'Software project or initiative', 'folder'),
    ('SERVICE', 'software', 'Microservice or backend service', 'server'),
    ('MODULE', 'software', 'Code module, package, or library', 'box'),
    ('API', 'software', 'API endpoint or interface', 'plug'),
    ('DATABASE', 'software', 'Database instance or cluster', 'database'),
    ('TABLE', 'software', 'Database table or collection', 'table'),
    ('MESSAGE', 'software', 'Message queue topic or event', 'message-square'),
    ('CONFIG', 'software', 'Configuration file or setting', 'settings'),
    ('ENV', 'software', 'Environment (dev, staging, prod)', 'cloud'),
    ('VERSION', 'software', 'Version or release tag', 'tag'),
    ('TECH', 'software', 'Technology, language, or framework', 'cpu'),

    -- Infrastructure types
    ('DEPLOYMENT', 'infrastructure', 'Deployment or release', 'upload-cloud'),
    ('INCIDENT', 'infrastructure', 'Incident or outage', 'alert-triangle'),

    -- Process types
    ('BUG', 'process', 'Bug or defect', 'bug'),
    ('TEST', 'process', 'Test case or suite', 'check-square')
ON CONFLICT (type_name) DO UPDATE SET
    category = EXCLUDED.category,
    description = EXCLUDED.description,
    icon = EXCLUDED.icon;

-- =============================================================================
-- RELATIONSHIP TYPES REFERENCE TABLE
-- =============================================================================

CREATE TABLE IF NOT EXISTS relationship_types (
    type_name TEXT PRIMARY KEY,
    category TEXT NOT NULL, -- 'ownership', 'dependency', 'runtime', 'lifecycle'
    description TEXT,
    inverse_type TEXT, -- For bidirectional traversal
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Insert canonical relationship types
INSERT INTO relationship_types (type_name, category, description, inverse_type) VALUES
    -- Ownership/responsibility
    ('OWNS', 'ownership', 'Entity owns or is responsible for another', 'OWNED_BY'),
    ('MAINTAINS', 'ownership', 'Entity maintains another', 'MAINTAINED_BY'),
    ('WORKS_AT', 'ownership', 'Person works at organization', 'EMPLOYS'),
    ('WORKS_ON', 'ownership', 'Person works on project/service', 'WORKED_ON_BY'),

    -- Software dependencies
    ('IMPLEMENTS', 'dependency', 'Service implements API or interface', 'IMPLEMENTED_BY'),
    ('CALLS', 'dependency', 'Service/module calls another', 'CALLED_BY'),
    ('DEPENDS_ON', 'dependency', 'Entity depends on another', 'DEPENDENCY_OF'),
    ('USES', 'dependency', 'Entity uses technology/library', 'USED_BY'),
    ('INTEGRATES_WITH', 'dependency', 'Service integrates with another', 'INTEGRATED_BY'),

    -- Runtime/infrastructure
    ('DEPLOYED_TO', 'runtime', 'Service deployed to environment', 'HOSTS'),
    ('RUNS_ON', 'runtime', 'Service runs on infrastructure', 'RUNS'),
    ('EMITS', 'runtime', 'Service emits events/messages', 'EMITTED_BY'),
    ('CONSUMES', 'runtime', 'Service consumes events/messages', 'CONSUMED_BY'),
    ('CONFIGURED_BY', 'runtime', 'Entity configured by config', 'CONFIGURES'),
    ('TRIGGERS', 'runtime', 'Entity triggers another', 'TRIGGERED_BY'),

    -- Lifecycle
    ('VERSIONED_AS', 'lifecycle', 'Entity has version', 'VERSION_OF'),
    ('CAUSED', 'lifecycle', 'Incident caused by entity', 'CAUSED_BY'),
    ('FIXED_BY', 'lifecycle', 'Bug fixed by commit/PR', 'FIXES'),
    ('TESTS', 'lifecycle', 'Test tests entity', 'TESTED_BY'),
    ('RELATED_TO', 'lifecycle', 'Generic relationship', 'RELATED_TO')
ON CONFLICT (type_name) DO UPDATE SET
    category = EXCLUDED.category,
    description = EXCLUDED.description,
    inverse_type = EXCLUDED.inverse_type;

-- =============================================================================
-- VALIDATION FUNCTIONS
-- =============================================================================

-- Function to validate entity type
CREATE OR REPLACE FUNCTION validate_entity_type(p_type TEXT)
RETURNS BOOLEAN AS $$
BEGIN
    RETURN EXISTS (SELECT 1 FROM entity_types WHERE type_name = UPPER(TRIM(p_type)) AND is_active = TRUE);
END;
$$ LANGUAGE plpgsql STABLE;

-- Function to normalize entity type
CREATE OR REPLACE FUNCTION normalize_entity_type(p_type TEXT)
RETURNS TEXT AS $$
BEGIN
    RETURN UPPER(TRIM(p_type));
END;
$$ LANGUAGE plpgsql IMMUTABLE;

-- Function to validate relationship type
CREATE OR REPLACE FUNCTION validate_relationship_type(p_type TEXT)
RETURNS BOOLEAN AS $$
BEGIN
    RETURN EXISTS (SELECT 1 FROM relationship_types WHERE type_name = UPPER(TRIM(p_type)) AND is_active = TRUE);
END;
$$ LANGUAGE plpgsql STABLE;

-- Function to normalize relationship type
CREATE OR REPLACE FUNCTION normalize_relationship_type(p_type TEXT)
RETURNS TEXT AS $$
BEGIN
    RETURN UPPER(TRIM(p_type));
END;
$$ LANGUAGE plpgsql IMMUTABLE;

-- Function to normalize entity name (trim, collapse whitespace)
CREATE OR REPLACE FUNCTION normalize_entity_name(p_name TEXT)
RETURNS TEXT AS $$
BEGIN
    RETURN TRIM(REGEXP_REPLACE(p_name, '\s+', ' ', 'g'));
END;
$$ LANGUAGE plpgsql IMMUTABLE;

-- =============================================================================
-- ADD INDEXES FOR NEW TYPES
-- =============================================================================

-- Composite indexes for tenant + type filtering (covers existing idx_entities_tenant_type)
CREATE INDEX IF NOT EXISTS idx_entities_tenant_type_name
    ON entities(tenant_id, entity_type, name);

-- Relationship type index
CREATE INDEX IF NOT EXISTS idx_entity_relationships_tenant_type
    ON entity_relationships(tenant_id, relationship_type);

-- Entity type category lookups (for filtering by software vs generic)
CREATE INDEX IF NOT EXISTS idx_entity_types_category ON entity_types(category);
CREATE INDEX IF NOT EXISTS idx_relationship_types_category ON relationship_types(category);

-- =============================================================================
-- MIGRATE EXISTING DATA (normalize types)
-- =============================================================================

-- Normalize existing entity types to uppercase
UPDATE entities
SET entity_type = UPPER(TRIM(entity_type))
WHERE entity_type != UPPER(TRIM(entity_type));

-- Normalize existing relationship types to uppercase
UPDATE entity_relationships
SET relationship_type = UPPER(TRIM(relationship_type))
WHERE relationship_type != UPPER(TRIM(relationship_type));

-- Add any existing types not in our canonical list (preserve custom types)
INSERT INTO entity_types (type_name, category, description)
SELECT DISTINCT UPPER(TRIM(entity_type)), 'custom', 'User-defined type'
FROM entities
WHERE NOT EXISTS (
    SELECT 1 FROM entity_types WHERE type_name = UPPER(TRIM(entities.entity_type))
)
ON CONFLICT (type_name) DO NOTHING;

INSERT INTO relationship_types (type_name, category, description)
SELECT DISTINCT UPPER(TRIM(relationship_type)), 'custom', 'User-defined relationship'
FROM entity_relationships
WHERE NOT EXISTS (
    SELECT 1 FROM relationship_types WHERE type_name = UPPER(TRIM(entity_relationships.relationship_type))
)
ON CONFLICT (type_name) DO NOTHING;

-- =============================================================================
-- VIEWS FOR EASY QUERYING
-- =============================================================================

-- View: Entities with type metadata
CREATE OR REPLACE VIEW v_entities_with_type AS
SELECT
    e.*,
    et.category AS type_category,
    et.description AS type_description,
    et.icon AS type_icon
FROM entities e
LEFT JOIN entity_types et ON et.type_name = e.entity_type;

-- View: Relationships with type metadata
CREATE OR REPLACE VIEW v_relationships_with_type AS
SELECT
    er.*,
    rt.category AS type_category,
    rt.description AS type_description,
    rt.inverse_type
FROM entity_relationships er
LEFT JOIN relationship_types rt ON rt.type_name = er.relationship_type;

-- View: Software entities only
CREATE OR REPLACE VIEW v_software_entities AS
SELECT e.*
FROM entities e
JOIN entity_types et ON et.type_name = e.entity_type
WHERE et.category = 'software';

-- =============================================================================
-- STATISTICS FUNCTIONS
-- =============================================================================

-- Get entity counts by type for a tenant
CREATE OR REPLACE FUNCTION get_entity_type_counts(p_tenant_id UUID)
RETURNS TABLE (
    entity_type TEXT,
    category TEXT,
    count BIGINT
) AS $$
BEGIN
    RETURN QUERY
    SELECT
        e.entity_type,
        COALESCE(et.category, 'unknown') AS category,
        COUNT(*) AS count
    FROM entities e
    LEFT JOIN entity_types et ON et.type_name = e.entity_type
    WHERE e.tenant_id = p_tenant_id
    GROUP BY e.entity_type, et.category
    ORDER BY count DESC;
END;
$$ LANGUAGE plpgsql STABLE;

-- Get relationship counts by type for a tenant
CREATE OR REPLACE FUNCTION get_relationship_type_counts(p_tenant_id UUID)
RETURNS TABLE (
    relationship_type TEXT,
    category TEXT,
    count BIGINT
) AS $$
BEGIN
    RETURN QUERY
    SELECT
        er.relationship_type,
        COALESCE(rt.category, 'unknown') AS category,
        COUNT(*) AS count
    FROM entity_relationships er
    LEFT JOIN relationship_types rt ON rt.type_name = er.relationship_type
    WHERE er.tenant_id = p_tenant_id
    GROUP BY er.relationship_type, rt.category
    ORDER BY count DESC;
END;
$$ LANGUAGE plpgsql STABLE;
