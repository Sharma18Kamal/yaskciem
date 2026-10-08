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

The registry contains CIEM-001 through CIEM-017 for privileged identities, excessive permissions, wildcard permissions, broad scope, dormancy, external identities, service identities, unused entitlements, production administration, redundant paths, privileged groups, nested privilege, wildcard resources, long chains, unused role assignments, and exploitable identity-to-resource paths.

Rules emit `Confirmed`, `Potential`, `Unknown`, or `Unsupported` evaluation states. Missing activity remains Unknown, powerful access is not automatically called excessive, service identities are not inherently risky, and broad scope requires evidence. Each finding contains provider, principal/resource/effective-access/access-path references, evidence, category, severity, deterministic fingerprint, and lifecycle status. The exploitable-path rule joins principal characteristics and effective access to modeled paths plus public-exposure, sensitive-data, critical-asset, and workload-vulnerability signals. Signal timestamps and per-signal coverage affect `PriorityScore`; stale evidence lowers score and yields `Potential`, while missing or unknown coverage is included explicitly in evidence details. The desktop list is sorted by the quality-adjusted score.

## Lifecycle and Persistence

Findings use Open, Resolved, Accepted, and Ignored states. Fingerprints support exact deduplication and future scan reconciliation. The `AddRiskFindingLifecycle` migration persists evaluation state, category, rule version, fingerprint, timestamps, access-path references, and evidence. Secrets and credentials are never stored.

Rule failures are isolated and logged as diagnostics so one failed rule does not prevent other rules from running. Dormancy, path thresholds, and the signal freshness window are configurable through `RiskRuleOptions`. Resource-security signals and their coverage are optional inputs: provider scans do not yet populate public exposure, sensitive-data, critical-asset, or vulnerability signals, so missing input is shown as Unknown rather than inferred safe. AccessPath nodes explicitly say they are modeled from effective-access evidence and not observed attacker activity.
