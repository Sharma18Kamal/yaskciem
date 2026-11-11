# Reporting and Scheduled Reports

Reporting uses immutable normalized snapshots to produce provider-neutral Executive Security, Technical, Identity, Entitlement, Identity Risk, Permission Usage, Toxic Combination, Attack Path, Remediation, Compliance Assessment, Audit History, Risk, and Recommendation reports. Supported outputs are JSON, CSV, HTML, XML, PDF, and XLSX. CSV is emitted as quoted section/record rows with spreadsheet-formula prefixes escaped; XLSX is a valid workbook with inline-string cells; PDF output is paginated.

Desktop's **Generate Report** first asks for a format, then a destination. The selected format controls both serialization and the saved extension; canceling either step creates no report. If normalizing a manually typed extension targets a different existing file, Desktop asks before replacing it. XML preserves the same category payload as JSON using a `Report` root, property elements, `Item` array entries, and a `kind` attribute on every element to distinguish objects, arrays, strings, numbers, booleans and null. Evidence text is XML-escaped.

The Executive report includes account and identity totals, privileged identities, critical/high findings, and effective-access decision distributions. Technical reports preserve normalized inventory and authorization evidence for audit workflows. The Compliance Assessment report contains a CIEM baseline snapshot and explicitly disclaims certification or attestation. Generated report executions are recorded in the local SQLite report history with category, format, UTC timestamp, filename, and success status.

`ReportScheduler` identifies due schedules and advances Daily, Weekly, or Monthly cadence. Scheduling is deterministic and does not perform cloud discovery or notification delivery. External delivery channels can consume generated report content through the existing alerting/notification extension points.

Reports contain analytical data only. Credentials, tokens, keys, and secrets are never included.
