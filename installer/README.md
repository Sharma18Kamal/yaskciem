# Windows MSIX installer

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
6. Verify SQLite initialization, CLI readiness messaging, scan execution, report export, and trial enforcement.
7. Publish checksums and the signed installer through the release channel.

The local development machine may not contain `MakeAppx.exe`; in that case package creation must run on a Windows SDK build agent. The package is not trusted for distribution until signed with the organization certificate.
