# Client SIEM/SOAR connector setup

## Delivery boundary

This milestone exports organization-scoped operational events to client-owned receivers:

| Client solution | Connector kind | Implemented transport |
|---|---|---|
| Splunk | `SplunkHec` | HTTPS HTTP Event Collector (HEC) |
| IBM QRadar | `QRadarTls` | TLS syslog with LEEF 2.0, mandatory client certificate |
| Microsoft Sentinel | `SentinelLogs` | Azure Monitor Logs Ingestion API into a custom table |
| Other SIEM/SOAR | `SignedWebhook` | Existing signed HTTPS webhook to a client-managed bridge |
| Carbon Black | Not selectable | Deferred until the client confirms edition and API requirements |

These are **outbound event exports**, not bidirectional integrations. Reading SIEM alerts, creating Sentinel incidents, invoking SOAR playbooks, and applying approved cloud responses are not implemented here. Receiving an event must not authorize permission removal, credential changes, or cloud writes. Existing Jira ticket creation remains available separately.

The shared server worker, not Desktop, sends events. A client administrator provisions the receiver and a deployment administrator configures the server channel. Organization policy administrators select only the channel alias and event filters. Different clients can use different products without sharing endpoints or credentials.

## 1. Upgrade and provision

1. Back up PostgreSQL, including operational events, notification rules, deliveries and attempts. Pause notification workers during the coordinated upgrade.
2. Using the DBA identity, apply migrations `001` through `005`, then [006-security-connectors.sql](../../infra/postgres/006-security-connectors.sql) and [007-cloud-activity.sql](../../infra/postgres/007-cloud-activity.sql). Production startup does not migrate schemas. Migration 006 expands the delivery-kind constraint; 007 adds on-demand activity snapshots. Readiness requires both. Existing SignedWebhook/Jira deliveries are preserved.
3. Deploy the updated server and Desktop. The staging bundle includes migrations 006 and 007. Resume workers after readiness succeeds.
4. Provision the chosen receiver below and permit only the necessary network egress. Require valid TLS certificates and approved host trust. Never bypass hostname/chain validation.
5. Configure each alias at:

   ```text
   CentralNotifications:Organizations:<tenant-guid>:<organization-guid>:Channels:<alias>:<setting>
   ```

   .NET environment configuration uses `__` in place of `:`. Settings are server-local; do not send receiver tokens or PFX passwords through notification-rule APIs. Use the deployment's secret provider for tokens and passwords, restrict certificate-file access to the server identity, and exclude secrets from source control, settings examples and diagnostic HTTP logging.

6. For downgrades, coordinate queue reconciliation first: older binaries do not support native SIEM delivery kinds. Do not drop the new constraint or rewrite queue kinds to force delivery.

## 2. Splunk HEC

The client enables HEC, provisions a dedicated token limited to the approved destination index, and supplies an HTTPS event endpoint. This adapter does not poll indexer acknowledgements; use a token without indexer-acknowledgement requirements for this initial transport.

| Setting | Example or requirement |
|---|---|
| `Kind` | `SplunkHec` |
| `Url` | `https://splunk.example.internal:8088/services/collector/event` |
| `Token` | Dedicated HEC token supplied by the secret provider |
| `Index` | Client-approved existing index, e.g. `security` |

The URL must have exactly `/services/collector/event`, no userinfo, query or fragment. Tokens must contain printable non-whitespace ASCII. TLS server validation is enabled; redirects and cookies are disabled.

Each event uses source `YASK-CIEM`, sourcetype `yask:ciem`, UTC epoch time and the configured index. A bounded JSON response with numeric `code: 0` is required for Delivered status; HTTP success alone is insufficient. Delivered means **HEC acceptance**, not proof that the event is indexed.

For client acceptance, confirm the scoped event is searchable:

```text
index=security sourcetype=yask:ciem | spath | dedup deliveryId
```

Provision extraction and durable deduplication by `deliveryId` for correlation rules and downstream playbooks. This example search deduplicates results; it is not a durable action-idempotency mechanism.

## 3. IBM QRadar

The client provisions a TLS syslog log source, typically port 6514, with **single-line newline framing**, not octet counting. Configure LEEF 2.0 parsing, the approved log-source identifier, event-ID mappings and custom properties for scope/delivery IDs. YASK/CIEM is a custom source; the client must validate its DSM/property mapping rather than assume automatic classification.

