-- Apply after 011 with the DBA identity. No emergency credentials or access grants are created.
BEGIN;
CREATE TABLE IF NOT EXISTS SharedBreakGlassReports (
    TenantId uuid NOT NULL, OrganizationId uuid NOT NULL, ReportId uuid NOT NULL,
    ReportedAtUtc timestamptz NOT NULL, RequestJson text NOT NULL, PayloadJson text NOT NULL,
    PRIMARY KEY (TenantId, OrganizationId, ReportId)
);
CREATE INDEX IF NOT EXISTS IX_SharedBreakGlassReports_OrgTime
    ON SharedBreakGlassReports (TenantId, OrganizationId, ReportedAtUtc DESC, ReportId);
CREATE TABLE IF NOT EXISTS SharedBreakGlassReviews (
    TenantId uuid NOT NULL, OrganizationId uuid NOT NULL, ReportId uuid NOT NULL,
    PayloadJson text NOT NULL,
    PRIMARY KEY (TenantId, OrganizationId, ReportId),
    FOREIGN KEY (TenantId, OrganizationId, ReportId)
        REFERENCES SharedBreakGlassReports (TenantId, OrganizationId, ReportId)
);
DROP TRIGGER IF EXISTS TR_SharedBreakGlassReports_AppendOnly ON SharedBreakGlassReports;
CREATE TRIGGER TR_SharedBreakGlassReports_AppendOnly BEFORE UPDATE OR DELETE ON SharedBreakGlassReports
    FOR EACH ROW EXECUTE FUNCTION yask_reject_security_evidence_mutation();
DROP TRIGGER IF EXISTS TR_SharedBreakGlassReports_NoTruncate ON SharedBreakGlassReports;
CREATE TRIGGER TR_SharedBreakGlassReports_NoTruncate BEFORE TRUNCATE ON SharedBreakGlassReports
    FOR EACH STATEMENT EXECUTE FUNCTION yask_reject_security_evidence_mutation();
DROP TRIGGER IF EXISTS TR_SharedBreakGlassReviews_AppendOnly ON SharedBreakGlassReviews;
CREATE TRIGGER TR_SharedBreakGlassReviews_AppendOnly BEFORE UPDATE OR DELETE ON SharedBreakGlassReviews
    FOR EACH ROW EXECUTE FUNCTION yask_reject_security_evidence_mutation();
DROP TRIGGER IF EXISTS TR_SharedBreakGlassReviews_NoTruncate ON SharedBreakGlassReviews;
CREATE TRIGGER TR_SharedBreakGlassReviews_NoTruncate BEFORE TRUNCATE ON SharedBreakGlassReviews
    FOR EACH STATEMENT EXECUTE FUNCTION yask_reject_security_evidence_mutation();
GRANT SELECT, INSERT ON SharedBreakGlassReports, SharedBreakGlassReviews TO yask_ciem_runtime;
COMMIT;
