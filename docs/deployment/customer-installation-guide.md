# YASK-CIEM: installation and first-use guide

For a shorter first-scan walkthrough, use the [offline setup and cloud sign-in
guide](getting-started.html). New Desktop builds include it under **Setup guide**.
Native MSI builds also add **YASK-CIEM Setup and Cloud Sign-in Guide** to Start;
the Setup EXE offers to open it after installation. These additions require a
new installer build; they do not update previously installed packages.

After setup, use the [complete Desktop user manual](../user-guide/user-manual.md)
for screen-by-screen options and operating procedures.

This guide is for an organization installing YASK-CIEM in its own environment.
It covers the Windows Desktop, the separate License Server, and read-only
onboarding for Azure, AWS, Google Cloud and optional Kubernetes.

Replace every `<placeholder>` with your organization's value. Run PowerShell
commands on Windows. Do not copy another customer's application IDs, tenant IDs,
storage URLs, credentials or subscription IDs.

## 1. Understand the components and sign-ins

| Component | Purpose | Where it runs |
| --- | --- | --- |
| Desktop | Interactive discovery, scans, analysis and reports | Each operator's Windows PC |
| License Server/API | Trial/license validation, shared organizations and background workers | Organization-managed Windows server; laptop for development testing only |
| PostgreSQL | Central subscription, shared workspace and audit state | Protected database host; local instance for laptop tests |
| Immutable archive | Retained central audit evidence | Approved Azure Blob or S3-compatible Object Lock storage |
| Provider CLIs | Authenticate read-only collection | Operator PC; separately on server for scheduled collection |

There are **two separate sign-ins**:

1. **Organizations > Sign in** authenticates the user to the YASK-CIEM API using
   Microsoft Entra ID. This is required even if you scan only AWS or Google Cloud.
2. **Cloud Accounts > Connect** selects a cloud provider and uses its CLI
   credentials. It does not sign in to the API or activate the trial.

An Entra tenant is needed for API identity, but an Azure VM is not mandatory.
Scanning AWS does not require hosting the API on AWS, and scanning GCP does not
require hosting it on GCP. A Blob/container URL is never the License Server URL.

## 2. Prepare prerequisites and approvals

### On each Desktop PC

- Windows 10/11 x64 and a trusted installation package.
- Network access to the License Server HTTPS endpoint, Entra sign-in and the
  selected cloud APIs, through approved proxy/firewall settings.
- Only the CLI tools needed: Azure CLI, AWS CLI v2, Google Cloud CLI and,
  optionally, `kubectl` plus the cluster's authentication plugin.
- A dedicated read-only cloud identity and explicitly approved scan scopes.
- Self-contained packages include their runtime. Source builds require the
  .NET 10 SDK selected by the repository's `global.json`, Git and PowerShell 7
  for the laptop setup scripts.
- The native MSI and Setup EXE include the .NET 10 runtimes and offline guide,
  but not provider CLIs or PostgreSQL. Install only the required official CLIs.
  PostgreSQL belongs on the License Server database host, not every Desktop PC.

### For the server administrator

- PostgreSQL 15+, DBA access for migrations, separate least-privilege runtime
  and archive logins, and backup/restore procedures.
- Two Entra app registrations: API and Desktop public client.
- Trusted HTTPS, protected configuration/secrets and a supervised server process.
- An approved immutable audit destination and retention policy. Locking
  retention can be irreversible and retain billable data; obtain approval first.

Do not grant cloud write/admin permissions just to make discovery succeed.
Cloud administration, JIT, activity sources, SIEM connectors and scheduled scans
are separate opt-in workflows, not prerequisites for a first Desktop scan.

## 3. Prepare the License Server once per environment

An operator joining an existing installation should obtain the API URL, tenant
ID, Desktop client ID and API scope from the administrator, then skip to step 4.

### 3.1 Register API and Desktop identities

Have the Entra administrator:

1. Create a dedicated API app registration. For a single-organization test,
   select single tenant; use a deliberately designed multitenant registration
   only when serving multiple customer tenants.
2. Configure API access token version **2**.
3. Set the API identifier URI to `api://<api-client-id>` and expose the delegated
   scope `access_as_user`.
4. Create a separate Desktop **public-client** app with the interactive redirect
   URI `http://localhost`. Match the supported tenant/account model to the API.
5. Add delegated Microsoft Graph `User.Read` and the API's
   `access_as_user` permission to the Desktop registration.
6. Ensure the corresponding enterprise applications exist. Complete user/admin
   consent according to tenant policy; do not bypass consent requirements.

The public Desktop client does not need a client secret. API Graph profile
permission is distinct from the Azure CLI Graph read permissions used for scans.

Server authentication must use:

| Setting | Value |
| --- | --- |
| `AzureAd:Instance` | `https://login.microsoftonline.com/` |
| `AzureAd:TenantId` | Approved tenant GUID for single tenant; `organizations` for the designed multitenant deployment |
| `AzureAd:ClientId` | API application client GUID |
| `AzureAd:Audience` | API application client GUID, **without** `api://`, for v2 tokens |
| JWT inbound mapping | Disabled in the supplied server code; policies use `tid`, `oid`, `scp`, `roles` |

Do not disable audience, issuer, signature, lifetime or scope checks to resolve
sign-in errors.

### 3.2 Choose one hosting path

#### A. Laptop development/test

1. Obtain the current source, install PostgreSQL, .NET SDK, Azure CLI and
   PowerShell 7.
2. Follow [laptop setup](local-license-server.md) to trust the localhost
   development certificate and establish PostgreSQL administrator access.
3. Run the one-time setup from the repository root:

   ```powershell
   .\scripts\configure-local-license-server.ps1 `
     -ApiClientId '<api-client-guid>' `
     -DesktopClientId '<desktop-client-guid>' `
     -TenantId '<tenant-guid>' `
     -StorageAccountResourceId '/subscriptions/<subscription-guid>/resourceGroups/<group>/providers/Microsoft.Storage/storageAccounts/<account>'
   ```

4. The script creates an isolated local database, applies migrations and stores
   generated secrets in the current user's Windows Credential Manager. It does
   not create Azure storage, register applications or start a trial. Never rerun
   it by deleting an existing database or trial history.
5. Configure the approved Azure archive separately using the
   [Azure Blob archive guide](azure-blob-audit-archive.md). Development archive
   access uses Azure CLI under the server's Windows identity, not Desktop SSO.
6. Start the server and keep the terminal open:

   ```powershell
   .\scripts\start-local-license-server.ps1
   ```

   The startup launcher supports Windows PowerShell 5.1 and PowerShell 7.
   Initial laptop configuration still requires PowerShell 7. Start the server
   under the same Windows user who performed setup so its protected credentials
   are available. Do not start a second instance if port 7443 is already in use.

The test URL is `https://localhost:7443/`. Other PCs cannot reach this through
their own `localhost`. This path is not a production hosting configuration.
Missing archive configuration may leave readiness at 503 even if licensing
works; that is incomplete setup, not production acceptance.

#### B. Organization-hosted deployment

1. Follow [PostgreSQL installation](postgresql-installation.md) and
   [database configuration](../../infra/postgres/README.md).
2. Apply **every numbered migration shipped with the matching release in
   filename order**, including the subscription migration, using the DBA
   identity. Follow the package manifest; gaps in sequence numbers are allowed.
   Do not invent, rename or renumber migration files. Production startup does
   not migrate the database.
3. Install the matching server bundle and configure the service identity using
   [License Server hosting](license-server-hosting.md).
4. Configure one approved archive: [Azure Blob](azure-blob-audit-archive.md) or
   S3-compatible Object Lock as described in the hosting guide. The production
   Azure adapter requires a host with an authorized Azure managed identity;
   it does not fall back to a Desktop/CLI login on an ordinary Windows server.