| Setting | Example or requirement |
|---|---|
| `Kind` | `QRadarTls` |
| `Url` | `tls://qradar.example.internal:6514` (explicit port required) |
| `LogSourceIdentifier` | `yask-client` (1-255 ASCII letters, digits, `.`, `-`, `_`) |
| `ClientCertificatePath` | Absolute server-local PKCS#12/PFX path, e.g. `C:\YaskSecrets\qradar-client.pfx` |
| `ClientCertificatePassword` | PFX password supplied by the secret provider, if required |

The certificate must contain a private key and be accepted by the QRadar receiver for mutual TLS. The collector's server certificate must match its hostname and chain to a trusted host root; arrange private-root trust through the client's normal administration procedures. TLS 1.2/1.3 and online certificate revocation checking are enabled. Certificate-validation overrides are not available.

On Windows, Schannel needs a non-ephemeral private key. The server uses default PFX key storage without requesting permanent persistence or exportability and disposes the import after sending; provision a protected, usable service-identity key-storage/profile location. Non-Windows hosts use ephemeral keys.

Messages are RFC 5424 syslog with a LEEF 2.0 payload:

```text
LEEF:2.0|YASK|CIEM|1.0|finding.created|0x09|cat=finding<TAB>sev=8<TAB>eventId=...<TAB>deliveryId=...
```

`<TAB>` above denotes a real tab delimiter. Attribute values escape backslashes, pipes, tabs and newlines. Severity maps Informational/Low/Medium/High/Critical to 1/3/5/8/10. Other properties are `tenantId`, `organizationId`, `provider`, `accountId`, `entityKey`, and `change`.

Syslog has **no ingestion acknowledgement**. Delivered means a completed TLS write, not confirmed QRadar parsing or storage. Before-write network failures can retry; ambiguous after-write failures and expired send leases become terminal dead letters to avoid blind duplication. Reconcile using `deliveryId` in QRadar before any manual action.

## 4. Microsoft Sentinel

The client provisions a Log Analytics workspace enabled for Sentinel, a custom table, and a data collection rule (DCR) with a Logs Ingestion endpoint. Configure these before enabling the notification rule:

- Example table: `YaskCiem_CL`, with `TimeGenerated` of type `datetime` and `Event` of type `dynamic`.
- Example input stream: `Custom-YaskCiem`, declaring those same two columns.
- DCR data flow: map that stream to the approved Log Analytics destination, with `transformKql: "source"` and output stream `Custom-YaskCiem_CL`.
- Grant the selected server managed identity **Monitoring Metrics Publisher** on the DCR. Confirm the principal and DCR belong to the appropriate destination tenant.

| Setting | Example or requirement |
|---|---|
| `Kind` | `SentinelLogs` |
| `Url` | The DCR/DCE HTTPS ingestion root ending in `.ingest.monitor.azure.com`, with no path/query/fragment |
| `RuleId` | DCR immutable ID, e.g. `dcr-...` (not its ARM resource ID) |
| `Stream` | Input stream, e.g. `Custom-YaskCiem` |
| `ManagedIdentityClientId` | Optional user-assigned managed identity client GUID; omit for system-assigned identity |

Only **public Azure** and **managed identity authentication** are supported by this adapter. The host must expose that managed identity and it must be authorized on the destination DCR. It does not use a Desktop/CLI identity, accept arbitrary client secrets, or automatically create a cross-tenant managed-identity relationship. Clients whose Sentinel workspace is in another tenant need a client-tenant host/bridge or a future tenant-local application-authentication adapter.

Requests use scope `https://monitor.azure.com/.default`, API version `2023-01-01`, and a JSON array with exact column names `TimeGenerated` and `Event`. HTTP 204 marks ingestion acceptance. It does not create an incident, analytics rule or SOAR action; the client separately configures Sentinel analytics/automation.

Example verification:

```kusto
YaskCiem_CL
| extend DeliveryId = tostring(Event.deliveryId)
| summarize arg_max(TimeGenerated, *) by DeliveryId
```

Build durable action deduplication separately if the client uses these events to trigger workflows.

## 5. Enable organization routing

In the Desktop Organizations workspace, select the native connector kind, enter its configured alias, choose the minimum severity and event filters, and save the rule. Creating/editing rules requires `ManagePolicies`; reading monitoring/catalog/health requires organization inventory and findings access.

The app now shows product-specific **please create/configure the receiver** instructions when a connector is selected. Before saving an enabled rule, the user must confirm the client-created receiver and organization server alias are configured. Selecting another kind/channel/rule resets confirmation. Disabling a rule remains available without setup confirmation. This is an on-demand setup guard, not a connectivity test or a replacement for server-side authorization/configuration checks; existing enabled policies continue independently on the server.

