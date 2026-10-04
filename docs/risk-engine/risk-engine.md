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