5. Configure trusted HTTPS, private database access, persistent secret storage,
   process supervision, monitoring and backup/restore. Do not expose PostgreSQL
   publicly or use localhost development certificates on customer machines.
6. Validate through the approved HTTPS endpoint:

   ```powershell
   Invoke-WebRequest 'https://ciem.example.org/health/live'
   Invoke-WebRequest 'https://ciem.example.org/api/v1/readiness'
   ```

   Expect 200 for liveness and, after complete provisioning, readiness.
   An unauthenticated subscription request must return 401. A liveness 200 alone
   does not verify database health, archive readiness or user authorization.

## 4. Install Desktop

1. Obtain Desktop and server builds from your organization's approved channel.
   Verify signatures, publisher and supplied checksums. Current repository
   test installers are development-signed candidates, not an accepted public
   customer release. Do not import development root certificates on customer PCs.
2. Close existing Desktop instances.
3. Install the trusted MSI/Setup EXE or organization-approved package following
   [Windows installation](installation.md). Per-machine native installation
   requires administrator approval; Desktop itself should run as a normal user.
4. Use matched builds containing the current authentication fixes. Updating
   source does **not** update an already installed MSI or running Desktop.
5. For source-based testing instead of a packaged install:

   ```powershell
   dotnet restore .\CloudEntitle.sln --configfile .\.nuget\NuGet.Config
   dotnet build .\src\CloudEntitle.Desktop\CloudEntitle.Desktop.csproj
   ```

Desktop stores local results in SQLite; PostgreSQL is not required on each
operator PC when a central server already exists.

## 5. Configure Desktop API access and activate the trial

Under the Windows user who will run Desktop, set the administrator-provided
non-secret values:

```powershell
[Environment]::SetEnvironmentVariable('YASK_CIEM_API_BASE_URL', 'https://ciem.example.org/', 'User')
[Environment]::SetEnvironmentVariable('YASK_CIEM_ENTRA_CLIENT_ID', '<desktop-client-guid>', 'User')
[Environment]::SetEnvironmentVariable('YASK_CIEM_API_SCOPE', 'api://<api-client-guid>/access_as_user', 'User')
```

For laptop tests only, use `https://localhost:7443/` as the API URL.

1. Restart Desktop from a process that has the new environment. Existing
   Explorer/terminal/VS Code processes may retain old values; signing out of
   Windows and back in refreshes them.
2. For source-based testing, run `.\scripts\start-local-desktop.ps1` from a new
   PowerShell terminal. It reloads these user values and checks API liveness.
3. Open **Organizations**, enter the actual Entra tenant GUID and select
   **Sign in**. Complete browser sign-in and any approved consent prompts.
4. Select **Start 30-day trial**, then **Refresh license**.
5. Confirm that the header shows an active trial/licensed entitlement, not
   validation unavailable. Trial registration is once per Entra tenant for
   720 hours; reinstalling Desktop does not reset it.
6. If using shared organization features, create/select the organization and
   have its administrator configure membership, roles and approved account
   bindings. API sign-in, cloud visibility and organization authorization are
   independent checks.

Offline/unavailable validation or an expired entitlement blocks new collection.
Existing local data remains readable/exportable. Do not reset subscription
records, change the clock or remove authorization checks to bypass this.

## 6. Connect Microsoft Azure

1. Install Azure CLI and verify `az --version` in a new terminal.
2. Have the Azure administrator grant a dedicated identity **Reader** on only
   the intended subscriptions. Do not grant Owner, Contributor or User Access
   Administrator for scans.
3. For Entra directory collection, obtain tenant-approved delegated Graph
   consent for `User.ReadBasic.All`, `Group.Read.All`, `Application.Read.All`.
   Optional PIM schedule collection needs
   `RoleAssignmentSchedule.Read.Directory` and
   `RoleEligibilitySchedule.Read.Directory`, plus supported directory roles
   where required. Consent here applies to the scanning Azure CLI identity/app,
   not automatically to the YASK-CIEM Desktop registration.
