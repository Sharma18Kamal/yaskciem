# PostgreSQL Setup

The License Server uses one PostgreSQL database for shared workspace data and central audit events. Shared CIEM scans are persisted in `SharedCiemScans` with account-scoped normalized records in `SharedCiemScanRecords`; the API enforces organization membership, account bindings, and scan idempotency. Scheduled full scans use `SharedScanSchedules` and a durable `SharedScanJobs` queue, with attempt history and per-source checkpoints in `SharedScanJobAttempts` and `SharedScanJobCheckpoints`. Workers claim jobs with row-level skip-locked semantics and expiring leases; active-account uniqueness prevents duplicate overlapping jobs across schedules or server instances. Access-review campaigns are stored centrally with their evidence snapshots, attestations, and append-only correlated audit events in `SharedAccessReviewAuditEvents`. Review campaign updates and event insertion commit atomically. PostgreSQL must be reachable from every License Server instance over a protected network connection. Use PostgreSQL 15 or later, TLS certificate validation, routine backups, and a secret provider for connection credentials and the audit HMAC key.

## Schema and Runtime Role

As a PostgreSQL administrator, apply `001-schema.sql` to the target database. It creates the required tables, indexes, append-only audit trigger, forced row-level security policies for audit events and tenant heads, and separate `yask_ciem_runtime` and `yask_ciem_archiver` NOLOGIN roles. Each audit transaction sets a transaction-local tenant ID; missing context matches no tenant. The archiver role can read audit rows across tenants and insert archive ledger rows, but cannot mutate audit events. Create separate login roles for the API and archive worker; do not run either service as the schema owner.

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
- The existing Entra settings required by the License Server.

Production startup does not create or migrate tables. `/api/v1/readiness` returns HTTP 503 until the database, shared workspace tables (including shared review audit, scan record, schedule, job, attempt, and checkpoint tables), and required audit schemas are accessible. Keep application credentials separate from the DBA migration identity.

The scan worker runs as part of the License Server process and uses that process identity's cloud CLI authentication. Install/configure the Azure CLI, AWS CLI, and/or Google Cloud CLI required by scheduled accounts under the service identity, and grant only the approved read permissions. The worker doesn't reuse a Desktop user's credentials. Schedule intervals are 5 minutes to 7 days; retry count is capped at four. Failed scans use bounded exponential retry, partial scans persist their source coverage and are terminal outcomes, and cancellation is stored durably for the worker to observe. Incremental scans/checkpoints are not used to resume cloud collection; checkpoints report source progress for job visibility only.

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

Install PostgreSQL client tools matching the server's major version. Configure the libpq connection environment for a dedicated backup login (`PGHOST`, optional `PGPORT`, `PGDATABASE`, and `PGUSER`) with read access to the database; provide passwords via a protected `PGPASSFILE` or approved credential mechanism, never command-line arguments. The restore-drill login needs permission to create and drop databases matching the guarded restore-test name. The backup is a custom-format `pg_dump` archive and does not include global roles, which remain managed by `001-schema.sql`.

```powershell
./scripts/backup-postgres.ps1 -BackupDirectory 'D:\YASK-CIEM-Backups'
$env:YASK_CIEM_ALLOW_RESTORE_TEST = 'YES'
./scripts/test-postgres-restore.ps1 `
  -BackupFile 'D:\YASK-CIEM-Backups\yask-ciem-postgres-<timestamp>.dump' `
  -RestoreDatabaseName 'yask_ciem_restore_<change-id>'
```

The restore drill verifies the SHA-256 sidecar and archive format, creates a new database only when its name matches the guarded `yask_ciem_restore_*` pattern and does not equal `PGDATABASE`, restores into it, checks required workspace/audit/archive tables and row-count queries, then drops only the database it created. Run this on a schedule and before upgrades; retain the dump and checksum together under the organization's backup retention policy. Do not point it at production. A successful drill should also be followed by application-level HMAC-chain verification and an Object Lock archive retrieval test before recording the recovery exercise as complete.

### Scheduling and Alerts

Configure the task account's `PGHOST`, `PGDATABASE`, `PGUSER`, and protected `PGPASSFILE` (or approved SSPI mapping), plus `YASK_CIEM_ALERT_WEBHOOK_URL` as an HTTPS secret in the task account's environment. The account needs read access to the database, write access to the backup directory, and `CREATEDB`/`DROPDB` capability for the guarded restore-drill databases. The alert URL is never printed in task logs.

Register a daily 02:00 backup and Sunday 03:00 restore drill (defaults; adjust for the organization's window):

```powershell
./scripts/register-postgres-maintenance-tasks.ps1 `
  -BackupDirectory 'D:\YASK-CIEM-Backups' `
  -RetentionDays 35 `
  -BackupTime '02:00' `
  -RestoreTime '03:00'
```

The script prompts for the Windows task account and registers limited-privilege tasks. The runner sends HTTPS success/failure alerts; archive worker alerts use `Monitoring__AlertWebhookUrl` or `YASK_CIEM_ALERT_WEBHOOK_URL` and fire on health-state changes (including recovery). Verify both tasks in Task Scheduler after registration. Run `scripts/run-postgres-maintenance.ps1 -Mode Backup ...` manually to verify credentials and webhook delivery before relying on the schedule.

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
