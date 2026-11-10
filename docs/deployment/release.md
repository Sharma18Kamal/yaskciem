# Release checklist

## Desktop production-readiness gate

See the [October 23, 2026 desktop assessment](desktop-production-readiness.md)
for verified artifact fingerprints, fresh test results and remaining Store
release gates. Desktop acceptance is separate from Azure backend provisioning,
which remains on hold. Passing tests does not certify the installed package.

## Local candidate 1.0.0.67

- Rebuilds the current source under a new version instead of replacing the
  November 9 version 1.0.0.66 installer with an indistinguishable rebuild.
  Desktop, native setup and License Server use version 1.0.0.67; native MSI
  ProductVersion is encoded as 1.0.67.
- Includes conditional permission evaluation fixes: conditions must match before
  contributing their original Allow/Deny effect, unsupported compound expressions
  remain unresolved, and explanations report matched/not-matched/required context.
  Normalization preserves conditional Allow and Deny as separate records.
- Carries current explicit cloud-provider selection, organization/API sign-in
  guidance, API v2 audience setup and unmapped JWT claim handling. Provider CLI
  authentication remains separate from organization authentication.
- Includes current server-managed licensing and Azure Blob/S3 audit archive
  support. Existing local credentials, database records and archive provisioning
  are not replaced by building packages.
- Adds the [current Desktop user manual](../user-guide/user-manual.md) and
  [installation guide](customer-installation-guide.md). The older study material
  is historical; its licensing and no-cloud-write claims are not current behavior.
- Release solution validation: **965 passed, 49 skipped, 0 failed** across 23
  test projects. PostgreSQL integration tests were not enabled; no live database
  was used for this run. TRX results are under `artifacts/validation-1.0.0.67`.
- Native MSI and Setup EXE signatures verify locally using the existing
  development certificate. The MSI Property/File tables confirm ProductVersion
  1.0.67 and embedded Desktop, EffectiveAccess and Normalization DLL version
  1.0.0.67. Setup reports 1.0.0.67; self-contained Desktop/ASP.NET runtimes are
  present. The License Server ZIP manifest and inspected application/library
  versions are 1.0.0.67; all 16 numbered SQL files match current source.
- Local development signatures are for trusted testing only, not public release.
  Clean-machine installation/upgrade, authenticated real-cloud acceptance and
  production signing remain separate gates. No installer was executed or running
  License Server replaced by this packaging task. MSIX was not rebuilt.

### Candidate files and SHA-256

Each package has an adjacent `.sha256` file. Paths are relative to the repository
root. Older package directories are preserved.

| File | SHA-256 |
| --- | --- |
| `artifacts/native-installers-1.0.0.67/YASK-CIEM-Setup-1.0.0.67.exe` | `0B23C027DD96EF94AE181A2F25F607EF5CCBFC7FF0F19E556189A130C3AB52E7` |
| `artifacts/native-installers-1.0.0.67/YASK-CIEM-1.0.0.67.msi` | `44FED2187FE2112931F72974E1858FE8725A3B54E88400ABA943E370531182CE` |
| `artifacts/license-server-1.0.0.67/YASK-CIEM-LicenseServer-win-x64-1.0.0.67.zip` | `D9A49B8EDC960EA64806047E5FC746983A566DF3C4F0CDFF3ACAFE070A577228` |

### Upgrading from an earlier candidate

1. Back up supported Desktop local data from Settings. For shared-server upgrade,
   separately back up PostgreSQL and retain protected settings/secret access.
2. Close Desktop and run the new native Setup EXE or MSI as administrator; do not
   install both native and MSIX editions for the same test.
3. Confirm Settings/header reports 1.0.0.67 and that existing local data loads.
4. For License Server, arrange a maintenance window, stop the supervised old
   process, extract the new ZIP into a separate versioned directory, and apply
   the existing protected configuration using the
   [hosting procedure](license-server-hosting.md). Do not overwrite credentials
   with example settings or start a second worker set against production.
5. Verify liveness and readiness, then organization sign-in, license refresh and
   one approved read-only account scan. Keep archive/coverage gaps visible.
6. Follow the [installation acceptance gates](customer-installation-guide.md)
   before treating the upgraded installation as accepted.

## Local candidate 1.0.0.64

