# Compliance and Audit

The Compliance module assesses evidence already collected by CIEM. It does not call cloud APIs or decide provider authorization. Controls consume read-only accounts, EffectiveAccess, AccessPaths, findings, identity governance records, and audit events.

Supported assessment profiles:

- CIEM baseline: read-only posture, evidence-backed access, high-risk visibility.
- Least privilege: fully evaluated allowed access and privileged access paths.
- Identity governance: explicit activity/status knowledge and external identity visibility.
- Access review: privileged-access evidence and audit-event availability.

Control results are `Pass`, `Fail`, `Partial`, or `NotAssessed`; evidence and gaps are retained for review. Missing data is disclosed rather than treated as a pass.

`AuditEventService` creates audit records while filtering sensitive metadata keys such as passwords, tokens, secrets, private keys, and credential keys. Audit configuration must never contain credentials.
