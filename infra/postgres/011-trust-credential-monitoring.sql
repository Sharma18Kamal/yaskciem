-- Apply after 010-priority-two.sql with the DBA identity.
BEGIN;
CREATE TABLE IF NOT EXISTS SharedTrustCredentialSnapshots (
    TenantId uuid NOT NULL,
    OrganizationId uuid NOT NULL,
    SnapshotId uuid NOT NULL,
    Provider integer NOT NULL,
    AccountId text NOT NULL,
    StartedAtUtc timestamptz NOT NULL,
    CollectedAtUtc timestamptz NOT NULL,
    UploadedBy uuid NOT NULL,
    PayloadJson text NOT NULL,
    PRIMARY KEY (TenantId, OrganizationId, SnapshotId),
    CHECK (Provider IN (0, 1, 2)),
    CHECK (length(AccountId) BETWEEN 1 AND 128),
    CHECK (CollectedAtUtc >= StartedAtUtc)
);
CREATE INDEX IF NOT EXISTS IX_SharedTrustCredentialSnapshots_AccountTime
    ON SharedTrustCredentialSnapshots (TenantId, OrganizationId, Provider, AccountId, CollectedAtUtc DESC, SnapshotId DESC);
DROP TRIGGER IF EXISTS TR_SharedTrustCredentialSnapshots_AppendOnly ON SharedTrustCredentialSnapshots;
CREATE TRIGGER TR_SharedTrustCredentialSnapshots_AppendOnly
    BEFORE UPDATE OR DELETE ON SharedTrustCredentialSnapshots
    FOR EACH ROW EXECUTE FUNCTION yask_reject_security_evidence_mutation();
DROP TRIGGER IF EXISTS TR_SharedTrustCredentialSnapshots_NoTruncate ON SharedTrustCredentialSnapshots;
CREATE TRIGGER TR_SharedTrustCredentialSnapshots_NoTruncate
    BEFORE TRUNCATE ON SharedTrustCredentialSnapshots
    FOR EACH STATEMENT EXECUTE FUNCTION yask_reject_security_evidence_mutation();
GRANT SELECT, INSERT ON SharedTrustCredentialSnapshots TO yask_ciem_runtime;
COMMIT;
