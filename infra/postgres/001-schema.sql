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

CREATE TABLE IF NOT EXISTS SharedAccessReviewAuditEvents (
    TenantId uuid NOT NULL,
    OrganizationId uuid NOT NULL,
    CampaignId uuid NOT NULL,
    EventId uuid NOT NULL,
    CorrelationId uuid NOT NULL,
    ItemId uuid NULL,
    OccurredAtUtc timestamptz NOT NULL,
    Actor text NOT NULL,
    ActorUserId uuid NULL,
    Action text NOT NULL,
    Decision text NULL,
    Rationale text NOT NULL,
    EvidenceSha256 text NULL,
    EventJson text NOT NULL,
    PRIMARY KEY (TenantId, OrganizationId, CampaignId, EventId),
    FOREIGN KEY (TenantId, OrganizationId, CampaignId)
        REFERENCES SharedAccessReviewCampaigns (TenantId, OrganizationId, CampaignId) ON DELETE RESTRICT
);

ALTER TABLE SharedAccessReviewAuditEvents ADD COLUMN IF NOT EXISTS ActorUserId uuid NULL;

CREATE INDEX IF NOT EXISTS IX_SharedAccessReviewAuditEvents_Correlation
    ON SharedAccessReviewAuditEvents (TenantId, OrganizationId, CorrelationId);

CREATE OR REPLACE FUNCTION yask_reject_shared_review_audit_mutation() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'Shared access-review audit events are append-only';
END;
$$;

DROP TRIGGER IF EXISTS TR_SharedAccessReviewAuditEvents_AppendOnly ON SharedAccessReviewAuditEvents;
CREATE TRIGGER TR_SharedAccessReviewAuditEvents_AppendOnly
BEFORE UPDATE OR DELETE ON SharedAccessReviewAuditEvents
FOR EACH ROW EXECUTE FUNCTION yask_reject_shared_review_audit_mutation();

DROP TRIGGER IF EXISTS TR_SharedAccessReviewAuditEvents_NoTruncate ON SharedAccessReviewAuditEvents;
CREATE TRIGGER TR_SharedAccessReviewAuditEvents_NoTruncate
BEFORE TRUNCATE ON SharedAccessReviewAuditEvents
FOR EACH STATEMENT EXECUTE FUNCTION yask_reject_shared_review_audit_mutation();

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

CREATE TABLE IF NOT EXISTS SharedCiemScans (
    TenantId uuid NOT NULL,
    OrganizationId uuid NOT NULL,
    ScanId uuid NOT NULL,
    CompletedAtUtc timestamptz NOT NULL,
    RequestHash text NOT NULL,
    PayloadJson text NOT NULL,
    PRIMARY KEY (TenantId, OrganizationId, ScanId)
);

CREATE INDEX IF NOT EXISTS IX_SharedCiemScans_Org_Completed
    ON SharedCiemScans (TenantId, OrganizationId, CompletedAtUtc DESC);

