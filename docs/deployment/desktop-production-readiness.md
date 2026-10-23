# Desktop production-readiness assessment

Assessment date: October 23, 2026.

## Follow-up: implemented local candidate 1.0.0.63

The assessment below records the **pre-upgrade baseline**. Subsequent owner
decisions authorize a free standalone edition, .NET 10 upgrade and native
EXE/MSI Store listing; live-cloud access is explicitly deferred.

The desktop trial gate and device-identity lookup are removed, existing trial
files are ignored without modification, and the shared solution now targets
.NET 10. SDK 10.0.402 and self-contained runtimes 10.0.13 are verified. Fresh
solution results: **457 passed, 28 PostgreSQL-dependent tests skipped,
0 failed**, including **221 desktop tests**. See the
[current release entry](release.md#local-candidate-10063) for implementation and
validation details.

The new local candidate files are:

| Artifact | SHA256 |
|---|---|
| `artifacts/native-installers-1.0.0.63/YASK-CIEM-1.0.0.63.msi` | `9834033311DC6C1418501CEA016BA4E54A42373CE86D665FF114408544EAAF3D` |
| `artifacts/native-installers-1.0.0.63/YASK-CIEM-Setup-1.0.0.63.exe` | `F1ACD82979CDAA7CA819F4A7A517A7BA87F6A460032D375F4F731E76246931F4` |

Both signatures are locally valid development signatures with thumbprint
`537C64CF83CF2C6C1EBC89553B7B4EDD13E7F104`, not production signatures, and
neither is timestamped. MSI ProductVersion is `1.0.63`; packaged desktop DLL
version is `1.0.0.63`. MSI file tables include the self-contained runtime files.
The interactive Setup EXE is not the unattended Store asset; the MSI is the
native unattended test candidate.

**Still not production-approved:** clean-device install/upgrade/rollback,
visual/accessibility acceptance, real-cloud acceptance, legal/contact details,
production signing and stable hosting, verified Partner Center configuration
and certification remain open. No real accounts or customer machines were
accessed. Provisioning remains disabled.

## Decision

**Not ready for an unrestricted public production release.** Suitable for a
controlled evaluation after installation is verified on the test device.
Azure backend provisioning is on hold; `STAGING_PROVISIONING_ENABLED` was
verified as `false`. No deployment, installer execution, certificate import,
cloud scan, Store submission or licensing behavior change was performed.

This is a release-readiness assessment, not a penetration test, certification
decision or installed-app acceptance result.

## Baseline and verified evidence

- Assessed source: local commit
  `b5cdb0cca28ba9c35091bbd68aa4afef7f29be2b`, including desktop version
  `1.0.0.62` from [Directory.Build.props](../../Directory.Build.props).
  The pre-existing validation-progress worktree change was preserved.
- Fresh Release build/test runs with SDK `9.0.319`, VSTest and xUnit:
  - Desktop: **220 passed, 0 failed, 0 skipped**.
  - Licensing: **11 passed, 0 failed, 0 skipped**.
  - Used `dotnet test <project> --configuration Release --no-restore`,
    with TRX output for each project. This rebuilt the test dependencies;
    it did not run the full solution or prove package installation.
- [Desktop composition](../../src/CloudEntitle.Desktop/App.xaml.cs) uses local
  SQLite, live CLI adapters and a local trial. Shared API services are
  registered only when `YASK_CIEM_API_BASE_URL` is a valid HTTPS URL; access
  reviews otherwise use local storage.
- [Organization UI](../../src/CloudEntitle.Desktop/ViewModels/OrganizationViewModel.cs)
  has explicit not-configured/shared-mode messages. Installed navigation and
  offline/error-state acceptance remain unverified.
- Existing native version `1.0.0.62` MSI and Setup EXE have locally valid
  Authenticode signatures with subject `CN=YASK-CIEM`. The repository identifies
  these as development-certificate artifacts; local trust is not proof that a
  clean customer's PC trusts them.
- Latest versioned local MSIX found: `1.0.0.61`. Its manifest and runtime
  configuration were read directly from the archive. Identity is
  `Sharma18Kamal.YASKCIEM`, publisher `CN=YASK-CIEM`; included .NET, Desktop and
  ASP.NET Core runtime versions are `9.0.21`.
- No `1.0.0.62` MSIX was found in the versioned artifact folders inspected.
  Existing binary provenance was not tied to the assessed source commit.
  Neither existing artifact is designated a final Store release candidate.

### Existing artifact fingerprints

| Artifact | SHA256 |
|---|---|
| `artifacts/msix-1.0.0.61/YASK-CIEM-1.0.0.61.msix` | `4B0D1C8D0896473B6DC09974A3310D071A8422E848DC7DA603F7416EC4F48E0B` |
| `artifacts/native-installers-1.0.0.62/YASK-CIEM-1.0.0.62.msi` | `C1A766E677C55E86C52E38BB22B82BCC9ED3980C302795C840589F8F9745B9C9` |
| `artifacts/native-installers-1.0.0.62/YASK-CIEM-Setup-1.0.0.62.exe` | `9FE056CF2857913D8664ACF23621D58C1B6519E61DE457DC118600AFAAF1D714` |

Fingerprints identify existing files only; they do not certify their behavior.

## Release gates

| Priority | Gate | Evidence / gap | Required exit proof |
|---|---|---|---|
| P0 | Usable entitlement after trial | [ShellViewModels](../../src/CloudEntitle.Desktop/ViewModels/ShellViewModels.cs) sets `IsTrialActive` only for an unexpired trial and reports application options locked after expiry. [RemoteLicenseService](../../src/CloudEntitle.Licensing/RemoteLicenseService.cs) exists but is not wired into desktop composition. | Choose the edition/monetization model; implement and test the intended free, paid or evaluation behavior, including day 15, offline operation and recovery. Do not sell a perpetual app that locks with no activation route. |
| P0 | Privacy and support identity | [Privacy policy](../legal/privacy-policy.md) still contains publisher legal-name, monitored privacy-contact and postal-address placeholders. | Owner supplies accurate details; publish the reviewed policy at a public URL and use it in the Store listing. Do not invent contact/legal information. |
| P0 | One reproducible Store candidate | Source/native version is `1.0.0.62`, latest inspected MSIX is `1.0.0.61`; only development identity/signing is evidenced. No Partner Center or certification evidence was inspected. | Confirm Store distribution route, reserved identity and publisher in Partner Center; build one version from an approved commit, record hash/runtime/manifest, pass package validation and certification. |
| P0 | Installed customer experience | Automated tests do not install the release package or exercise Store updates. | Clean standard-user install/launch, upgrade with preserved data/trial state, uninstall/reinstall, protocol activation and chosen update channel verified against the exact candidate. |
| P0 | Live advertised cloud support | Current run used offline/mock-based tests, not real customer-equivalent cloud sessions. | For each advertised provider, verify selected-account read-only discovery, least-privilege/partial permissions, expired sessions, missing CLI, cancellation, exports and findings against known fixtures in isolated accounts. Restrict claims to accepted providers. |
| P1 | Runtime lifetime | [Project](../../src/CloudEntitle.Desktop/CloudEntitle.Desktop.csproj) targets .NET 9. It is supported on assessment date but reaches end of support November 10, 2026. | Prefer a separately reviewed .NET 10 LTS migration before public launch; rebuild and retest dependencies and packages. Do not merely change the target or SDK and assume compatibility. |
| P1 | Supported Windows/device matrix | Manifest minimum Windows build is `17763`; this alone is not a tested or supported-runtime OS promise. Existing scaling tests simulate work areas. | Align advertised Windows editions/builds with runtime support; visually test startup, restore/maximize, navigation, keyboard access and 100/125/150/200% scaling, including 2560x1600 at 200%. |
| P1 | Data and diagnostics acceptance | Local storage and backup implementations/tests exist; installed crash/recovery and credential-safe diagnostics were not assessed end-to-end. | Verify backup/restore, interrupted scans, persistent data, disk/permission failures and logs using synthetic identities; document retention/deletion and avoid uploading real tenant data in evidence. |
| P1 | Standalone feature boundaries | Core local composition does not require the staging backend. Central sharing, server-side schedules and central audit synchronization do. | With API/SSO configuration absent, verify core scans work and unavailable shared features are clearly labelled/disabled. Local scheduled scans require the app to stay open; do not advertise an always-on service. |

P0 blocks the intended public release; P1 must also be resolved or explicitly
bounded by a supported, accurately advertised release scope. None of the gates
above is satisfied merely by the successful infrastructure-validation run.

## Store signing and update distinction

For Store-distributed **MSIX**, Microsoft signs the package on submission; a
separately purchased production certificate is not automatically required.
The publisher/identity must still match the Store reservation. The current
template identity has not been verified against Partner Center.

Direct-download/sideload distribution needs its own trusted signing and update
arrangements. An App Installer HTTPS feed is relevant to that route, not a
mandatory addition to a Store-managed MSIX update path. A Store listing that
distributes a native EXE/MSI has a different submission path and must be
confirmed before choosing packaging changes.

## Closure sequence without Azure provisioning

1. Owner selects the standalone edition's entitlement model and Store route.
   Existing trial behavior remains unchanged until that decision is approved.
2. Complete privacy/support details and scope the Store claims to working
   desktop capabilities. These details require owner input.
3. Migrate to a supported long-lived runtime in a separate reviewed change;
   run affected suites and rebuild a single Store candidate.
4. Execute the clean-machine and UI matrix against that candidate. Record
   package hash, Windows build, install/update channel and each result.
5. Execute isolated real-cloud acceptance for each advertised provider.
   Use read-only test identities; do not reuse deployment-admin sessions.
6. Resolve failures, rerun affected acceptance, and submit through Partner
   Center. Public availability follows actual certification and release
   approval, not this report.

Infrastructure provisioning, shared hosted-service acceptance and production
licensing-backend hosting remain separate work. Choosing Store-based
entitlements or a free standalone edition does not inherently require deploying
the existing Azure staging stack.

## External references verified on assessment date

- [.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core):
  .NET 9 end of support November 10, 2026; .NET 10 LTS end of support
  November 14, 2028. Servicing also requires current patches.
- [Microsoft MSIX signing overview](https://learn.microsoft.com/en-us/windows/msix/package/signing-package-overview):
  distinguishes self-signed development packages, production signing and
  Microsoft Store signing.
