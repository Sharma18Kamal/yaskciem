# Windows MSIX installer

To create a single-file, self-contained Setup EXE that includes the MSIX app and .NET 10 runtime, and offers an explicit current-user trust step for the bundled public publisher certificate:

```powershell
.\scripts\package-msix.ps1 -PackageVersion 1.0.0.36 -OutputPath .\artifacts\msix-1.0.0.36
.\scripts\package-msix-installer.ps1 `
  -PackageVersion 1.0.0.36 `
  -PackagePath .\artifacts\msix-1.0.0.36\YASK-CIEM-1.0.0.36.msix `
  -OutputPath .\artifacts\installer-1.0.0.36
```

The setup EXE asks the user to type `TRUST` before importing `yask-ciem-dev.cer` into `CurrentUser\Root` and `CurrentUser\TrustedPeople`; it does not need administrator rights and does not contain the signing `.pfx` private key. On a clean PC, the Setup EXE itself may not launch until the public certificate is first trusted in `CurrentUser\Root`; verify its thumbprint before importing. These packages are signed with the local development certificate and should only be distributed to trusted test machines/users. Public releases should use an organization-trusted code-signing certificate.

The repository now includes an MSIX packaging definition and script. Run this on a Windows packaging machine with the Windows SDK installed:

```powershell
.\scripts\publish-windows.ps1
```

The output defaults to `artifacts/publish` and targets `win-x64`.

To create an MSIX package:

```powershell
.\scripts\package-msix.ps1 -PackageVersion 1.0.0.0
```

The output is written to `artifacts/msix/YASK-CIEM-1.0.0.0.msix`.

## MSIX automatic updates

MSIX auto-updates use Windows App Installer. The `.appinstaller` feed and MSIX must be hosted at stable HTTPS URLs, and the MSIX must be signed with a certificate trusted by the installing machines. Build both artifacts by supplying the public URLs and signing certificate:

```powershell
./scripts/package-msix.ps1 `
  -PackageVersion 1.0.0.0 `
  -CertificatePath ./certs/yask-ciem.pfx `
  -CertificatePassword $env:YASK_CIEM_CERT_PASSWORD `
  -AppInstallerUri https://downloads.example.com/YASK-CIEM.appinstaller `
  -PackageUri https://downloads.example.com/YASK-CIEM-1.0.0.0.msix
```

Publish the generated MSIX at the exact `PackageUri` and the generated `YASK-CIEM.appinstaller` at the exact `AppInstallerUri`. Install users from the `.appinstaller` file (not the bare `.msix`) to enable update checks. The feed checks on launch and in the background; update activation is blocked while a required update is applied. Subsequent releases must keep the same feed URL and package identity, increase the four-part package version, and update the package URL in the newly generated feed.

The feed is generated only when both HTTPS URLs are supplied and the package is signed. The package script preserves other files already in the output directory.

## ZIP fallback

If the Windows SDK is not installed, create a runnable ZIP package instead:

```powershell
.\scripts\package-windows-zip.ps1
```

The output is written to `artifacts/windows-zip/YASK-CIEM-win-x64.zip`. Extract it and run `CloudEntitle.Desktop.exe`. The target machine needs the matching .NET desktop runtime.

## Native MSI and Setup EXE

To create a WiX MSI and a setup EXE that embeds and launches the MSI:

```powershell
dotnet tool install --tool-path "$env:TEMP\yask-wix-tools-v5" --add-source https://api.nuget.org/v3/index.json --version 5.0.2 wix
```

The package script requires WiX 5, SignTool, and the YASK-CIEM signing certificate in the current user's certificate store. From the repository root, run:

```powershell
.\scripts\package-native-installers.ps1 `
  -PackageVersion 1.0.0.64 `
  -WixExePath "$env:TEMP\yask-wix-tools-v5\wix.exe"
```

The output directory defaults to `artifacts/native-installers-<version>` and contains `YASK-CIEM-<version>.msi` and `YASK-CIEM-Setup-<version>.exe`. The MSI installs per-machine under Program Files and adds a Start Menu shortcut. The setup EXE embeds the MSI and triggers the standard Windows elevation prompt.

Version 1.0.0.63 and later bundles the desktop and required .NET 10 runtimes, so
these native packages do not require a separate runtime installation. The
package version must match `Directory.Build.props`. Earlier 1.0.0.62 native
packages were framework-dependent and required .NET 9 Desktop and ASP.NET Core
runtimes.

The MSI is the unattended installation candidate (`msiexec /i <package.msi> /qn
/norestart /l*v <log>`); it still requires elevation for per-machine installation.
The interactive Setup EXE prompts and is not an unattended Store installer.
It now invokes Windows Installer directly without pre-uninstalling the old
version; MSI major upgrades run inside the installation transaction and prevent
downgrades to lower MSI versions. Newly built packages explicitly include equal
MSI versions in major-upgrade detection, so a rebuilt 1.0.0.63 package replaces
an earlier 1.0.0.63 package with a different ProductCode instead of leaving two
products in the same directory. Re-running the identical MSI can enter
maintenance mode. Equal-version rebuilds cannot be ordered: an older rebuild
can replace a newer rebuild, so use only the designated latest candidate and
increment the version for subsequent public releases. Actual
install/upgrade/rollback acceptance is still required on an isolated test PC.

Local candidate packages use the development signing certificate and are for
trusted test use only. Never distribute the `.pfx` or its password. The selected
EXE/MSI Store listing route requires an owner-verified listing, stable download
URLs, appropriate production signing, silent-install behavior and certification.
Microsoft signing of Store-distributed MSIX does not make this native MSI
production-signed.

## User-level Start Menu installer

For local testing without MSIX certificate trust or administrator rights, run:

```powershell
.\scripts\install-windows-user.ps1
```

This publishes the latest desktop build to `%LocalAppData%\Programs\YASK-CIEM` and creates `YASK-CIEM.lnk` in the current user's Start Menu. It does not modify the application database or reports under `%LocalAppData%\YASK-CIEM`.

To sign it:

```powershell
.\scripts\package-msix.ps1 `
  -PackageVersion 1.0.0.0 `
  -CertificatePath .\certs\yask-ciem.pfx `
  -CertificatePassword $env:YASK_CIEM_CERT_PASSWORD
```

## MSI/MSIX release steps

1. Run `package-msix.ps1` on a Windows build agent with the Windows SDK installed.
2. Replace the generated placeholder PNG assets in the package script with branded assets.
3. Use a certificate whose subject exactly matches the manifest `Publisher` value.
4. Sign the package and install it on a clean Windows 10/11 test VM.
5. Verify SQLite initialization, CLI readiness messaging, scan execution, report export, and free-edition access without a trial lock.
6. Publish checksums and the signed installer through the release channel.

The local development machine may not contain `MakeAppx.exe`; in that case package creation must run on a Windows SDK build agent. The package is not trusted for distribution until signed with the organization certificate.
