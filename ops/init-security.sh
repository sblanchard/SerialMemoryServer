#!/bin/bash
# =============================================================================
# SerialMemory Security Initialization Script
# =============================================================================
# Creates the application database user with limited privileges.
# This script runs as part of PostgreSQL docker-entrypoint-initdb.d
# =============================================================================

set -e

# Use environment variables with defaults
APP_USER="${APP_DB_USER:-serialmemory_app}"
APP_PASS="${APP_DB_PASSWORD:-changeme_in_production}"

echo "Creating application user: $APP_USER"

# Create application user with limited privileges
psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<-EOSQL
    -- Create application role if not exists
    DO \$\$
    BEGIN
        IF NOT EXISTS (SELECT FROM pg_catalog.pg_roles WHERE rolname = '$APP_USER') THEN
            CREATE USER "$APP_USER" WITH PASSWORD '$APP_PASS';
            RAISE NOTICE 'Created database user: $APP_USER';
        ELSE
            ALTER USER "$APP_USER" WITH PASSWORD '$APP_PASS';
            RAISE NOTICE 'Updated password for user: $APP_USER';
        END IF;
    END
    \$\$;

    -- Grant connect privilege to the database
    GRANT CONNECT ON DATABASE $POSTGRES_DB TO "$APP_USER";

    -- Grant usage on public schema
    GRANT USAGE ON SCHEMA public TO "$APP_USER";

    -- Set default privileges for future tables
    ALTER DEFAULT PRIVILEGES IN SCHEMA public
        GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO "$APP_USER";

    ALTER DEFAULT PRIVILEGES IN SCHEMA public
        GRANT USAGE, SELECT ON SEQUENCES TO "$APP_USER";

    ALTER DEFAULT PRIVILEGES IN SCHEMA public
        GRANT EXECUTE ON FUNCTIONS TO "$APP_USER";

    -- Revoke public create (security hardening)
    REVOKE CREATE ON SCHEMA public FROM PUBLIC;
EOSQL

echo "Security initialization complete. Application user '$APP_USER' configured."
