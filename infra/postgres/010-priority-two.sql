-- Apply after 009 with the DBA identity. No endpoints or cloud roles are provisioned.
BEGIN;
ALTER TABLE SharedNotificationDeliveries DROP CONSTRAINT IF EXISTS ck_sharednotificationdeliveries_connectorkind_v2;
ALTER TABLE SharedNotificationDeliveries ADD CONSTRAINT ck_sharednotificationdeliveries_connectorkind_v2
    CHECK (ConnectorKind IN ('SignedWebhook','Jira','SplunkHec','QRadarTls','SentinelLogs','ServiceNow','TeamsWorkflow'));
CREATE TABLE IF NOT EXISTS SharedPrivilegedAccessRequests (
    TenantId uuid NOT NULL, OrganizationId uuid NOT NULL, RequestId uuid NOT NULL,
    Status text NOT NULL, UpdatedAtUtc timestamptz NOT NULL, PayloadJson text NOT NULL,
    PRIMARY KEY (TenantId, OrganizationId, RequestId)
);
CREATE INDEX IF NOT EXISTS IX_SharedPrivilegedAccessRequests_StatusTime
    ON SharedPrivilegedAccessRequests (Status, UpdatedAtUtc);
CREATE TABLE IF NOT EXISTS SharedPrivilegedAccessAudit (
    TenantId uuid NOT NULL, OrganizationId uuid NOT NULL, RequestId uuid NOT NULL,
    EventId uuid NOT NULL, AtUtc timestamptz NOT NULL, PayloadJson text NOT NULL,
    PRIMARY KEY (TenantId, OrganizationId, EventId),
    FOREIGN KEY (TenantId, OrganizationId, RequestId)
        REFERENCES SharedPrivilegedAccessRequests (TenantId, OrganizationId, RequestId)
);
DROP TRIGGER IF EXISTS TR_SharedPrivilegedAccessAudit_AppendOnly ON SharedPrivilegedAccessAudit;
CREATE TRIGGER TR_SharedPrivilegedAccessAudit_AppendOnly BEFORE UPDATE OR DELETE ON SharedPrivilegedAccessAudit
    FOR EACH ROW EXECUTE FUNCTION yask_reject_security_evidence_mutation();
DROP TRIGGER IF EXISTS TR_SharedPrivilegedAccessAudit_NoTruncate ON SharedPrivilegedAccessAudit;
CREATE TRIGGER TR_SharedPrivilegedAccessAudit_NoTruncate BEFORE TRUNCATE ON SharedPrivilegedAccessAudit
    FOR EACH STATEMENT EXECUTE FUNCTION yask_reject_security_evidence_mutation();
GRANT SELECT, INSERT, UPDATE ON SharedPrivilegedAccessRequests TO yask_ciem_runtime;
GRANT SELECT, INSERT ON SharedPrivilegedAccessAudit TO yask_ciem_runtime;
COMMIT;
