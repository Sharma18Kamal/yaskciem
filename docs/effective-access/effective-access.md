# Effective Access Evaluation

`CloudEntitle.EffectiveAccess` evaluates normalized provider evidence against a selected principal, resource, and action. `ExplainAsync` returns a decision, evaluation status, diagnostics, source evidence, and one or more structured access paths. `EvaluateAsync` associates those paths with each effective-access record.

Paths preserve the available chain, for example:

`User -> Group -> Assumed role -> Policy -> Permission -> Scope -> Resource`

The evaluator currently supports account-scoped and unexpired direct/inherited group membership, optional role-trust relationships, hierarchical Azure and GCP scopes, AWS ARN/wildcard resource matching, and provider-specific explicit-deny handling. Azure `NotActions` and `NotDataActions` remain role-scoped exclusions; they do not override grants from other roles. AWS explicit denies override applicable allows. GCP inherited bindings apply only at or below their recorded scope.

Conditions are never assumed true. A small, explicit subset can be evaluated when matching runtime attributes are supplied: equality/inequality comparisons for normalized claims and GCP `resource.name.startsWith(...)`. Other Azure, AWS, and GCP condition expressions remain `Conditional` with their original evidence in the path. AWS role trust is evaluated only when trust relationships are provided in the request.

## Conditional-decision acceptance

Conditional allow/deny effects are kept distinct during normalization, including
when the principal, permission, scope and expression are identical. Reordering
those records must not change deny precedence or discard the opposite effect.
Conditional entitlement IDs include the effect; re-normalizing old snapshots can
therefore change those IDs. Existing unconditional entitlement IDs are unchanged.
This fix does not reconstruct evidence already discarded in an older snapshot;
recollect or reimport the original records before evaluating that case again.

A condition object, conditional flag or conditional access type prevents a record
from being treated as an unconditional grant/deny. A matching supported condition
applies its original effect; a false condition makes that record not applicable.
Missing context or unsupported syntax leaves it conditional. An independent allow
with an unresolved conditional deny remains a **partially evaluated model Allow**,
not proof that the cloud provider permits the operation.

The supported grammar is deliberately small: one claim-to-literal `==` or `!=`
comparison, or GCP `resource.name.startsWith` with a quoted literal prefix.
Compound expressions, function arguments that are expressions, escaped string
literals and full provider condition languages are not evaluated. For normalized
comparisons, the existing Azure/AWS case-insensitive comparison and GCP
case-sensitive comparison remain model behavior, not an emulation of every
provider-native comparison operator.

Explanations retain the condition, source and model-context outcome. Diagnostics
identify `CONDITION_MATCHED`, `CONDITION_NOT_MATCHED` or `CONDITION_REQUIRED`.
Per-record paths describe contributions; an Allow path does not override an
applicable explicit Deny in the final decision. Persisted evaluation details
retain diagnostics and paths for subsequent inspection.

Run the offline acceptance/regression suites from the repository root:

```powershell
dotnet test .\tests\CloudEntitle.Normalization.Tests\CloudEntitle.Normalization.Tests.csproj --no-restore
dotnet test .\tests\CloudEntitle.EffectiveAccess.Tests\CloudEntitle.EffectiveAccess.Tests.csproj --no-restore
```

These are synthetic normalization/model tests, not live API or installed-package
acceptance. The matrix covers conditional Allow/Deny effects, raw and normalized
conditional representations, matching/nonmatching/missing context across
Azure/AWS/GCP, supported/unsupported grammar, deny precedence and persisted
partial-result explanations. Existing what-if tests run in the same engine suite.

## Coverage and Limitations

An `Allow` can be marked partially evaluated when normalization failed, a policy record is unsupported, or unresolved conditional evidence could change the result. Unrecognized Azure deny records are not promoted to global denies; they produce incomplete/unknown coverage instead.

This is not yet a complete cloud-provider authorization simulator. Full Azure condition evaluation and deny-assignment scopes, AWS permissions boundaries, SCPs, session policies and all resource-policy interactions, and GCP IAM Deny policies and principal access boundaries require richer provider-native policy records and evaluators. Live CLI scans currently expose display-oriented access summaries rather than the normalized principal-to-policy-to-permission evidence used by this engine, so the desktop simulator remains Demo Mode only for now. Unsupported or missing evidence must not be presented as a definitive provider-side denial or grant.

If activity data is unavailable, access must remain unknown/potential rather than being reported as confirmed unused access.
