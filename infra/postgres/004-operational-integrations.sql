-- Apply after 003-governance-deadlines.sql using the DBA identity.
ALTER TABLE SharedNotificationDeliveries ADD COLUMN IF NOT EXISTS ConnectorKind text NOT NULL DEFAULT 'SignedWebhook'
    CHECK (ConnectorKind IN ('SignedWebhook', 'Jira'));
ALTER TABLE SharedNotificationDeliveries ADD COLUMN IF NOT EXISTS RequestedBy uuid NULL;
