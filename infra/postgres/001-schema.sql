CREATE TABLE IF NOT EXISTS SharedOrganizationDirectories (
    TenantId uuid NOT NULL PRIMARY KEY,
    Revision bigint NOT NULL,
    PayloadJson text NOT NULL
);

CREATE TABLE IF NOT EXISTS SharedAccountBindings (
    Provider text NOT NULL,
    ExternalAccountId text NOT NULL,
    TenantId uuid NOT NULL,
    OrganizationId uuid NOT NULL,
    PRIMARY KEY (Provider, ExternalAccountId)
);

CREATE TABLE IF NOT EXISTS SharedAccessReviewCampaigns (
    TenantId uuid NOT NULL,
    OrganizationId uuid NOT NULL,
    CampaignId uuid NOT NULL,
    CreatedAtUtc timestamptz NOT NULL,
    PayloadJson text NOT NULL,
    PRIMARY KEY (TenantId, OrganizationId, CampaignId)
);

CREATE INDEX IF NOT EXISTS IX_SharedAccessReviewCampaigns_Org_Created
    ON SharedAccessReviewCampaigns (TenantId, OrganizationId, CreatedAtUtc);

CREATE TABLE IF NOT EXISTS SharedScanSummaries (
    TenantId uuid NOT NULL,
    OrganizationId uuid NOT NULL,
    ScanId uuid NOT NULL,
    CompletedAtUtc timestamptz NOT NULL,
    PayloadJson text NOT NULL,
    PRIMARY KEY (TenantId, OrganizationId, ScanId)
);

CREATE INDEX IF NOT EXISTS IX_SharedScanSummaries_Org_Completed
    ON SharedScanSummaries (TenantId, OrganizationId, CompletedAtUtc);

CREATE TABLE IF NOT EXISTS ciem_audit_heads (
    tenant_id uuid PRIMARY KEY,
    last_sequence bigint NOT NULL CHECK (last_sequence >= 0),
    last_hash text NOT NULL
);

CREATE TABLE IF NOT EXISTS ciem_audit_events (
    tenant_id uuid NOT NULL,
    sequence bigint NOT NULL CHECK (sequence > 0),
    event_id uuid NOT NULL,
    event_json text NOT NULL,
    PRIMARY KEY (tenant_id, sequence),
    UNIQUE (tenant_id, event_id)
);

CREATE OR REPLACE FUNCTION yask_reject_audit_event_mutation() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'Audit events are append-only';
END;
$$;

DROP TRIGGER IF EXISTS TR_CiemAuditEvents_AppendOnly ON ciem_audit_events;
CREATE TRIGGER TR_CiemAuditEvents_AppendOnly
BEFORE UPDATE OR DELETE ON ciem_audit_events
FOR EACH ROW EXECUTE FUNCTION yask_reject_audit_event_mutation();

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'yask_ciem_runtime') THEN
        CREATE ROLE yask_ciem_runtime NOLOGIN;
    END IF;
END;
$$;

GRANT USAGE ON SCHEMA public TO yask_ciem_runtime;
GRANT SELECT, INSERT, UPDATE ON SharedOrganizationDirectories TO yask_ciem_runtime;
GRANT SELECT, INSERT, DELETE ON SharedAccountBindings TO yask_ciem_runtime;
GRANT SELECT, INSERT, UPDATE ON SharedAccessReviewCampaigns TO yask_ciem_runtime;
GRANT SELECT, INSERT ON SharedScanSummaries TO yask_ciem_runtime;
GRANT SELECT, INSERT, UPDATE ON ciem_audit_heads TO yask_ciem_runtime;
GRANT SELECT, INSERT ON ciem_audit_events TO yask_ciem_runtime;