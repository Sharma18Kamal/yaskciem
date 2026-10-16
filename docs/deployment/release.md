# Release checklist

## Local build 1.0.0.57

- AWS connection detection checks named CLI profiles when default credentials are unavailable.
- AWS Disconnect excludes the provider from new scans and discovery for the current app session without deleting credentials or revoking IAM keys. Refresh does not reconnect it; Connect resumes existing valid credentials. Restarting the app checks CLI credentials again.
- Self-contained Windows x64 MSIX and Setup EXE were built and signed with the local development certificate; both signatures and packaged versions were verified. These are trusted-test artifacts, not public production releases.
- Release test suite: 344 passed, 5 PostgreSQL integration tests skipped because `YASK_CIEM_TEST_POSTGRES` was not configured, 0 failed.
- Clean-machine installation, upgrade behavior, and live AWS acceptance remain manual validation steps; building the installer does not install it.

## Automated checks

- CI restores and builds the complete solution in Release mode with `.nuget/NuGet.Config`.
- CI runs the complete test suite against the Release build.
- CI parses all packaging/install PowerShell scripts and creates an unsigned MSIX smoke package with Windows SDK `MakeAppx`.
- CI uploads the unsigned MSIX as a short-lived QA artifact (7-day retention). It is not signed or suitable for distribution.
- Before release, create the signed MSIX and App Installer feed with `scripts/package-msix.ps1`, then verify the signature and install/update flow on a clean Windows VM.

## Runtime checks

- Install on a clean supported Windows 10/11 VM as a standard user; verify Start Menu launch and clean uninstall.
- Install from the `.appinstaller` feed, publish a higher package version at the same package URL, and verify on-launch/background update behavior and user-data preservation.
- Confirm `az`, `aws`, and `gcloud` missing, unauthenticated, and authenticated states are handled without crashing; verify discovery remains read-only.
- Confirm findings, inventory, risk rules, and trial state persist under `%LocalAppData%\\YASK-CIEM` and JSON/CSV report export works.
- Confirm trial reuse is rejected across normalized email and device identifiers, and validate expiration/clock rollback behavior.
- Verify the installed build’s logs contain no credential values and that configured API security headers are present.

## Release blockers

- Release signing requires the organization-owned code-signing certificate and trusted publisher chain; CI intentionally produces unsigned QA artifacts only.
- Automatic updates require stable HTTPS hosting for the signed MSIX and `.appinstaller` feed. The per-user ZIP installer has no automatic update mechanism.
- Remote HTTPS license validation and a server-approved trial backend are not implemented; local trial state is not production anti-tamper enforcement.
- Real cloud acceptance testing requires isolated Azure, AWS, and GCP test accounts with least-privilege read-only identities and authenticated CLI sessions.
- The current application targets .NET 9. A production framework upgrade must be scheduled and validated before declaring the requested .NET 10 target complete.
