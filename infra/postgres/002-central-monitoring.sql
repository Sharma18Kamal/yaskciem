-- Apply after 001-schema.sql, using the DBA migration identity.
CREATE TABLE IF NOT EXISTS SharedMonitoringBaselines (
    TenantId uuid NOT NULL, OrganizationId uuid NOT NULL, Provider text NOT NULL,
    ExternalAccountId text NOT NULL, Kind text NOT NULL, Profile text NOT NULL,
    ScanId uuid NOT NULL, CompletedAtUtc timestamptz NOT NULL, EntitiesJson text NOT NULL,
    PRIMARY KEY (TenantId, OrganizationId, Provider, ExternalAccountId, Kind, Profile)
);
CREATE TABLE IF NOT EXISTS SharedNotificationRules (
    TenantId uuid NOT NULL, OrganizationId uuid NOT NULL, RuleId uuid NOT NULL, PayloadJson text NOT NULL,
    PRIMARY KEY (TenantId, OrganizationId, RuleId)
);
CREATE TABLE IF NOT EXISTS SharedDriftEvents (
    TenantId uuid NOT NULL, OrganizationId uuid NOT NULL, EventId uuid NOT NULL,
    DetectedAtUtc timestamptz NOT NULL, PayloadJson text NOT NULL,
    PRIMARY KEY (TenantId, OrganizationId, EventId)
);
CREATE INDEX IF NOT EXISTS IX_SharedDriftEvents_Organization
    ON SharedDriftEvents (TenantId, OrganizationId, DetectedAtUtc DESC, EventId);
CREATE TABLE IF NOT EXISTS SharedNotificationDeliveries (
    TenantId uuid NOT NULL, OrganizationId uuid NOT NULL, DeliveryId uuid NOT NULL,
    EventId uuid NOT NULL, RuleId uuid NOT NULL, Channel text NOT NULL,
    Status text NOT NULL CHECK (Status IN ('Pending', 'Sending', 'Delivered', 'Failed', 'Suppressed')),
    AttemptCount integer NOT NULL DEFAULT 0, CreatedAtUtc timestamptz NOT NULL,
    NextAttemptAtUtc timestamptz NULL, CompletedAtUtc timestamptz NULL, FailureCode text NULL,
    LeaseExpiresAtUtc timestamptz NULL, ActiveAttemptId uuid NULL,
    PRIMARY KEY (TenantId, OrganizationId, DeliveryId),
    UNIQUE (TenantId, OrganizationId, EventId, Channel),
    FOREIGN KEY (TenantId, OrganizationId, EventId)
        REFERENCES SharedDriftEvents (TenantId, OrganizationId, EventId)
);
CREATE INDEX IF NOT EXISTS IX_SharedNotificationDeliveries_Queue
    ON SharedNotificationDeliveries (NextAttemptAtUtc, CreatedAtUtc) WHERE Status IN ('Pending', 'Sending');
CREATE INDEX IF NOT EXISTS IX_SharedNotificationDeliveries_Organization
    ON SharedNotificationDeliveries (TenantId, OrganizationId, CreatedAtUtc DESC, DeliveryId);
CREATE TABLE IF NOT EXISTS SharedNotificationAttempts (
    TenantId uuid NOT NULL, OrganizationId uuid NOT NULL, DeliveryId uuid NOT NULL,
    AttemptId uuid NOT NULL, AttemptNumber integer NOT NULL, StartedAtUtc timestamptz NOT NULL,
    CompletedAtUtc timestamptz NULL, HttpStatus integer NULL, FailureCode text NULL,
    PRIMARY KEY (TenantId, OrganizationId, DeliveryId, AttemptId),
    UNIQUE (TenantId, OrganizationId, DeliveryId, AttemptNumber),
    FOREIGN KEY (TenantId, OrganizationId, DeliveryId)
        REFERENCES SharedNotificationDeliveries (TenantId, OrganizationId, DeliveryId)
);
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname='yask_ciem_runtime') THEN
        GRANT SELECT, INSERT, UPDATE ON SharedMonitoringBaselines, SharedNotificationRules,
            SharedNotificationDeliveries, SharedNotificationAttempts TO yask_ciem_runtime;
        GRANT SELECT, INSERT ON SharedDriftEvents TO yask_ciem_runtime;
    END IF;
END;
$$;