CREATE TABLE IF NOT EXISTS SharedCiemScanRecords (
    TenantId uuid NOT NULL,
    OrganizationId uuid NOT NULL,
    ScanId uuid NOT NULL,
    Provider text NOT NULL,
    RecordKind text NOT NULL,
    RecordId text NOT NULL,
    ExternalAccountId text NOT NULL,
    PayloadJson text NOT NULL,
    PRIMARY KEY (TenantId, OrganizationId, ScanId, Provider, RecordKind, ExternalAccountId, RecordId),
    FOREIGN KEY (TenantId, OrganizationId, ScanId)
        REFERENCES SharedCiemScans (TenantId, OrganizationId, ScanId) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS IX_SharedCiemScanRecords_Kind
    ON SharedCiemScanRecords (TenantId, OrganizationId, ScanId, RecordKind, ExternalAccountId);

CREATE TABLE IF NOT EXISTS SharedScanSchedules (
    TenantId uuid NOT NULL,
    OrganizationId uuid NOT NULL,
    ScheduleId uuid NOT NULL,
    Provider text NOT NULL,
    ExternalAccountId text NOT NULL,
    DisplayName text NOT NULL,
    IntervalMinutes integer NOT NULL CHECK (IntervalMinutes BETWEEN 5 AND 10080),
    MaxRetries integer NOT NULL CHECK (MaxRetries BETWEEN 0 AND 4),
    Enabled boolean NOT NULL,
    NextRunAtUtc timestamptz NOT NULL,
    CreatedAtUtc timestamptz NOT NULL,
    UpdatedAtUtc timestamptz NOT NULL,
    LastSuccessfulScanAtUtc timestamptz NULL,
    PRIMARY KEY (TenantId, OrganizationId, ScheduleId),
    UNIQUE (TenantId, OrganizationId, Provider, ExternalAccountId)
);

CREATE INDEX IF NOT EXISTS IX_SharedScanSchedules_Due
    ON SharedScanSchedules (NextRunAtUtc) WHERE Enabled;

CREATE TABLE IF NOT EXISTS SharedScanJobs (
    TenantId uuid NOT NULL,
    OrganizationId uuid NOT NULL,
    JobId uuid NOT NULL,
    ScheduleId uuid NOT NULL,
    Provider text NOT NULL,
    ExternalAccountId text NOT NULL,
    DisplayName text NOT NULL,
    Status text NOT NULL CHECK (Status IN ('Queued', 'Running', 'Succeeded', 'Partial', 'Failed', 'Cancelled')),
    AttemptCount integer NOT NULL DEFAULT 0 CHECK (AttemptCount >= 0),
    MaxRetries integer NOT NULL CHECK (MaxRetries BETWEEN 0 AND 4),
    QueuedAtUtc timestamptz NOT NULL,
    StartedAtUtc timestamptz NULL,
    CompletedAtUtc timestamptz NULL,
    NextAttemptAtUtc timestamptz NULL,
    LeaseExpiresAtUtc timestamptz NULL,
    ActiveAttemptId uuid NULL,
    DurationMilliseconds bigint NULL,
    ScanId uuid NULL,
    CancellationRequested boolean NOT NULL DEFAULT false,
    LastError text NULL,
    PRIMARY KEY (TenantId, OrganizationId, JobId),
    FOREIGN KEY (TenantId, OrganizationId, ScheduleId)
        REFERENCES SharedScanSchedules (TenantId, OrganizationId, ScheduleId) ON DELETE RESTRICT
);

ALTER TABLE SharedScanJobs ADD COLUMN IF NOT EXISTS ActiveAttemptId uuid NULL;

CREATE UNIQUE INDEX IF NOT EXISTS UX_SharedScanJobs_ActiveAccount
    ON SharedScanJobs (TenantId, OrganizationId, Provider, ExternalAccountId)
    WHERE Status IN ('Queued', 'Running');
CREATE INDEX IF NOT EXISTS IX_SharedScanJobs_Queue
    ON SharedScanJobs (Status, NextAttemptAtUtc, QueuedAtUtc);
CREATE INDEX IF NOT EXISTS IX_SharedScanJobs_Org_Queued
    ON SharedScanJobs (TenantId, OrganizationId, QueuedAtUtc DESC);

CREATE TABLE IF NOT EXISTS SharedScanJobAttempts (
    TenantId uuid NOT NULL,
    OrganizationId uuid NOT NULL,
    JobId uuid NOT NULL,
    AttemptId uuid NOT NULL,
    AttemptNumber integer NOT NULL CHECK (AttemptNumber > 0),
    StartedAtUtc timestamptz NOT NULL,
    CompletedAtUtc timestamptz NULL,
    DurationMilliseconds bigint NULL,
    Outcome text NOT NULL,
    Error text NULL,
    PRIMARY KEY (TenantId, OrganizationId, JobId, AttemptId),
    UNIQUE (TenantId, OrganizationId, JobId, AttemptNumber),
    FOREIGN KEY (TenantId, OrganizationId, JobId)
        REFERENCES SharedScanJobs (TenantId, OrganizationId, JobId) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS SharedScanJobCheckpoints (
    TenantId uuid NOT NULL,
    OrganizationId uuid NOT NULL,
    JobId uuid NOT NULL,
    DataSource text NOT NULL,
    Status text NOT NULL,
    Records integer NOT NULL CHECK (Records >= 0),
    UpdatedAtUtc timestamptz NOT NULL,
    Details text NULL,
    PRIMARY KEY (TenantId, OrganizationId, JobId, DataSource),
    FOREIGN KEY (TenantId, OrganizationId, JobId)
        REFERENCES SharedScanJobs (TenantId, OrganizationId, JobId) ON DELETE CASCADE
);

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

CREATE TABLE IF NOT EXISTS ciem_audit_archive_segments (
    tenant_id uuid NOT NULL,
    first_sequence bigint NOT NULL CHECK (first_sequence > 0),
    last_sequence bigint NOT NULL CHECK (last_sequence >= first_sequence),
    last_hash text NOT NULL,
    sha256 text NOT NULL,
    object_key text NOT NULL UNIQUE,
    retain_until_utc timestamptz NOT NULL,
    archived_at_utc timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, first_sequence),
    UNIQUE (tenant_id, last_sequence)
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

ALTER TABLE ciem_audit_heads ENABLE ROW LEVEL SECURITY;
ALTER TABLE ciem_audit_heads FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS ciem_audit_heads_tenant_isolation ON ciem_audit_heads;
CREATE POLICY ciem_audit_heads_tenant_isolation ON ciem_audit_heads
    USING (tenant_id::text = current_setting('app.tenant_id', true))
    WITH CHECK (tenant_id::text = current_setting('app.tenant_id', true));

ALTER TABLE ciem_audit_events ENABLE ROW LEVEL SECURITY;
ALTER TABLE ciem_audit_events FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS ciem_audit_events_tenant_isolation ON ciem_audit_events;
CREATE POLICY ciem_audit_events_tenant_isolation ON ciem_audit_events
    USING (tenant_id::text = current_setting('app.tenant_id', true))
    WITH CHECK (tenant_id::text = current_setting('app.tenant_id', true));

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'yask_ciem_runtime') THEN
        CREATE ROLE yask_ciem_runtime NOLOGIN;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'yask_ciem_archiver') THEN
        CREATE ROLE yask_ciem_archiver NOLOGIN;
    END IF;
