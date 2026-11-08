-- Apply after 005-operations-scale.sql using the DBA identity.
BEGIN;
ALTER TABLE SharedNotificationDeliveries DROP CONSTRAINT IF EXISTS sharednotificationdeliveries_connectorkind_check;
ALTER TABLE SharedNotificationDeliveries DROP CONSTRAINT IF EXISTS ck_sharednotificationdeliveries_connectorkind_v2;
ALTER TABLE SharedNotificationDeliveries ADD CONSTRAINT ck_sharednotificationdeliveries_connectorkind_v2
    CHECK (ConnectorKind IN ('SignedWebhook', 'Jira', 'SplunkHec', 'QRadarTls', 'SentinelLogs'));
COMMIT;
