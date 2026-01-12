-- Fix RLS for internal_admin role bypass
-- This allows MemoryLayerWorker and other background services to access all tenants' data

-- UUID overload (most common)
CREATE OR REPLACE FUNCTION public.rls_tenant_check(p_row_tenant_id uuid)
RETURNS boolean
LANGUAGE plpgsql
STABLE SECURITY DEFINER
AS $$
BEGIN
    -- Internal admin bypass (for background workers)
    IF current_setting('app.role', true) = 'internal_admin' THEN
        RETURN TRUE;
    END IF;

    -- Self-hosted mode: always allow (no tenant isolation)
    IF current_setting('app.mode', true) = 'self-hosted' THEN
        RETURN TRUE;
    END IF;

    -- Check if current user's tenant matches the row's tenant
    RETURN p_row_tenant_id::text = current_setting('app.current_tenant_id', true);
EXCEPTION WHEN OTHERS THEN
    RETURN TRUE;
END;
$$;

-- Text overload
CREATE OR REPLACE FUNCTION public.rls_tenant_check(p_row_tenant_id text)
RETURNS boolean
LANGUAGE plpgsql
STABLE SECURITY DEFINER
AS $$
BEGIN
    -- Internal admin bypass (for background workers)
    IF current_setting('app.role', true) = 'internal_admin' THEN
        RETURN TRUE;
    END IF;

    -- Self-hosted mode: always allow (no tenant isolation)
    IF current_setting('app.mode', true) = 'self-hosted' THEN
        RETURN TRUE;
    END IF;

    -- Check if current user's tenant matches the row's tenant
    RETURN p_row_tenant_id = current_setting('app.current_tenant_id', true);
EXCEPTION WHEN OTHERS THEN
    RETURN TRUE;
END;
$$;

-- VARCHAR overload
CREATE OR REPLACE FUNCTION public.rls_tenant_check(p_row_tenant_id character varying)
RETURNS boolean
LANGUAGE plpgsql
STABLE SECURITY DEFINER
AS $$
BEGIN
    -- Internal admin bypass (for background workers)
    IF current_setting('app.role', true) = 'internal_admin' THEN
        RETURN TRUE;
    END IF;

    -- Self-hosted mode: always allow (no tenant isolation)
    IF current_setting('app.mode', true) = 'self-hosted' THEN
        RETURN TRUE;
    END IF;

    -- Check if current user's tenant matches the row's tenant
    RETURN p_row_tenant_id = current_setting('app.current_tenant_id', true);
EXCEPTION WHEN OTHERS THEN
    RETURN TRUE;
END;
$$;