END;
$$;

DROP POLICY IF EXISTS ciem_audit_heads_archive_read ON ciem_audit_heads;
CREATE POLICY ciem_audit_heads_archive_read ON ciem_audit_heads
    FOR SELECT TO yask_ciem_archiver USING (true);
DROP POLICY IF EXISTS ciem_audit_events_archive_read ON ciem_audit_events;
CREATE POLICY ciem_audit_events_archive_read ON ciem_audit_events
    FOR SELECT TO yask_ciem_archiver USING (true);

GRANT USAGE ON SCHEMA public TO yask_ciem_runtime;
GRANT USAGE ON SCHEMA public TO yask_ciem_archiver;
GRANT SELECT, INSERT, UPDATE ON SharedOrganizationDirectories TO yask_ciem_runtime;
GRANT SELECT, INSERT, DELETE ON SharedAccountBindings TO yask_ciem_runtime;
GRANT SELECT, INSERT, UPDATE ON SharedAccessReviewCampaigns TO yask_ciem_runtime;
REVOKE ALL ON SharedAccessReviewAuditEvents FROM PUBLIC;
REVOKE UPDATE, DELETE, TRUNCATE ON SharedAccessReviewAuditEvents FROM yask_ciem_runtime;
GRANT SELECT, INSERT ON SharedAccessReviewAuditEvents TO yask_ciem_runtime;
GRANT SELECT, INSERT ON SharedScanSummaries TO yask_ciem_runtime;
GRANT SELECT, INSERT ON SharedCiemScans, SharedCiemScanRecords TO yask_ciem_runtime;
GRANT SELECT, INSERT, UPDATE ON SharedScanSchedules TO yask_ciem_runtime;
GRANT SELECT, INSERT, UPDATE ON SharedScanJobs TO yask_ciem_runtime;
GRANT SELECT, INSERT, UPDATE ON SharedScanJobAttempts, SharedScanJobCheckpoints TO yask_ciem_runtime;
GRANT SELECT, INSERT, UPDATE ON ciem_audit_heads TO yask_ciem_runtime;
REVOKE ALL ON ciem_audit_events FROM PUBLIC;
REVOKE UPDATE, DELETE, TRUNCATE ON ciem_audit_events FROM yask_ciem_runtime;
GRANT SELECT, INSERT ON ciem_audit_events TO yask_ciem_runtime;
REVOKE ALL ON ciem_audit_archive_segments FROM PUBLIC;
GRANT SELECT, INSERT ON ciem_audit_heads, ciem_audit_events TO yask_ciem_archiver;
GRANT SELECT, INSERT ON ciem_audit_archive_segments TO yask_ciem_archiver;
