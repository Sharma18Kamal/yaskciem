# Native IAM decision transition capture

This feature **does not relabel policy keywords, assignment changes, activity bursts or simulators as confirmed escalation**. Existing policy/role observations remain Potential.

`ConfirmedDecisionEscalation` means only that directly collected GCP IAM decisions changed **NOT_GRANTED → GRANTED** for the same configured source/account/actor/principal/permission/full resource/captured context, with complete native allow/deny/PAB explanations, changed provider policy versions and nonoverlapping bounded observations. It does not establish causal exploitation, uninterrupted historical access, access to other actions, or successful service execution. Credentials, group membership and enforcement propagation can differ between observations; this is not a causal IAM-change attribution.

## Supported native scope

The first profile intentionally supports only `resourcemanager.projects.setIamPolicy` on the exact bound GCP project. The collector calls these fixed public endpoints directly, with server-owned OAuth:

1. `GET https://www.googleapis.com/oauth2/v2/userinfo`: the verified email and immutable OAuth subject ID must exactly match the configured collector identity.
2. `GET https://cloudresourcemanager.googleapis.com/v3/projects/{projectId}` and read-only parent-folder lookups: verify the active project identity and bounded resource hierarchy.
3. `POST https://policytroubleshooter.googleapis.com/v3beta/iam:troubleshoot`: this POST is an authorization **read/evaluation**, not a policy write. It sends one exact principal email, permission and full resource name, no fabricated historic condition context.

The provider's `overallAccessState` is used: `CAN_ACCESS` maps to GRANTED, `CANNOT_ACCESS` to NOT_GRANTED. Individual allow bindings cannot override the overall decision. Every expected ancestor must appear in both allow and deny explanations; visible policies need etags; all applicable PAB explanations must be present. Any missing/unknown component, provider error, duplicate JSON property, conditional binding/rule, missing policy version, mismatched tuple or nonempty unsupported context yields Partial/UNKNOWN and can never confirm. Conservative completeness checks may reject valid provider results; their exact blockers are retained rather than manufacturing evidence.

Stable **v3 evaluates allow and deny only**; **v3beta adds Principal Access Boundary policies (Preview)**. An administrator must accept that preview explicitly. v1 allow-only responses are insufficient. The v3beta schema and source verification profile are pinned; response format changes fail closed.

### Native fields versus application provenance

The native success response has `overallAccessState`, `accessTuple`, `allowPolicyExplanation`, `denyPolicyExplanation` and `pabPolicyExplanation`. It does **not** have an `authoritative`, `coverageComplete`, global policy epoch, historical evaluation timestamp or signed provenance field. `CompleteVerified` and the verification profile are local conservative gate results, not Google-issued completeness certificates. Snapshot UUIDs, capture interval timestamps, observed-context timestamp, source/configuration hash and response SHA-256 are also server-created provenance. Policy etags and PAB enforcement versions come from the returned provider policies.

Concretely, allow explanations contain `explainedPolicies[].fullResourceName`, `policy.etag`, `policy.bindings` and ordered `bindingExplanations`. Deny explanations contain `explainedResources[].fullResourceName`, `explainedPolicies[].policy` (IAM v2 `Policy`, including `name`, `etag`, `rules`) and ordered `ruleExplanations`. PAB explanations contain `principalAccessBoundaryAccessState`, `explainedBindingsAndPolicies[].explainedPolicyBinding.policyBinding` (`name`, `etag`), `explainedPolicy.policy` (`name`, `etag`, `details.rules`) and `explainedPolicy.policyVersion` (`version`, `enforcementState`). There is no native PAB “complete” boolean; unavailable policies/roles are represented by omitted visibility fields and UNKNOWN state enums.

Tests use schema-shaped synthetic provider responses—not a live capture or proof that every project returns those shapes. Fixtures include real documented allow/deny/PAB property names, enum values and policy etags; they deliberately include explicit empty repeated fields. Protobuf JSON may omit empty arrays, and full resource aliases may differ between numeric project IDs and configured project IDs. This implementation rejects missing required explanation arrays/ancestor matches as Partial rather than assuming absence or rewriting provider scope. Such conservative false negatives are intentional, and live compatibility is not claimed without a consented client-native capture.

Successful troubleshoot response schemas do not define generic top-level `errors`. Error-path tests inject that field as an invalid/ambiguous provider response and verify rejection; actual transport errors return non-2xx and are rejected before capture. Documented UNKNOWN states and missing conditional context are separately tested.

Policy Troubleshooter does not account for Cloud Storage ACLs or VPC Service Controls, so this profile excludes Storage and makes no operational-access claim. Azure/AWS simulator/policy data cannot satisfy full native provenance and is Unsupported. No historical policy replay or all-action scan is claimed.

## Explicit client-owned setup and consent

Apply `infra\postgres\014-authoritative-decisions.sql` after migration 013 using the DBA identity. The runtime only receives SELECT/INSERT on append-only decision snapshots and analyses. UPDATE, DELETE and TRUNCATE are blocked, including cascade truncation. Wire the embedded resource as `CloudEntitle.LicenseServer.AuthoritativeDecisionSchema.sql`.

Client administrators configure their source in the license server's `AuthoritativeDecisions:Sources` section, scoped to the Entra CIEM tenant, shared organization and already bound GCP project:

