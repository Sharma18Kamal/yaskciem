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

## GitHub Actions alternative (not executed)

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

### Administrator setup before the first run

1. Review and merge the infrastructure, scripts and workflow to the repository's
   protected default branch. The workflow accepts no arbitrary checkout ref,
   resource group, subscription, region or template input. Preserve branch
   reviews and restrict changes to workflows/infrastructure to trusted owners.
2. Create GitHub environments named `staging-preview` and `staging`. For
   `staging`, require an independent reviewer, prevent self-review, disallow
   administrator bypass and allow deployments from protected branches only.
   The preview job checks reviewers, prevention of self-review and the protected
   branch policy through GitHub's environment API; inaccessible or missing
   settings stop the workflow. The documented API response does not expose the
   administrator-bypass setting, so verify that setting manually in GitHub.
   If the repository's
   GitHub plan does not support these protections, do not enable provisioning.
   Restrict `staging-preview` to the protected default branch as well.
3. Have an Azure identity administrator configure an Entra application/service
   principal for GitHub OIDC. Use issuer `https://token.actions.githubusercontent.com`,
   audience `api://AzureADTokenExchange`, and **two exact** federated subjects:
   `repo:Sharma18Kamal/yaskciem:environment:staging-preview` and
   `repo:Sharma18Kamal/yaskciem:environment:staging`.
   Do not use wildcard subjects or a client secret. Tenant:
   `56c9fa43-26a9-40e2-80b1-899780a2d78f`; subscription:
   `455b906b-799c-4c50-9d04-b092ea12116d`.
4. Set `AZURE_CLIENT_ID` and `AZURE_DEPLOYER_OBJECT_ID` as variables accessible
   to **both** environments. The latter is the enterprise application's
   **service principal object ID**, not its application/client ID or application
   object ID. Preview and provisioning must use the same principal/configuration.
   Bicep now accepts `deployerPrincipalType=ServicePrincipal`; interactive
   deployments still default to `User`.
5. Approve narrowly scoped RBAC through your Azure administrator. This template
   needs subscription deployments, resource writes within the existing group,
   a subscription custom role definition and role assignments at Key Vault and
   backup-container scopes. Plain Contributor is insufficient. Have the
   administrator validate a custom least-privilege role or a reviewed combination
   of Contributor and RBAC administration permissions; do not grant Owner just
   to make a failed run pass. ARM what-if must also have resource read access.
6. Generate strong, unique VM/PostgreSQL administrator credentials outside logs
   and store them as GitHub Actions secrets `YASK_VM_ADMIN_PASSWORD` and
   `YASK_POSTGRES_ADMIN_PASSWORD`, accessible to both environments with **identical
   values**. No Azure client secret is used. Keep these credentials stable across
   preview, provisioning, retries and later runs: changing them may rotate live
   credentials. Never paste them into dispatch inputs, source or chat.
   Secret parameters are converted to `SecureString` and passed in memory to
   Azure PowerShell; no secret parameter file is generated.
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
