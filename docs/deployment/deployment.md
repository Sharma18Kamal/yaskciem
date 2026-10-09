# Deployment

## Environment

- Windows 10 / 11
- .NET 10 SDK
- Visual Studio 2022 with .NET desktop development workload
- Azure, AWS, and GCP test subscriptions/projects with read-only access

## Release packaging

- MSI installer for enterprise deployment
- MSIX installer for app store-style installation
- Self-hosted License Server bundle: [YASK-CIEM-LicenseServer-win-x64-1.0.0.48.zip](../../artifacts/license-server-1.0.0.48/YASK-CIEM-LicenseServer-win-x64-1.0.0.48.zip) with [SHA-256 sidecar](../../artifacts/license-server-1.0.0.48/YASK-CIEM-LicenseServer-win-x64-1.0.0.48.zip.sha256)
- Follow the [Self-Hosted License Server Deployment](license-server-hosting.md) guide; PostgreSQL, Object Lock storage, Entra registration, and secrets are provisioned separately.
- GitHub Actions for restore, build, and test automation