```json
{
  "AuthoritativeDecisions": {
    "Sources": [
      {
        "TenantId": "11111111-1111-1111-1111-111111111111",
        "OrganizationId": "22222222-2222-2222-2222-222222222222",
        "SourceId": "native-gcp",
        "Provider": "Gcp",
        "AccountId": "sample-project-123",
        "CollectorIdentity": "collector@example.invalid",
        "ExpectedOAuthSubjectId": "123456789012345678901",
        "OAuthTokenEnvironmentVariable": "YASK_GCP_DECISION_TOKEN",
        "Enabled": true,
        "AcceptGcpPreview": true,
        "Principals": ["alice@example.invalid"],
        "PrivilegedPermissions": ["resourcemanager.projects.setIamPolicy"]
      }
    ]
  }
}
```

The token itself is **not configuration JSON**, not a Desktop input and not an evidence-upload field. Supply an expiring OAuth access token to the protected server process environment using client-controlled secret delivery. Obtain it out-of-band through an approved OAuth workflow with `cloud-platform` and `userinfo.email` scopes; refresh it out-of-band before expiry. Obtain the expected collector's immutable OAuth subject ID out-of-band and configure it along with the email; a recycled email with a different subject is rejected. Automatic credential acquisition/refresh is deliberately not implemented. No token-acquisition CLI stdout is routed through Desktop logging. The dedicated HttpClient disables redirects, cookies and request logging; credentials are never persisted in snapshots or returned by the API. Native reads have a 30-second timeout, 1 MiB per-response bound, 12-level hierarchy bound and one concurrent capture per configured source.

Enable Policy Troubleshooter out-of-band. Grant only necessary review access: Security Reviewer and Deny Reviewer on the resource organization, ancestor read access, the PAB review/binding permissions on the principal's organization, and Workspace group/domain review rights when needed. Missing access is a blocker, not negative authorization evidence. This application does not enable APIs, grant roles or mutate cloud resources.

In Desktop, open **Authoritative IAM decision transitions**, load configured connections explicitly, select the exact configured sample, acknowledge one-shot read consent, then **Capture and save**. No provider request happens on application startup, sign-in, connection listing, history refresh or saved analysis. Every capture consumes consent. Source, principal, binding, identity, organization or permission changes cancel pending UI work and clear consent/results; late completions are discarded. Cancellation cannot undo an immutable capture already completed on the server.

## API and confirmation gate

`AuthoritativeDecisions.http` demonstrates all routes under `/api/v1/organizations/{id}/effective-decisions`. All require authenticated `CiemApiAccess` plus inventory/finding permission; capture and analysis additionally require ManagePolicies and an exact organizational account binding. No upload route exists. Request bodies cannot set native decisions, policy epochs, verification profiles or authoritative booleans.

Saved observations include server timestamps, collector identity, configuration hash, canonical full provider context hash (including fetched effective tags), response SHA-256 and provider policy etags. Raw native bodies stay server-side and are re-evaluated before comparison; client-facing history returns projections only. Both contexts must have been observed during their own reads, and hashes must match. The after observation must be no older than five minutes at initial analysis, the pair no more than 24 hours apart, and capture intervals strictly nonoverlapping. The configuration identity/revision, exact sample and client privileged-action allowlist must match. Changed provider etags are necessary, but **not** asserted to prove causality.

Analysis is saved immutably and deduplicated by source/before/after pair. A confirmed pair emits one `privilege-escalation.confirmeddecision` operational event. Reissuing an old confirmed analysis under newly stale/changed comparison conditions returns conflict rather than fabricating a fresh confirmation; its original immutable report remains available through GET.

Responses with inadequate native coverage are still useful saved captures, but analysis returns Potential or Inconclusive with exact blockers. Failed identity, scope, transport, timeout or oversized reads create no snapshot and never confirm.

## Verification

Parent-controlled tests (no live provider calls):

- Integration filter: `FullyQualifiedName~AuthoritativeDecision`
- Desktop filter: `FullyQualifiedName~AuthoritativeDecision`
- PostgreSQL integration additionally requires `YASK_CIEM_TEST_POSTGRES`.

Tests cover native collection/provenance, complete allow+deny+PAB responses, unknown/error/condition/visibility/ancestor/tuple failures, canonical context, source/epoch/time/allowlist gates, denied→granted persistence, deduplication and append-only protections, plus consent/reset/cancellation/late-result behavior. No cloud writes or live cloud tests are required.

## Official semantics verified

- [GCP v3beta IAM troubleshoot response](https://docs.cloud.google.com/policy-intelligence/docs/reference/policytroubleshooter/rest/v3beta/iam/troubleshoot): full overall access state, PAB explanation and policy version schema.
- [GCP v3 IAM troubleshoot response](https://docs.cloud.google.com/policy-intelligence/docs/reference/policytroubleshooter/rest/v3/iam/troubleshoot): allow/deny-only response; UNKNOWN_INFO and UNKNOWN_CONDITIONAL.
- [Troubleshoot access guide](https://docs.cloud.google.com/policy-intelligence/docs/troubleshoot-access): v3 versus v3beta/PAB, prerequisites, group/domain visibility, supported principals and ACL/VPC Service Controls exclusions.