4. Authenticate and verify:

   ```powershell
   az login --tenant '<tenant-guid>'
   az account list --output table
   az account set --subscription '<subscription-guid>'
   az account show --output table
   ```

5. In **Cloud Accounts**, select Azure **Connect**. Existing valid CLI
   credentials can be reused; otherwise complete the launched login flow.
6. Discover subscriptions, select only approved scopes and follow step 10.
7. Review ARM, Entra, PIM and activity coverage separately. Successful
   subscription listing does not prove Graph consent or full data coverage.

See [Azure permissions and limitations](../providers/azure.md).

## 7. Connect Amazon Web Services

1. Install AWS CLI v2 and verify `aws --version`.
2. Prefer IAM Identity Center SSO with a dedicated read-only permission set,
   such as an organization-reviewed `SecurityAudit` baseline. Confirm the
   actual IAM read permissions required by the adapter.
3. Configure and authenticate a profile for each account/permission set:

   ```powershell
   aws configure sso --profile ciem-audit
   aws sso login --profile ciem-audit
   aws sts get-caller-identity --profile ciem-audit
   aws configure list-profiles
   ```

4. Repeat with distinct profile names for other target accounts. The app scans
   working profile/account pairs; an Organizations roster alone does not grant
   access or automatically assume a role into member accounts.
5. In **Cloud Accounts**, select AWS **Connect**. The current launcher can open
   `aws configure --profile yask-ciem`, which is IAM-key configuration, **not**
   IAM Identity Center setup. For SSO, prepare profiles externally as above.
   Do not enter long-lived keys just because that terminal opens. Close an
   unused configuration terminal; the app can detect working SSO profiles.
6. Confirm AWS shows connected, discover accounts and inspect each profile and
   inferred permission set before selecting the intended rows.
7. Optional sources require additional reads: existing Access Analyzer
   analyzers/findings, `cloudtrail:LookupEvents` for IAM change history, and
   `organizations:ListAccounts` for a permitted organization roster.
8. Follow step 10 and review source coverage per account/profile.

The app's AWS **Disconnect** excludes AWS from subsequent app discovery/scans;
it does not delete profiles, deactivate IAM keys or revoke shared SSO sessions.
Stop an active scan separately. Revoke access through AWS governance.

See [AWS permissions, profile behavior and limitations](../providers/aws.md).

## 8. Connect Google Cloud

1. Install Google Cloud CLI and verify `gcloud --version`.
2. Have the Google Cloud administrator grant a dedicated read-only identity
   access to the intended projects and, if required, ancestor folders or
   organization. Review Resource Manager/IAM metadata and policy reads,
   service-account reads and role-definition reads explicitly; a generic
   Viewer assignment alone is not proof of complete coverage.
3. Have the administrator enable Cloud Asset Inventory for the intended
   projects if not already enabled. This is an approved provisioning operation,
   not something the scanner performs.
4. Resource discovery needs `cloudasset.assets.searchAllResources`.
   Inherited IAM inspection needs `cloudasset.assets.searchAllIamPolicies`
   at the relevant ancestor scopes. Optional audit reads need
   `logging.logEntries.list` and any additional log-specific access.
5. Authenticate and verify:

   ```powershell
   gcloud auth login
   gcloud auth list
   gcloud projects list
   gcloud config set project '<project-id>'
   gcloud projects get-iam-policy '<project-id>' --format=json
   ```

6. In **Cloud Accounts**, select Google Cloud **Connect** and complete or reuse
   the CLI sign-in. The Desktop live CLI path uses the active `gcloud` account;
   Application Default Credentials login is not a replacement for that check.
