# Production Operations Runbook

This runbook covers the License Server's current local/on-premises operations baseline. No hosted monitoring service, cloud exporter, automatic failover, or production deployment is configured by this repository.

## Health and metrics

- `GET /health/live` is an anonymous liveness probe. It does not depend on PostgreSQL.
- `GET /health/ready` and `GET /api/v1/readiness` report readiness. They require PostgreSQL operations schema/indexes, a fresh telemetry sample, scan queue depth/age within configured thresholds, and recent heartbeats from scan, notification, governance, telemetry, and retention workers. Archive health is also included by `/api/v1/readiness`.
- `GET /metrics` exposes Prometheus text and requires the `operations.read` scope or `Ciem.Operations.Read` app role. Restrict scraping to the internal monitoring network and use TLS.
- Metrics cover API request count and latency totals by bounded route template, scan queue depth/age, active scans, ingestion lag and last-ingested age, 24-hour scan completion/success, connector failures, and dead letters. Tenant identifiers are not metric labels. `System.Diagnostics.Metrics` instruments are available for future in-process OpenTelemetry export; no exporter is configured yet.

Defaults are 1,000 queued scans and 3,600 seconds maximum oldest queued age before readiness fails. Tune using `Operations__Readiness__MaxQueueDepth` and `Operations__Readiness__MaxQueueAgeSeconds`. Investigate queue growth by checking worker heartbeats, provider CLI availability/credentials, and `scripts/check-postgres-operations.ps1` output before increasing thresholds.

## Tenant limits and database maintenance

Defaults limit each tenant to 100 schedules and 25 queued/running scan jobs. Override with `Operations__Limits__MaxScanSchedulesPerTenant` and `Operations__Limits__MaxActiveScanJobsPerTenant`. Immediate duplicate requests remain idempotent; new work beyond the tenant limit returns HTTP 429. These limits protect queue capacity and are enforced under a per-tenant PostgreSQL transaction lock.

Apply schema migrations `001` through `005` with the DBA identity, then run:

```powershell
./scripts/check-postgres-operations.ps1
```

The check reports required index validity, bounded queue query plans, and table vacuum/analyze statistics. Review slow plans and stale statistics before adding indexes; coordinate any index changes with DBA migration procedures.

## Retention policy

`Operations__RetentionDays` defaults to 90. A daily worker prunes terminal Succeeded/Partial/Cancelled scan jobs (with their job attempts/checkpoints) and completed attempt rows belonging to old Delivered/Suppressed notifications. It processes at most 500 rows per batch and 20 batches per day by default; configure `Operations__RetentionBatchSize` (1–5,000) and `Operations__RetentionMaxBatchesPerRun` (1–100) to match measured workload. A warning is logged if the run reaches its batch limit.

Delivery records are retained as permanent idempotency/replay tombstones, even after their successful attempt details are pruned. Failed/dead-letter deliveries and their attempt history are retained for investigation. Drift events, notification rules/baselines, scan summaries/records/evidence, review and exception evidence, and central audit data are not removed by this worker. This is not a complete database-size cap: set and review capacity plans for retained evidence and audit data separately. Audit retention and Object Lock archive policy remain governed by the audit data owner.

## Backup, recovery, and restore drills

The agreed baseline is **RPO 24 hours / RTO 4 hours**: create and verify a PostgreSQL backup daily, store it with its SHA-256 sidecar in protected backup storage, and conduct a disposable-database restore drill quarterly. Backups should be copied off the database host and access restricted; this repository does not encrypt backup files itself. Use encrypted storage and follow organization key-management policy.

Register the repository's scheduled tasks with:

```powershell
./scripts/register-postgres-maintenance-tasks.ps1 `
  -BackupDirectory 'D:\YASK-CIEM-Backups' `
  -RetentionDays 35 `
  -BackupTime '02:00' `
  -RestoreTime '03:00'
```

The defaults create daily backups and quarterly restore drills on January 1, April 1, July 1, and October 1. The 35-day file retention is independent of the 90-day operational-row retention. The task account must have the backup login configured in `PGUSER`/protected `PGPASSFILE`, the separate restore login in `YASK_CIEM_RESTORE_PGUSER`/`YASK_CIEM_RESTORE_PGPASSFILE`, backup-directory permissions, and HTTPS alert secret. Confirm Task Scheduler's last run/result and alert delivery after registering.

For a manual restore drill, set `YASK_CIEM_ALLOW_RESTORE_TEST=YES` and run `scripts/test-postgres-restore.ps1` against a verified backup, naming a new disposable database `yask_ciem_restore_<change-id>`. The script validates the checksum, restores the archive, verifies required tables/indexes/function and row-count queries, then drops only the database it created. Never target the production database. Record drill start/end times and compare achieved restore time with the four-hour objective. A successful scripted drill validates PostgreSQL restore mechanics; it is not a production failover or a full application-level DR exercise. Also verify audit HMAC chains and retrieve a retained Object Lock segment before marking the quarterly exercise complete.

Daily backups yield a theoretical worst-case data loss of up to one day, only if the backup is successfully copied to independent storage and restore succeeds. There is no automated standby promotion or cross-host service failover. Treat RPO/RTO as operational objectives, not a tested service guarantee, until recovery exercises measure them.

## Incident triage

1. Check `/health/live`, `/health/ready`, `/api/v1/readiness`, and the protected `/metrics` endpoint.
2. Review structured logs for worker failures and safe error codes; do not log webhook credentials, response bodies, or connection strings.
3. Check database connectivity, index validity, queued scan depth/age, and failed/dead-letter deliveries.
4. Restore from the latest verified backup to an isolated database for recovery; preserve the source database and evidence until the incident owner authorizes a recovery cutover.
5. Record incident time, last known successful backup, measured data-loss window, recovery duration, and follow-up actions.
