# Release checklist

## Desktop production-readiness gate

See the [October 23, 2026 desktop assessment](desktop-production-readiness.md)
for verified artifact fingerprints, fresh test results and remaining Store
release gates. Desktop acceptance is separate from Azure backend provisioning,
which remains on hold. Passing tests does not certify the installed package.

## Local candidate 1.0.0.63

- Owner selected a **free standalone edition**, native EXE/MSI Store listing and
  no real-cloud account access at this stage. The display name `YASK-CIEM` does
  not establish a verified Partner Center product ID or submission configuration.
- Desktop trial service/device-identity lookup, startup verification, timer and
  UI gates are removed. Existing trial files are not modified; free desktop
  access does not change cloud/organization authorization.
- Shared solution targets .NET 10; SDK 10.0.402 and Microsoft EF/Extensions/SQLite
  packages 10.0.13 are used. CI/CD SDK selection is aligned. The licensing
  library remains tested but is no longer a desktop project dependency.
- The native packages bundle .NET, Windows Desktop and ASP.NET Core 10.0.13.
  App/Setup version is 1.0.0.63; encoded MSI ProductVersion is 1.0.63.
  The script rejects source/package version mismatches.
- Setup no longer pre-uninstalls an existing version. MSI controls transactional
  upgrades, rollback and downgrade refusal. Reboot-required exit status is
  preserved. This is verified by source contracts, not an executed installation.
- Full .NET 10 solution tests: **457 passed, 28 skipped, 0 failed**.
  The 28 PostgreSQL tests require `YASK_CIEM_TEST_POSTGRES`; no database was
  provisioned or credentials requested. Desktop accounts for **221 passed**,
  including free-edition and native-installer regression contracts.
- NuGet advisory audit of all resolved solution packages, including transitives:
  no reported advisories. Identity.Web's older cryptography dependencies are
  explicitly pinned to 10.0.13. The SDK reports NU1510 for the XML reference
  being redundant in the Web project; it is retained to keep downstream test
  consumers patched. This package audit is not an application security review.
- All 26 offline pipeline cases, 9 authorization harness cases and staging ZIP
  installer safeguards pass without Azure calls. Packaging scripts parse.
- Signed local development MSI and interactive Setup EXE were built in
  `artifacts/native-installers-1.0.0.63`. MSI table inspection confirms version,
  upgrade code, desktop DLL version and bundled runtime files. Neither has a
  timestamp or production signing chain.
- No installer was executed, certificate imported, Store listing changed, live
  cloud scan run or Azure infrastructure provisioned. Clean-device acceptance,
  production signing/hosting and actual Store certification remain open.

## Local build 1.0.0.62

- Fits and centers the initial window inside the Windows work area in device-independent units, including taskbar space, instead of always requesting 1440x900. Minimum dimensions are reduced only when the work area is smaller.
- Adds vertical sidebar scrolling so every navigation item is reachable in shorter windows. Dashboard commands wrap onto additional rows with spacing instead of crowding a single row.
- Focused workspace checks: 16 passed, including actual WPF window sizing at 2560x1600 / 200% and 1920x1080 / 100%, 125%, 150%, and 200%, using simulated logical work areas. These tests do not change Windows display settings or provide visual acceptance.
- Full desktop Release suite: 160 passed, 0 failed. Native MSI and MSI-launching Setup EXE were built in `artifacts/native-installers-1.0.0.62`; both have valid local development-certificate signatures. The published desktop DLL and Setup EXE report file version 1.0.0.62. Native packages still require .NET 9 Desktop Runtime x64 and ASP.NET Core Runtime 9 x64.
- Before Store resubmission, visually verify startup, restored/maximized windows, sidebar scrolling, and dashboard commands at the reviewer's 2560x1600 / 200% settings. Installation, upgrade acceptance, and Store submission are separate validation steps.

## Local build 1.0.0.61

