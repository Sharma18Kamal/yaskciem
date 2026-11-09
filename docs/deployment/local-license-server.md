# Laptop License Server testing

The Windows laptop can run both Desktop and the License Server. No Azure VM is
needed. They remain separate processes: Desktop obtains the user's Entra tokens;
the API validates them and persists trial/license state in PostgreSQL.
Cloud provider sign-in is separate from organization/API sign-in.

## Initial setup

Use PowerShell 7 under the Windows user that will run both applications.
Install PostgreSQL 15+, .NET 10 SDK and Azure CLI. Configure two dedicated,
single-tenant Entra registrations:

- API: token version 2, identifier `api://<api-client-id>` and the delegated
  `access_as_user` scope.
- Desktop public client: `http://localhost` redirect, delegated Graph `User.Read`
  and that API scope. Allow user consent only as permitted by the tenant policy;
  obtain administrator approval if required. Do not reuse deployment identities.
- Create their enterprise application/service principal objects in the tenant.

For version 2 access tokens, `AzureAd:Audience` is the API client GUID, without
`api://`. The Desktop scope still uses
`api://<api-client-id>/access_as_user`. Using the scope's URI as the audience
causes HTTP 401 with `IDX10214` even after a successful sign-in. If an existing
local configuration has that value, correct only `AzureAd:Audience` in
`%LOCALAPPDATA%\YASK-CIEM\LocalLicenseServer\appsettings.Local.json` and restart
the License Server; do not rerun database setup or reset trial history.

The API preserves original JWT claim names (`tid`, `oid`, `scp`, `roles`).
Inbound claim mapping must remain disabled: otherwise valid tokens can pass
authentication but receive HTTP 403 because the API cannot find its required
tenant, user and delegated scope claims. This is a server configuration issue,
not a reason to grant broader cloud permissions or remove authorization checks.

Trust the local development HTTPS certificate:

```powershell
dotnet dev-certs https --trust
dotnet dev-certs https --check --trust
```

Run the setup script with the actual registration and archive resource IDs:

```powershell
.\scripts\configure-local-license-server.ps1 `
  -ApiClientId '<api-client-guid>' `
  -DesktopClientId '<desktop-client-guid>' `
  -TenantId '<tenant-guid>' `
  -StorageAccountResourceId '/subscriptions/<subscription-guid>/resourceGroups/<group>/providers/Microsoft.Storage/storageAccounts/<account>'
```

Use `-WhatIf` for a non-writing preview (it still checks PostgreSQL administrator
access). PostgreSQL administrator authentication must already be configured for
the current Windows user; the script never prompts for or resets that password.

The script refuses to overwrite an existing `yask_ciem_local` database or the
dedicated logins. It creates a new database, applies all numbered migrations, and
grants the runtime/archive logins only their corresponding application roles.
It does not start a trial, change trial history, grant paid access or alter other
databases. If setup is interrupted, review the partial state rather than deleting
the database or credentials and blindly retrying.

Generated database passwords and the HMAC integrity key are stored in that user's
Windows Credential Manager under `YASK-CIEM/LocalLicenseServer/yask_ciem_local/`.
Non-secret settings are in
`%LOCALAPPDATA%\YASK-CIEM\LocalLicenseServer\appsettings.Local.json`.
Do not delete those credentials or lose the audit key while retaining this database.

If PostgreSQL's loopback rules use SSPI, a DBA must explicitly approve these
narrow rules **before** its broader SSPI entries in `pg_hba.conf`:

```text
host yask_ciem_local yask_ciem_local_runtime,yask_ciem_local_archive 127.0.0.1/32 scram-sha-256
host yask_ciem_local yask_ciem_local_runtime,yask_ciem_local_archive ::1/128 scram-sha-256
```

Reload using `SELECT pg_reload_conf();` and check `pg_hba_file_rules` for errors.
Do not replace SSPI rules or enable `trust` authentication.

## Run and test

In the first PowerShell terminal:

```powershell
.\scripts\start-local-license-server.ps1
```

This sets Development mode, loads the local non-secret settings, and binds HTTPS
to `https://localhost:7443`. It does not expose a public server or install a
Windows service. Keep it running; Ctrl+C stops it.

Close old Desktop instances. In a second terminal:

```powershell
.\scripts\start-local-desktop.ps1
```

The Desktop launcher explicitly reloads the three user-scoped API settings, so a
stale VS Code/Explorer environment cannot silently use the previous endpoint.
It runs Desktop from source and verifies trusted server liveness first.

1. In **Organizations**, enter the tenant ID and sign in. Complete the separate
   API consent prompt if tenant policy allows it.
   Cached Azure/AWS CLI sessions do not authenticate the License Server and are
   not automatically selected as cloud connections at Desktop startup.
2. Select **Start 30-day trial**, then **Refresh license**. Trial registration is
   an explicit persistent operation; it is not performed by setup.
3. Connect the desired cloud provider and select a non-production, read-only
   account for the first scan.
4. Treat an archive/readiness warning as incomplete end-to-end testing, even if
   the trial endpoint or Desktop scan succeeds.

`/health/live` must return 200. Unauthenticated `/api/v1/subscription` must return
401. `/api/v1/readiness` must remain 503 until all dependencies, worker heartbeats
and the immutable Azure archive pass their checks. Neither setup nor the launcher
bypasses authentication, license checks or readiness.

Azure archive authentication is explicitly `AzureCli` in Development only.
Run `az login` under the same Windows identity; Desktop sign-in does not supply
archive credentials. Storage provisioning, RBAC and retention locking require
separate approval; see [Azure archive setup](azure-blob-audit-archive.md).

This setup uses unencrypted PostgreSQL connections over loopback only. Never
reuse it for remote database access or production hosting. Production requires
TLS-verified PostgreSQL, managed identity, a trusted hosted HTTPS endpoint and
approved service supervision.
