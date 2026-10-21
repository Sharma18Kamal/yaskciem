-- Apply after 001-schema.sql and 002-central-monitoring.sql using the DBA identity.
ALTER TABLE SharedAccessReviewCampaigns ADD COLUMN IF NOT EXISTS NextGovernanceCheckAtUtc timestamptz NULL DEFAULT now();
CREATE INDEX IF NOT EXISTS IX_SharedAccessReviewCampaigns_DeadlineQueue
    ON SharedAccessReviewCampaigns (NextGovernanceCheckAtUtc)
    WHERE NextGovernanceCheckAtUtc IS NOT NULL;
