# Existing Central US staging deployment

> **Status:** Ready for Validation

## 1. Project overview

Document the existing independently reviewed GitHub Actions/Bicep pipeline.
No azd migration, architecture redesign, credential rotation or application
activation is included. Provisioning remains disabled pending formal validation.

## 2. Requirements and approved target

- Subscription: `455b906b-799c-4c50-9d04-b092ea12116d`
- Tenant: `56c9fa43-26a9-40e2-80b1-899780a2d78f`
- Existing resource group: `Yask-Ciem-Staging`; metadata location unchanged.
- New resource location: `centralus`
- Windows D2as_v5 VM, PostgreSQL B2s/15, network, Key Vault, backup and monitoring.
- Classification: small non-HA staging; not production.
- Existing resource group metadata location: West US 2. Do not relocate it.

## 3. Components

- Existing .NET API/worker and Windows desktop application, React operations
  console and PostgreSQL migrations remain unchanged.
- Infrastructure: [main template](../infra/main.bicep), parameters and modules.
- Release orchestration: [protected workflow](../.github/workflows/staging.yml).
- This release provisions infrastructure only; application installation,
  migrations, service startup, TLS, Entra and recovery acceptance are excluded.

## 4. Recipe selection

Selected: Bicep, orchestrated by existing GitHub Actions.

Retain the reviewed deployment pipeline; no azd initialization or new environment
is required. Existing plan/validation documents do not replace protected reviews.

## 5. Architecture, approval and deployment sequence

Deployment owner approved the infrastructure-only scope, staging exceptions and
costs on 2026-10-23. Independent environment approvals remain mandatory.
The latest fixed retail subtotal is USD 211.47/month at 730 hours, excluding
usage-based charges and taxes; it is not a spending cap.

Approved exceptions: password-compatible database, Azure-services firewall,
public-access Key Vault, single-instance/no HA and initially unlocked backup
immutability. RDP remains disabled. Preserve current credentials.

The separate OIDC deployment identity uses client ID
`5bc90f84-57b2-4487-866f-bc86cf96f671` and service principal
`e5f3aecd-613d-4a2c-95b3-209ed796b6cf`. Both environments require independent
review, prevent self-review, disable administrator bypass and restrict branches.
The original preview-only principal is preserved.

Only after validation passes: enable the provisioning flag, dispatch the existing
workflow on protected main with `provision=true`, obtain independent preview
approval, review that run's evidence, and obtain separate staging approval.
Inspect deployment operations and health, then disable provisioning again.
On failure, inspect actual partial state and costs before any retry.
Do not delete resources, remove locks or broaden permissions implicitly.

## 6. Provisioning limits and validation checklist

Read-only readiness checks on 2026-10-23:

| Resource | Planned quantity | Observed readiness |
| --- | --- | --- |
| VM D2as_v5 | 1 / 2 vCPUs | Regional 0/20 and DASv5 0/20 cores; SKU listed without restrictions |
| PostgreSQL B2s/15 | 1 / 32 GiB | Regional SKU/version/storage support listed; capacity not reserved |
| Network/public IP | 1 VNet, 1 NSG, 1 NIC, 1 public IP | No group resources; DNS label available; formal full quota proof incomplete |
| Storage/Key Vault | 1 each | Names available; formal full quota proof incomplete |
| Monitoring | 1 workspace, 1 Application Insights | Providers registered; formal full quota proof incomplete |
| Existing resource group | 0 new | Retained with original CanNotDelete lock |

Name availability is not reservation. Quota is not physical capacity.
The readiness report is not formal validation of every service limit.

- [ ] All validation checks pass.
  - [ ] Core validation helper: CLI/auth/build, scope validation and what-if.
  - [ ] Azure Policy validation at applicable scopes.
  - [ ] Complete formal resource-limit verification.
  - [ ] Build verification recorded.
  - [ ] Static role verification recorded.
- [ ] Validation proof complete and workflow-authorized status update.
- [ ] Provisioning enabled only after validation and deployment-owner approval.

### Static role review from existing preparation

VM identity: Key Vault Secrets User at the vault; Blob Data Reader and custom
non-delete backup writer at the backup container. Deployer: Secrets Officer at
the vault. Administrator bootstrap owns role definitions. Deployment grants:
subscription Reader/submission-only role and staging-group Contributor plus
conditioned assignment writer. Formal re-verification remains pending.

## 7. Validation proof

### Verified existing evidence (2026-10-23)

- Main CI run `39960613578`, commit `6c537a53e337a35e700b82a24f6fb3aa65655e00`:
  success; includes infrastructure contract, offline authorization and release tests.
- Preview run `39951512148`: independently approved; 23 creates and only the
  existing group ignored; no deployment attempted, observed inventory unchanged.
- Downloaded compiled template SHA256
  `030C9BFA19A8E813D15A78BFDCF93D17C849EC41122DEB649A88712AF12655D4`
  matches that preview. Infrastructure/template inputs unchanged since preview.
- Live authorization run `39960882084`: four HTTP 201 and three HTTP 403
  AuthorizationFailed; seven assignment IDs absent after cleanup; disposable
  identity deleted; original deletion lock restored; permanent grants retained.
- Public retail fixed subtotal arithmetic verified: USD 211.47/month, plus usage.
- Provisioning variable confirmed `false`; no provisioning run dispatched.

### Formal validation blocker

The Bicep validation recipe requires its shared CLI helper, including
`az deployment sub validate` and what-if with required secure parameters.
The checked-in parameters deliberately exclude VM/PostgreSQL passwords.
Those values are only in protected GitHub environments and cannot be recovered.
The helper accepts a parameter-file path and prints unredacted Azure failures.
Do not export or regenerate passwords, write a plaintext secret parameter file,
substitute dummy values, run prompting commands unattended, or call this
validation complete based only on prior what-if.

Next implementation decision: provide a reviewed protected validation path that
uses existing secrets in memory, validates the exact deployment identity/target,
and redacts sensitive provider errors. Review the helper integration for secure
parameter transport before execution; do not modify external skill files.
Complete applicable policy and quota checks, build/static-role verification,
record actual commands/timestamps and resolve all failures before setting
Validated through the validation workflow.

No formal OVERALL PASS has been obtained. Deployment approval does not override
this blocker. Keep provisioning disabled.
