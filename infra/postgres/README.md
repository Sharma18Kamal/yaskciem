# PostgreSQL Setup

The License Server uses one PostgreSQL database for shared workspace data and central audit events. Shared CIEM scans are persisted in `SharedCiemScans` with account-scoped normalized records in `SharedCiemScanRecords`; the API enforces organization membership, account bindings, and scan idempotency. Scheduled full scans use `SharedScanSchedules` and a durable `SharedScanJobs` queue, with attempt history and per-source checkpoints in `SharedScanJobAttempts` and `SharedScanJobCheckpoints`. Workers claim jobs with row-level skip-locked semantics and expiring leases; active-account uniqueness prevents duplicate overlapping jobs across schedules or server instances. Access-review campaigns are stored centrally with their evidence snapshots, attestations, and append-only correlated audit events in `SharedAccessReviewAuditEvents`. Review campaign updates and event insertion commit atomically. PostgreSQL must be reachable from every License Server instance over a protected network connection. Use PostgreSQL 15 or later, TLS certificate validation, routine backups, and a secret provider for connection credentials and the audit HMAC key.

## Schema and Runtime Role

As a PostgreSQL administrator, apply `001-schema.sql`, `002-central-monitoring.sql`, `003-governance-deadlines.sql`, then `004-operational-integrations.sql` to the target database. The base schema creates the required tables, indexes, append-only audit trigger, forced row-level security policies for audit events and tenant heads, and separate `yask_ciem_runtime` and `yask_ciem_archiver` NOLOGIN roles. The monitoring migration adds shared comparable-scan baselines, drift events, notification rules, deliveries, attempts, indexes, and runtime grants. Each audit transaction sets a transaction-local tenant ID; missing context matches no tenant. The archiver role can read audit rows across tenants and insert archive ledger rows, but cannot mutate audit events. Create separate login roles for the API and archive worker; do not run either service as the schema owner.

For example, after applying the schema, create a login using your organization's authentication policy and grant membership:

```sql
CREATE ROLE yask_ciem_service LOGIN;
GRANT yask_ciem_runtime TO yask_ciem_service;
CREATE ROLE yask_ciem_archive_service LOGIN;
GRANT yask_ciem_archiver TO yask_ciem_archive_service;
```

Do not put a password in a checked-in SQL file. Supply credentials using the deployment's secret provider or use an approved certificate/GSS authentication method.

## License Server Configuration

Configure:

- `ConnectionStrings__Postgres` with a PostgreSQL connection string using TLS validation, for example `Host=db.example.internal;Database=yask_ciem;Username=yask_ciem_service;Password=<secret>;SSL Mode=VerifyFull;Root Certificate=<trusted-ca-file>`.
- `AuditRetention__Provider=Postgres`.
- `AuditRetention__MinimumRetentionDays=2555` or the retention period approved by the data owner.
- `AuditRetention__IntegrityKey` with a Base64-encoded, cryptographically random key of at least 32 bytes for the initial legacy key, or `AuditRetention__IntegrityKeys__<key-id>` values plus `AuditRetention__IntegrityKeyId` for versioned keys.
- Optional operational controls: `Operations__RetentionDays=90`, `Operations__Limits__MaxScanSchedulesPerTenant=100`, `Operations__Limits__MaxActiveScanJobsPerTenant=25`, `Operations__Readiness__MaxQueueDepth=1000`, and `Operations__Readiness__MaxQueueAgeSeconds=3600`. Retention batches default to 500 rows, up to 20 batches daily; tune with `Operations__RetentionBatchSize` (1–5000) and `Operations__RetentionMaxBatchesPerRun` (1–100).
- The existing Entra settings required by the License Server.

Production startup does not create or migrate tables. Apply `005-operations-scale.sql` after migrations `001`–`004` using the DBA migration identity before deploying the updated server. `/api/v1/readiness` and `/health/ready` return HTTP 503 until the database, shared workspace tables, operations indexes, retention function, queue thresholds, and worker heartbeats are healthy. `/health/live` is an anonymous liveness check; `/metrics` requires the `operations.read` scope or `Ciem.Operations.Read` role. Keep application credentials separate from the DBA migration identity.

The scan worker runs as part of the License Server process and uses that process identity's cloud CLI authentication. Install/configure the Azure CLI, AWS CLI, and/or Google Cloud CLI required by scheduled accounts under the service identity, and grant only the approved read permissions. The worker doesn't reuse a Desktop user's credentials. Schedule intervals are 5 minutes to 7 days; retry count is capped at four. Failed scans use bounded exponential retry, partial scans persist their source coverage and are terminal outcomes, and cancellation is stored durably for the worker to observe. Incremental scans/checkpoints are not used to resume cloud collection; checkpoints report source progress for job visibility only.

