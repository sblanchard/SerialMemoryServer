-- =============================================================================
-- SerialMemory Security Initialization Script
-- =============================================================================
-- This script MUST run BEFORE all other init scripts (00-security.sql)
-- It creates the application user with limited privileges.
--
-- Environment variables (set in docker-compose):
--   APP_DB_USER     - Application database user
--   APP_DB_PASSWORD - Application database password
-- =============================================================================

-- Create application role with login capability
DO $$
DECLARE
    app_user TEXT := current_setting('my.app_user', true);
    app_pass TEXT := current_setting('my.app_password', true);
BEGIN
    -- Use environment variables passed from Docker
    app_user := COALESCE(app_user, current_setting('APP_DB_USER', true), 'serialmemory_app');
    app_pass := COALESCE(app_pass, current_setting('APP_DB_PASSWORD', true), 'changeme');

    -- Create user if not exists
    IF NOT EXISTS (SELECT FROM pg_catalog.pg_roles WHERE rolname = app_user) THEN
        EXECUTE format('CREATE USER %I WITH PASSWORD %L', app_user, app_pass);
        RAISE NOTICE 'Created database user: %', app_user;
    ELSE
        -- Update password if user exists
        EXECUTE format('ALTER USER %I WITH PASSWORD %L', app_user, app_pass);
        RAISE NOTICE 'Updated password for user: %', app_user;
    END IF;
END
$$;

-- Create application user using environment variable (shell expansion)
-- This runs outside PL/pgSQL to use environment variables directly
\set app_user `echo $APP_DB_USER`
\set app_pass `echo $APP_DB_PASSWORD`

-- Handle case where variables might be empty (use defaults)
SELECT COALESCE(NULLIF(:'app_user', ''), 'serialmemory_app') AS app_user \gset
SELECT COALESCE(NULLIF(:'app_pass', ''), 'changeme_in_production') AS app_pass \gset

-- Create role if not exists (idempotent)
DO $$
BEGIN
    IF NOT EXISTS (SELECT FROM pg_catalog.pg_roles WHERE rolname = :'app_user') THEN
        EXECUTE format('CREATE USER %I WITH PASSWORD %L', :'app_user', :'app_pass');
    END IF;
END
$$;

-- Grant connect privilege to the database
GRANT CONNECT ON DATABASE contextdb TO :app_user;

-- Grant usage on public schema
GRANT USAGE ON SCHEMA public TO :app_user;

-- Grant privileges on all existing tables (will be created by subsequent scripts)
-- This must be run again after tables are created, or use default privileges
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO :app_user;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT USAGE, SELECT ON SEQUENCES TO :app_user;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT EXECUTE ON FUNCTIONS TO :app_user;

-- Revoke public schema create from public (security hardening)
REVOKE CREATE ON SCHEMA public FROM PUBLIC;

-- Log completion
DO $$
BEGIN
    RAISE NOTICE 'Security initialization complete. Application user configured with limited privileges.';
END
$$;
