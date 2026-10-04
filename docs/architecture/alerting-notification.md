# Alerting and Notification

Alerting converts high-signal CIEM events into deduplicated, evidence-backed alerts. Sources include open high/critical risk findings, newly added EffectiveAccess drift, failed compliance controls, and privileged identities with unknown activity evidence.

Alerting uses configurable minimum severity and source toggles. Stable fingerprints suppress duplicate notifications. Messages include evidence but never credentials or secrets.

Notification channels are represented as pluggable types (`InProcess`, `Log`, `Webhook`). The desktop live-scan path persists seven-day posture snapshots, computes trends and scan-to-scan access/finding changes, and can deliver newly observed privileged access and high/critical findings through an HTTPS webhook. The first scan establishes a baseline without sending an initial-state alert flood.

Configure delivery by setting the current user's environment variable `YASK_CIEM_ALERT_WEBHOOK_URL` to a trusted HTTPS webhook URL, then restart YASK-CIEM. Do not commit the URL or share it in diagnostics. The URL is never written to scan history or logs, and non-HTTPS endpoints are rejected. Delivery failures and unconfigured alerts remain pending in local scan history and are retried on the next scan. Successful delivery is recorded with the scan; webhook response bodies are not logged.

Alerting is analytical. It does not mutate cloud resources, approve remediation, disable identities, or make authorization decisions.
