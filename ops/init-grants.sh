#!/bin/bash
# =============================================================================
# SerialMemory Post-Init Grants Script
# =============================================================================
# Grants privileges on all tables AFTER they've been created.
# This script must run LAST (99-grants.sh)
# =============================================================================

set -e

APP_USER="${APP_DB_USER:-serialmemory_app}"

echo "Granting privileges to application user: $APP_USER"

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<-EOSQL
    -- Grant privileges on all existing tables
    GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO "$APP_USER";

    -- Grant usage on all sequences
    GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO "$APP_USER";

    -- Grant execute on all functions
    GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA public TO "$APP_USER";

    -- Explicitly deny DROP, TRUNCATE, ALTER (app user cannot modify schema)
    -- These are implicitly denied by not granting them

    -- Log all tables the user has access to
    DO \$\$
    DECLARE
        table_count INTEGER;
    BEGIN
        SELECT COUNT(*) INTO table_count
        FROM information_schema.tables
        WHERE table_schema = 'public' AND table_type = 'BASE TABLE';

        RAISE NOTICE 'Granted privileges on % tables to $APP_USER', table_count;
    END
    \$\$;
EOSQL

echo "Post-init grants complete for '$APP_USER'."
