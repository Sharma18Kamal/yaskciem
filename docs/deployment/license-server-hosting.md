# Self-Hosted License Server Deployment

This deployment bundle runs the ASP.NET Core License Server on Windows x64. It is separate from the YASK-CIEM desktop MSI/MSIX. It does not install PostgreSQL or an S3-compatible Object Lock service; provision those separately before starting the API.

## Prerequisites

- Windows Server 2019 or later (or an organization-approved Windows Server release), with the .NET runtime included in the self-contained bundle.
- PostgreSQL 15+ reachable from the License Server host; apply `infra/postgres/001-schema.sql` and create the dedicated `yask_ciem_runtime` and `yask_ciem_archiver` logins as described in [PostgreSQL setup](../../infra/postgres/README.md).
- An independently administered S3-compatible bucket with versioning and Object Lock enabled, default COMPLIANCE retention at least equal to the audit retention policy, and a least-privilege archive identity.
- A multitenant Entra API app registration, its client ID/audience, and the required scopes/roles.
- A service identity that can read its configuration and Windows Credential Manager entries and reach the database and archive endpoint.

## Install the Bundle

1. Download and verify the signed release archive from the organization's approved distribution channel. Extract `YASK-CIEM-LicenseServer-win-x64-<version>.zip` to a protected directory such as `C:\Program Files\YASK-CIEM\LicenseServer`.
2. Copy `config\license-server.settings.example.json` to `appsettings.Production.json`. Replace the Entra client ID/audience, active key ID, PostgreSQL database name, Object Lock endpoint, signing region, and bucket with approved values. Never put passwords, access keys, or actual integrity keys in the example template or source control.
3. Configure `ConnectionStrings__Postgres` through the service identity's approved secret provider. Apply the DBA schema and grant that identity only the appropriate runtime role.
4. Log on as the Windows identity that will run the License Server and execute the archive wizard from the extracted package:

   ```powershell
   .\scripts\configure-audit-archive.ps1 `
     -SettingsPath '.\appsettings.Production.json'
   ```

   The wizard prompts for the Object Lock endpoint, signing region, bucket, archive database connection string, S3 access key, and S3 secret key. It stores only non-secret values in JSON; database and S3 secrets go to that identity's Windows Credential Manager. Run the server under the same identity so it can retrieve those credentials.
5. Configure Entra settings and the integrity-key ring using the service identity's secret/configuration provider. Provide every historical audit key still needed to verify retained rows and archived segments. Configure `YASK_CIEM_ALERT_WEBHOOK_URL` or `Monitoring__AlertWebhookUrl` through the protected service environment if alert delivery is required.
6. Restrict inbound access to the License Server HTTPS reverse proxy and required private networks. Do not expose PostgreSQL (5432) or the S3 API/console to the public internet. Configure TLS at the approved reverse proxy and set the API's URL binding to a private listener.
7. Start the server executable under the configured service identity using the organization's approved Windows service/process supervisor. For a foreground smoke test from the package directory:

   ```powershell
   $env:ASPNETCORE_ENVIRONMENT = 'Production'
   $env:ASPNETCORE_URLS = 'http://127.0.0.1:5080'
   .\publish\CloudEntitle.LicenseServer.exe
   ```

   Do not use the foreground command as the production process supervisor.
8. From the server host or approved reverse proxy, request `/api/v1/readiness`. It must return HTTP 200 and show PostgreSQL and Object Lock archive ready, with archive lag below configured thresholds. Then perform a test audit append, verify it is archived, and run the read-only archive reconciliation command.

## Package Contents and Limits

The self-contained ZIP includes the License Server publish output, PostgreSQL schema, non-secret configuration template, and archive setup wizard. It does not include the desktop app, PostgreSQL binaries, Object Lock server, Entra app registration, database data, or production secrets. The package script does not register a Windows service; service identity, process supervision, TLS/reverse-proxy configuration, firewall policy, database provisioning, and Object Lock bucket provisioning remain administrator tasks.

Uninstalling the bundle must not delete PostgreSQL data or WORM archive objects. Back up and retain those separately according to the organization's recovery and compliance policy.
