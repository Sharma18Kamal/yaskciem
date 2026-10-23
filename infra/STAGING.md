# Central US Windows staging

This bundle implements the approved Windows VM hosting choice without changing
the application or replacing AWS CIEM and the existing S3-compatible Object Lock
audit archive. Infrastructure generation is **not** deployment approval.

| Target | Value |
| --- | --- |
| Subscription | Visual Studio Enterprise Subscription |
| Subscription ID | `455b906b-799c-4c50-9d04-b092ea12116d` |
| Existing resource group | `Yask-Ciem-Staging` |
| Resource group metadata location | West US 2 (unchanged) |
| New resource location | Central US |
| Compute | Windows Server 2022, `Standard_D2as_v5`, 128 GiB Standard SSD |
| Database | PostgreSQL Flexible Server, `Standard_B2s`, 32 GiB, `yask_ciem` |
| Baseline estimate | About USD 212.24/month, not an invoice or spending cap |

The estimate excludes network transfer, transaction charges, backup overage,
monitoring beyond free allowances, certificates/DNS, taxes, and credits.
Single-instance staging is not an HA production architecture.

## GitHub Actions alternative

The manually dispatched [staging workflow](../.github/workflows/staging.yml)
is an independently reviewed alternative to the VS Code extension workflow.
It does not satisfy, emulate, or alter that workflow's required MCP inventory
gate. No Azure resources, Entra registrations, GitHub environments, secrets or
federated credentials are created merely by adding these files.

The workflow builds the Windows package and compiled ARM template, captures a
subscription resource snapshot, and runs ARM what-if. Provisioning defaults to
**off**. Selecting `provision` enables a second job behind the protected
`staging` environment. That job takes a fresh baseline, reruns what-if, rejects
changed template/public parameters/resource change types, and provisions only
through the compiled Bicep template. Deletes and unresolved/unsupported changes
are rejected, not automatically accepted. This is an infrastructure pipeline,
**not a complete application release or P2.3 acceptance pipeline**.
The sole allowed `Ignore` is the exact existing `Yask-Ciem-Staging` resource
group in the approved subscription: Bicep references it as `existing`, so ARM
leaves it untouched. Ignored child resources and other groups remain blocked.

### Administrator setup before the first run

#### Current preview-only configuration

The preview environment is now configured with a dedicated Entra identity:

| Setting | Value |
| --- | --- |
| Application client ID | `2c2505b8-b56f-4515-ac0d-0bbd5618c34d` |
| Service principal object ID | `e5640711-5501-4b02-aad4-9986d495380b` |
| Federated subject | `repo:Sharma18Kamal@317556502/yaskciem@1387494170:environment:staging-preview` |
| GitHub environment branch policy | Exact branch `main` |
| Provisioning flag | Repository `STAGING_PROVISIONING_ENABLED=false` |

Only the preview federation exists. There is no Azure client secret or
`staging` federation. The protected `staging` GitHub environment exists with
independent reviewer `demouser-reviewer`, self-review prevention, administrator
bypass disabled, and protected-branch-only deployments; its identity variables
and password secrets still require administrator handoff. The
[preview role](github-preview-role.json) grants subscription control-plane
reads (needed by the existing subscription inventory) and ARM what-if only.
It has no resource deployment writes, RBAC writes or data-plane permissions.
The [federation definition](github-preview-federation.json) uses an exact
environment subject, not a branch wildcard.
The repository enables GitHub immutable subjects (`use_immutable_subject=true`).
The exact subject above was verified against both the repository OIDC settings
and the actual runner claim; the older name-only subject does not authenticate.

`AZURE_CLIENT_ID`, `AZURE_DEPLOYER_OBJECT_ID`, and the approved staging-exception
flag are set on `staging-preview`. Random VM and database administrator passwords
were generated in memory and uploaded directly as the two GitHub environment
secrets. Values were not logged, placed in local files or applied to a live
resource. Preserve existing secrets on retry; they cannot be downloaded from
GitHub. Subsequent preview-only runs have succeeded, without provisioning.

Preview requests use `ValidationLevel=ProviderNoRbac` so provider checks require
read permissions rather than resource write permissions. This requires a recent
Az.Resources version supporting that level. It **does not prove deployment RBAC**.
The installer hashes and reads the ZIP through one read-only file handle that
disallows writes and replacement until verification/extraction finishes.

