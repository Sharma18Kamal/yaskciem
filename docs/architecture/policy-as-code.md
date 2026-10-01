# Policy-as-Code and Custom Rules

Custom risk rules extend the existing Risk Engine without creating a second authorization evaluator. Rules consume the same EffectiveAccess, normalized permission, principal, resource, entitlement, AccessPath, and provider evidence context as built-in rules.

Example JSON:

```json
[
  {
    "ruleId": "CUSTOM-001",
    "name": "Privileged production wildcard",
    "description": "Review privileged wildcard actions reaching production resources.",
    "severity": "High",
    "category": "ExcessiveAccess",
    "enabled": true,
    "actionContains": "*",
    "requiresPrivilegedAccess": true,
    "recommendation": "Review narrowing the action and resource scope."
  }
]
```

Rules must use IDs beginning with `CUSTOM-`, have a name and criteria, and are rejected when duplicate or empty. Supported criteria include provider, permission name, action/resource/scope text, privileged identity, external identity, and service identity. Evaluation state remains explicit and findings retain the normal evidence chain and deterministic fingerprint.

Policy files are configuration input only. They cannot invoke cloud APIs, mutate permissions, access credentials, or bypass the built-in evidence and false-positive safeguards.
