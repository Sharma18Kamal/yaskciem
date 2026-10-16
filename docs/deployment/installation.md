# Install YASK-CIEM on Windows

YASK-CIEM supports Windows 10/11. Choose the installation method supplied by your organization. For a normal release, use the signed MSIX or App Installer feed. Use the ZIP for a portable/manual installation, or the per-user script when installing from a source checkout.

## Before you start

- Use a Windows account allowed to install applications.
- The current self-contained Setup EXE includes the .NET 9 runtime. Framework-dependent ZIP/native packages need the .NET 9 Desktop Runtime; source builds need the .NET 9 SDK.
- Install only the cloud CLI tools you plan to use: Azure CLI (`az`), AWS CLI (`aws`), and/or Google Cloud CLI (`gcloud`). Cloud discovery is optional until you configure a provider.
- Obtain installation files from your organization’s trusted release location. Do not install unsigned packages or trust a signing certificate from an unknown source.

> **PostgreSQL is not required for the desktop app.** Install it only when preparing a self-hosted License Server/backend. For online and offline server prerequisites, follow [Install PostgreSQL for Self-Hosted YASK-CIEM](postgresql-installation.md).

## Current Setup EXE

The current local build is [YASK-CIEM-Setup-1.0.0.59.exe](../../artifacts/installer-1.0.0.59/YASK-CIEM-Setup-1.0.0.59.exe). It contains the self-contained MSIX, the matching public publisher certificate, and the .NET runtime; a separate .NET Desktop Runtime installation is not required. Run it and follow the Windows prompts, then launch **YASK-CIEM** from Start.

This package uses the YASK-CIEM development certificate. On a clean PC, Windows may block the Setup EXE until its signer is trusted. Only if you received the installer and [public development certificate](../../artifacts/certs/yask-ciem-dev.cer) through a trusted channel, verify that the certificate thumbprint is `537C64CF83CF2C6C1EBC89553B7B4EDD13E7F104`, then import it into the **Current User > Trusted Root Certification Authorities** store. For example, from the certificate's folder in PowerShell:

```powershell
Import-Certificate -FilePath .\yask-ciem-dev.cer -CertStoreLocation Cert:\CurrentUser\Root
```

Then run Setup. If the certificate is not already in the current user's Root store, Setup asks you to type `TRUST` before adding it. Setup also adds the public certificate to `TrustedPeople` for MSIX installation and removes trust entries it added if installation fails. It never contains the signing private key. Use this development certificate only in a trusted development/test environment; public distribution requires an organization-trusted code-signing certificate. The generated executable is local build output and is not automatically uploaded to GitHub; at about 112.4 MB, it exceeds GitHub's 100 MB single-file limit.

## Option 1: Install from an App Installer feed

This is the recommended release option when your organization provides a `.appinstaller` file. It supports automatic update checks when the feed is configured by the release publisher.

1. Download the `.appinstaller` file from the trusted release location.
2. Open it and review the publisher shown by Windows. Continue only if it matches your organization’s expected publisher.
3. Select **Install** in App Installer and wait for setup to finish.
4. Open **Start**, search for **YASK-CIEM**, and launch it.

Updates are checked on launch and in the background according to the feed settings. Install from the feed rather than opening a standalone `.msix` if you need automatic updates.

## Option 2: Install a signed MSIX package

Use this method if your organization supplied a signed `.msix` package but no App Installer feed.

1. Confirm the publisher and signature are trusted by your organization. Do not bypass Windows publisher warnings. The current local package is [YASK-CIEM-1.0.0.59.msix](../../artifacts/msix-1.0.0.59/YASK-CIEM-1.0.0.59.msix). It is signed with the local development certificate and is suitable only for trusted test machines, not public production distribution.
2. Double-click the `.msix` package and follow the App Installer prompts. Alternatively, open PowerShell in the download folder and run:

   ```powershell
   Add-AppxPackage -Path .\YASK-CIEM-1.0.0.59.msix
   ```

3. Launch YASK-CIEM from **Start**.

If a later release has a different versioned filename, replace the example path with the exact `.msix` filename you downloaded.

A standalone MSIX does not provide automatic updates unless it is installed through an App Installer feed. Install newer releases using the organization’s published update instructions.

## Option 3: Install the native MSI or Setup EXE

Use the native package when your organization requires a per-machine MSI deployment. The MSI installs under Program Files and requires administrator rights. The Setup EXE launches the MSI and triggers the standard Windows elevation prompt.

The native packages below remain at 1.0.0.56 and do not contain the 1.0.0.57 AWS fixes. Use the current self-contained Setup EXE above for those fixes.

- [YASK-CIEM-1.0.0.56.msi](../../artifacts/native-installers-1.0.0.56/YASK-CIEM-1.0.0.56.msi)
- [YASK-CIEM-Setup-1.0.0.56.exe](../../artifacts/native-installers-1.0.0.56/YASK-CIEM-Setup-1.0.0.56.exe)