The same workspace has an on-demand cloud activity panel. It explains required read/logging permissions for the selected account and requests explicit consent before one read. Regional CloudTrail history and Azure Activity Log require no new log endpoint; fuller data-plane evidence may require the client to provision trails/diagnostic destinations or enable GCP Data Access logging. The app does not create those resources, enable logging, or grant permissions. Saved-evidence loading does not contact live cloud APIs. See [activity collection](../architecture/continuous-monitoring.md#on-demand-cloud-activity-collection) for coverage limits.

The [HTTP examples](../../src/CloudEntitle.LicenseServer/OperationalIntegrations.http) include catalog, health and an opt-in Splunk rule. For QRadar or Sentinel, use a distinct rule ID and replace `connectorKind` and `channel` with `QRadarTls`/`qradar` or `SentinelLogs`/`sentinel`. Keep `allowAutomaticTickets: false` for exports.

Rules apply to **future events only**, not historical backfills. The exact event types are listed in the [operational event contract](../architecture/api-integration.md#event-contract-and-atomic-producers). An explicit nonempty `eventTypes` allowlist replaces the legacy source/change filters; minimum severity and enabled status still apply. Overlapping rules deduplicate on event/channel. Disabling routing suppresses queued policy sends at eligibility check; an already in-flight request cannot be recalled.

Splunk's `event` and Sentinel's `Event` contain:

```json
{
  "version": "v1",
  "tenantId": "<tenant-guid>",
  "organizationId": "<organization-guid>",
  "deliveryId": "<delivery-guid>",
  "eventId": "<event-guid>",
  "eventType": "finding.created",
  "provider": "Aws",
  "accountId": "<account-id>",
  "severity": "High",
  "entityKey": "<stable-entity-key>",
  "subjectId": null,
  "scanId": "<scan-guid>",
  "previousScanId": "<previous-scan-guid>",
  "previousStateHash": null,
  "currentStateHash": "<state-hash>",
  "detectedAtUtc": "<UTC-timestamp>"
}
```

Events contain identifiers and hashes, not full finding evidence, raw cloud logs, credentials, or approval instructions. Review/scan events may use empty scan IDs. QRadar uses the scoped LEEF subset documented above. Export integration does not itself provide cloud activity collection or complete action-level permission evidence.

## 6. Acceptance and operational checks

1. Verify server readiness and the organization catalog. The catalog explicitly reports `ImportAlerts: false` and `ExecuteResponseActions: false`; Carbon Black exports are also false.
2. Enable a low-risk test rule in an approved test organization/account. Run comparable complete scans to produce controlled changes. Confirm the correct tenant/account/delivery IDs in the client receiver.
3. Check delivery status and attempt history. `/connectors/health` is **passive**, not an endpoint probe: configuration validity, delivery totals, retries, dead letters and historical latency do not prove live receiver connectivity.
4. Splunk/Sentinel use at-least-once delivery with bounded retries (up to five) for network failures, timeouts and HTTP 408/429/5xx. Neither receiver is assumed to honor `Idempotency-Key`. Deduplicate by delivery ID. Malformed/nonzero/oversized HEC success acknowledgements are terminal `outcome_unknown`; reconcile before manual resend.
5. Confirm invalid authentication/certificates remain visible as failures and no raw receiver errors or secrets appear in API/queue history. Keep external diagnostics equally restricted.
6. Verify rule disabling, tenant isolation and receiver-side duplicate handling before production rollout. Historic dead letters remain visible; this release has no queue replay/purge UI. Do not edit queue rows or change aliases to bypass deduplication.

Automated tests cover formatting, acknowledgement handling, safe configuration failures, local untrusted-TLS rejection, organization authorization, durable routing/deduplication, migration replay/readiness and QRadar lease ambiguity. They do **not** replace client acceptance against real Splunk, QRadar or Sentinel deployments. No live client credentials, SIEM endpoints or cloud mutations are required by those tests.

### Vendor references

- [Splunk HEC event data](https://help.splunk.com/en/splunk-enterprise/get-data-in/get-started-with-getting-data-in/9.4/get-data-with-http-event-collector/format-events-for-http-event-collector)
- [IBM QRadar TLS syslog configuration](https://www.ibm.com/docs/en/qsip/7.5.0?topic=options-tls-syslog-protocol-configuration)
- [Azure Monitor Logs Ingestion overview](https://learn.microsoft.com/en-us/azure/azure-monitor/logs/logs-ingestion-api-overview)
- [Azure Monitor Logs Ingestion code samples](https://learn.microsoft.com/en-us/azure/azure-monitor/logs/tutorial-logs-ingestion-code)
