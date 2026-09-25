# Windows installer handoff

The application publish artifact is produced by:

```powershell
.\scripts\publish-windows.ps1
```

The output defaults to `artifacts/publish` and targets `win-x64`.

## MSI/MSIX release steps

1. Run the publish script on a Windows build agent.
2. Package the published directory with the organization-approved WiX or MSIX tooling.
3. Add the application publisher identity and signing certificate.
4. Sign the installer and package contents.
5. Install on a clean Windows 10/11 test VM.
6. Verify SQLite initialization, CLI readiness messaging, scan execution, report export, and trial enforcement.
7. Publish checksums and the signed installer through the release channel.

Installer generation and signing are intentionally kept outside the source build until the organization selects the packaging technology and certificate identity.
