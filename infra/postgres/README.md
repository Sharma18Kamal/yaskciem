# PostgreSQL Setup

The License Server uses one PostgreSQL database for shared workspace data and central audit events. PostgreSQL must be reachable from every License Server instance over a protected network connection. Use PostgreSQL 15 or later, TLS certificate validation, routine backups, and a secret provider for connection credentials and the audit HMAC key.

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

Production startup does not create or migrate tables. `/api/v1/readiness` returns HTTP 503 until the database and both required schemas are accessible. Keep application credentials separate from the DBA migration identity.

## WORM Archive

Provision an independently administered, S3-compatible bucket outside the application runtime with versioning and Object Lock enabled. Set its default retention to COMPLIANCE mode for at least the approved audit retention period. The archive service also applies COMPLIANCE retention to each object, verifies its SHA-256 metadata, version, and retain-until date, then advances the PostgreSQL archive ledger. Segments are capped by `SegmentEventCount` and the remaining tail is sealed each poll cycle.

Configure the License Server with:

- `AuditArchive__Provider=S3ObjectLock`
- `ConnectionStrings__AuditArchive` using the separate `yask_ciem_archive_service` login.
- `AuditArchive__Endpoint=https://<on-prem-object-store-endpoint>` and `AuditArchive__Region=<signing-region>`.
- `AuditArchive__Bucket=<object-lock-enabled-bucket>`.
- `AuditArchive__AccessKey` and `AuditArchive__SecretKey` from a secret provider. Grant only bucket-versioning/Object-Lock inspection, object read/metadata, object write, and retention extension permissions.
- Optional `AuditArchive__ObjectPrefix` (default `yask-ciem/audit/v1`), `AuditArchive__SegmentEventCount` (default 500), and `AuditArchive__PollIntervalSeconds` (default 60).

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

## HMAC Key Rotation

New audit events include `IntegrityKeyId` in their HMAC input. Existing records without this field are treated as `legacy` and verified with the legacy payload format. To rotate, first configure both the existing key under `AuditRetention__IntegrityKeys__legacy` and a new random 32-byte-or-longer key under a new ID, then set `AuditRetention__IntegrityKeyId` to the new ID and restart all API/archiver instances together. Remove the old `AuditRetention__IntegrityKey` setting when using the key-ring form to avoid duplicate `legacy` configuration. Keep every prior key required by retained database rows and archived segments; retire a key only after those records have expired under policy and recovery/verification requirements are met. Never reuse a key ID for different key material.

## Retention and Migration Notes

The audit table is protected from normal `UPDATE` and `DELETE` operations by a trigger and runtime-role grants. This is not WORM: a database owner or superuser can remove the trigger or alter the data. The separate Object Lock archive provides immutable sealed segments; regularly verify restoration and retain the archive administrator credentials separately from the API operator.

The application does not automatically migrate SQLite workspace data. Before cutover, export the existing organization directories, account bindings, reviews, and scan summaries, load them into the corresponding PostgreSQL tables, and verify tenant counts and representative records. Keep the original SQLite files until the migration is accepted.