- Native MSI and its MSI-launching Setup EXE were also built and signed. The MSI was copied into `artifacts/installer-1.0.0.61` alongside the existing MSIX-based setup without replacing it. Verified MSI ProductVersion is 1.0.61, app version is 1.0.0.61, and install scope is per-machine. Native packages require the .NET 9 Desktop Runtime x64 and administrator installation; no installer was executed.
- Moves Security Workspace into the right side of the global header. The bold credit `Developed By: Sharma Kamal K` appears beside YASK-CIEM on the left, with header controls in a separate row.
- AWS Connect opens a terminal running `aws configure --profile yask-ciem` for direct IAM-user profile configuration. Other named profiles and the default profile are unchanged. Connection verification waits can be canceled and have a three-minute overall deadline.
- Focused header/authentication/navigation tests: 56 passed. Full Release suite: 353 passed, 5 PostgreSQL integration tests skipped because no test database was configured, 0 failed.
- Self-contained Windows x64 MSIX and Setup EXE were built with matching 1.0.0.61 versions and valid local development-certificate signatures. Installation, visual acceptance, and live AWS configuration were not performed. These are trusted-test artifacts, not public production releases.

## Local build 1.0.0.60

- Providers require explicit Connect selection in each app session. Existing Azure CLI credentials no longer become an active app connection when GCP is selected; refresh verifies selected providers only. Already-selected shared connection state is preserved.
- Starting a connection attempt clears the previous provider's error, so a successful GCP connection does not retain an AWS credential error.
- Provider navigation regression tests: 37 passed. The full Release test command passed with 5 PostgreSQL integration tests skipped because no test database was configured.
- Self-contained Windows x64 MSIX and Setup EXE were built with matching 1.0.0.60 versions and valid local development-certificate signatures. Installation and live-cloud acceptance were not performed. Invalid AWS credentials still require correction outside the app; cloud credentials and policies were not modified.

## Local build 1.0.0.59

- Adds the bold text `Developed By: SHARMA Kamal K` above the Dashboard toolbar, with wrapping for narrower layouts.
- Workspace UI regression tests: 8 passed. The full Release test command passed; 5 PostgreSQL integration tests were skipped because no test database was configured.
- Self-contained Windows x64 MSIX and Setup EXE were built with matching 1.0.0.59 versions and verified development-certificate signatures. These are trusted-test artifacts; installation and clean-machine upgrade acceptance were not performed.

## Local build 1.0.0.58

- Connecting a provider checks only that provider rather than waiting for unrelated cloud CLIs. Cancellation clears pending UI state without another all-provider refresh.
- CLI connection checks have a 15-second cancellation deadline. AWS default and named-profile identity checks run concurrently with paging disabled and five-second connect/read timeouts; a successful profile cancels slower probes.
- AWS Connect verifies existing IAM-user or SSO credentials without automatically starting SSO configuration. Invalid credentials return actionable guidance rather than a repeated sign-in wait.
- Focused connection/navigation tests: 52 passed. The full Release test command completed successfully. PostgreSQL integration tests still require `YASK_CIEM_TEST_POSTGRES`.
- Self-contained Windows x64 MSIX and Setup EXE were built with matching 1.0.0.58 versions and valid local development-certificate signatures. Installation and live-cloud acceptance were not performed; these remain trusted-test artifacts.

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
- Confirm findings, inventory and settings persist at their documented locations and JSON/CSV report export works.
- Confirm the free edition stays usable after fourteen days and ignores missing, expired and damaged legacy trial files without changing or deleting them.
- Verify the installed build’s logs contain no credential values and that configured API security headers are present.

## Release blockers

- Direct distribution requires trusted production signing; Store-distributed MSIX is signed by Microsoft on submission and must match the reserved Store identity. CI intentionally produces unsigned QA artifacts only.
- Direct MSIX automatic updates require stable HTTPS hosting for the signed MSIX and `.appinstaller` feed; Store-managed MSIX uses the Store update channel instead. The per-user ZIP installer has no automatic update mechanism.
- Free standalone behavior is implemented; installed upgrade acceptance must verify legacy trial state cannot lock the app. Do not advertise paid entitlements or hosted services as included.
- Complete the privacy policy's publisher/contact/address placeholders and publish the reviewed policy and support details for the Store listing.
- Real cloud acceptance testing requires isolated Azure, AWS, and GCP test accounts with least-privilege read-only identities and authenticated CLI sessions.
- The current source and native candidate target .NET 10 LTS. Installed-package acceptance on the supported Windows matrix remains required; passing build/unit tests alone does not validate deployment.