7. Discover projects, select approved rows and follow step 10.
8. Review project IAM, assets, ancestor policies, service accounts and audit
   sources independently. Missing ancestor access means incomplete inheritance,
   not proof that no inherited permissions exist.

See [GCP permissions and limitations](../providers/gcp.md).

## 9. Optional: Kubernetes, AKS, EKS and GKE

1. Install `kubectl` and the provider-required authentication plugin.
2. Have the cluster administrator provide an approved kubeconfig/context using
   the organization's onboarding process. The app does not create contexts or
   refresh cluster credentials.
3. Grant only the required reads for Roles, RoleBindings, ClusterRoles,
   ClusterRoleBindings, ServiceAccounts, Pods and Deployments at the intended
   namespace/cluster scope. Do not grant `cluster-admin` for scanning.
4. Check local context names and representative authorization:

   ```powershell
   kubectl config get-contexts
   kubectl --context '<context-name>' auth can-i list roles --all-namespaces
   kubectl --context '<context-name>' auth can-i list clusterrolebindings
   ```

5. Discover accounts in Desktop and explicitly select the intended Kubernetes
   context rows. Selecting an Azure/AWS/GCP account does not select all clusters.
6. Keep required cloud CLI sessions valid for workload-identity trust checks.
   An annotation alone does not prove working cloud federation or effective
   cloud permissions.

The scanner covers configured contexts, not every cluster in a cloud account.
See [Kubernetes coverage](../providers/kubernetes.md).

## 10. Run the first read-only scan

1. Start with one non-production account/project/subscription or cluster.
2. Check the header for an active entitlement and the intended cloud connection.
   Cached CLI credentials alone do not automatically select a provider at
   startup; use **Connect** in each new app session.
3. Open **Dashboard > Discover accounts**.
4. Inspect the provider, account ID, profile/permission set and cluster context.
   Select only approved rows; avoid **Select all** for the first test.
5. Select **Scan Now**. Use **Stop Scan** if you need to cancel collection.
6. Review completion status and per-source coverage. Treat `Denied`,
   `Unavailable`, `Unsupported`, `Unknown` or partial results as evidence gaps,
   not as a clean security assessment.
7. Review **Identities**, **Resources**, **Entitlements**, **Effective Access**,
   **Access Paths**, **Risk Findings** and **Recommendations**.
8. Use **Scan History** for the saved result and timestamp. Use **Generate
   Report** to export the chosen report; confirm its scope and evidence age.
9. Protect exports and local data under your organization's security policy.

For a narrower onboarding test, **Cloud Accounts** also offers explicit scope
verification followed by **Scan verified account**. That mode deliberately
excludes other providers, broad directory/organization sources and Kubernetes
contexts; it is not equivalent to a full Dashboard scan.

Scans and offline what-if analysis do not change cloud resources. Separate
cloud-administration/JIT workflows can write when explicitly configured and
approved; leave them disabled during baseline installation.

## 11. Optional shared and SIEM workflows

- Central schedules need approved account bindings and cloud CLI authentication
  under the **server service identity**. They do not inherit Desktop credentials.
- Splunk HEC, QRadar TLS/LEEF, Sentinel Logs Ingestion, signed webhooks and
  ticketing need separately provisioned receivers and organization channel rules.
- Native Sentinel export requires an authorized Azure managed identity, not
  a laptop's Desktop/CLI sign-in.
- Inbound SIEM alerts require a client-managed normalized bridge. Native vendor
  polling and automated SOAR remediation are not implemented.

Follow [security connector setup](security-connectors.md) and
[operations procedures](operations-runbook.md) only after baseline acceptance.

## 12. Troubleshooting

