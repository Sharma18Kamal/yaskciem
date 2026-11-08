-- Apply after 008-inbound-security.sql with the DBA identity.
BEGIN;
CREATE TABLE IF NOT EXISTS SharedEntitlementEvidenceSnapshots (
    TenantId uuid NOT NULL,
    OrganizationId uuid NOT NULL,
    SnapshotId uuid NOT NULL,
    Provider integer NOT NULL,
    AccountId text NOT NULL,
    StartedAtUtc timestamptz NOT NULL,
    CollectedAtUtc timestamptz NOT NULL,
    UploadedBy uuid NOT NULL,
    PayloadJson text NOT NULL,
    PRIMARY KEY (TenantId, OrganizationId, SnapshotId)
);
CREATE INDEX IF NOT EXISTS IX_SharedEntitlementEvidenceSnapshots_AccountTime
    ON SharedEntitlementEvidenceSnapshots (TenantId, OrganizationId, Provider, AccountId, CollectedAtUtc DESC, SnapshotId DESC);
DROP TRIGGER IF EXISTS TR_SharedEntitlementEvidenceSnapshots_AppendOnly ON SharedEntitlementEvidenceSnapshots;
CREATE TRIGGER TR_SharedEntitlementEvidenceSnapshots_AppendOnly BEFORE UPDATE OR DELETE ON SharedEntitlementEvidenceSnapshots
FOR EACH ROW EXECUTE FUNCTION yask_reject_security_evidence_mutation();
DROP TRIGGER IF EXISTS TR_SharedEntitlementEvidenceSnapshots_NoTruncate ON SharedEntitlementEvidenceSnapshots;
CREATE TRIGGER TR_SharedEntitlementEvidenceSnapshots_NoTruncate BEFORE TRUNCATE ON SharedEntitlementEvidenceSnapshots
FOR EACH STATEMENT EXECUTE FUNCTION yask_reject_security_evidence_mutation();
GRANT SELECT, INSERT ON SharedEntitlementEvidenceSnapshots TO yask_ciem_runtime;
COMMIT;