The provisioning path retains `Provider` validation and must remain disabled:
the preview identity cannot deploy even if someone changes the dispatch input.
Do not broaden its role to Owner/Contributor as an error workaround.

The full deployment setup below is future work, not evidence of completion.
Before enabling it, establish independent review, protected branches and a
separately approved deployment identity/permission model. If the principal changes,
generate a new preview with that configuration; old public-parameter hashes will
not match. Plan a protected credential handoff for future deployment; do not
regenerate or rotate live credentials implicitly. Never grant a deployment
federation merely to make a preview run pass.

#### Selected deployment identity and administrator bootstrap (not applied)

Use a **new** Entra application/service principal for deployment; preserve the
existing preview-only application's federation and read/what-if permissions.
Do not add deployment permissions to the existing preview service principal.
The new application's client ID and enterprise application's object ID must be
recorded separately. Use OIDC, not an application password.

The administrator-only [bootstrap template](bootstrap.bicep) creates the
pre-existing backup-writer role referenced by [main.bicep](main.bicep), plus
the following deployment grants. It does not create the application/federations,
VM, database, secrets, GitHub settings, or application resources.

| Grant to the new deployment principal | Scope | Permissions / restrictions |
| --- | --- | --- |
| Reader | Subscription | Control-plane inventory/provider/quota reads; no data-plane access |
| Custom subscription deployment submitter | Subscription | Only `Microsoft.Resources/deployments/write`, `validate/action`, and `whatIf/action` |
| Contributor | Existing `Yask-Ciem-Staging` group | Resource management within this group, including destructive writes; not a read-only or create-only role |
| Custom role assignment writer, with condition version 2.0 | Existing staging group and descendants | Only `Microsoft.Authorization/roleAssignments/write`; no assignment deletes or custom role-definition writes |

The assignment condition permits:

- Key Vault Secrets Officer **only to the new deployment principal itself**.
- Key Vault Secrets User, Storage Blob Data Reader, and the existing custom
  backup-writer role **only to service principals**, including the future VM's
  system-assigned managed identity.
- No Owner, Contributor, RBAC administrator, User Access Administrator, or
  assignment-writer grants through the conditioned role.

The future VM principal ID is unknown at bootstrap time. Accordingly the latter
three grants are bounded by role, principal type, and resource-group scope, **not**
by one specific principal ID or just the eventual vault/container. A trusted
workflow could assign these data roles to other service principals in that
group. Contributor also has substantial control over the group's resources.
The backup role allows overwrites as well as creates/appends; blob immutability,
not the role name alone, supplies retention protection. Accept these residual
permissions explicitly before applying bootstrap.

