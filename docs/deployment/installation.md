# Install YASK-CIEM on Windows

YASK-CIEM supports Windows 10/11. Choose the installation method supplied by your organization. For a normal release, use the signed MSIX or App Installer feed. Use the ZIP for a portable/manual installation, or the per-user script when installing from a source checkout.

## Before you start

- Use a Windows account allowed to install applications.
- Install the .NET 9 Desktop Runtime on the target PC. Current packages are framework-dependent; the .NET SDK is needed only for the source-based installation.
- Install only the cloud CLI tools you plan to use: Azure CLI (`az`), AWS CLI (`aws`), and/or Google Cloud CLI (`gcloud`). Cloud discovery is optional until you configure a provider.
- Obtain installation files from your organization’s trusted release location. Do not install unsigned packages or trust a signing certificate from an unknown source.

## Option 1: Install from an App Installer feed

This is the recommended release option when your organization provides a `.appinstaller` file. It supports automatic update checks when the feed is configured by the release publisher.

1. Download the `.appinstaller` file from the trusted release location.
2. Open it and review the publisher shown by Windows. Continue only if it matches your organization’s expected publisher.
3. Select **Install** in App Installer and wait for setup to finish.
4. Open **Start**, search for **YASK-CIEM**, and launch it.

Updates are checked on launch and in the background according to the feed settings. Install from the feed rather than opening a standalone `.msix` if you need automatic updates.

## Option 2: Install a signed MSIX package

Use this method if your organization supplied a signed `.msix` package but no App Installer feed.

1. Confirm the publisher and signature are trusted by your organization. Do not bypass Windows publisher warnings.
2. Double-click the `.msix` package and follow the App Installer prompts. Alternatively, open PowerShell in the download folder and run:

   ```powershell
   Add-AppxPackage -Path .\YASK-CIEM-1.0.0.4.msix
   ```

3. Launch YASK-CIEM from **Start**.

If the release package has a different versioned filename, replace the example path with the exact `.msix` filename you downloaded.

A standalone MSIX does not provide automatic updates unless it is installed through an App Installer feed. Install newer releases using the organization’s published update instructions.

## Option 3: Run from a ZIP package

1. Download the Windows ZIP from the trusted release location.
2. Extract it to a folder writable by your user, for example `%LocalAppData%\Programs\YASK-CIEM`.
3. Open the extracted folder and run `CloudEntitle.Desktop.exe`.
4. Create a shortcut manually if desired.

The matching .NET 9 Desktop Runtime must be installed. ZIP installations do not update automatically: download and extract each new release. Keep application data outside the extracted folder; it is stored under `%LocalAppData%\YASK-CIEM`.

## Option 4: Install from a source checkout

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
