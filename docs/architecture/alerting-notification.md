# Alerting and Notification

Alerting converts high-signal CIEM events into deduplicated, evidence-backed alerts. Sources include open high/critical risk findings, newly added EffectiveAccess drift, failed compliance controls, and privileged identities with unknown activity evidence.

Alerting uses configurable minimum severity and source toggles. Stable fingerprints suppress duplicate notifications. Messages include evidence but never credentials or secrets.

Notification channels are represented as pluggable types (`InProcess`, `Log`, `Webhook`). The desktop live-scan path persists seven-day posture snapshots, computes trends and scan-to-scan access/finding changes, and can deliver newly observed privileged access and high/critical findings through an HTTPS webhook. The first scan establishes a baseline without sending an initial-state alert flood.

Configure delivery by setting the current user's environment variable `YASK_CIEM_ALERT_WEBHOOK_URL` to a trusted HTTPS webhook URL, then restart YASK-CIEM. Do not commit the URL or share it in diagnostics. The URL is never written to scan history or logs, and non-HTTPS endpoints are rejected. Delivery failures and unconfigured alerts remain pending in local scan history and are retried on the next scan. Successful delivery is recorded with the scan; webhook response bodies are not logged.

Alerting is analytical. It does not mutate cloud resources, approve remediation, disable identities, or make authorization decisions.

## Shared rules and durable delivery (P1.2)

In shared mode, **Organizations > Central drift and alert delivery** shows central rules, drift events, delivery states, and individual delivery attempts. Rules select minimum severity, findings/effective access, and added/removed/changed transitions. Saving requires `ManagePolicies`; reading requires both `ViewInventory` and `ViewFindings`. Rules may be disabled without deleting history. There are at most 50 rules per organization. Rules apply prospectively; saving a rule does not replay historical events.

For changed events, review severity is the higher of the previous and current severity, so downgrades of previously critical findings or privileged access can still meet an important-change rule. Added events use current severity; removed events use previous severity.

PostgreSQL stores baselines, events, rules, pending deliveries, and attempts independently of Desktop uptime. Events and outbox entries commit atomically with scans. Overlapping rules targeting the same event/channel produce one delivery. The worker checks the current matching rules before sending; deliveries with no remaining enabled matching rule become `Suppressed`. A send already in flight cannot be revoked.

Delivery states are `Pending`, `Sending`, `Delivered`, `Failed`, and `Suppressed`. A skip-locked queue and one-minute, attempt-fenced leases support multiple server instances and recovery after crashes. HTTPS requests have a 15-second deadline; redirects and cookies are disabled. Success is HTTP 2xx. Network failures, timeouts, HTTP 408/429/5xx, and temporarily missing channel configuration retry, with delays of 30/60/120/240 seconds and at most five attempts total. Other HTTP errors or invalid channel configuration fail permanently. Expired worker attempts are recorded as `lease_expired` and retried within that same budget. An exhausted delivery remains visibly failed; there is no automatic infinite retry or historical replay.

Attempts store timestamps, HTTP status codes, and fixed failure codes only. Webhook URLs, signing keys, request exceptions, and response bodies are not recorded in delivery state or application delivery logs. Responses are not read beyond headers. Notifications contain account/scan references and hashed state evidence, not arbitrary scan payloads.

### Server channel configuration

Create an organization rule with a channel alias such as `security`. Configure its endpoint and an independent high-entropy signing key through the License Server's secret configuration provider:

```text
CentralNotifications:Organizations:<tenant-guid>:<organization-guid>:Channels:security:Url
CentralNotifications:Organizations:<tenant-guid>:<organization-guid>:Channels:security:SigningKey
```

For environment variables replace each `:` with `__`. GUIDs use the `D` format (hyphenated). The endpoint must be absolute HTTPS without embedded username/password or fragments. Signing keys must contain at least 32 UTF-8 bytes; use a cryptographically random secret of at least 32 bytes encoded as Base64. Never commit real endpoints/keys or place them in a rule name or alias. Each tenant/organization has a separate channel namespace; one organization's rules cannot reference another's secret. Only trusted server administrators configure destinations; restrict service egress accordingly. The existing local `YASK_CIEM_ALERT_WEBHOOK_URL`/Windows Credential Manager configuration is independent and is not imported into shared rules. If both local and central delivery are enabled, they are independent notification streams, not cross-stream deduplicated.

### Stable IDs, signatures, and receiver replay protection

Delivery is **at least once**, not exactly once. HTTP success followed by a worker crash can cause a repeat send. Each retry carries the same event ID, delivery ID, payload, and `Idempotency-Key` (the delivery ID), with a freshly generated timestamp/signature:

- `X-Yask-Event-Id`: stable event GUID.
- `X-Yask-Delivery-Id` and `Idempotency-Key`: stable delivery GUID (one per event/channel).
- `X-Yask-Timestamp`: UTC Unix seconds.
- `X-Yask-Signature`: `v1=` followed by lowercase hex HMAC-SHA256.

The signed bytes are UTF-8 `<timestamp>.<event-id-D>.` followed by the exact raw request body bytes. Verify signatures in constant time **before processing** and reject requests older than five minutes or more than 30 seconds in the future. The integration library provides `NotificationWebhookSignature.Verify` for this check. Verify that signed header IDs agree with the body IDs. The receiver must atomically retain processed delivery IDs and apply each delivery's effects only once; authenticated duplicate deliveries should return 2xx without repeating effects. Retain deduplication IDs for the lifetime of the event/delivery history, not just the signature freshness window. A fresh retry timestamp does not make an already-processed delivery new. No inbound webhook receiver is exposed by this application.
