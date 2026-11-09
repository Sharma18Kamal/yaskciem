-- Apply after 014 with the DBA identity. Resolve any reported duplicate live
-- grants manually before retrying; this migration never deletes JIT evidence.
BEGIN;
DO $$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM SharedPrivilegedAccessRequests
        WHERE Status IN ('PendingApproval','Approved','Queued','Issuing','Submitted','Active',
                         'ExpiryPending','OutcomeUnknown','RevokeQueued','RevokeIssuing','RevokeOutcomeUnknown')
        GROUP BY TenantId, OrganizationId,
            PayloadJson::jsonb->>'requestedBy',
            PayloadJson::jsonb->>'provider',
            lower(PayloadJson::jsonb->>'subscriptionId'),
            lower(PayloadJson::jsonb->>'scope'),
            lower(PayloadJson::jsonb->>'roleDefinitionId')
        HAVING count(*) > 1
    ) THEN
        RAISE EXCEPTION 'Duplicate outstanding JIT requests exist; resolve them by provider verification before applying 015-jit-safety-guards.sql.';
    END IF;
END $$;
CREATE UNIQUE INDEX IF NOT EXISTS UX_SharedPrivilegedAccessRequests_LiveGrant
    ON SharedPrivilegedAccessRequests (
        TenantId, OrganizationId,
        (PayloadJson::jsonb->>'requestedBy'),
        (PayloadJson::jsonb->>'provider'),
        (lower(PayloadJson::jsonb->>'subscriptionId')),
        (lower(PayloadJson::jsonb->>'scope')),
        (lower(PayloadJson::jsonb->>'roleDefinitionId'))
    )
    WHERE Status IN ('PendingApproval','Approved','Queued','Issuing','Submitted','Active',
                     'ExpiryPending','OutcomeUnknown','RevokeQueued','RevokeIssuing','RevokeOutcomeUnknown');
COMMIT;
