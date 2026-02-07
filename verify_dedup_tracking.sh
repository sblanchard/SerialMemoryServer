#!/bin/bash
# Verification script for dedup metadata tracking

echo "=== Verifying Dedup Metadata Tracking ==="
echo ""

# Check if we can connect to the database
if command -v psql &> /dev/null; then
    # Local PostgreSQL
    DB_CMD="PGPASSWORD=postgres psql -h localhost -p 5432 -U postgres -d contextdb"
elif command -v docker &> /dev/null && docker ps --filter "name=postgres" --format "{{.Names}}" | grep -q postgres; then
    # Docker container
    CONTAINER=$(docker ps --filter "name=postgres" --format "{{.Names}}" | head -1)
    DB_CMD="docker exec -i $CONTAINER psql -U postgres -d contextdb"
else
    echo "❌ Cannot find PostgreSQL (tried local psql and docker)"
    exit 1
fi

echo "📊 Recent memory_ingest events with metadata:"
echo ""

$DB_CMD <<'SQL'
SELECT
    event_timestamp AT TIME ZONE 'UTC' as timestamp_utc,
    tool_name,
    success,
    latency_ms,
    CASE
        WHEN metadata IS NOT NULL THEN
            jsonb_pretty(metadata)
        ELSE
            'NULL'
    END as metadata
FROM usage_events
WHERE tool_name = 'memory_ingest'
ORDER BY event_timestamp DESC
LIMIT 10;
SQL

echo ""
echo "=== Checking for Dedup Metadata ==="
echo ""

$DB_CMD <<'SQL'
SELECT
    COUNT(*) as total_ingests,
    COUNT(*) FILTER (WHERE metadata IS NOT NULL) as with_metadata,
    COUNT(*) FILTER (WHERE metadata->>'dedup_detected' = 'true') as dedup_detected_count,
    COUNT(*) FILTER (WHERE metadata->>'dedup_mode' IS NOT NULL) as has_dedup_mode
FROM usage_events
WHERE tool_name = 'memory_ingest';
SQL

echo ""
echo "=== Sample Dedup Metadata ==="
echo ""

$DB_CMD <<'SQL'
SELECT
    event_timestamp AT TIME ZONE 'UTC' as timestamp_utc,
    metadata->>'dedup_mode' as dedup_mode,
    metadata->>'dedup_detected' as dedup_detected,
    metadata->>'dedup_threshold' as dedup_threshold,
    metadata->>'similarity' as similarity
FROM usage_events
WHERE tool_name = 'memory_ingest'
  AND metadata IS NOT NULL
ORDER BY event_timestamp DESC
LIMIT 5;
SQL

echo ""
echo "✅ Verification complete"
