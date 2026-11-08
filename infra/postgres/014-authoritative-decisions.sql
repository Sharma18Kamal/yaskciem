-- Apply after 013-continuous-change-feeds.sql with the DBA identity.
BEGIN;
CREATE TABLE IF NOT EXISTS SharedEffectiveDecisionSnapshots (
    TenantId uuid NOT NULL,
    OrganizationId uuid NOT NULL,
    SnapshotId uuid NOT NULL,
    SourceId text NOT NULL,
    AccountId text NOT NULL,
    StartedAtUtc timestamptz NOT NULL,
    CompletedAtUtc timestamptz NOT NULL,
    CollectedBy uuid NOT NULL,
    PayloadJson text NOT NULL,
    PRIMARY KEY (TenantId, OrganizationId, SnapshotId),
    CHECK (length(SourceId) BETWEEN 1 AND 64),
    CHECK (length(AccountId) BETWEEN 1 AND 128),
    CHECK (CompletedAtUtc >= StartedAtUtc),
    CHECK (octet_length(PayloadJson) <= 2097152)
);
CREATE INDEX IF NOT EXISTS IX_SharedEffectiveDecisionSnapshots_SourceTime
    ON SharedEffectiveDecisionSnapshots (TenantId, OrganizationId, SourceId, CompletedAtUtc DESC, SnapshotId DESC);
CREATE TABLE IF NOT EXISTS SharedDecisionEscalationAnalyses (
    TenantId uuid NOT NULL,
    OrganizationId uuid NOT NULL,
    AnalysisId uuid NOT NULL,
    SourceId text NOT NULL,
    BeforeSnapshotId uuid NOT NULL,
    AfterSnapshotId uuid NOT NULL,
    AnalyzedAtUtc timestamptz NOT NULL,
    PayloadJson text NOT NULL,
    PRIMARY KEY (TenantId, OrganizationId, AnalysisId),
    UNIQUE (TenantId, OrganizationId, SourceId, BeforeSnapshotId, AfterSnapshotId),
    FOREIGN KEY (TenantId, OrganizationId, BeforeSnapshotId)
        REFERENCES SharedEffectiveDecisionSnapshots (TenantId, OrganizationId, SnapshotId),
    FOREIGN KEY (TenantId, OrganizationId, AfterSnapshotId)
        REFERENCES SharedEffectiveDecisionSnapshots (TenantId, OrganizationId, SnapshotId),
    CHECK (BeforeSnapshotId <> AfterSnapshotId)
);
DROP TRIGGER IF EXISTS TR_SharedEffectiveDecisionSnapshots_AppendOnly ON SharedEffectiveDecisionSnapshots;
CREATE TRIGGER TR_SharedEffectiveDecisionSnapshots_AppendOnly
    BEFORE UPDATE OR DELETE ON SharedEffectiveDecisionSnapshots
    FOR EACH ROW EXECUTE FUNCTION yask_reject_security_evidence_mutation();
DROP TRIGGER IF EXISTS TR_SharedEffectiveDecisionSnapshots_NoTruncate ON SharedEffectiveDecisionSnapshots;
CREATE TRIGGER TR_SharedEffectiveDecisionSnapshots_NoTruncate
    BEFORE TRUNCATE ON SharedEffectiveDecisionSnapshots
    FOR EACH STATEMENT EXECUTE FUNCTION yask_reject_security_evidence_mutation();
DROP TRIGGER IF EXISTS TR_SharedDecisionEscalationAnalyses_AppendOnly ON SharedDecisionEscalationAnalyses;
CREATE TRIGGER TR_SharedDecisionEscalationAnalyses_AppendOnly
    BEFORE UPDATE OR DELETE ON SharedDecisionEscalationAnalyses
    FOR EACH ROW EXECUTE FUNCTION yask_reject_security_evidence_mutation();
DROP TRIGGER IF EXISTS TR_SharedDecisionEscalationAnalyses_NoTruncate ON SharedDecisionEscalationAnalyses;
CREATE TRIGGER TR_SharedDecisionEscalationAnalyses_NoTruncate
    BEFORE TRUNCATE ON SharedDecisionEscalationAnalyses
    FOR EACH STATEMENT EXECUTE FUNCTION yask_reject_security_evidence_mutation();
GRANT SELECT, INSERT ON SharedEffectiveDecisionSnapshots, SharedDecisionEscalationAnalyses TO yask_ciem_runtime;
COMMIT;
