# Alerting and Notification

Alerting converts high-signal CIEM events into deduplicated, evidence-backed alerts. Sources include open high/critical risk findings, newly added EffectiveAccess drift, failed compliance controls, and privileged identities with unknown activity evidence.

Alerting uses configurable minimum severity and source toggles. Stable fingerprints suppress duplicate notifications. Messages include evidence but never credentials or secrets.

Notification channels are represented as pluggable types (`InProcess`, `Log`, `Webhook`). The current implementation creates notification messages only; external delivery adapters can be added later with secure credential handling and delivery retry policies.

Alerting is analytical. It does not mutate cloud resources, approve remediation, disable identities, or make authorization decisions.
