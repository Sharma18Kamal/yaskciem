# Self-Hosted License Server Deployment

This deployment bundle runs the ASP.NET Core License Server on Windows x64. It is separate from the YASK-CIEM desktop MSI/MSIX. It does not install PostgreSQL or an S3-compatible Object Lock service; provision those separately before starting the API.

## Prerequisites

- Windows Server 2019 or later (or an organization-approved Windows Server release), with the .NET runtime included in the self-contained bundle.
- PostgreSQL 15+ reachable from the License Server host; apply `infra/postgres/001-schema.sql` and create the dedicated `yask_ciem_runtime` and `yask_ciem_archiver` logins as described in [PostgreSQL setup](../../infra/postgres/README.md).
- An independently administered S3-compatible bucket with versioning and Object Lock enabled, default COMPLIANCE retention at least equal to the audit retention policy, and a least-privilege archive identity.
- A multitenant Entra API app registration, its client ID/audience, and the required scopes/roles.
- A service identity that can read its configuration and Windows Credential Manager entries and reach the database and archive endpoint.

## Install the Bundle

1. Obtain the archive and its SHA256 from the organization's approved distribution channel and verify the checksum. The generated ZIP checksum detects corruption; it is not a publisher signature. Extract `YASK-CIEM-LicenseServer-win-x64-<version>.zip` to a protected directory such as `C:\Program Files\YASK-CIEM\LicenseServer`.
2. Under the Windows identity that will run the server, run the configuration wizard with real Entra app registration IDs and the approved HTTPS URL:

   ```powershell
   .\scripts\configure-license-server.ps1 `
     -SettingsPath '.\publish\appsettings.Production.json' `
     -ApiBaseUri 'https://ciem.example.org/' `
     -ApiClientId '<Entra-API-client-GUID>' `
     -DesktopClientId '<Entra-desktop-public-client-GUID>'
   ```

   The wizard prompts securely for the runtime PostgreSQL connection string and Base64 audit HMAC key. It stores them in that identity's Windows Credential Manager, not JSON. It refuses to overwrite an existing settings file. Use `-WhatIf` for a non-writing preview. It does not provision infrastructure or start a service. For an existing installation, retain all historical integrity keys and configure key rotation through the existing secret provider instead of replacing its key ring.
3. Configure `ConnectionStrings__Postgres` through the service identity's approved secret provider. Apply the DBA schema and grant that identity only the appropriate runtime role.
4. Log on as the Windows identity that will run the License Server and execute the archive wizard from the extracted package:

   ```powershell
   .\scripts\configure-audit-archive.ps1 `
     -SettingsPath '.\publish\appsettings.Production.json'
   ```

   The wizard prompts for the Object Lock endpoint, signing region, bucket, archive database connection string, S3 access key, and S3 secret key. It stores only non-secret values in JSON; database and S3 secrets go to that identity's Windows Credential Manager. Run the server under the same identity so it can retrieve those credentials.
5. Configure Entra settings and the integrity-key ring using the service identity's secret/configuration provider. Provide every historical audit key still needed to verify retained rows and archived segments. Configure `YASK_CIEM_ALERT_WEBHOOK_URL` or `Monitoring__AlertWebhookUrl` through the protected service environment if alert delivery is required.
6. Restrict inbound access to the License Server HTTPS reverse proxy and required private networks. Do not expose PostgreSQL (5432) or the S3 API/console to the public internet. Configure TLS at the approved reverse proxy and set the API's URL binding to a private listener.
7. Start the server executable under the configured service identity using the organization's approved Windows service/process supervisor. For a foreground smoke test from the package directory:

   ```powershell
   $env:ASPNETCORE_ENVIRONMENT = 'Production'
   $env:ASPNETCORE_URLS = 'http://127.0.0.1:5080'
   Set-Location .\publish
   .\CloudEntitle.LicenseServer.exe
   ```

   Do not use the foreground command as the production process supervisor.
8. From the server host or approved reverse proxy, request `/api/v1/readiness`. It must return HTTP 200 and show PostgreSQL and Object Lock archive ready, with archive lag below configured thresholds. Then perform a test audit append, verify it is archived, and run the read-only archive reconciliation command.

## Trial and license configuration (1.0.0.65)

Apply **all numbered migrations in filename order**, including `017-subscriptions.sql`,
with the DBA identity before deploying this version. The package now includes every
numbered migration, not just the base schema. Back up `CiemSubscriptions` with the
rest of the database: deleting or recreating it resets registration history.

The Entra API registration must expose `access_as_user`; configure the desktop
public client with delegated consent and its interactive redirect URI. On each
desktop set these non-secret user environment variables, then restart:

```powershell
[Environment]::SetEnvironmentVariable('YASK_CIEM_API_BASE_URL', 'https://ciem.example.org/', 'User')
[Environment]::SetEnvironmentVariable('YASK_CIEM_ENTRA_CLIENT_ID', '<desktop-client-GUID>', 'User')
[Environment]::SetEnvironmentVariable('YASK_CIEM_API_SCOPE', 'api://<API-client-GUID>/access_as_user', 'User')
```

The wizard can set these for its current Windows user with
`-ConfigureDesktopForCurrentUser`; normally configure desktop users separately
from the server's service identity. Sign in under **Organizations**, then select
**Start 30-day trial**. No hardware fingerprint, email-based local trial state,
or anonymous license registration is used. Each authenticated tenant gets exactly
720 hours once, shared across its users/organizations. Repeated registration returns
the original dates, including after expiry. `GET /api/v1/subscription` never starts
a trial; `POST /api/v1/subscription/trial` explicitly registers it.

New cloud collection, scheduled scans, JIT grants and API writes require an active
trial/license; failed validation blocks them. Existing reads/exports, local Demo
Mode, cancellation, rejection and revocation remain available. Background JIT
observation/revocation is never stopped by license expiry. There is no offline
license grace period or automatic paid activation/payment integration.

After a paid entitlement is approved, an authorized licensing DBA may set
`CiemSubscriptions.LicensedUntilUtc` to the approved UTC expiry for the exact tenant
using a parameterized administrative database operation, record it in the
organization's change/audit process, and have the user select **Refresh license**.
The API runtime has no update/delete/truncate privilege on this table and cannot
extend a trial or grant itself a paid license. End users cannot provision paid
access. This is self-hosted administrator-managed licensing, not vendor-signed
tamper-resistant licensing against the infrastructure owner.

## Package boundary

The self-contained ZIP includes the License Server publish output, all numbered PostgreSQL migrations, non-secret configuration template, and server/archive setup wizards. It does not include the desktop app, PostgreSQL binaries, Object Lock server, Entra app registration, database data, or production secrets. The package script does not register a Windows service; service identity, process supervision, TLS/reverse-proxy configuration, firewall policy, database provisioning, and Object Lock bucket provisioning remain administrator tasks.

Uninstalling the bundle must not delete PostgreSQL data or WORM archive objects. Back up and retain those separately according to the organization's recovery and compliance policy.
