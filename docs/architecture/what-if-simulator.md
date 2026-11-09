# What-if permission simulation

Open **What-if Simulator** in Desktop. This is separate from the existing
single-tuple Demo Mode permission check and structural remediation dry-run.
It reuses the AWS/Azure/GCP authorization evaluators and existing risk,
toxic-combination and attack-path engines, but never approves remediation or
calls a cloud API.

1. Refresh and select a saved local account, then **Load saved inventory**; or
   **Import offline JSON** with the schema below.
2. Select an operation, target type, identity and/or role/policy, and an exact
   provider permission. For replacement, supply the replacement action too.
   A direct identity addition requires one exact inventoried resource scope.
3. Run the isolated simulation. Role/policy changes include all inventoried
   assignments of that target; identity-target changes apply only to direct
   grants. Additions to roles/policies reuse only explicitly inventoried
   assignment scopes and retain their conditions.
4. Review changed identity/resource/action tuples, source evidence, inheritance,
   deny/condition diagnostics, risk findings, toxic combinations and attack paths.
5. Export a JSON report. It contains hypothetical evidence and is not an
   executable policy or provider change request.

For `Microsoft.Storage/storageAccounts/listKeys/action`, results include only
inventoried storage-account resources, not VMs or arbitrary descendants. The
report shows affected identities/resources, inheritance, before/after model
decisions, lost or uncertain access and retained independent grants. Known
storage keys, sessions and SAS tokens are not revoked by a modeled removal.
Different actions such as regenerating keys are **not** automatically considered
equivalent or operationally safe replacements.

## Prediction boundaries

-   `ModeledAccessLost` / `ModeledAccessAdded` mean the offline model changed its
  decision for the selected action. They are **not** provider-confirmed denial or
  grant.
- `RetainedByAlternative` requires a remaining grant for the identical action,
  principal and resource with fully evaluated model decisions.
- Missing source coverage, normalization problems and unresolved conditions
  produce potential/inconclusive impacts. Saved local inventory does not retain
  complete native policy/condition semantics and therefore remains partial.
- The removal-only reduction percentage is lost modeled pairs divided by
  previously allowed **affected target** pairs, not a global risk score,
  exploit probability or count of all cloud resources. It is Unknown for
  uncertain results.
- The risk comparison reruns existing risk rules on the before/after snapshots.
  The displayed score is the maximum finding priority score, not a sum or
  calibrated probability. Toxic-combination rules and attack paths are rerun
  using the same normalized snapshot and trust evidence.
- Full risk/toxic/path recalculation is bounded. If the scenario exceeds the
  analysis work budget, no before/after security score is reported. Azure
  action removal from wildcard statements is limited to the queried action;
  full risk/toxic/path comparison is withheld because the current evaluator
  cannot preserve unrelated wildcard actions during exclusion.
- Operational dependencies are Unknown unless activity/usage evidence is
  supplied. Affected access pairs are shown, but the simulator does not infer
  workload dependency or certify that a permission is safe to remove.
- Only inventoried identities, memberships and resources are considered. Resource
  scope matching outside the explicit Azure listKeys profile is a model candidate,
  not proof that a provider supports that action on that resource type.
- AWS SCPs/boundaries/session policies, GCP PAB, ACLs, federated sessions and full
  provider condition languages are not comprehensively modeled. Unsupported
  semantics must be included in the bundle's limitations.
- AWS/GCP wildcard action subtraction is rejected: an incomplete action universe
  cannot safely generate a replacement allow list. No global Deny is substituted.
  Azure wildcard contribution subtraction is modeled **only for the queried
  action**; no unrelated actions are removed and no role is edited. Actual
  NotActions edits, multi-block semantics and built-in/custom-role restrictions
  require a separate reviewed policy-generation workflow.
- Role assumption/impersonation chains are not accepted by this profile.
- Inputs and local inventory remain unchanged. Report fingerprints identify the
  exact offline input; they do not attest its origin or completeness.

## Offline export bundle (schema 1.0)

