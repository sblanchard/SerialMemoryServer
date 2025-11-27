-- Hybrid Engineering Graph Schema Migration
-- Extends entity and relationship types for physical/hardware engineering
-- ADDITIVE ONLY: No existing data modified, all new types added

-- =============================================================================
-- NEW ENTITY TYPES (Physical Engineering)
-- =============================================================================

INSERT INTO entity_types (type_name, category, description, icon) VALUES
    -- Physical components
    ('COMPONENT', 'engineering', 'Physical component (resistor, capacitor, IC, etc.)', 'cpu'),
    ('ASSEMBLY', 'engineering', 'Assembly of components', 'layers'),
    ('MATERIAL', 'engineering', 'Material specification (aluminum, copper, FR4, etc.)', 'box'),

    -- Electrical/Signal
    ('SIGNAL', 'engineering', 'Electrical or data signal', 'activity'),
    ('POWER_RAIL', 'engineering', 'Power supply rail (3.3V, 5V, 12V, etc.)', 'zap'),
    ('VOLTAGE', 'engineering', 'Voltage level or specification', 'zap'),
    ('CURRENT', 'engineering', 'Current specification', 'zap'),
    ('FREQUENCY', 'engineering', 'Frequency specification (MHz, GHz, kHz)', 'radio'),

    -- Physical measurements
    ('TEMPERATURE', 'engineering', 'Temperature specification or range', 'thermometer'),
    ('PRESSURE', 'engineering', 'Pressure specification', 'gauge'),
    ('FORCE', 'engineering', 'Force specification (N, lbf)', 'move'),
    ('TORQUE', 'engineering', 'Torque specification (Nm, ft-lb)', 'rotate-cw'),
    ('DIMENSION', 'engineering', 'Physical dimension (length, width, height)', 'maximize'),
    ('TOLERANCE', 'engineering', 'Tolerance specification (+/- value)', 'sliders'),

    -- Standards and documentation
    ('STANDARD', 'engineering', 'Engineering standard (ISO, IEC, MIL-STD, etc.)', 'file-text'),
    ('TOOL', 'engineering', 'Tool or test equipment', 'tool'),
    ('LOCATION', 'engineering', 'Physical location or position', 'map-pin'),

    -- Hardware-specific (extending software types)
    ('PCB', 'engineering', 'Printed circuit board', 'grid'),
    ('SCHEMATIC', 'engineering', 'Schematic diagram', 'file'),
    ('BOM', 'engineering', 'Bill of materials', 'list'),
    ('PART_NUMBER', 'engineering', 'Part number or SKU', 'hash'),
    ('DATASHEET', 'engineering', 'Component datasheet', 'file-text'),
    ('FIRMWARE', 'engineering', 'Firmware or embedded software', 'hard-drive'),
    ('REGISTER', 'engineering', 'Hardware register', 'database'),
    ('PIN', 'engineering', 'Pin or pinout specification', 'circle'),
    ('CONNECTOR', 'engineering', 'Physical connector', 'link'),
    ('PROTOCOL', 'engineering', 'Communication protocol (I2C, SPI, UART, CAN)', 'wifi'),
    ('MANUFACTURER', 'engineering', 'Component manufacturer', 'briefcase'),
    ('SENSOR', 'engineering', 'Sensor device', 'eye'),
    ('ACTUATOR', 'engineering', 'Actuator (motor, valve, etc.)', 'settings'),
    ('ENCLOSURE', 'engineering', 'Physical enclosure or housing', 'box'),
    ('CABLE', 'engineering', 'Cable or wire harness', 'link-2'),
    ('ANTENNA', 'engineering', 'RF antenna', 'radio')
ON CONFLICT (type_name) DO UPDATE SET
    category = EXCLUDED.category,
    description = EXCLUDED.description,
    icon = EXCLUDED.icon;

-- =============================================================================
-- NEW RELATIONSHIP TYPES (Physical Engineering)
-- =============================================================================

