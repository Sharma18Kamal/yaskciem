# Compliance and Audit

The Compliance module assesses evidence already collected by CIEM. It does not call cloud APIs or decide provider authorization. Controls consume read-only accounts, EffectiveAccess, AccessPaths, findings, identity governance records, and audit events.

Supported assessment profiles:

- CIEM baseline: read-only posture, evidence-backed access, high-risk visibility.
- Least privilege: fully evaluated allowed access and privileged access paths.
- Identity governance: explicit activity/status knowledge and external identity visibility.
- Access review: privileged-access evidence and audit-event availability.

Control results are `Pass`, `Fail`, `Partial`, or `NotAssessed`; evidence and gaps are retained for review. Missing data is disclosed rather than treated as a pass.

`AuditEventService` creates audit records while filtering sensitive metadata keys such as passwords, tokens, secrets, private keys, and credential keys. Audit configuration must never contain credentials.

The desktop Dashboard can export an organization-scoped compliance evidence bundle for CIEM Baseline, Least Privilege, Identity Governance, or Access Review. The ZIP contains a versioned `manifest.json`, a complete `assessment.json`, and a spreadsheet-safe `controls.csv` with control state, evidence, and gaps. The manifest identifies the organization and Entra tenant, records assessment and source-scan timestamps, and reports control counts. Account and evidence selection is enforced by the export workflow using the authenticated organization membership and stable provider plus external-account bindings; a Viewer cannot export.

The bundle is an evidence snapshot, not an audit certification or provider authorization decision. Existing organization membership and audit persistence remain local to the desktop profile; export permission is enforced by the current desktop organization's local role directory. Do not treat the bundle as centrally governed until organization state and authorization move behind a shared trusted service.