The importer accepts a **single-account authorization projection**, not an
unadapted CloudTrail log, standalone role JSON or arbitrary multi-cloud backup.
Prepare this format locally from exported AWS policy statements, Azure role
definitions/assignments or GCP role permissions/IAM bindings. Include the
principal, role/policy, permission, assignment scope and actual resource
inventory with stable matching IDs. Copy original policy statements/documents
into their evidence fields; never include credentials, tokens, key values or
unrelated confidential fields. No live credentials or internet connection are
needed to import or analyze the resulting bundle.

The root fields are `SchemaVersion`, `SourceLabel`, `Discovery`, `Resources`,
`GroupMemberships` and `Limitations`. Discovery uses the existing
`ProviderDiscoveryResult` contract with Accounts, Principals, Roles, Policies,
Permissions, AuthorizationRecords and CollectedAt. String enum names are
required. Omitted/unknown collection coverage belongs in Limitations; an empty
list means only that the client supplied no additional model limitations,
**never** that provider completeness was independently verified.

Minimal Azure listKeys example:

```json
{
  "SchemaVersion": "1.0",
  "SourceLabel": "Client-exported Azure example",
  "Discovery": {
    "Provider": "Azure",
    "Accounts": [
      { "Id": "11111111-1111-1111-1111-111111111111", "CloudProvider": "Azure" }
    ],
    "Principals": [
      { "Id": "22222222-2222-2222-2222-222222222222", "AccountId": "11111111-1111-1111-1111-111111111111", "CloudProvider": "Azure", "Name": "Storage analyst", "IdentityType": "User" }
    ],
    "Roles": [
      { "Id": "33333333-3333-3333-3333-333333333333", "AccountId": "11111111-1111-1111-1111-111111111111", "Name": "Client storage role" }
    ],
    "Policies": [],
    "Permissions": [
      { "Id": "44444444-4444-4444-4444-444444444444", "AccountId": "11111111-1111-1111-1111-111111111111", "Name": "List storage keys", "Action": "Microsoft.Storage/storageAccounts/listKeys/action", "AccessType": "Allow" }
    ],
    "AuthorizationRecords": [
      {
        "SourceId": "exported-role-assignment-1",
        "SourceName": "Client storage role",
        "CloudAccountId": "11111111-1111-1111-1111-111111111111",
        "PrincipalId": "22222222-2222-2222-2222-222222222222",
        "RoleId": "33333333-3333-3333-3333-333333333333",
        "PermissionId": "44444444-4444-4444-4444-444444444444",
        "Scope": "/subscriptions/example",
        "ResourcePattern": "*",
        "AssignmentType": "Role",
        "AccessType": "Allow",
        "SourceType": "AzureRoleDefinition",
        "OriginalStatement": "Client-observed actions include Microsoft.Storage/storageAccounts/listKeys/action",
        "IsInherited": true,
        "SourceScope": "/subscriptions/example"
      }
    ],
    "CollectedAt": "2026-11-08T12:00:00Z"
  },
  "Resources": [
    { "Id": "55555555-5555-5555-5555-555555555555", "AccountId": "11111111-1111-1111-1111-111111111111", "Name": "Storage", "Type": "Microsoft.Storage/storageAccounts", "ResourceId": "/subscriptions/example/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/data" }
  ],
  "GroupMemberships": [],
  "Limitations": ["Example does not include native deny assignments or group inventory."]
}
```

For AWS use Provider `Aws`, exact actions such as `s3:GetObject`, resource ARN
patterns, explicit effects and policy/role IDs. For GCP use Provider `Gcp`,
exact permissions such as `storage.objects.get`, native project/folder scope and
condition expressions when present. Native deny source types must retain their
provider meaning; a generic deny-labelled record is not universal deny proof.

Imports reject duplicate JSON fields, foreign-account references, invalid IDs,
normalization errors and unsupported schema fields rather than returning an
empty successful analysis. Bounds: 8 MiB JSON, 20,000 permission/authorization
records, 10,000 principal/resource pairs and 10-million grant/pair and
normalization/reference work budgets.
Split large exports explicitly; no successful truncation is performed.

In organization mode, current inventory/finding permissions and matching
provider/account bindings are required. Imported internal account IDs must match
the bound inventory IDs. Identity/organization/account/action changes discard
late results. Standalone mode analyzes the local user's selected files only.

The simulation does not generate provider policy documents, approve or apply
changes, guarantee operational safety, or implement provider-complete condition
evaluation. No simulated data is persisted as authorization state.