These native packages are framework-dependent; install the .NET 9 Desktop Runtime x64 first. They are signed with the local development certificate and are for trusted test use only. Production distribution requires an organization-trusted code-signing certificate.

## Option 4: Run from a ZIP package

1. Download the Windows ZIP from the trusted release location.
2. Extract it to a folder writable by your user, for example `%LocalAppData%\Programs\YASK-CIEM`.
3. Open the extracted folder and run `CloudEntitle.Desktop.exe`.
4. Create a shortcut manually if desired.

The matching .NET 9 Desktop Runtime must be installed. ZIP installations do not update automatically: download and extract each new release. Keep application data outside the extracted folder; it is stored under `%LocalAppData%\YASK-CIEM`.

## Option 5: Install from a source checkout

This option is intended for development and internal testing. It builds the application on the PC.

1. Install Git and the .NET 9 SDK.
2. Clone the repository and open PowerShell in its root directory.
3. Run:

   ```powershell
   .\scripts\install-windows-user.ps1
   ```

4. Open **Start**, search for **YASK-CIEM**, and launch it.

The script installs the current build under `%LocalAppData%\Programs\YASK-CIEM` and creates a Start Menu shortcut. It does not require administrator rights. Rerun the script to replace the application binaries with a newly built version.

## Configure cloud access

The application uses the official cloud CLI sessions for read-only discovery. Authenticate only with accounts and least-privilege permissions approved by your organization. YASK-CIEM does not ask for cloud passwords in the application UI.

Azure:

```powershell
az login
az account show
```

AWS:

```powershell
aws configure
aws sts get-caller-identity
```

Use your organization-approved AWS identity method where available; avoid storing long-lived credentials when possible.

Google Cloud:

```powershell
gcloud auth login
gcloud auth application-default login
gcloud projects list
```

Close and reopen the application after setting up or changing CLI authentication. A provider’s CLI and authentication are needed only when scanning that provider.

## Continuous monitoring

Scheduled scans are disabled by default. In **Settings > Continuous monitoring**, enable the schedule and choose an interval from 5 minutes to 7 days. Scans run only while YASK-CIEM is open and use the same read-only CLI sessions as manual scans. Each scheduled run is serialized with manual scans, compares all connected providers with their previous comparable scans, saves results to scan history, and evaluates posture-change alerts.

To deliver eligible high-impact posture alerts, open **Settings > Posture Alerts** and save an organization-approved HTTPS webhook endpoint. YASK-CIEM stores it in Windows Credential Manager for the current user; it is not written to the schedule or settings file or included in delivery error messages. Existing deployments can continue to use `YASK_CIEM_ALERT_WEBHOOK_URL` as a fallback. If the webhook is unavailable, eligible alerts remain pending and are retried on a later scan.

The schedule is stored locally at `%LocalAppData%\YASK-CIEM\monitoring-schedule.json`. Disabling the schedule stops future scans; it does not delete scan history or pending alert records.

## Local backup and restore

Use **Settings > Local Data Backup** to create or restore a `.yaskbackup` archive. The archive contains the SQLite database and supported local workflow records and includes hashes for integrity checks. Ticketing and posture-webhook credentials, CLI tokens, logs, and generated reports are excluded. Treat the archive as sensitive access data. Restore replaces current local records and requires an application restart to reload them.

## Application data

YASK-CIEM stores application files separately from install binaries:

```text
%LocalAppData%\CloudEntitle\Data\cloudentitle.db
%LocalAppData%\CloudEntitle\Logs\
%LocalAppData%\YASK-CIEM\trial-state.json
%LocalAppData%\YASK-CIEM\reports\
```

Uninstalling the application does not remove these files. Back up reports or other files you need before removing application data.

## Uninstall

- **MSIX/App Installer:** Open **Settings > Apps > Installed apps**, select **YASK-CIEM**, then select **Uninstall**. App Installer does not remove user data stored under `%LocalAppData%\YASK-CIEM`.
- **ZIP:** Close the application and delete the folder where you extracted the package.
- **Per-user script installation:** Close the application, remove `%LocalAppData%\Programs\YASK-CIEM`, and delete the `YASK-CIEM.lnk` shortcut from `%AppData%\Microsoft\Windows\Start Menu\Programs`.

Remove the listed `%LocalAppData%\CloudEntitle` and `%LocalAppData%\YASK-CIEM` folders only if you intentionally want to delete local application data, logs, trial state, and reports.

## Troubleshooting

- **Windows reports an untrusted publisher:** Stop and confirm the package and certificate with your release administrator. Do not bypass the warning or import an unfamiliar certificate.
- **The app fails to start after installing:** Install or repair the .NET 9 Desktop Runtime, then try again.
- **A provider is unavailable:** Install its CLI, authenticate in the same Windows user account, and restart YASK-CIEM.
- **There is no separate cloud login button:** Use the provider CLI commands above; the desktop app reuses those sessions.

The current local trial mechanism is not a server-controlled production license. For release and licensing limitations, see [Release checklist](release.md) and [Licensing](../licensing/licensing.md).