The workflow must **never deploy bootstrap.bicep** or receive Owner,
unconditioned RBAC administration, or role-definition write permissions.
An authorized Azure administrator must review/bootstrap the exact target
subscription separately and inspect the resulting assignments and condition.
Conditions on assignment writes use the documented
[authorization attributes](https://learn.microsoft.com/azure/role-based-access-control/conditions-authorization-actions-attributes).
Local compilation/contract tests do not certify live Azure condition enforcement.
An administrator must also validate an allowed assignment and denied
out-of-allowlist/wrong-principal assignments using the deployment identity in a
controlled window, removing any test grants with administrator credentials.

Because the current workflow requires **the same principal in both jobs**,
the new deployment application needs both exact environment federations listed
below and matching client/object-ID variables in both environments. The old
preview-only application remains unchanged but will no longer be the application's
selected workflow login after that approved switch.
**Both `staging-preview` and `staging` must require independent approval,
prevent self-review, disallow administrator bypass, and restrict deployments to
protected branches before the new write-capable identity is selected.**
An OIDC token issued to the same principal carries its write permissions even
in a preview job; the `provision=false` input is not an Azure permission boundary.
Retain trusted-workflow/branch review protections. If ungated read-only previews
are required, redesign the workflow for separate preview and deployment
principals instead of reusing the write-capable identity.

Administrator sequence after independent PR approval/merge:

1. Keep `STAGING_PROVISIONING_ENABLED=false`. Create the new application/service
   principal, record its client/object IDs, and verify it is not the old
   preview-only principal. Do not create a client secret.
2. Protect **both** environments as described above; add only the two exact
   federations from step 3 below to the new application.
3. Review the bootstrap template and exact permissions, then obtain separate
   approval for the IAM changes. An Azure administrator may review its what-if
   and apply it outside GitHub Actions. These commands are instructions only:

   ```powershell
   $subscriptionId = '455b906b-799c-4c50-9d04-b092ea12116d'
   $tenantId = '56c9fa43-26a9-40e2-80b1-899780a2d78f'
   $newDeployerObjectId = '<new-service-principal-object-id>'
   $account = az account show --output json | ConvertFrom-Json
   if ($LASTEXITCODE -ne 0 -or $account.id -ne $subscriptionId -or $account.tenantId -ne $tenantId) {
       throw 'Select the approved Azure subscription and tenant before bootstrap.'
   }
   if ($newDeployerObjectId -eq 'e5640711-5501-4b02-aad4-9986d495380b') {
       throw 'Do not bootstrap the preview-only principal.'
   }
   $newDeployerObjectId = [guid]::Parse($newDeployerObjectId).ToString()
   az deployment sub what-if --subscription $subscriptionId --location centralus `
       --template-file .\infra\bootstrap.bicep --parameters deployerObjectId=$newDeployerObjectId
   if ($LASTEXITCODE -ne 0) { throw 'Bootstrap what-if failed.' }
   # Only after separate review and explicit approval of these IAM changes:
   az deployment sub create --subscription $subscriptionId --location centralus `
       --name yask-staging-identity-bootstrap --template-file .\infra\bootstrap.bicep `
       --parameters deployerObjectId=$newDeployerObjectId
   if ($LASTEXITCODE -ne 0) { throw 'Bootstrap failed; inspect ARM operations before retrying.' }
   ```

4. Inspect all grants, scopes, custom role actions and the complete assignment
   condition using the administrator identity; verify the new principal has no
   inherited/group-based permissions that bypass these restrictions. Record
   evidence of live authorization checks. Do not silently broaden a failed grant.
5. Set `AZURE_CLIENT_ID` and `AZURE_DEPLOYER_OBJECT_ID` in **both**
   environments to the new principal. Retain the previously approved staging
   exception flag in preview and set it to `true` in staging only for that same
   approved decision. Do not change the existing preview-only application's
   federation or RBAC.
6. The credential custodian has retained the original password values and will
   enter them directly under repository **Settings > Environments > staging >
   Environment secrets** as `YASK_VM_ADMIN_PASSWORD` and
   `YASK_POSTGRES_ADMIN_PASSWORD`. They must match the existing `staging-preview`
   values. Never paste values into chat, commands, logs, source, or workflow
   inputs. Do not rotate/regenerate either password. GitHub secret names and
   timestamps cannot prove equality; the custodian must attest to this handoff.
7. Run a new default-branch preview with `provision=false`, approve its protected
   preview environment, and inspect evidence. The bootstrap moves the custom
   backup role out of the application plan, so historical change counts/hashes
   are not the new baseline. Keep provisioning disabled until separate release
   approval; that approval must not be inferred from this preparation PR.

#### Workflow configuration requirements

1. Review and merge the infrastructure, scripts and workflow to the repository's
   protected default branch. The workflow accepts no arbitrary checkout ref,
   resource group, subscription, region or template input. Preserve branch
   reviews and restrict changes to workflows/infrastructure to trusted owners.
2. Create GitHub environments named `staging-preview` and `staging`. For
   `staging`, require an independent reviewer, prevent self-review, disallow
   administrator bypass and allow deployments from protected branches only.
   For provisioning requests, the preview job checks reviewers, prevention of self-review and the protected
   branch policy through GitHub's environment API; inaccessible or missing
   settings stop provisioning. Preview-only requests do not require a deployment
   reviewer. Provisioning also requires `STAGING_PROVISIONING_ENABLED=true`;
   leave this repository variable `false` until independent approval is configured.
   The documented API response does not expose the
   administrator-bypass setting, so verify that setting manually in GitHub.
   If the repository's
   GitHub plan does not support these protections, do not enable provisioning.
   Restrict `staging-preview` to the default branch (an exact `main` branch policy
   is suitable during preview-only setup). This is not a substitute for branch
   protection, which must be configured before enabling provisioning.
3. Have an Azure identity administrator configure an Entra application/service
   principal for GitHub OIDC. Use issuer `https://token.actions.githubusercontent.com`,
   audience `api://AzureADTokenExchange`, and **two exact** federated subjects:
   `repo:Sharma18Kamal@317556502/yaskciem@1387494170:environment:staging-preview` and
   `repo:Sharma18Kamal@317556502/yaskciem@1387494170:environment:staging`.
   Do not use wildcard subjects or a client secret. Tenant:
   `56c9fa43-26a9-40e2-80b1-899780a2d78f`; subscription:
   `455b906b-799c-4c50-9d04-b092ea12116d`.
4. Set `AZURE_CLIENT_ID` and `AZURE_DEPLOYER_OBJECT_ID` as variables accessible
   to **both** environments. The latter is the enterprise application's
   **service principal object ID**, not its application/client ID or application
   object ID. Preview and provisioning must use the same principal/configuration.
   Bicep now accepts `deployerPrincipalType=ServicePrincipal`; interactive
   deployments still default to `User`.
5. Approve the administrator bootstrap above separately. The application
   template needs subscription deployment submission, resource writes within
   the existing group, the **pre-existing** custom backup role and restricted
   role assignments at Key Vault/backup-container scopes. It no longer creates
   role definitions. Plain Contributor is insufficient; do not grant Owner or
   unconditioned RBAC administration just to make a failed run pass.
6. Generate strong, unique VM/PostgreSQL administrator credentials outside logs
   and store them as GitHub Actions secrets `YASK_VM_ADMIN_PASSWORD` and
   `YASK_POSTGRES_ADMIN_PASSWORD`, accessible to both environments with **identical
   values**. No Azure client secret is used. Keep these credentials stable across
   preview, provisioning, retries and later runs: changing them may rotate live
   credentials. Never paste them into dispatch inputs, source or chat.
   Secret parameters are passed in memory to Azure PowerShell; the compiled
   template must declare both credentials as `secureString` before any request.
   No secret parameter file is generated. CLR `SecureString` values are not used
   in the parameter object because the what-if SDK fails to serialize them.
7. A deployment owner must explicitly review the staging exceptions: retain
   password-compatible PostgreSQL/Key Vault, and the Azure-services database
   firewall rule. Record the approval in your change-control system and set
   `STAGING_PASSWORD_KEYVAULT_EXCEPTION_APPROVED` to the exact string `true`
   in both environments. This flag records a decision for **this alternative
   pipeline**, not compliance with the incompatible extension workflow.
8. Confirm all seven resource providers are registered (including
   `Microsoft.Insights`), Central US offers/SKUs, global names and DNS availability,
   existing resource ownership and available quota. The script checks provider
   registration and two available regional/DASv5 vCPUs; these do not guarantee
   real-time VM capacity or PostgreSQL offer availability. The existing
   resource group must already exist and is never recreated or relocated.

### Run and review

First dispatch **Azure staging infrastructure** on the default branch with
`provision=false`. Download `staging-template`, `staging-preview`, and
`staging-package`. Verify the package SHA256, compiled template and inventory.
The public preview intentionally contains only resource IDs and change types,
**not property-level diffs**. Review property changes in the compiled template
and parameters; secret Before/After values and raw SDK errors are never uploaded.
What-if itself is a live Azure operation, not a local validation.

After review, dispatch again with `provision=true`. The new run produces its own
preview and waits at `staging`: the independent reviewer must examine **that
run's** artifacts before approving. Provisioning repeats the preview and refuses
different resource IDs/change types or public input/template hashes. This is not
an exact property-level plan lock: external actors can change resource properties
after preview, so maintain exclusive ownership during the deployment window.
Concurrent runs of this workflow queue rather than cancel an active deployment.

Result artifacts contain real deployment names, timestamps, status and before/after
resource snapshots even on failure. Observed new subscription resources are
**unattributed**, may belong to other actors, and are never a deletion list.
`Get-AzResource` is not exhaustive for child resources or RBAC definitions and
assignments. Consult ARM deployment operations for authoritative attribution.
No cleanup, network weakening, credential regeneration or automatic retry occurs.
If a runner is cancelled or lost, the server-side ARM deployment may continue;
inspect its actual state before retrying. Artifact retention is 14 days for
packages/previews and 30 days for deployment evidence; copy evidence to your
approved durable change-record store when longer retention is required.

The hosted runner cannot reach subnet-restricted backup storage, and this
workflow does not upload backups, install the application on the VM, run SQL
migrations, configure TLS/Entra, or certify service health. Do not report staging
ready after `infrastructure-succeeded`. Complete the application and acceptance
steps below through an approved release path under the service identity.

Offline guard tests (no Azure calls):

```powershell
.\infra\scripts\Test-StagingPipeline.ps1
```

## Validate and prepare

From the repository root, using PowerShell 7 and Azure CLI:

```powershell
.\infra\scripts\Test-StagingArtifacts.ps1
```

This compiles Bicep and verifies resource coverage, parameter hygiene, and script
syntax locally. It does not run a deployment or prove Azure capacity, permissions,
DNS availability, TLS, or application health.

Review [main.bicep](main.bicep) and
[main.parameters.json](main.parameters.json). Secure parameters intentionally have
no checked-in values. Generate credentials only during approved deployment and
retain them in the approved secret store for retries; never regenerate a deployed
database credential without coordinating its consumers. Do not log secret-bearing
commands, parameter objects, or connection strings.

[staging.settings.example.json](staging.settings.example.json) contains only
non-secret host settings, enables the console, and binds the API to loopback.
Replace every placeholder, supply the PostgreSQL connection and versioned audit
integrity keys through the protected host configuration, and configure the
archive's Credential Manager entries before starting. Register the SPA redirect
URI `https://<staging-host>/console/auth.html` and delegated API scope
`api://<staging-api-client-id>/access_as_user`; a SPA client secret is not required.
This example intentionally cannot start a fully configured service on its own.

PostgreSQL capabilities report the engine patch level, while the ARM server
resource selects the supported **major** version (`15`). Azure controls the
installed maintenance patch. Do not send `15.19` to an ARM field that only accepts
`15`, or claim that this template pins a patch release.

The application currently uses password-compatible Npgsql connections and Windows
Credential Manager for the archive. Key Vault in this approved design stores
deployment secrets; provisioning it does not automatically make the application a
Key Vault or Entra database-token client.

## Provisioning gate

Do not provision before the separate deployment approval. At deployment time:

1. Reconfirm the tenant, subscription, Central US offer/SKU availability and
   quotas, existing resource group, registered providers, and required RBAC.
2. Record a baseline resource inventory before provisioning.
3. Resolve the deployer object ID, DNS label availability, and any explicitly
   approved administrator source IP. RDP must remain disabled unless restricted
   to a single approved source; do not enable unrestricted RDP.
4. Generate and securely retain required credentials without printing them.
5. Run ARM validation/what-if with secure parameters. Review all changes,
   particularly the existing resource group, networking, and irreversible
   retention settings.
6. Provision through the Bicep entry point only. Do not use imperative `az ... create`
   fallbacks. Capture the actual deployment name and resource inventory on both
   success and failure.

**Database exposure:** the user-approved `0.0.0.0` to `0.0.0.0` PostgreSQL rule
allows connections from **all Azure services**, not only this VM. It is not the
full-internet address range, but it is broader than private networking. Database
authentication and TLS are still required. This is a staging exception, not
evidence that the private-network production requirement has been met. Do not
extend it to a full-internet rule to run migrations.

## Application package

### Verified host staging (no service activation)

With provisioning disabled and no independent reviewer available, the offline
installer can be prepared and tested without changing Azure resources:

```powershell
.\infra\scripts\Test-StagingInstaller.ps1
.\infra\scripts\Install-StagingPackage.ps1 `
  -PackagePath 'C:\ApprovedDownloads\release.zip' `
  -ExpectedSha256 '<independently-approved-64-character-SHA256>' `
  -Destination 'C:\Program Files\YASK-CIEM\releases\1.0.0.1' -WhatIf
```

Use the actual release ZIP and approved checksum. Obtain the installer from a
trusted reviewed source, not an unverified archive. Remove `-WhatIf` only on an
approved host with an existing access-controlled parent directory. The installer
checks the ZIP hash, every manifest file hash, required application/console/SQL
files and Windows-safe paths before writing. It refuses overwrites, extra files
and packaged production configuration. An interrupted extraction leaves the
new directory for diagnosis; it is never activated automatically.

This stages files only: it does not install a Windows service, start a process,
apply migrations, configure secrets, modify ACLs/firewall/IIS/TLS or claim
readiness. A raw ASP.NET executable must not be registered with `sc.exe` unless
Windows service integration is supported. Choose an approved supervisor and
service identity before implementing activation. Keep the old release intact
until migrations, configuration, audit archive, TLS and detailed readiness have
been verified through the release process.

On an approved build host with .NET 9 SDK and Node.js 22.18.0 or later:

```powershell
.\infra\scripts\Build-StagingPackage.ps1 -Version 1.0.0.49
```

Choose an unused four-part package version. The command refuses to overwrite its
output directory. It publishes self-contained `win-x64` binaries with
`BuildWebConsole=true`, verifies the console entry point, includes **all five**
SQL migrations and the backup-role script, and creates a ZIP, SHA-256 checksum,
and file manifest under the ignored `artifacts` directory. It does not deploy or
include a host-specific production configuration. A failed build leaves its
named output directory for diagnosis.

Do not copy production configuration, HMAC keys, archive credentials or cloud
CLI profiles into this bundle. Review the package manifest and hash before
transfer to the staging host.

## Complete deployment before calling staging ready

Infrastructure alone does not satisfy P2.3. Follow the existing
[Windows hosting guide](../docs/deployment/license-server-hosting.md),
[PostgreSQL guide](../docs/deployment/postgresql-installation.md), and
[operations runbook](../docs/deployment/operations-runbook.md).

- Configure an approved supervisor under a dedicated Windows service identity.
  Install only the provider CLIs required by staging accounts, and configure
  that identity's least-privilege provider authentication.
- Apply migrations `001` through `005` using the DBA identity. Create separate
  runtime, archive, backup, and disposable-restore logins with the documented
  grants. Never run the API with the PostgreSQL administrator account.
- Configure the database connection with `SSL Mode=VerifyFull` and the trusted
  server certificate chain. Preserve archive HMAC keys and Object Lock
  COMPLIANCE retention; configure Credential Manager under the same Windows
  identity that runs the server.
- Register the dedicated staging API and SPA in the target Entra tenant.
  Configure exact redirect URIs, API audience/scopes and console settings.
  Verify a real allowed user, disallowed user, and each organization role.
- Terminate HTTPS with a trusted certificate at IIS or the approved reverse
  proxy, keep the API listener private/loopback, test forwarded identity/scheme
  behavior, and validate certificate renewal. A public IP and port 443 rule do
  not configure TLS.
- Verify `/console/`, `/health/live`, `/health/ready`, and
  `/api/v1/readiness`. The detailed readiness response must report
  `productionReady=true`; liveness alone is not acceptance.
- Configure authenticated `/metrics` collection with `operations.read` or
  `Ciem.Operations.Read`. Anonymous collection must fail. Provisioning App
  Insights does not itself instrument the process or configure a Prometheus
  scraper. Do not forward bearer tokens or credential-bearing bodies into logs.
- Exercise alerts for readiness failures, queue growth/age, failed/partial
  scans, connector dead letters, and backup failures. Keep alert recipients and
  escalation ownership explicit.
- Schedule daily backups using the dedicated role, upload both dump and
  checksum to the protected independent GRS backup container using managed
  identity, and verify upload/hash/retention. Do not count a local dump as an
  off-host backup. Confirm and approve retention before locking immutable policy;
  locks can prevent deletion even during cleanup.
- Restore a representative **nonempty** backup to a disposable database using
  the dedicated restore identity. Compare source/restore row counts and audit
  chain integrity, retain a safe evidence report, and measure RPO <=24 hours and
  RTO <=4 hours. The existing restore script checks schema and prints row
  counts; zero rows or printed counts alone do not prove this acceptance gate.
- Test up to five tenants, 50 accounts and two concurrent scans. Verify bounded
  pagination, tenant isolation, account-overlap prevention, retries, queue
  limits, and visible failed/partial outcomes.

Do not mark deployment succeeded until the application is deployed and live
health probes pass. Record separately which P2.3 exercises passed, failed or
remain untested. Never turn an untested backup, restore, role, or alert check into
a success-shaped fallback.
