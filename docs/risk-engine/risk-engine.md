# Risk Engine Boundary

`CloudEntitle.RiskEngine` is an independent application-layer boundary for evidence-based CIEM risk analysis.

## Rules

- The risk engine consumes normalized domain data and produces findings and recommendations.
- It must not reference WPF, cloud SDKs, credential stores, or provider-specific clients.
- Rules are configurable and must distinguish confirmed evidence from potential or unknown activity.
- V1 ends at recommendations. Remediation is intentionally outside this boundary.

## Usage-Based Least Privilege

Usage findings require an `ActivityEvidenceCoverage` record for the same provider and account. Coverage declares the observed time window and whether action names, resource IDs, and role IDs were collected. The default usage observation window is 30 days.

- `Unused Entitlement` is confirmed only when the complete window contains no successful matching action for the principal, permission pattern, resource, and (when available) role. Partial or missing attribution stays Unknown; unsupported activity sources stay Unsupported.
- `Unused Role Assignment` is confirmed only with complete role-ID coverage and no successful event for that role. Activity from a different role does not count as use.
- `Excessive Permission` uses a complete window before interpreting missing write/delete activity; absent or incomplete coverage yields Unknown.
- Least-privilege policy documents are generated only from successful, role-attributed action/resource observations with complete coverage. AWS IAM policy JSON, Azure custom-role definitions, and GCP custom-role definitions are review-only proposals; no cloud mutation is performed.

The current live CLI connectors do not yet emit complete per-action `ActivityRecord` data or `ActivityEvidenceCoverage`. AWS role last-used timestamps are role-level evidence and are not treated as permission-level activity. Until a connector supplies complete coverage and attribution, unused findings remain Unknown and policy synthesis is withheld.

## Exploitable Attack-Path Prioritization

Callers can provide `ResourceSecuritySignal` records on `RiskEvaluationRequest`, keyed by the normalized resource ID. Each record carries its source, confirmation state, severity, and optional exploitability/CVSS evidence. Supported signal types are public exposure, sensitive data, and workload vulnerability. Confirmed signals are appended as typed nodes to access paths when supplied to `AccessPathRequest`.

`ExploitableAttackPathRule` correlates those resource signals with an allowed effective-access record and its access path. It considers privileged/external identities, non-human identity types, and administrative/write/delete capabilities. It records the linked access path, signal sources, and a capped 0-100 priority score. Score points come from privileged access (25), external identity (20), non-human identity (20), dangerous permission (10), public exposure (25), sensitive data (20), vulnerability severity/exploitability (up to 35), and path completeness (up to 10). Scores map to Low/Medium/High/Critical thresholds at 30/40/60/80, and risk results are ordered by score then severity.

The current CLI connectors do not yet populate vulnerability, data-classification, or confirmed public-exposure signals. Until those inventory integrations supply evidence, the rule will only fire from explicit resource tags or caller-provided signals; it does not infer CVEs or data sensitivity from resource names.

## Dashboard Evidence Quality

The Dashboard displays elapsed time since the latest completed live scan, successful source-query rows (`Collected` or `NoData`) versus all coverage rows, and incomplete-source status counts. Unsupported permission sources count only `Unsupported` coverage records whose source identifies permission, effective-access, or action-level evaluation; this is a source-gap count, not a count of individual permission objects.

Attribution confidence is the percentage of effective-access assignments with both a provider change timestamp and actor. Partial attribution is shown separately. Unknown and Unsupported evaluation totals combine resource-risk findings with modeled access paths in those states; they are not added to severity-based risk totals. A successful `NoData` query is distinct from Denied, Unavailable, Unsupported, or Skipped coverage.
