# Opt-in native cloud administration

The Windows desktop Organizations workspace can perform explicitly approved
manual cloud role/policy writes. Discovery, activity collection and recommendation
simulations remain read-only. This is not automatic least-privilege remediation,
native JIT granting, or an approved SOAR response engine.

## Client preparation

The client provisions cloud identities and permissions **on demand**. The app does
not create credentials, grant itself permissions, provision log destinations or
contact SIEM/SOAR endpoints automatically. It uses the desktop's already signed-in
`az`, `aws` or `gcloud` identity, not the server managed identity.

1. Configure organization Entra sign-in and bind the verified cloud account.
2. Grant the administrator `ViewInventory`, `ManagePolicies` and
   `ManageRemediations` organization permissions. Shared deployments re-check these
   through the workspace API on every operation; standalone membership is local,
   mutable state, not a server security boundary.
3. Prepare an appropriately scoped cloud CLI identity. Native cloud authorization
   remains the enforcement boundary; organization permission does not confer IAM
   permission. Do not grant unrestricted administrator privileges merely to use
   this feature.
4. Arrange workload testing, an independent break-glass identity and a separate
   provider-console rollback path before approving any change.

| Provider | Read permissions | Additional writes, only for selected operations |
|---|---|---|
| Azure | Subscription read, role assignments read, role definitions read | `Microsoft.Authorization/roleAssignments/write` or `/delete`; custom role updates need `Microsoft.Authorization/roleDefinitions/write` at the subscription |
| AWS | STS identity; IAM GetUser/GetRole/GetGroup, ListAttached*Policies, List*Policies and Get*Policy as applicable | Attach/DetachUserPolicy, RolePolicy or GroupPolicy; Put/DeleteUserPolicy, RolePolicy or GroupPolicy |
| GCP | Project describe, `resourcemanager.projects.getIamPolicy`, `iam.denypolicies.list` and `.get` as applicable | `resourcemanager.projects.setIamPolicy`; `iam.denypolicies.create`, `.update` or `.delete` |

Read/restore permissions must also cover rollback. For Azure writes, Entra target
object IDs must already be known. No directory object or service account is
created by the app. GCP impersonation and credential/token-file overrides are
rejected because this workflow cannot verify their effective identity.

## Supported operations and input

- **Azure role assignment add/remove:** Subject is a user/group/service-principal
  object GUID. Target kind/ID is the exact role-assignment GUID; choose a new GUID
  for creation. Role/policy is the role-definition GUID. Scope must be the bound
  subscription or a descendant resource group/resource. Removal checks all three
  identifiers and captures assignment conditions/description for restoration.
- **Azure custom role update:** Role/policy is an existing custom-role GUID.
  Supply ARM role properties (`roleName`, `description`, `permissions`,
  `assignableScopes`), directly or under `properties`. All assignable scopes must
  lie in the selected subscription. Built-in role editing is rejected.
  `actions`/`dataActions` allow access; `notActions`/`notDataActions` are exclusions,
  **not explicit deny**. Arbitrary customer-created Azure deny assignments are not
  supported; Azure deny assignments are generally provider-managed.
- **AWS attachment add/remove:** Subject is an IAM NAME, target kind/ID is
  `user`, `role` or `group`, and Role/policy is a managed-policy ARN in the selected
  account or the `aws` namespace. This changes a policy attachment, not role trust
  or group membership.
- **AWS inline policy put/delete:** Use the same subject/kind and a policy NAME.
  Put accepts a native IAM JSON document with `Statement`, `Effect: Allow|Deny`,
  actions/resources and optional conditions. Existing documents are captured
  before replacement; creation is also supported. Trust policies, SCPs,
  permissions boundaries, credential creation and managed-policy version editing
  are outside this workflow.
- **GCP project IAM replacement:** Supply a complete project policy with
  `bindings`, including conditions as applicable. Preserve all unrelated
  bindings. `auditConfigs` must match the captured cloud policy exactly; changing
  audit logging is rejected. The app supplies the current etag and version 3
  when conditions exist. Use provider member syntax (`user:`, `group:`,
  `serviceAccount:` or supported federation principal forms).
