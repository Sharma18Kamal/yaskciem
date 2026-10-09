# PostgreSQL Setup

The License Server uses one PostgreSQL database for shared workspace data and central audit events. PostgreSQL must be reachable from every License Server instance over a protected network connection. Use PostgreSQL 15 or later, TLS certificate validation, routine backups, and a secret provider for connection credentials and the audit HMAC key.

## Schema and Runtime Role

As a PostgreSQL administrator, apply `001-schema.sql` to the target database. It creates the required tables, indexes, append-only audit trigger, and a `yask_ciem_runtime` NOLOGIN role with the application privileges. Create the login used by the service separately and grant it membership in `yask_ciem_runtime`; do not run the application as the schema owner.

For example, after applying the schema, create a login using your organization's authentication policy and grant membership:

```sql
CREATE ROLE yask_ciem_service LOGIN;
GRANT yask_ciem_runtime TO yask_ciem_service;
```

Do not put a password in a checked-in SQL file. Supply credentials using the deployment's secret provider or use an approved certificate/GSS authentication method.

## License Server Configuration

Configure:

- `ConnectionStrings__Postgres` with a PostgreSQL connection string using TLS validation, for example `Host=db.example.internal;Database=yask_ciem;Username=yask_ciem_service;Password=<secret>;SSL Mode=VerifyFull;Root Certificate=<trusted-ca-file>`.
- `AuditRetention__Provider=Postgres`.
- `AuditRetention__MinimumRetentionDays=2555` or the retention period approved by the data owner.
- `AuditRetention__IntegrityKey` with a Base64-encoded, cryptographically random key of at least 32 bytes, delivered through a secret provider.
- The existing Entra settings required by the License Server.

Production startup does not create or migrate tables. `/api/v1/readiness` returns HTTP 503 until the database and both required schemas are accessible. Keep application credentials separate from the DBA migration identity.

## Retention and Migration Notes

The audit table is protected from normal `UPDATE` and `DELETE` operations by a trigger and runtime-role grants. This is not WORM: a database owner or superuser can remove the trigger or alter the data. If immutable retention is a compliance requirement, export sealed audit segments to independently administered on-premises WORM storage and verify restoration regularly.

The application does not automatically migrate SQLite workspace data. Before cutover, export the existing organization directories, account bindings, reviews, and scan summaries, load them into the corresponding PostgreSQL tables, and verify tenant counts and representative records. Keep the original SQLite files until the migration is accepted.