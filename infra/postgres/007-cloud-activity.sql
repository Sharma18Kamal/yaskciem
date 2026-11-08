-- Apply after 006-security-connectors.sql using the DBA identity.
BEGIN;
CREATE TABLE IF NOT EXISTS SharedCloudActivitySnapshots (
    TenantId uuid NOT NULL,
    OrganizationId uuid NOT NULL,
    SnapshotId uuid NOT NULL,
    Provider integer NOT NULL,
    AccountId text NOT NULL,
    ThroughUtc timestamptz NOT NULL,
    UploadedBy uuid NOT NULL,
    PayloadJson text NOT NULL,
    PRIMARY KEY (TenantId, OrganizationId, SnapshotId)
);
CREATE INDEX IF NOT EXISTS IX_SharedCloudActivitySnapshots_AccountTime
    ON SharedCloudActivitySnapshots (TenantId, OrganizationId, Provider, AccountId, ThroughUtc DESC);
GRANT SELECT, INSERT ON SharedCloudActivitySnapshots TO yask_ciem_runtime;
COMMIT;