- Moves the rotating hourglass to a persistent shell control aligned to the
  right edge of the content viewport, outside the page templates and scroll
  area. Scan state remains visible when navigating away from Dashboard.
- Animation now starts only after the control is loaded and visible, uses a
  cloned storyboard in the control's own name scope, and removes its clocks
  when hidden/unloaded. UI exceptions remain logged/reported, not suppressed.
- Workspace pages are reused by view-model instance instead of repeatedly
  creating their control trees. Repeated selection of the current page no
  longer raises redundant navigation changes. A 120ms fade respects the
  Windows client-area-animation preference.
- SQLite snapshot reads run off the dispatcher because the SQLite provider's
  async query calls can execute synchronously. Database contexts are created
  and disposed within the background operation; UI updates still resume on
  the original caller's context.
- Runtime rendering regression visits Dashboard, Cloud Accounts, Identities,
  Resources, Entitlements and Settings three times with synthetic scan state,
  asserting page reuse, right-edge placement and scan hide/show/reload behavior.
  It does not scan a real tenant or prove performance with customer data.
- New native MSI/EXE and MSIX candidates use version 1.0.0.64, avoiding another
  same-version update ambiguity. Local signatures are development-only.
  Exact customer-popup diagnosis still requires the affected device's current
  `application-*.log`; available development-machine logs predate the report.
  Installed upgrade, visual acceptance and Store certification remain pending.
- Validation: 223 desktop tests and 11 infrastructure tests passed. The compiled
  shell template retains the same workspace and cached page controls across
  navigation. A separate persistence regression verifies snapshot context
  creation is on a worker thread, not the calling thread. MSI, Setup EXE and
  MSIX signatures verified locally; all app/package versions are 1.0.0.64
  (MSI ProductVersion 1.0.64), with .NET runtimes 10.0.13 bundled.

## Local candidate 1.0.0.63

- Same-version upgrade correction: original and UI-refresh MSI Upgrade tables
  excluded equality (`WIX_UPGRADE_DETECTED` attributes 1). Rebuilt native
  packages in `artifacts/native-installers-1.0.0.63-upgrade-fix` explicitly
  include equal versions and retain transactional replacement. Use this
  designated candidate, not the superseded UI-refresh installer, over an older
  1.0.0.63 installation. Equal-version builds cannot enforce chronological
  downgrade protection; future public releases must increment versions.
  Actual installed upgrade and launch remain acceptance gates.
- Same-version UI refresh: full four-part installed-app version appears in the
  top-left brand area; contact email appears below the developer credit. The
  scan hourglass uses vector sand transfer and forward 0/180/360-degree turns,
  stops when hidden, and no longer reverses like a pendulum. Refreshed native
  test packages are in `artifacts/native-installers-1.0.0.63-ui-refresh`;
  original candidate files are preserved. Same-version replacement behavior
  still needs installed-device verification; use a fresh test VM for this build.
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
- Confirm tenant trial registration is idempotent and expires exactly 30 days after its original server timestamp; reinstall and desktop clock changes must not restart it. After expiry or server failure, confirm new scans/grants are blocked, data viewing/export remains available, and server revocation continues.
- Verify the installed build’s logs contain no credential values and that configured API security headers are present.

## Release blockers

- Direct distribution requires trusted production signing; Store-distributed MSIX is signed by Microsoft on submission and must match the reserved Store identity. CI intentionally produces unsigned QA artifacts only.
- Direct MSIX automatic updates require stable HTTPS hosting for the signed MSIX and `.appinstaller` feed; Store-managed MSIX uses the Store update channel instead. The per-user ZIP installer has no automatic update mechanism.
- Version 1.0.0.65 uses a server-managed trial, not unlimited free access. Deployment acceptance must verify the configured HTTPS server, Entra sign-in, trial expiry and authorized paid-entitlement provisioning. Hosted services are not bundled with the desktop.
- Complete the privacy policy's publisher/contact/address placeholders and publish the reviewed policy and support details for the Store listing.
- Real cloud acceptance testing requires isolated Azure, AWS, and GCP test accounts with least-privilege read-only identities and authenticated CLI sessions.
- The current source and native candidate target .NET 10 LTS. Installed-package acceptance on the supported Windows matrix remains required; passing build/unit tests alone does not validate deployment.
