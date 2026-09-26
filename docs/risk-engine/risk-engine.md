# Risk Engine Boundary

`CloudEntitle.RiskEngine` is an independent application-layer boundary for evidence-based CIEM risk analysis.

## Rules

- The risk engine consumes normalized domain data and produces findings and recommendations.
- It must not reference WPF, cloud SDKs, credential stores, or provider-specific clients.
- Rules are configurable and must distinguish confirmed evidence from potential or unknown activity.
- V1 ends at recommendations. Remediation is intentionally outside this boundary.
