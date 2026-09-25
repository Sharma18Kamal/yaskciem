# Release checklist

## Automated checks

- Restore with `.nuget/NuGet.Config`.
- Build `src/CloudEntitle.Desktop/CloudEntitle.Desktop.csproj` in Release mode.
- Run `dotnet test CloudEntitle.sln`.
- Run `.\scripts\publish-windows.ps1` on Windows.

## Runtime checks

- Confirm `az`, `aws`, and `gcloud` are installed and authenticated.
- Confirm provider discovery is read-only.
- Confirm missing or unauthenticated tools produce a status message rather than a crash.
- Confirm findings, inventory, risk rules, and trial state persist under `%LocalAppData%\\YASK-CIEM`.
- Confirm JSON report export works.
- Confirm email and MAC-based trial reuse is rejected.

## Release blockers

- The local scaffold targets .NET 9 because that is the installed SDK. Production should move to the brief's .NET 10 target after the production SDK is available.
- MSI/MSIX packaging and code signing require a selected packaging tool and organization certificate.
- Remote HTTPS license validation is not implemented; the local trial service is not a substitute for a production licensing backend.
- Real cloud validation requires test tenants/accounts and authenticated CLI sessions.