| Symptom | Check/action |
| --- | --- |
| API URL configuration error | Set all three Desktop variables; use the HTTPS API URL, never a Blob URL; relaunch with a refreshed environment. |
| Sign-in required | Use Organizations sign-in; cloud CLI authentication is separate. |
| Organizations Sign in disabled | Enter a valid tenant GUID. |
| Consent/admin-approval error | Ask the tenant administrator to review the requested delegated permissions and tenant policy. |
| HTTP 401 / `IDX10214` | Check API client ID/audience for v2 tokens, issuer/tenant, token validity and that Desktop requests the API scope, not a Graph token. |
| HTTP 403 after successful authentication | Check original claim preservation, required scope and organization membership/roles; inspect server logs before granting anything. |
| Trial not started/expired | Register explicitly or obtain an approved paid entitlement from the licensing administrator; do not reset database history. |
| No active CLI sign-in | Select the provider's Connect button and verify its CLI identity under the same Windows user. |
| Cloud API AccessDenied/Forbidden | Check the specific denied source and least-privilege scope; this is different from a License Server 403. |
| Missing AWS member accounts | Configure a working profile for each account/permission set; roster visibility is insufficient. |
| Missing Azure directory/PIM | Review Azure CLI Graph consent and supported directory role requirements separately from ARM Reader. |
| Missing GCP assets/inheritance | Check enabled API, CAI permissions and ancestor-scope access. |
| TLS certificate failure | Fix trust, hostname and certificate validity; never bypass certificate validation. |
| Readiness 503 with liveness 200 | Inspect database, schema, workers, queue and immutable archive readiness; do not mark production ready. |

Desktop logs are under `%LOCALAPPDATA%\CloudEntitle\Logs`.
The local database is `%LOCALAPPDATA%\CloudEntitle\Data\cloudentitle.db`;
default reports are under `%LOCALAPPDATA%\YASK-CIEM\reports`.
Collect sanitized error codes, timestamps and affected scope for support.
Never share access tokens, Authorization headers, secrets, raw credential files
or unredacted sensitive exports.

## 13. Acceptance and ongoing operation

Start with the [offline permission-analysis acceptance cases](../effective-access/effective-access.md#conditional-decision-acceptance).
They verify normalized condition handling and explanations; they do not prove
complete live-cloud authorization coverage. Progress through these gates:

1. Run normalization and effective-access/what-if regression suites on the exact
   candidate source. Preserve actual results, including failures and skips.
2. Verify provider-native evidence survives collection, normalization and saved
   inventory. Use known identities, grants, exclusions/denies, groups and
   conditions; treat unsupported native semantics as incomplete coverage.
3. With explicit owner approval, compare the modeled results against
   provider-native evidence and approved read-only probes in isolated Azure,
   AWS and GCP accounts. Record expected and actual decisions by action/resource.
   Do not perform destructive actions just to validate a permission.
4. Build the candidate packages containing the fixes and test clean-device
   installation, API sign-in, trial validation, selected-scope scanning, reports,
   upgrades and recovery. Source tests do not update an installed package.
5. Sign off only the providers/features supported by recorded evidence. Keep
   blocked live checks and unverified archive/recovery work visibly pending.

- [ ] Trusted, current Desktop and server builds installed.
- [ ] Correct Entra registrations, consent and API scope verified.
- [ ] Unauthenticated API requests rejected; signed-in authorized access works.
- [ ] Trial/license validated without changing registration history.
- [ ] Only explicitly selected providers/scopes collected.
- [ ] Each required cloud source checked for successful or documented partial coverage.
- [ ] Saved scan and report reviewed against the intended account/profile/context.
- [ ] Full production readiness, immutable archive verification and protected
      database/key backups completed before production sign-off.
- [ ] Restore and upgrade procedures tested; scheduled identities and connectors
      configured only when explicitly required.

Back up Desktop data using the app's supported backup workflow and retain central
PostgreSQL data, archive evidence and all required historical audit integrity
keys together. Close Desktop before package upgrades and preserve application
data. Follow [release acceptance](release.md) and the
[operations runbook](operations-runbook.md); installation alone is not evidence
of real-cloud acceptance or complete security coverage.