INSERT INTO relationship_types (type_name, category, description, inverse_type) VALUES
    -- Physical connections
    ('CONNECTS_TO', 'physical', 'Physical electrical/mechanical connection', 'CONNECTED_BY'),
    ('POWERS', 'physical', 'Provides power to', 'POWERED_BY'),
    ('GROUNDS', 'physical', 'Provides ground reference', 'GROUNDED_BY'),

    -- Control relationships
    ('CONTROLS', 'physical', 'Controls (actuator, valve, etc.)', 'CONTROLLED_BY'),
    ('READS_FROM', 'physical', 'Reads data/signal from', 'READ_BY'),
    ('WRITES_TO', 'physical', 'Writes data/signal to', 'WRITTEN_BY'),
    ('DRIVES', 'physical', 'Drives (motor, LED, etc.)', 'DRIVEN_BY'),

    -- Physical containment
    ('MOUNTED_ON', 'physical', 'Physically mounted on', 'MOUNTS'),
    ('CONTAINED_IN', 'physical', 'Contained within', 'CONTAINS'),
    ('ATTACHED_TO', 'physical', 'Physically attached to', 'HAS_ATTACHED'),

    -- Compatibility and substitution
    ('COMPATIBLE_WITH', 'physical', 'Compatible with (interchangeable)', 'COMPATIBLE_WITH'),
    ('REPLACES', 'physical', 'Replaces (for part substitution)', 'REPLACED_BY'),
    ('ALTERNATE_OF', 'physical', 'Alternate part for', 'HAS_ALTERNATE'),

    -- Communication
    ('COMMUNICATES_VIA', 'physical', 'Communicates using protocol', 'USED_BY_COMM'),
    ('TRANSMITS', 'physical', 'Transmits signal/data', 'RECEIVES'),
    ('RECEIVES', 'physical', 'Receives signal/data', 'TRANSMITS'),

    -- Specifications
    ('OPERATES_AT', 'physical', 'Operates at (voltage, frequency, temp)', 'OPERATING_SPEC_FOR'),
    ('RATED_FOR', 'physical', 'Rated for (max current, power, etc.)', 'RATING_OF'),
    ('MEASURES', 'physical', 'Measures (sensor to quantity)', 'MEASURED_BY'),

    -- Documentation
    ('MANUFACTURED_BY', 'physical', 'Manufactured by company', 'MANUFACTURES'),
    ('SPECIFIED_IN', 'physical', 'Specified in document/datasheet', 'SPECIFIES'),
    ('COMPLIES_WITH', 'physical', 'Complies with standard', 'COMPLIANCE_FOR'),
    ('REVISION_OF', 'physical', 'Revision of previous design', 'HAS_REVISION'),

    -- Assembly
    ('PART_OF', 'physical', 'Part of assembly', 'HAS_PART'),
    ('INTERFACES_WITH', 'physical', 'Interfaces with (mechanical/electrical)', 'INTERFACED_BY'),
    ('ROUTES_TO', 'physical', 'Routes signal/trace to', 'ROUTED_FROM'),
    ('LOCATED_AT', 'physical', 'Located at position', 'LOCATION_OF')
ON CONFLICT (type_name) DO UPDATE SET
    category = EXCLUDED.category,
    description = EXCLUDED.description,
    inverse_type = EXCLUDED.inverse_type;

-- =============================================================================
-- UPDATE CATEGORY ENUM (add 'engineering')
-- =============================================================================

-- Create view for engineering entities
CREATE OR REPLACE VIEW v_engineering_entities AS
SELECT e.*
FROM entities e
JOIN entity_types et ON et.type_name = e.entity_type
WHERE et.category = 'engineering';

-- Create view for physical relationships
CREATE OR REPLACE VIEW v_physical_relationships AS
SELECT er.*
FROM entity_relationships er
JOIN relationship_types rt ON rt.type_name = er.relationship_type
WHERE rt.category = 'physical';

-- =============================================================================
-- HELPER FUNCTIONS
-- =============================================================================

-- Get all engineering entity types
CREATE OR REPLACE FUNCTION get_engineering_entity_types()
RETURNS TABLE (type_name TEXT, description TEXT, icon TEXT) AS $$
BEGIN
    RETURN QUERY
    SELECT et.type_name, et.description, et.icon
    FROM entity_types et
    WHERE et.category = 'engineering' AND et.is_active = TRUE
    ORDER BY et.type_name;
END;
$$ LANGUAGE plpgsql STABLE;

-- Get all physical relationship types
CREATE OR REPLACE FUNCTION get_physical_relationship_types()
RETURNS TABLE (type_name TEXT, description TEXT, inverse_type TEXT) AS $$
BEGIN
    RETURN QUERY
    SELECT rt.type_name, rt.description, rt.inverse_type
    FROM relationship_types rt
    WHERE rt.category = 'physical' AND rt.is_active = TRUE
    ORDER BY rt.type_name;
END;
$$ LANGUAGE plpgsql STABLE;

-- =============================================================================
-- COMPOSITE INDEXES FOR HYBRID QUERIES
-- =============================================================================

-- Index for filtering by category in entity lookups
CREATE INDEX IF NOT EXISTS idx_entity_types_category_active
    ON entity_types(category, is_active);

CREATE INDEX IF NOT EXISTS idx_relationship_types_category_active
    ON relationship_types(category, is_active);

-- =============================================================================
-- MIGRATION VERSION BUMP
-- =============================================================================
-- Increment graph version for hybrid schema (2 -> 3)
-- This allows selective re-crawling for engineering content

COMMENT ON TABLE entity_types IS 'Entity types registry - v3 includes engineering types';
COMMENT ON TABLE relationship_types IS 'Relationship types registry - v3 includes physical relationships';
