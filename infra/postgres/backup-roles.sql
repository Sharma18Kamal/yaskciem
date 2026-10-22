-- Run as the database owner/DBA after applying schema migrations.
-- Set login passwords separately with psql's interactive \password command
-- or the organization's approved secret-management workflow.

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'yask_ciem_backup') THEN
        CREATE ROLE yask_ciem_backup NOLOGIN;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'yask_ciem_restore') THEN
        CREATE ROLE yask_ciem_restore NOLOGIN;
    END IF;
END;
$$;

ALTER ROLE yask_ciem_backup
    LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION BYPASSRLS;
ALTER ROLE yask_ciem_restore
    LOGIN NOSUPERUSER CREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;

DO $$
BEGIN
    EXECUTE format('GRANT CONNECT ON DATABASE %I TO yask_ciem_backup', current_database());
END;
$$;

GRANT USAGE ON SCHEMA public TO yask_ciem_backup;
GRANT SELECT ON ALL TABLES IN SCHEMA public TO yask_ciem_backup;
GRANT SELECT ON ALL SEQUENCES IN SCHEMA public TO yask_ciem_backup;

-- Keep future objects created by the schema/migration owner readable by the
-- backup login. Re-run this file as each additional migration owner if needed.
DO $$
DECLARE
    owner_role record;
BEGIN
    FOR owner_role IN
        SELECT DISTINCT role_name
        FROM (
            SELECT tableowner AS role_name FROM pg_tables WHERE schemaname = 'public'
            UNION
            SELECT current_user
        ) owners
    LOOP
        EXECUTE format(
            'ALTER DEFAULT PRIVILEGES FOR ROLE %I IN SCHEMA public GRANT SELECT ON TABLES TO yask_ciem_backup',
            owner_role.role_name);
        EXECUTE format(
            'ALTER DEFAULT PRIVILEGES FOR ROLE %I IN SCHEMA public GRANT SELECT ON SEQUENCES TO yask_ciem_backup',
            owner_role.role_name);
    END LOOP;
END;
$$;
