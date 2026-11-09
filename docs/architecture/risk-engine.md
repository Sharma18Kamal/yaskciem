# Multi-Cloud CIEM Risk Engine

The Risk Engine consumes normalized entitlements, authoritative EffectiveAccess records, AccessPath graphs, principals, resources, roles, permissions, and activity metadata. It does not call cloud APIs or reimplement Azure, AWS, or GCP authorization.

```mermaid
flowchart TD
    A[Normalized Entitlements] --> D[Risk Engine]
    B[Effective Access] --> D
    C[Access Paths] --> D
    E[Activity Records] --> D
    F[Provider Evidence] --> D
    D --> G[Risk Rule Registry]
    G --> H[Privilege Rules]
    G --> I[Excessive Access Rules]
    G --> J[Scope Rules]
    G --> K[Identity Rules]
    G --> L[Dormancy Rules]
    G --> M[Access Path Rules]
    H --> N[Risk Findings]
    I --> N
    J --> N
    K --> N
    L --> N
    M --> N
    N --> O[Evidence]
    N --> P[Access Path]
    N --> Q[Dashboard]
    N --> R[Future Recommendation Engine]
```

## Rules and Accuracy

The registry contains CIEM-001 through CIEM-022 for privileged identities, excessive permissions, wildcard permissions, broad scope, dormancy, external identities, service identities, unused entitlements, production administration, redundant paths, privileged groups, nested privilege, wildcard resources, long chains, unused role assignments, exploitable identity-to-resource paths, workload identity lifecycle, toxic permission combinations, and stale credentials.

Rules emit `Confirmed`, `Potential`, `Unknown`, or `Unsupported` evaluation states. Missing activity remains Unknown, powerful access is not automatically called excessive, service identities are not inherently risky, and broad scope requires evidence. Each finding contains provider, principal/resource/effective-access/access-path references, evidence, category, severity, deterministic fingerprint, and lifecycle status. The exploitable-path rule joins principal characteristics and effective access to modeled paths plus public-exposure, sensitive-data, critical-asset, and workload-vulnerability signals. Signal timestamps and per-signal coverage affect `PriorityScore`; stale evidence lowers score and yields `Potential`, while missing or unknown coverage is included explicitly in evidence details. The desktop list is sorted by the quality-adjusted score.

`PermissionUsageAnalyticsService` summarizes supplied successful activity against effective permissions, including last-used time, exact action frequencies, and 30/60/90-day counts. A permission is marked unused only when activity coverage spans the whole window and includes the attribution fields required by the grant; incomplete coverage is reported as Unknown. The service computes over existing `ActivityRecord` and `ActivityEvidenceCoverage` inputs; it does not collect cloud audit events itself, and an absent event is not evidence of unused access unless coverage is complete.

The separate [on-demand audit collector](continuous-monitoring.md#on-demand-cloud-activity-collection) now reads CloudTrail event history, Azure Activity Log and GCP Audit Logs into scoped shared snapshots and displays observed action frequencies. These partial, client-uploaded observations are not yet an authoritative attribution bridge into every entitlement. The calculator rejects missing permission references, treats blank permission actions as Unknown, and suppresses unused conclusions when successful activity lacks the principal/role/resource attribution needed by the grant.

`CIEM-021` evaluates configurable toxic permission combinations as review-only findings. The default rules cover sensitive-storage reads plus public-access control, critical-database access plus IAM modification, and credential creation plus administrative privilege. Rules may constrain provider, resource sensitivity, scope overlap, privilege, and a required trust path. Same-account permission pairs must belong to the same principal and provider; cross-account or cross-cloud findings require an explicit, evidence-backed trust path, a target privileged permission, and matching principal/account/provider identifiers. Scope, conditions, and incomplete effective-access evaluation reduce confidence; a known-unsatisfied condition suppresses the candidate. A finding remains Potential and does not claim that a provider-side authorization or exploit path has been proven.

Trust evidence retains each path edge's source and target identities, providers/accounts, relationship type/status, evidence source/reference, source-permission requirement, condition evidence, and observation time. Existing verified federation metadata can be mapped to trust edges, and callers may provide provider-verified trust edges. Native AWS role-assumption, Azure delegation/managed-identity, and GCP impersonation adapters are not yet wired into the live scan graph; inferred federation metadata is not a replacement for provider-side verification.

Condition evaluation is three-valued (`Satisfied`, `NotSatisfied`, `Unknown`). The AWS JSON adapter currently normalizes StringEquals/StringNotEquals, StringEqualsIgnoreCase, StringLike/StringNotLike, ARN Equals/NotEquals/Like/NotLike, Bool, NumericEquals, NumericGreaterThan, and NumericLessThan. Unsupported operators or malformed/incomplete evidence remain Unknown; set qualifiers, IfExists operators, and other AWS policy semantics are not evaluated. Provider-specific Azure and GCP condition expressions require normalization adapters; absent condition expressions or evaluation context are Unknown, never treated as satisfied. The current entitlement evidence contract retains only a condition-present flag, so complete live condition evaluation is limited until collectors preserve the original expressions and applicable request context.

Custom rules can be previewed against the local normalized inventory, then created through the Toxic Combinations workspace. Rule creation/change records before/after rule JSON, version, actor attribution when available, and timestamp through the existing audit-event store. Preview is a UI workflow guard, not a transaction-level prerequisite for direct service callers. Findings and recommendations remain non-executable; this workflow does not modify cloud permissions.

`CIEM-022` reports an expired or over-age credential only when the credential inventory is explicitly complete; credential age defaults to 90 days through `RiskRuleOptions.MaxCredentialAgeDays`. Incomplete credential inventory does not imply that credentials are safe or stale.

## Lifecycle and Persistence

Findings use Open, Resolved, Accepted, and Ignored states. Fingerprints support exact deduplication and future scan reconciliation. The `AddRiskFindingLifecycle` migration persists evaluation state, category, rule version, fingerprint, timestamps, access-path references, and evidence. Secrets and credentials are never stored.

Rule failures are isolated and logged as diagnostics so one failed rule does not prevent other rules from running. Dormancy, path thresholds, and the signal freshness window are configurable through `RiskRuleOptions`. Live scans now preserve Azure resource tags and selected network properties (`publicNetworkAccess`, `allowBlobPublicAccess`, and `networkAcls.defaultAction`), GCP Cloud Asset labels, and AWS Resource Groups Tagging API inventory, then map explicit exposure, data-classification, critical-asset, and CVE/workload-vulnerability hints into typed signals. AWS inventory requires `tag:GetResources` and includes tagged resources only. Tag/label signals are unconfirmed and coverage is `Partial`; they are useful leads, not substitutes for Defender for Cloud, Inspector, Security Command Center, or a data-classification scanner. Native cloud vulnerability findings are not yet queried. Missing or unavailable provider data is not treated as a safe result.

Kubernetes workload-trust mismatches are represented separately as `WorkloadIdentityRisk` signals; verified, denied, unavailable, unsupported, and not-configured checks retain distinct coverage states. The live scan builds resource paths from provider-reported role assignments and in-scope resources, then runs the existing `CIEM-017` attack-path rule. These role-level edges are deliberately `PartiallyEvaluated` and findings remain `Potential`; action-level authorization and runtime conditions are not inferred. Signal nodes, coverage, modeled paths, and graph findings are persisted with the seven-day scan history and shown in Access Paths/Risk Findings. AccessPath nodes continue to distinguish modeled access evidence from observed attacker activity.
