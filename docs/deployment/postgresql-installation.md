# Install PostgreSQL for Self-Hosted YASK-CIEM

This guide prepares PostgreSQL for a **self-hosted License Server** deployment. The Windows desktop client does not require PostgreSQL: it uses local SQLite and works without the License Server. The current desktop MSI/MSIX does not install the License Server or PostgreSQL.

Use PostgreSQL 15 or later. Install it on the License Server host for a single-machine deployment, or on a dedicated database host reachable only by the License Server over a protected private network. Keep PostgreSQL bound to localhost when both components are on the same machine; do not open port 5432 to the public internet.

## Online Installation

1. Sign in with a Windows account authorized to install machine-wide software. Open an elevated PowerShell window.
2. Install PostgreSQL from the official WinGet package, which downloads the PostgreSQL Windows installer:

   ```powershell
   winget install --id PostgreSQL.PostgreSQL.15 --exact --accept-source-agreements --accept-package-agreements
   ```

3. In the PostgreSQL installer, select the Database Server and Command Line Tools. Keep the default port `5432` unless your organization has assigned another port. Set a strong `postgres` administrator password at the installer prompt; do not put it in a command, script, or checked-in file.
4. Confirm the Windows service is running and the server accepts local connections:

   ```powershell
   $pgBin = 'C:\Program Files\PostgreSQL\15\bin'
   Get-Service -Name 'postgresql-x64-15'
   & "$pgBin\pg_isready.exe" --host=127.0.0.1 --port=5432
   ```

   If a different major version or install directory was selected, adjust `$pgBin` and the service name.
5. Create the YASK-CIEM database as the PostgreSQL administrator. This prompts for the administrator password:

   ```powershell
   & "$pgBin\createdb.exe" --host=127.0.0.1 --port=5432 --username=postgres yask_ciem
   ```

6. From the repository root, apply the DBA-managed schema to the new database:

   ```powershell
   & "$pgBin\psql.exe" --host=127.0.0.1 --port=5432 --username=postgres `
     --dbname=yask_ciem --set=ON_ERROR_STOP=1 --file=infra/postgres/001-schema.sql
   & "$pgBin\psql.exe" --host=127.0.0.1 --port=5432 --username=postgres `
     --dbname=yask_ciem --set=ON_ERROR_STOP=1 --file=infra\postgres\002-central-monitoring.sql
   & "$pgBin\psql.exe" --host=127.0.0.1 --port=5432 --username=postgres `
     --dbname=yask_ciem --set=ON_ERROR_STOP=1 --file=infra\postgres\003-governance-deadlines.sql
   & "$pgBin\psql.exe" --host=127.0.0.1 --port=5432 --username=postgres `
     --dbname=yask_ciem --set=ON_ERROR_STOP=1 --file=infra\postgres\004-operational-integrations.sql
   ```

7. Create separate service logins and grant their roles. At the `psql` prompt, enter the SQL below, then use `\password` to set each password interactively:

   ```sql
   CREATE ROLE yask_ciem_service LOGIN;
   GRANT yask_ciem_runtime TO yask_ciem_service;
   CREATE ROLE yask_ciem_archive_service LOGIN;
   GRANT yask_ciem_archiver TO yask_ciem_archive_service;
   \password yask_ciem_service
   \password yask_ciem_archive_service
   ```

   Do not run the License Server as `postgres` or as the schema owner. The archive login is separate because it needs cross-tenant read access to seal audit segments.
8. Store the service connection strings in the host's secret/configuration provider, not in source control. Configure `ConnectionStrings__Postgres` with the `yask_ciem_service` login and `ConnectionStrings__AuditArchive` with `yask_ciem_archive_service`. For a same-host service, use `Host=127.0.0.1;Port=5432;Database=yask_ciem;...`. For a remote database, use the approved DNS name, TLS certificate validation (`SSL Mode=VerifyFull`), and firewall rules limited to the License Server host.
9. Configure the remaining License Server settings, including Entra authentication and the audit integrity key ring. Apply the S3-compatible Object Lock archive setup from [PostgreSQL setup](../../infra/postgres/README.md); PostgreSQL alone does not make the audit archive WORM.
10. Start the self-hosted License Server and verify `/api/v1/readiness`. It should return HTTP 200 only when the database schemas and the independently administered Object Lock archive are both ready. PostgreSQL installation alone is not sufficient to make the entire service ready.

## Offline Installation

Use this method when the target server cannot access the internet. Stage files on a connected, trusted Windows machine, then transfer them using your organization's approved media process.

1. On the connected staging machine, download the official PostgreSQL installer for Windows x64:

   ```powershell
   New-Item -ItemType Directory -Path .\postgresql-offline -Force | Out-Null
   winget download --id PostgreSQL.PostgreSQL.15 --exact --architecture x64 `
     --download-directory .\postgresql-offline --accept-source-agreements --accept-package-agreements
   ```

2. Record the downloaded filename and verify its SHA-256 against the official PostgreSQL/EDB release information or the matching WinGet manifest before transfer:

   ```powershell
   Get-FileHash .\postgresql-offline\* -Algorithm SHA256
   ```

   Do not use `--ignore-security-hash` to bypass a failed verification.
3. Also stage the YASK-CIEM License Server deployment package, this repository's `infra/postgres/001-schema.sql`, `002-central-monitoring.sql`, `003-governance-deadlines.sql`, `004-operational-integrations.sql`, and the approved Object Lock archive setup materials. Verify each artifact using its publisher-provided signature or checksum.
4. On the offline target, sign in with an administrator account and run the staged PostgreSQL Windows installer interactively. Select Database Server and Command Line Tools, retain port `5432` unless instructed otherwise, and set the `postgres` administrator password at the installer prompt. Do not place passwords in a response file or command-line argument.
5. Follow the online guide from step 4 to verify the service, create the database, apply the schema, create separate service logins, and configure the License Server secret provider. Keep the service local-only when co-located; for a separate database host, configure only the approved private-network access.

## Desktop-Only Install

If you are installing only the Windows desktop application, skip this guide. PostgreSQL, the License Server connection strings, and the Object Lock archive are not prerequisites for local/offline desktop use.
