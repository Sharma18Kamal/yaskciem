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

## ZIP fallback

If the Windows SDK is not installed, create a runnable ZIP package instead:

```powershell
.\scripts\package-windows-zip.ps1
```

The output is written to `artifacts/windows-zip/YASK-CIEM-win-x64.zip`. Extract it and run `CloudEntitle.Desktop.exe`. The target machine needs the matching .NET desktop runtime.

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
