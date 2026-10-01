# Reporting and Scheduled Reports

Reporting uses immutable normalized snapshots to produce provider-neutral Executive, Technical, Identity, Entitlement, Risk, and Recommendation reports. Supported output formats are JSON, HTML, and an extensible CSV path.

The Executive report includes account and identity totals, privileged identities, critical/high findings, and effective-access decision distributions. Technical reports preserve normalized inventory and authorization evidence for audit workflows.

`ReportScheduler` identifies due schedules and advances Daily, Weekly, or Monthly cadence. Scheduling is deterministic and does not perform cloud discovery or notification delivery. External delivery channels can consume generated report content through the existing alerting/notification extension points.

Reports contain analytical data only. Credentials, tokens, keys, and secrets are never included.
