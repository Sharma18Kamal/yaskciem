-- Client bridges only; no provider resources are provisioned. Apply after 012.
BEGIN;
CREATE TABLE IF NOT EXISTS SharedContinuousChangeFeeds (
    TenantId uuid NOT NULL, OrganizationId uuid NOT NULL, Alias text NOT NULL,
    Provider text NOT NULL, AccountId text NOT NULL, SourceId text NOT NULL,
    ConfigurationSha256 text NOT NULL, SourceEpoch uuid NOT NULL,
    Enabled boolean NOT NULL, Connected boolean NOT NULL, Cursor text NULL,
    Health text NOT NULL, ConsecutiveFailures integer NOT NULL DEFAULT 0,
    LastSuccessAtUtc timestamptz NULL, NextPollAtUtc timestamptz NULL, FailureCode text NULL, SourceWatermarkUtc timestamptz NULL,
    CoverageJson text NOT NULL, ActiveAttemptId uuid NULL, LeaseExpiresAtUtc timestamptz NULL,
    PRIMARY KEY (TenantId,OrganizationId,Alias),
    CHECK (Provider IN ('Azure','Aws','Gcp')), CHECK (length(Alias) BETWEEN 1 AND 64),
    CHECK (ConsecutiveFailures >= 0), CHECK (NOT Connected OR Enabled)
);
CREATE INDEX IF NOT EXISTS IX_SharedContinuousChangeFeeds_Due
    ON SharedContinuousChangeFeeds (NextPollAtUtc) WHERE Enabled AND Connected;
CREATE TABLE IF NOT EXISTS SharedContinuousChangeAttempts (
    TenantId uuid NOT NULL, OrganizationId uuid NOT NULL, Alias text NOT NULL, SourceEpoch uuid NOT NULL,
    AttemptId uuid NOT NULL, StartedAtUtc timestamptz NOT NULL, CompletedAtUtc timestamptz NULL,
    Outcome text NOT NULL, FailureCode text NULL,
    PRIMARY KEY (TenantId,OrganizationId,Alias,SourceEpoch,AttemptId)
);
CREATE INDEX IF NOT EXISTS IX_SharedContinuousChangeAttempts_History
    ON SharedContinuousChangeAttempts (TenantId,OrganizationId,Alias,StartedAtUtc DESC);
CREATE TABLE IF NOT EXISTS SharedContinuousChangeBatches (
    TenantId uuid NOT NULL, OrganizationId uuid NOT NULL, Alias text NOT NULL, SourceEpoch uuid NOT NULL,
    BatchId uuid NOT NULL, PayloadSha256 text NOT NULL, ReceiptJson text NOT NULL, CoverageJson text NOT NULL, PayloadJson text NOT NULL,
    PRIMARY KEY (TenantId,OrganizationId,Alias,SourceEpoch,BatchId)
);
CREATE TABLE IF NOT EXISTS SharedContinuousChangeRevisions (
    TenantId uuid NOT NULL, OrganizationId uuid NOT NULL, Alias text NOT NULL, SourceEpoch uuid NOT NULL,
    SourceId text NOT NULL, SourceEventId text NOT NULL, RevisionId text NOT NULL,
    ReceivedAtUtc timestamptz NOT NULL, SourceEventAtUtc timestamptz NOT NULL, PayloadJson text NOT NULL,
    PRIMARY KEY (TenantId,OrganizationId,Alias,SourceEpoch,SourceEventId,RevisionId)
);
CREATE INDEX IF NOT EXISTS IX_SharedContinuousChangeRevisions_History
    ON SharedContinuousChangeRevisions (TenantId,OrganizationId,Alias,ReceivedAtUtc DESC,SourceEventId,RevisionId);
CREATE OR REPLACE FUNCTION yask_reject_change_feed_mutation() RETURNS trigger
LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'Continuous change evidence is append-only'; END; $$;
DROP TRIGGER IF EXISTS TR_SharedContinuousChangeBatches_AppendOnly ON SharedContinuousChangeBatches;
CREATE TRIGGER TR_SharedContinuousChangeBatches_AppendOnly BEFORE UPDATE OR DELETE ON SharedContinuousChangeBatches
    FOR EACH ROW EXECUTE FUNCTION yask_reject_change_feed_mutation();
DROP TRIGGER IF EXISTS TR_SharedContinuousChangeBatches_NoTruncate ON SharedContinuousChangeBatches;
CREATE TRIGGER TR_SharedContinuousChangeBatches_NoTruncate BEFORE TRUNCATE ON SharedContinuousChangeBatches
    FOR EACH STATEMENT EXECUTE FUNCTION yask_reject_change_feed_mutation();
DROP TRIGGER IF EXISTS TR_SharedContinuousChangeRevisions_AppendOnly ON SharedContinuousChangeRevisions;
CREATE TRIGGER TR_SharedContinuousChangeRevisions_AppendOnly BEFORE UPDATE OR DELETE ON SharedContinuousChangeRevisions
    FOR EACH ROW EXECUTE FUNCTION yask_reject_change_feed_mutation();
DROP TRIGGER IF EXISTS TR_SharedContinuousChangeRevisions_NoTruncate ON SharedContinuousChangeRevisions;
CREATE TRIGGER TR_SharedContinuousChangeRevisions_NoTruncate BEFORE TRUNCATE ON SharedContinuousChangeRevisions
    FOR EACH STATEMENT EXECUTE FUNCTION yask_reject_change_feed_mutation();
GRANT SELECT,INSERT,UPDATE ON SharedContinuousChangeFeeds,SharedContinuousChangeAttempts TO yask_ciem_runtime;
GRANT SELECT,INSERT ON SharedContinuousChangeBatches,SharedContinuousChangeRevisions TO yask_ciem_runtime;
COMMIT;
