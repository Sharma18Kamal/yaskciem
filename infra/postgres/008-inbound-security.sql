-- Apply after 007-cloud-activity.sql with the DBA identity.
BEGIN;
CREATE TABLE IF NOT EXISTS SharedSecurityAlertCursors (
    TenantId uuid NOT NULL,
    OrganizationId uuid NOT NULL,
    Product text NOT NULL,
    Channel text NOT NULL,
    Cursor text NOT NULL,
    PRIMARY KEY (TenantId, OrganizationId, Product, Channel)
);
CREATE TABLE IF NOT EXISTS SharedSecurityAlertBatches (
    TenantId uuid NOT NULL,
    OrganizationId uuid NOT NULL,
    Product text NOT NULL,
    Channel text NOT NULL,
    BatchId uuid NOT NULL,
    PayloadSha256 text NOT NULL,
    ReceiptJson text NOT NULL,
    PRIMARY KEY (TenantId, OrganizationId, Product, Channel, BatchId)
);
CREATE TABLE IF NOT EXISTS SharedSecurityAlertRevisions (
    TenantId uuid NOT NULL,
    OrganizationId uuid NOT NULL,
    Product text NOT NULL,
    Channel text NOT NULL,
    AlertId text NOT NULL,
    UpdatedAtUtc timestamptz NOT NULL,
    ReceivedAtUtc timestamptz NOT NULL,
    UploadedBy uuid NOT NULL,
    PayloadJson text NOT NULL,
    PRIMARY KEY (TenantId, OrganizationId, Product, Channel, AlertId, UpdatedAtUtc)
);
CREATE INDEX IF NOT EXISTS IX_SharedSecurityAlertRevisions_Recent
    ON SharedSecurityAlertRevisions (TenantId, OrganizationId, Product, Channel, UpdatedAtUtc DESC, AlertId);
CREATE OR REPLACE FUNCTION yask_reject_security_evidence_mutation() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'Security alert evidence is append-only';
END;
$$;
DROP TRIGGER IF EXISTS TR_SharedSecurityAlertRevisions_AppendOnly ON SharedSecurityAlertRevisions;
CREATE TRIGGER TR_SharedSecurityAlertRevisions_AppendOnly BEFORE UPDATE OR DELETE ON SharedSecurityAlertRevisions
FOR EACH ROW EXECUTE FUNCTION yask_reject_security_evidence_mutation();
DROP TRIGGER IF EXISTS TR_SharedSecurityAlertBatches_AppendOnly ON SharedSecurityAlertBatches;
CREATE TRIGGER TR_SharedSecurityAlertBatches_AppendOnly BEFORE UPDATE OR DELETE ON SharedSecurityAlertBatches
FOR EACH ROW EXECUTE FUNCTION yask_reject_security_evidence_mutation();
DROP TRIGGER IF EXISTS TR_SharedSecurityAlertRevisions_NoTruncate ON SharedSecurityAlertRevisions;
CREATE TRIGGER TR_SharedSecurityAlertRevisions_NoTruncate BEFORE TRUNCATE ON SharedSecurityAlertRevisions
FOR EACH STATEMENT EXECUTE FUNCTION yask_reject_security_evidence_mutation();
DROP TRIGGER IF EXISTS TR_SharedSecurityAlertBatches_NoTruncate ON SharedSecurityAlertBatches;
CREATE TRIGGER TR_SharedSecurityAlertBatches_NoTruncate BEFORE TRUNCATE ON SharedSecurityAlertBatches
FOR EACH STATEMENT EXECUTE FUNCTION yask_reject_security_evidence_mutation();
GRANT SELECT, INSERT ON SharedSecurityAlertRevisions, SharedSecurityAlertBatches TO yask_ciem_runtime;
GRANT SELECT, INSERT, UPDATE ON SharedSecurityAlertCursors TO yask_ciem_runtime;
COMMIT;