The central notification worker consumes the shared PostgreSQL outbox independently of the Desktop. Configure organization-scoped HTTPS webhook channel secrets on the server as described in [alerting and notification](../../docs/architecture/alerting-notification.md#server-channel-configuration), then create rules in the Organizations workspace or API. Rules reference aliases, never URLs. Keep response-body/credential capture disabled in external HTTP diagnostics as well. The worker retries transient failures up to five attempts, records only safe failure codes/HTTP status, and leaves exhausted deliveries visibly failed. Delivery leases recover across restarts. By default, the daily operations worker prunes terminal scan jobs and completed attempt history for Delivered/Suppressed deliveries after 90 days. It retains delivery rows permanently as replay/idempotency tombstones; failed/dead-letter deliveries and attempts, drift events, baselines, scan records/evidence, and governance/audit evidence are not purged. Include all these rows in backups and see the [operations runbook](../../docs/deployment/operations-runbook.md) for the exact policy.

The governance worker evaluates shared review reminders, exception expiry and escalation independently of Desktop requests. Migration `003-governance-deadlines.sql` adds an indexed per-campaign next-check timestamp and queues existing campaigns once for evaluation. Relevant campaigns are rechecked every minute in bounded, transactionally locked batches; cancelled/draft and fully resolved non-exception campaigns leave the queue. State and immutable review audit events commit together, with safe retry after rollback. Readiness requires the new column; apply the migration with the DBA identity before deploying the updated server. No new runtime privileges are required because the existing campaign/audit table grants cover this work. Back up campaign payloads and review audit events together. Reminders remain in-app workflow records, not webhook/email deliveries.

Migration `004-operational-integrations.sql` extends the existing shared delivery queue with connector kind and explicit authenticated requester metadata. Existing deliveries default to SignedWebhook. Apply it before upgrading; readiness checks both columns. No new privileges are needed. Operational finding/review/scan events use the existing `SharedDriftEvents` table and durable outbox. Preserve event/delivery/attempt history together in backups; terminal Failed deliveries are retained as dead letters. Jira ambiguous sends are not automatically retried. Configure channels and ticket policy using the [operational integration guide](../../docs/architecture/api-integration.md#p14-shared-operational-integrations).

## WORM Archive

Provision an independently administered, S3-compatible bucket outside the application runtime with versioning and Object Lock enabled. Set its default retention to COMPLIANCE mode for at least the approved audit retention period. The archive service also applies COMPLIANCE retention to each object, verifies its SHA-256 metadata, version, and retain-until date, then advances the PostgreSQL archive ledger. Segments are capped by `SegmentEventCount` and the remaining tail is sealed each poll cycle.

Configure the License Server with:

- `AuditArchive__Provider=S3ObjectLock`
- `ConnectionStrings__AuditArchive` using the separate `yask_ciem_archive_service` login.
- `AuditArchive__Endpoint=https://<on-prem-object-store-endpoint>` and `AuditArchive__Region=<signing-region>`.
- `AuditArchive__Bucket=<object-lock-enabled-bucket>`.
- `AuditArchive__AccessKey` and `AuditArchive__SecretKey` from a secret provider. Grant only bucket-versioning/Object-Lock inspection, object read/metadata, object write, and retention extension permissions.
- Optional `AuditArchive__ObjectPrefix` (default `yask-ciem/audit/v1`), `AuditArchive__SegmentEventCount` (default 500), and `AuditArchive__PollIntervalSeconds` (default 60).

Use [`audit-archive.settings.example.json`](audit-archive.settings.example.json) as the non-secret settings template. Copy its `AuditArchive` section into the License Server's production configuration and replace the endpoint, signing region, and bucket with values from the storage administrator. The endpoint must be HTTPS and the signing region must match the S3-compatible service's configured SigV4 region. Supply `ConnectionStrings__AuditArchive`, `AuditArchive__AccessKey`, and `AuditArchive__SecretKey` separately through the secret provider; do not add them to the JSON file. Readiness remains unhealthy until the configured bucket passes versioning/Object Lock/COMPLIANCE checks.

On Windows, administrators can collect these values interactively using:

```powershell
./scripts/configure-audit-archive.ps1 -SettingsPath 'C:\Program Files\YASK-CIEM\LicenseServer\appsettings.Production.json'
```

Run the wizard as the same Windows identity that runs the License Server. It prompts for the non-secret endpoint, signing region, and bucket, merges those into the settings file, and stores the archive PostgreSQL connection string and S3 credentials in that identity's Windows Credential Manager. Secret prompts are masked; secret values are never written to the JSON file. Restart the License Server under that same identity after setup. If the service runs as a dedicated account, run the wizard in that account's session (or use your enterprise secret provider instead).

The archive database login is intentionally separate because it must read all tenant audit rows to seal segments; PostgreSQL RLS grants this cross-tenant read only to the `yask_ciem_archiver` role. `/api/v1/readiness` remains HTTP 503 until the bucket's versioning and COMPLIANCE retention configuration has been verified and the archive worker is healthy. The WORM bucket itself must be provisioned and managed by a separate storage administrator; the application does not create or weaken its retention policy.

Readiness reports the pending archive event count, oldest unarchived event time, and last successful segment time. Defaults mark archive readiness unhealthy when more than 5,000 events are pending or the oldest pending event exceeds 60 minutes. Tune with `AuditArchive__MaxPendingEvents` and `AuditArchive__MaxLagMinutes`; the worker logs a structured warning when either threshold is exceeded. PostgreSQL connection/schema checks and archive connectivity/Object Lock checks are both required for HTTP 200 from `/api/v1/readiness`.

## Backup and Restore Drills

Install PostgreSQL client tools matching the server's major version. Apply `backup-roles.sql` as a PostgreSQL administrator after migrations, then set login passwords separately using `psql`'s interactive `\password` command or the organization's approved secret-management workflow. Configure ordered `scram-sha-256` host rules before broader SSPI/GSS rules: limit the backup login to the target database and backup host range; limit the restore login to the maintenance/restore host range because it must connect to `postgres` and its newly created restore database. The backup login is not a superuser and cannot create databases or roles; it has `CONNECT` to the target database, `USAGE` on `public`, and `SELECT` on current/future tables and sequences. It has `BYPASSRLS` solely because `pg_dump` must read forced-RLS audit tables completely. This is a cluster-wide role attribute: do not grant this login access to other databases or membership in other roles. The restore login is also not a superuser; it has only the `CREATEDB` attribute needed to create its guarded disposable restore database. Do not reuse either account for the application.

For the daily backup, configure `PGHOST`, optional `PGPORT`, `PGDATABASE`, `PGUSER=yask_ciem_backup`, and its password in a protected `PGPASSFILE` or approved credential mechanism, never command-line arguments. For quarterly restore drills, configure `YASK_CIEM_RESTORE_PGUSER=yask_ciem_restore` and `YASK_CIEM_RESTORE_PGPASSFILE` to a separate protected passfile. The maintenance runner switches to this restore-only login for database creation, restore, validation, and cleanup, then restores the backup login settings. The backup is a custom-format `pg_dump` archive and does not include global roles, which remain managed by DBA scripts.

```powershell
./scripts/backup-postgres.ps1 -BackupDirectory 'D:\YASK-CIEM-Backups'
$env:PGUSER = $env:YASK_CIEM_RESTORE_PGUSER
$env:PGPASSFILE = $env:YASK_CIEM_RESTORE_PGPASSFILE
$env:YASK_CIEM_ALLOW_RESTORE_TEST = 'YES'
./scripts/test-postgres-restore.ps1 `
  -BackupFile 'D:\YASK-CIEM-Backups\yask-ciem-postgres-<timestamp>.dump' `
  -RestoreDatabaseName 'yask_ciem_restore_<change-id>'
```

For manual drills, restore the original backup `PGUSER` and `PGPASSFILE` after the command; the scheduled maintenance runner manages this switch automatically.

The restore drill verifies the SHA-256 sidecar and archive format, creates a new database only when its name matches the guarded `yask_ciem_restore_*` pattern and does not equal `PGDATABASE`, restores into it, checks required workspace/audit/archive tables and row-count queries, then drops only the database it created. Run this on a schedule and before upgrades; retain the dump and checksum together under the organization's backup retention policy. Do not point it at production. A successful drill should also be followed by application-level HMAC-chain verification and an Object Lock archive retrieval test before recording the recovery exercise as complete.

### Scheduling and Alerts

Configure the task account's `PGHOST`, `PGDATABASE`, backup `PGUSER=yask_ciem_backup`, and protected backup `PGPASSFILE`, plus `YASK_CIEM_RESTORE_PGUSER=yask_ciem_restore`, protected `YASK_CIEM_RESTORE_PGPASSFILE`, and `YASK_CIEM_ALERT_WEBHOOK_URL` as an HTTPS secret in the task account's environment. The account needs access to both passfiles and the backup directory; database privileges are limited to the separate roles described above. The alert URL is never printed in task logs.

Register a daily 02:00 backup and quarterly 03:00 restore drill (defaults; adjust for the organization's window):

```powershell
./scripts/register-postgres-maintenance-tasks.ps1 `
  -BackupDirectory 'D:\YASK-CIEM-Backups' `
  -RetentionDays 35 `
  -BackupTime '02:00' `
  -RestoreTime '03:00'
```

The script prompts for the Windows task account and registers a daily backup and quarterly restore drill (first day of January, April, July, and October). Backup-file retention is a separate setting from the 90-day operational row retention. The runner sends HTTPS success/failure alerts; archive worker alerts use `Monitoring__AlertWebhookUrl` or `YASK_CIEM_ALERT_WEBHOOK_URL` and fire on health-state changes (including recovery). Verify both tasks in Task Scheduler after registration. Run `scripts/run-postgres-maintenance.ps1 -Mode Backup ...` manually to verify credentials and webhook delivery before relying on the schedule.

## Existing SQLite Workspace Migration

The utility migrates the former shared License Server workspace SQLite database, not the desktop's local `cloudentitle.db`. It supports the legacy shared tables for organization directories, account bindings, access reviews, and scan summaries. It does not migrate central audit history, desktop inventory/findings, or normalized CIEM scan records. New full scan uploads are written directly to PostgreSQL.

First run a read-only count:

```powershell
dotnet run --project tools/CloudEntitle.PostgresMaintenance -- migrate-sqlite 'C:\path\to\shared-workspace.db'
```

After a PostgreSQL backup, confirm the destination database and load the required schema, configure `ConnectionStrings__Postgres` using an authorized migration login, then apply explicitly:

```powershell
$env:YASK_CIEM_ALLOW_SQLITE_MIGRATION = 'YES'
dotnet run --project tools/CloudEntitle.PostgresMaintenance -- migrate-sqlite 'C:\path\to\shared-workspace.db' --apply
Remove-Item Env:YASK_CIEM_ALLOW_SQLITE_MIGRATION
```

The migration uses one PostgreSQL transaction, never overwrites destination rows, and aborts on key conflicts. Compare source/destination counts and representative tenant records before cutover; keep the source database unchanged until accepted.

## Object-Lock Archive Reconciliation

Run this read-only command from an operator host with the archive PostgreSQL connection, S3 Object Lock credentials, and every retained audit integrity key loaded from the approved secret provider. Configure `AuditRetention__IntegrityKey` for a legacy-only store, or `AuditRetention__IntegrityKeys__<key-id>` for each retained key (including `legacy` for older records) and `AuditRetention__IntegrityKeyId` for the active key ID:

```powershell
dotnet run --project tools/CloudEntitle.PostgresMaintenance -- reconcile-archive
```

The command checks each ledger row against the stored object SHA-256, manifest tenant/sequence range/last hash, object metadata, COMPLIANCE mode, and retain-until date. It also recomputes every event HMAC using that event's recorded key ID, verifies tenant and sequence continuity, and checks the previous-hash chain through the segment's terminal ledger hash. It exits nonzero and identifies mismatched ledger keys without printing credentials or object contents. Run on a schedule and alert on nonzero exit; missing historical keys or any chain/object mismatch is a failed reconciliation.

## HMAC Key Rotation

New audit events include `IntegrityKeyId` in their HMAC input. Existing records without this field are treated as `legacy` and verified with the legacy payload format. To rotate, first configure both the existing key under `AuditRetention__IntegrityKeys__legacy` and a new random 32-byte-or-longer key under a new ID, then set `AuditRetention__IntegrityKeyId` to the new ID and restart all API/archiver instances together. Remove the old `AuditRetention__IntegrityKey` setting when using the key-ring form to avoid duplicate `legacy` configuration. Keep every prior key required by retained database rows and archived segments; retire a key only after those records have expired under policy and recovery/verification requirements are met. Never reuse a key ID for different key material.

## Retention and Migration Notes

The audit table is protected from normal `UPDATE` and `DELETE` operations by a trigger and runtime-role grants. This is not WORM: a database owner or superuser can remove the trigger or alter the data. The separate Object Lock archive provides immutable sealed segments; regularly verify restoration and retain the archive administrator credentials separately from the API operator.

The application does not automatically migrate SQLite workspace data. Before cutover, export the existing organization directories, account bindings, reviews, and scan summaries, load them into the corresponding PostgreSQL tables, and verify tenant counts and representative records. Keep the original SQLite files until the migration is accepted.
