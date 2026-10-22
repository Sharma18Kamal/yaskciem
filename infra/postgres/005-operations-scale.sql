-- Apply after 004-operational-integrations.sql with the DBA migration identity.
-- These indexes support bounded queue polling, tenant history reads, and retention batches.
CREATE INDEX IF NOT EXISTS IX_SharedScanJobs_Retention
    ON SharedScanJobs (CompletedAtUtc, JobId)
    WHERE Status IN ('Succeeded', 'Partial', 'Cancelled');

CREATE INDEX IF NOT EXISTS IX_SharedNotificationAttempts_Retention
    ON SharedNotificationAttempts (CompletedAtUtc, DeliveryId)
    WHERE CompletedAtUtc IS NOT NULL;

CREATE INDEX IF NOT EXISTS IX_SharedNotificationAttempts_FailureTime
    ON SharedNotificationAttempts (StartedAtUtc)
    WHERE FailureCode IS NOT NULL OR HttpStatus >= 400;

CREATE INDEX IF NOT EXISTS IX_SharedScanSchedules_Tenant
    ON SharedScanSchedules (TenantId, OrganizationId, CreatedAtUtc DESC);

DO $migration$
DECLARE
    target_schema text := current_schema();
    function_body text := $function$
BEGIN
    IF p_cutoff IS NULL OR p_batch < 1 OR p_batch > 5000 THEN
        RAISE EXCEPTION 'Invalid operations retention batch';
    END IF;

    WITH candidates AS (
        SELECT ctid FROM SharedScanJobs
        WHERE CompletedAtUtc < p_cutoff AND Status IN ('Succeeded', 'Partial', 'Cancelled')
        ORDER BY CompletedAtUtc, JobId
        LIMIT p_batch FOR UPDATE SKIP LOCKED
    )
    DELETE FROM SharedScanJobs AS job USING candidates
    WHERE job.ctid = candidates.ctid;
    GET DIAGNOSTICS deleted_scan_jobs = ROW_COUNT;

    WITH candidates AS (
        SELECT attempt.ctid
        FROM SharedNotificationAttempts AS attempt
        INNER JOIN SharedNotificationDeliveries AS delivery
          ON delivery.TenantId=attempt.TenantId
         AND delivery.OrganizationId=attempt.OrganizationId
         AND delivery.DeliveryId=attempt.DeliveryId
        WHERE delivery.CompletedAtUtc < p_cutoff
          AND delivery.Status IN ('Delivered', 'Suppressed')
          AND attempt.CompletedAtUtc < p_cutoff
        ORDER BY attempt.CompletedAtUtc, attempt.DeliveryId, attempt.AttemptNumber
        LIMIT p_batch FOR UPDATE SKIP LOCKED
    )
    DELETE FROM SharedNotificationAttempts AS attempt USING candidates
    WHERE attempt.ctid = candidates.ctid;
    GET DIAGNOSTICS deleted_notification_attempts = ROW_COUNT;

    RETURN NEXT;
END;
$function$;
BEGIN
    EXECUTE format(
        'CREATE OR REPLACE FUNCTION %I.yask_prune_operational_history(p_cutoff timestamptz, p_batch integer)
         RETURNS TABLE (deleted_scan_jobs bigint, deleted_notification_attempts bigint)
         LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, %I AS %L',
        target_schema, target_schema, function_body);
    EXECUTE format(
        'REVOKE ALL ON FUNCTION %I.yask_prune_operational_history(timestamptz, integer) FROM PUBLIC',
        target_schema);
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'yask_ciem_runtime') THEN
        EXECUTE format(
            'GRANT EXECUTE ON FUNCTION %I.yask_prune_operational_history(timestamptz, integer) TO yask_ciem_runtime',
            target_schema);
    END IF;
END;
$migration$;