- **GCP deny policy put/delete:** Role/policy is the deny policy ID. Put accepts
  native `rules`/`denyRule` JSON. Permission names use the IAM v2 deny format, such
  as `storage.googleapis.com/objects.get`, not allow-policy permission names.
  Existing policies are read using `get`, not inferred from list summaries.
  Updates/deletes use the current etag. Review denial exceptions and inherited
  organization/folder policies.

Devices/services act through cloud identities: Azure managed-identity service
principals, AWS IAM roles or GCP service accounts/federated principals. Do not pass
a device object ID as a principal or IAM NAME. This UI is an advanced native JSON
editor, not an automatic device-to-identity resolver.

## Approval and execution

1. Select a bound account in **LIVE CLOUD ADMINISTRATION**.
2. Enable administrator read/write mode for this session, choose an operation,
   fill provider-specific fields and supply a change reason.
3. Select **Prepare live preview (read only)**. Review the captured Before and
   proposed After JSON and the displayed cloud CLI identity.
4. Review dependencies, inherited permissions, conditions, audit logging,
   break-glass access and application tests. Check the approval acknowledgment.
5. Select **Approve and apply LIVE**, then confirm the real-write dialog. A single
   currently authorized administrator may approve and explicitly apply.
6. Run a fresh scan and workload tests after provider state verification.

Editing inputs invalidates the selected preview. Sign-out/organization changes
disable write mode and clear displayed plans. Previews expire after 15 minutes.
The service re-checks organization/account authorization, cloud identity and
captured target state before mutation, and records approval before dispatch.
Neither scans nor sign-in trigger a write. Disabling the UI cannot recall an
already dispatched cloud request.

## Verification, rollback and unknown outcomes

Successful command exit alone is insufficient: the app re-reads the provider
state. Permission propagation or interrupted commands can yield `ApplyUnknown`
or `RollbackUnknown`. These states **cannot retry the write**. Use **Reconcile
(read only)**; it compares cloud state to both snapshots without mutation.
Matching neither snapshot requires manual incident review.

Rollback requires a new explicit review/confirmation, the same administrator and
cloud identity, current authorization, and target state still matching the
verified After snapshot. It restores only that captured target, not the entire
effective authorization graph. Other grants and workload changes can invalidate
rollback.

GCP etags provide version checks. Azure/AWS read-before-write checks are **not
atomic transactions**: another administrator can race the write. Serialize changes
operationally and verify afterwards. Semantic comparison ignores provider etags,
deny-policy output metadata and Azure principalType enrichment, while preserving
conditions and permission values. Equivalent ordering of policy arrays/keys does
not constitute drift.

## Local audit and recovery limits

Transitions, organization/actor IDs, cloud identity, reason and before/after JSON
are flushed to `%LOCALAPPDATA%\YASK-CIEM\cloud-administration`.
Windows directory ACLs restrict normal access to the current user. Policy command
files use its private `staging` subdirectory and are deleted after execution; a
process crash can leave a staging file for manual cleanup.

The journal is **not encrypted, centralized, immutable or tamper-proof**. Do not
put credentials/secrets into policy documents or reasons. Approval journal failure
blocks dispatch. Failure after dispatch is an unknown outcome; preserve the
session and audit evidence. Local audit files cannot authorize replay. Plans are
session-only: after restart, use provider-side inspection and create a fresh
reviewed plan; automatic journal recovery is not implemented.

## Acceptance and Priority 1 status

Development validation uses simulated CLI responses, including all supported
operations, account/identity/authorization failures, drift, expiration, conditions,
etag forwarding, rollback, unknown-outcome reconciliation and UI consent. No
client endpoint or live cloud write was used. Before production use, test each
required operation and rollback in a client-controlled disposable cloud scope.

Complete data-plane ingestion, authoritative entitlement attribution and reliable
30/60/90-day used/unused classification remain prerequisites for automated removal.
Inbound SIEM, complete attack/lifecycle detection and approved SOAR remediation
remain separate pending milestones. See [security connectors](security-connectors.md)
for client-provisioned outbound integration and its actual capability limits.
