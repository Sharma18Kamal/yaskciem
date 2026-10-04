# Effective Access Evaluation

`CloudEntitle.EffectiveAccess` evaluates normalized provider evidence against a selected principal, resource, and action. `ExplainAsync` returns a decision, evaluation status, diagnostics, source evidence, and one or more structured access paths. `EvaluateAsync` associates those paths with each effective-access record.

Paths preserve the available chain, for example:

`User -> Group -> Assumed role -> Policy -> Permission -> Scope -> Resource`

The evaluator currently supports account-scoped and unexpired direct/inherited group membership, optional role-trust relationships, hierarchical Azure and GCP scopes, AWS ARN/wildcard resource matching, and provider-specific explicit-deny handling. Azure `NotActions` and `NotDataActions` remain role-scoped exclusions; they do not override grants from other roles. AWS explicit denies override applicable allows. GCP inherited bindings apply only at or below their recorded scope.

Conditions are never assumed true. A small, explicit subset can be evaluated when matching runtime attributes are supplied: equality/inequality comparisons for normalized claims and GCP `resource.name.startsWith(...)`. Other Azure, AWS, and GCP condition expressions remain `Conditional` with their original evidence in the path. AWS role trust is evaluated only when trust relationships are provided in the request.

## Coverage and Limitations

An `Allow` can be marked partially evaluated when normalization failed, a policy record is unsupported, or unresolved conditional evidence could change the result. Unrecognized Azure deny records are not promoted to global denies; they produce incomplete/unknown coverage instead.

This is not yet a complete cloud-provider authorization simulator. Full Azure condition evaluation and deny-assignment scopes, AWS permissions boundaries, SCPs, session policies and all resource-policy interactions, and GCP IAM Deny policies and principal access boundaries require richer provider-native policy records and evaluators. Live CLI scans currently expose display-oriented access summaries rather than the normalized principal-to-policy-to-permission evidence used by this engine, so the desktop simulator remains Demo Mode only for now. Unsupported or missing evidence must not be presented as a definitive provider-side denial or grant.

If activity data is unavailable, access must remain unknown/potential rather than being reported as confirmed unused access.
