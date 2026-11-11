# Client SIEM/SOAR connector setup

## Delivery boundary

This milestone exports organization-scoped operational events to client-owned receivers:

| Client solution | Connector kind | Implemented transport |
| --- | --- | --- |
| Splunk | `SplunkHec` | HTTPS HTTP Event Collector (HEC) |
| IBM QRadar | `QRadarTls` | TLS syslog with LEEF 2.0, mandatory client certificate |
| Microsoft Sentinel | `SentinelLogs` | Azure Monitor Logs Ingestion API into a custom table |
| Other SIEM/SOAR | `SignedWebhook` | Existing signed HTTPS webhook to a client-managed bridge |
| Carbon Black | Not selectable | Deferred until the client confirms edition and API requirements |

These transports are **outbound event exports**, not native bidirectional integrations. A separate, explicitly enabled [inbound normalized client bridge](#7-inbound-normalized-client-bridge) accepts alerts from Splunk, QRadar, Sentinel or another client bridge. Native product API polling, creating Sentinel incidents, invoking SOAR playbooks and applying approved cloud responses are not implemented by these connectors. Receiving an event or alert must not authorize permission removal, credential changes or cloud writes. Existing Jira ticket creation remains available separately.

The shared server worker, not Desktop, sends events. A client administrator provisions the receiver and a deployment administrator configures the server channel. Organization policy administrators select only the channel alias and event filters. Different clients can use different products without sharing endpoints or credentials.

## 1. Upgrade and provision

1. Back up PostgreSQL, including operational events, notification rules, deliveries and attempts. Pause notification workers during the coordinated upgrade.
2. Using the DBA identity, apply every numbered migration shipped with the matching release in filename order. Follow the package manifest; sequence gaps are allowed and must not be filled by renaming or inventing scripts. Production startup does not migrate schemas. In particular, [006-security-connectors.sql](../../infra/postgres/006-security-connectors.sql) expands the delivery-kind constraint; [007-cloud-activity.sql](../../infra/postgres/007-cloud-activity.sql) adds on-demand activity snapshots; [008-inbound-security.sql](../../infra/postgres/008-inbound-security.sql) adds bridge cursors and evidence; and [009-entitlement-evidence.sql](../../infra/postgres/009-entitlement-evidence.sql), [010-priority-two.sql](../../infra/postgres/010-priority-two.sql), [011-trust-credential-monitoring.sql](../../infra/postgres/011-trust-credential-monitoring.sql) and [012-break-glass-reports.sql](../../infra/postgres/012-break-glass-reports.sql) add related evidence and readiness requirements. Existing SignedWebhook/Jira deliveries are preserved. Resume workers only after readiness succeeds. See [document setup](cloud-activity.md#on-demand-permission-documents-and-saved-observations), [Priority Two setup](priority-two.md) and [client emergency reporting](emergency-access.md).
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
| --- | --- |
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
| --- | --- |
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
| --- | --- |
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

The same workspace has an on-demand cloud activity panel. It explains required read/logging permissions for the selected source and requests explicit consent before one read. Regional CloudTrail history and Azure Activity Log require no new log endpoint. The source selector also supports existing CloudTrail Lake stores, CloudTrail S3 archives, Azure Log Analytics workspaces and diagnostic Blob archives; GCP project reads include enabled Data Access logs. Clients provision those destinations and logging, then supply the scoped source identifiers. CloudTrail Lake queries can incur charges and are unavailable to new customers after May 2026. The app does not create those resources, enable logging, or grant permissions. Saved-evidence loading does not contact live cloud APIs. See [data-plane setup](cloud-activity.md) and [activity collection](../architecture/continuous-monitoring.md#on-demand-cloud-activity-collection) for actual coverage and attribution limits.

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

1. Verify server readiness and the organization catalog. Outbound transports report `ImportAlerts: false`; the distinct `InboundClientBridge` entry reports `ImportAlerts: true`, `ExportEvents: false` and client-owned product polling. All entries report `ExecuteResponseActions: false`; Carbon Black exports/imports remain deferred.
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

## 7. Inbound normalized client bridge

### Provision on demand

Nothing polls a SIEM at sign-in, during a scan or when reading saved alerts. The Desktop panel asks the administrator to create/configure a client bridge and server alias before use. The client owns the SIEM endpoint, polling/playbook automation, product credentials and source pagination. The application supplies a canonical HTTPS batch endpoint, not a vendor polling adapter or bridge deployment service.

1. The client administrator selects exact product `Splunk`, `QRadar`, `Sentinel` or `ClientBridge` and an organization-specific alias (1-64 ASCII letters/digits/`-`/`_`, beginning with a letter/digit).
2. The deployment administrator enables that organization alias:

   ```text
   InboundSecurity:Organizations:<tenant-guid>:<organization-guid>:Channels:<alias>:Enabled=true
   InboundSecurity:Organizations:<tenant-guid>:<organization-guid>:Channels:<alias>:Product=Sentinel
   ```

   Use GUIDs in canonical hyphenated format. Environment settings replace `:` with `__`. Aliases are disabled by default and must match the submitted product exactly. This configuration contains no SIEM URLs or credentials.
3. Approve the shared server's HTTPS endpoint and TLS trust. Acquire an Entra bearer token for the existing CIEM API audience/access policy. The authenticated identity must be a member of the selected organization; importing requires `ManagePolicies`, `ViewFindings` and `ViewInventory`. Saved reads/cursor reads require `ViewFindings` and `ViewInventory`. A SIEM token is not a CIEM token; having a cloud or product administrator role alone does not authorize the organization API.
4. Implement/test the client mapping and checkpoint persistence below in an approved test environment. Do not upload credentials, raw event bodies, unnecessary personal data or signed evidence links. Never log Authorization headers.
5. Only after explicit client approval should the bridge call the endpoint. Disable the alias to stop further imports; saved evidence remains readable. This does not revoke permissions or close findings.

### Product mapping boundary

| Source | Client mapping responsibility |
| --- | --- |
| Splunk | Read approved saved-search/Enterprise Security notable results or use a client alert action. Supply a stable notable ID and the actual revision/update time, not the polling time. Normalize local urgency and investigation state with an agreed mapping. |
| QRadar | Read authorized offense API results. Map offense ID, actual last-updated time, severity and status. Severity scale and ID namespace are client/version-specific; use `Unknown` when not established. |
| Sentinel | Read workspace incidents or use a client incident playbook. Map stable incident ARM ID, `lastModifiedTimeUtc`, title, severity and state (`New` to `Open`, `Active` to `Investigating`, `Closed` to `Closed`). Keep the workspace/source stable within the alias. |
| ClientBridge | Map another approved product into the same contract; no arbitrary cloud response instructions are accepted. Carbon Black requires edition/API and client mapping confirmation first. |

The bridge must follow the source's pagination and retain a durable client checkpoint. A timestamp alone can miss equal-timestamp events: use source-defined continuation plus an overlap/deduplication strategy, and ingest status updates/closures as new revisions. The server cursor is opaque and **not proof of complete source collection**. Native paging/rate-limit handling, source availability monitoring and bridge restart recovery are the client's responsibility.

See [Sentinel incident list API](https://learn.microsoft.com/en-us/rest/api/securityinsights/incidents/list?view=rest-securityinsights-2024-03-01) for workspace incidents and continuation links. Product API versions/permissions must be confirmed for the deployed client edition.

### API and atomic checkpoint contract

Use the examples in [SecurityAlerts.http](../../src/CloudEntitle.LicenseServer/SecurityAlerts.http), replacing server URL, GUIDs, token and timestamps.

```text
GET  /api/v1/organizations/<org>/security-alerts/<product>/<alias>/cursor
POST /api/v1/organizations/<org>/security-alerts/<product>/<alias>/batches
GET  /api/v1/organizations/<org>/security-alerts/<product>/<alias>?limit=100
```

POST body:

```json
{
  "batchId": "00000000-0000-0000-0000-000000000010",
  "previousCursor": null,
  "cursor": "client-checkpoint-1",
  "alerts": [{
    "alertId": "source-incident-1001",
    "updatedAtUtc": "2026-11-08T12:00:00Z",
    "title": "Privileged identity activity requires review",
    "severity": "High",
    "status": "Open",
    "principal": null,
    "resource": null,
    "evidenceUrl": "https://siem.example.invalid/incident/1001"
  }]
}
```

- Each batch has a new nonempty GUID, at most 500 alerts and at most 2 MiB of request/serialized payload. Unknown-length/chunked oversized requests are also rejected before JSON deserialization. Use JSON Content-Type; malformed/invalid payloads return 400, oversize 413 and unsupported Content-Type 415.
- Severity: `Critical`, `High`, `Medium`, `Low`, `Informational`, `Unknown`. Status: `Open`, `Investigating`, `Closed`, `Unknown`. Do not invent a severity or identity association from missing evidence.
- Revision time must be between Unix epoch and current server time plus one minute. Key: tenant/organization/product/alias/alert ID/update timestamp. PostgreSQL timestamps have microsecond precision; use UTC microsecond timestamps consistently. Different submicrosecond timestamps or representations for the same key can conservatively conflict; do not change payloads on retries.
- Evidence URLs, if present, must be HTTPS without userinfo, query strings or fragments. They are stored references, never fetched or executed. Principal/resource are untrusted strings, not cloud bindings or proof of exploitation.
- Initial `previousCursor` is null. Later requests must match the current server cursor exactly and advance to a different nonempty opaque cursor (maximum 2048 characters). Empty alert batches can commit a checkpoint without asserting coverage.
- Alert revisions, the receipt and cursor advancement commit in one transaction. A stale cursor, reused batch ID with different content or conflicting existing revision returns 409 and commits nothing. Concurrent first imports have one winner.
- On timeout/unknown HTTP outcome, retry the **same batch ID and payload**. Exact replay returns the original receipt even if another batch advanced the cursor meanwhile; reread the server cursor before preparing another batch. Never blindly change a failed batch's cursor to bypass conflicts.
- A 201 receipt's `revisions` counts accepted entries, including exact already stored revisions; it does not count newly inserted rows, resolved findings or executed actions.
- Saved GET returns at most 500 latest revisions (default 100) ordered by update time. Older imported revisions cannot rewind an alert. This is a bounded saved view, not a complete historical alert export.

### Evidence and response safety

The receiving actor and reception time are retained. Runtime has no UPDATE/DELETE/TRUNCATE grants on alert revisions or receipts; row and statement triggers reject those mutations. A database administrator can alter guards/data; these tables are **not WORM, cloud-attested evidence or a tamper-proof audit archive**. No automated alert/receipt pruning is implemented; coordinate retention, backups and storage monitoring under the client's evidence policy.

SIEM imports do not enqueue outbound responses, mutate CIEM finding state, create recommendations, approve remediation, execute playbooks, grant JIT access or change cloud permissions. Manual cloud administration remains a separate preview/confirmation/verification workflow. Complete activity/entitlement attribution and operational dependency evidence are still required before automated least-privilege removal.

Acceptance: verify unauthorized/foreign-tenant denial, disabled/product-mismatch alias denial, exact replay, cursor conflict, whole-batch rollback, older revision handling, Desktop sign-out/selection race guards and explicit failures. Passing local tests is not acceptance against a live vendor deployment. Client-approved native polling and approved SOAR execution remain separate work.
