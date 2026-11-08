-- YASK-CIEM Postgres schema: tenancy via row-level security + hash-chained append-only audit log.
-- NOTE: not executed in CI (no Postgres server available during authoring); validate before use.
-- The app must run: SELECT set_config('app.organization_id', '<guid>', false);  per connection/transaction.
-- The app role must NOT be the table owner and must NOT have BYPASSRLS.

CREATE EXTENSION IF NOT EXISTS pgcrypto;

CREATE OR REPLACE FUNCTION app_org() RETURNS uuid
LANGUAGE sql STABLE AS $$ SELECT NULLIF(current_setting('app.organization_id', true), '')::uuid $$;

CREATE TABLE connector_results (
    id              bigserial PRIMARY KEY,
    organization_id uuid        NOT NULL,
    provider        text        NOT NULL,
    external_id     text        NOT NULL,
    collected_at    timestamptz NOT NULL,
    result_json     jsonb       NOT NULL
);
CREATE INDEX ix_connector_results_scope
    ON connector_results (organization_id, provider, external_id, collected_at DESC);

CREATE TABLE audit_log (
    organization_id uuid        NOT NULL,
    seq             bigint      NOT NULL,
    "timestamp"     timestamptz NOT NULL,
    actor           text        NOT NULL,
    action          text        NOT NULL,
    payload         text        NOT NULL,
    previous_hash   text        NOT NULL,
    hash            text        NOT NULL,
    PRIMARY KEY (organization_id, seq)
);

-- Append-only: block UPDATE/DELETE/TRUNCATE for everyone, including the owner.
CREATE OR REPLACE FUNCTION audit_log_immutable() RETURNS trigger
LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'audit_log is append-only'; END $$;

CREATE TRIGGER audit_log_no_mutation BEFORE UPDATE OR DELETE ON audit_log
    FOR EACH ROW EXECUTE FUNCTION audit_log_immutable();
CREATE TRIGGER audit_log_no_truncate BEFORE TRUNCATE ON audit_log
    FOR EACH STATEMENT EXECUTE FUNCTION audit_log_immutable();

-- Chain linkage enforced server-side: seq must follow the previous row and previous_hash must match its hash.
-- The canonical hash is computed by the application (AuditChain.ComputeHash).
CREATE OR REPLACE FUNCTION audit_log_check_chain() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE last_row audit_log%ROWTYPE;
BEGIN
    PERFORM pg_advisory_xact_lock(hashtextextended(NEW.organization_id::text, 0));
    SELECT * INTO last_row FROM audit_log
        WHERE organization_id = NEW.organization_id ORDER BY seq DESC LIMIT 1;
    IF NOT FOUND THEN
        IF NEW.seq <> 1 THEN RAISE EXCEPTION 'chain must start at seq 1'; END IF;
    ELSIF NEW.seq <> last_row.seq + 1 OR NEW.previous_hash <> last_row.hash THEN
        RAISE EXCEPTION 'audit chain broken for organization %', NEW.organization_id;
    END IF;
    RETURN NEW;
END $$;

CREATE TRIGGER audit_log_chain BEFORE INSERT ON audit_log
    FOR EACH ROW EXECUTE FUNCTION audit_log_check_chain();

-- Row-level security: rows are only visible/writable for the current organization.
ALTER TABLE connector_results ENABLE ROW LEVEL SECURITY;
ALTER TABLE connector_results FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON connector_results
    USING (organization_id = app_org()) WITH CHECK (organization_id = app_org());

ALTER TABLE audit_log ENABLE ROW LEVEL SECURITY;
ALTER TABLE audit_log FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_select ON audit_log FOR SELECT USING (organization_id = app_org());
CREATE POLICY tenant_insert ON audit_log FOR INSERT WITH CHECK (organization_id = app_org());
