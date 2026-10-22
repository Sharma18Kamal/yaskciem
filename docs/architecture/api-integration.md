# API and Integration Layer

The solution now exposes a versioned integration contract through `CloudEntitle.Integration` and the existing ASP.NET License Server host.

## Read-only web operations console

P2.1 adds an optional same-origin TypeScript console at `/console/`, using Entra
SPA sign-in and the existing v1 read contracts below. It provides account/provider
filters, finding evidence, source coverage, review backlog and scoped snapshot
history, with a default 72-hour stale threshold. It adds no operational write
actions or database access. See [console setup and limits](../deployment/web-operations-console.md)
and [hosting HTTP examples](../../src/CloudEntitle.LicenseServer/WebConsole.http).

## Endpoints

- `GET /health`: host health check.
- `GET /api/v1/health`: versioned API health response.
- `GET /api/v1/readiness`: API/configuration readiness response.
- `GET /api/v1/providers`: read-only Azure/AWS/GCP capability declarations.
- `GET /api/v1/organizations`, `POST /api/v1/organizations`, and `GET /api/v1/organizations/{id}/workspace`: shared organization discovery, creation, and role-filtered workspace data.
- Organization-scoped member, custom-role, cloud-account binding, and `/audit-history` routes: server-authorized membership management and audit history.
- `GET`/`PUT /api/v1/organizations/{id}/access-reviews[/{campaignId}]` and `GET /api/v1/organizations/{id}/access-reviews/reviewers`: organization-authorized shared campaigns, assignments, eligible team reviewers, and decisions.
- `GET /api/v1/organizations/{id}/governance/report`: completion and overdue work by provider/account/team, current exception aging, and the latest 500 governance audit events.
- `GET /api/v1/organizations/{id}/governance/assignees`: active organization owners and eligible reviewers.
- `PATCH /api/v1/organizations/{id}/access-reviews/{campaignId}/items/{itemId}/assignment`: audited owner/reviewer/account/team/deadline reassignment with expected assignment version and reason; stale writes return 409.
- `POST`/`GET /api/v1/organizations/{id}/scan-summaries`: scan summary upload/history for bound accounts; uploads can include the normalized scan records.
- `POST`/`GET /api/v1/organizations/{id}/scan-schedules`, `PUT /api/v1/organizations/{id}/scan-schedules/{scheduleId}`, and `POST .../{scheduleId}/run-now`: create/pause per-account full-scan schedules, inspect schedules, or enqueue an immediate run.
- `GET /api/v1/organizations/{id}/scan-jobs[?limit=...]`, `GET /api/v1/organizations/{id}/scan-jobs/{jobId}`, and `POST .../{jobId}/cancel`: inspect job state, attempts, per-source checkpoints, duration, and outcomes or request cancellation.
- `GET /api/v1/organizations/{id}/scans/{scanId}/records`: paginated identity, resource, role, permission, effective-access, finding, and access-path records for one scan, with optional kind/account filters.
- `GET /api/v1/organizations/{id}/findings`: findings from a selected scan or the latest completed scan.
- `GET /api/v1/organizations/{id}/drift-events[?limit=100]`: comparable-scan finding/effective-access changes.
- `GET /api/v1/organizations/{id}/notification-rules` and `PUT .../notification-rules/{ruleId}`: shared severity/source/change rules referencing server channel aliases.
- `GET /api/v1/organizations/{id}/notification-deliveries[?limit=100]` and `GET .../notification-deliveries/{deliveryId}`: delivery/retry state and individual safe attempt outcomes.
- `GET /api/v1/organizations/{id}/operational-events[?limit=100]`: standardized operational events, including central drift, overdue reviews, and terminal scan failures.
- `GET /api/v1/organizations/{id}/connectors/health`: passive connector health, delivery latency, retry counts, and dead-letter totals.
- `POST /api/v1/organizations/{id}/operational-events/{eventId}/ticket`: explicitly queue a Jira ticket (202 + delivery status Location); never perform external I/O in the request handler.
- `POST`/`GET /api/v1/audit/events`: Entra-tenant central audit append/read routes; read requires the additional audit scope/role.

Governance report/assignee reads require `ViewFindings` or `ManageRemediations`. Assignment changes require `ManageRemediations`, an active accepted organization owner with inventory access (or no owner), an authorized reviewer, a future deadline, a current `ExpectedAssignmentVersion`, and a reason. New account attribution must match an organization/provider binding and requires inventory access for validation. PATCH returns the updated campaign (200), validation errors (400), forbidden/not-found (403/404), or a stale-assignment conflict (409). Full campaign updates also enforce assignment versions, preserving idempotent decision replays.

Reports exclude draft/cancelled campaigns and count pending/investigation/expired exceptions as unresolved. Exception age is measured in whole UTC days since the latest exception decision, with 0-29, 30-89, 90+ buckets; missing historical timestamps remain unknown. History includes the latest 500 governance audit events. A central deadline worker records reminders, expiry and escalation without API reads; Desktop never writes lifecycle state in shared mode. Renewals by the assigned reviewer create new attestations and clear current expiry/escalation state without deleting history. No governance action invokes cloud grant/revoke APIs. See [governance semantics](identity-governance.md) and [HTTP examples](../../src/CloudEntitle.LicenseServer/Governance.http).

Responses use `ApiEnvelope<T>` where appropriate and carry a correlation ID and contract version. The desktop/API client uses `CiemApiClient` with `HttpClient` and JSON contracts rather than raw EF entities.

Provider capability declarations describe read-only authentication modes only. Cloud credentials, access tokens, secrets, and provider SDK objects are not exposed through the API contract.

The License Server validates multitenant Microsoft Entra bearer tokens. Workspace routes require `access_as_user` or `Ciem.Api.Access`; the audit read route additionally requires `audit.read` or `Ciem.Audit.Read`. Tenant and actor IDs come only from validated `tid` and `oid` claims. Organization membership and the requested action are checked by the server for every organization-scoped operation; tenant directory payloads are stored separately by Entra tenant. Scan uploads require `RunScan`, and every included provider/account pair must be bound to the organization. They carry normalized inventory and findings as account-scoped JSON records; uploads are atomic, deduplicated by scan ID and content hash, and conflicting reuse of an ID is rejected. Uploads are capped at 20,000 records and 15 MiB of record payload. Record and finding reads require `ViewInventory` and `ViewFindings`, respectively. Shared review writes require `ManageRemediations`; the server validates active authorized reviewers, prevents a campaign creator from reviewing their own campaign, stamps decisions from the authenticated actor, and rejects evidence changes. Each review event includes actor email and Entra object ID, timestamp, decision/rationale where applicable, evidence digest, and a correlation ID (echoed in the API envelope); it is persisted append-only with the campaign update in one transaction.

Scan schedule and job operations require `RunScan` and an account binding owned by the organization. Schedules are per provider/account, have an interval of 5 minutes to 7 days and 0-4 retries, and initially run full scans only. The PostgreSQL queue uses `FOR UPDATE SKIP LOCKED`, a partial unique active-account index, durable attempts/checkpoints, worker leases, and cancellation flags; HTTP handlers only enqueue or read work. A hosted worker discovers and scans one bound account at a time using the License Server process's configured read-only cloud CLI credentials. Partial source coverage is retained with the scan and shown as `Partial`; failed jobs retry with bounded exponential delay. Incremental scanning is not enabled. Workers must be deployed with the required provider CLIs and service identity configured for the intended accounts; Desktop CLI credentials are not forwarded to the server.

The initial scheduled-scan executor persists normalized inventory, roles, permissions, and effective-access data where the connector supports them. It does not yet run the Desktop risk-evaluation pipeline, so scheduled scan summaries currently report zero findings; do not interpret that count as a clean risk assessment.

Scan summaries can declare `MonitoringScopes` for complete account/kind/profile snapshots. In the scan transaction, central monitoring compares them with the last complete baseline and writes stable drift events and a deduplicated outbox; missing scopes remain backward-compatible but are not compared. Reading central monitoring requires both `ViewInventory` and `ViewFindings`; rule writes require `ManagePolicies`. Rule IDs in PUT routes must match the body. Event/delivery list limits are 1-500 (default 100). Channels are aliases only; no credential-bearing URL is accepted by these APIs. See [continuous monitoring](continuous-monitoring.md) for comparison semantics and [alerting](alerting-notification.md) for configuration and the signed/idempotent delivery protocol.

Configure the API app registration for organizational accounts, expose `access_as_user` and/or `Ciem.Api.Access`, and set `AzureAd__Instance=https://login.microsoftonline.com/`, `AzureAd__TenantId=organizations`, `AzureAd__ClientId=<api-client-id>`, and `AzureAd__Audience=<api-app-id-uri>`. Configure `ConnectionStrings__Postgres` for the shared PostgreSQL database. The same database stores organization workspaces, scan schedules/jobs/attempts/checkpoints and scan records, access-review campaigns and append-only review audit events, central drift/notification state, and central audit events; production startup verifies the schemas but does not create or migrate them. Apply the DBA-managed schemas in `infra/postgres/001-schema.sql`, `infra/postgres/002-central-monitoring.sql`, `infra/postgres/003-governance-deadlines.sql`, then `infra/postgres/004-operational-integrations.sql` before starting the License Server.

To enable the Desktop shared workflow, set `YASK_CIEM_API_BASE_URL` to the License Server HTTPS base URL and `YASK_CIEM_API_SCOPE` to the delegated scope (for example `api://<api-client-id>/access_as_user`). Entra sign-in obtains the API token separately from the Microsoft Graph `User.Read` token. When configured, organizations, member management, review campaigns, shared scan records, and server-side scan schedules use the shared API; without it, the desktop intentionally retains its local/offline stores. Manual summary publishing remains available. Scheduled scans run on the server worker, independently of the Desktop session, using server credentials; they are not cloud event-stream/change-feed collection.

The desktop Settings workspace offers Jira Software, Jira Service Management, ServiceNow, BMC Helix ITSM, ManageEngine ServiceDesk Plus, Ivanti Neurons for ITSM, Zendesk, SolarWinds Service Desk, Dynamics 365 Customer Service, Azure DevOps Boards, and Other. Each provider has its own HTTPS site URL and project/request-type mapping. Native ticket creation and status refresh are implemented for Jira, Jira Service Management, and ServiceNow. These adapters use HTTPS REST APIs with HTTP Basic authentication; enter the provider username and API token/secret, which are stored per Windows user in Windows Credential Manager. Settings JSON contains provider URLs and non-secret mappings only.

The remaining catalog providers use a generic signed-webhook fallback when a webhook URL and secret are configured. Creation sends a JSON evidence snapshot with an idempotency key, timestamp, and HMAC-SHA256 signature in `X-YASK-CIEM-Signature`. A webhook may return a ticket ID, HTTPS ticket URL, status, and status URL. Status refresh is accepted only when that URL has the same HTTPS origin as the configured webhook endpoint. Provider-specific field mapping and status transitions for these fallback providers belong in the receiving integration.

Ticket creation is explicit and user-triggered from a selected recommendation; scans and recommendation generation never create tickets automatically. Requests are deduplicated by ticketing provider, source cloud provider, and recommendation fingerprint. A per-key file lock serializes submissions across Desktop processes for the same Windows user, and the local request index is checked again before provider creation. The request index, external IDs, status, closure verification, and links are local to this device under `%LOCALAPPDATA%\YASK-CIEM\ticketing`; this is not a shared or cross-device ticket registry. The ticketing provider remains authoritative, and refresh is an explicit user action. A fresh Jira status-category/status read or ServiceNow incident state 6 (Resolved) or 7 (Closed) verifies closure; an observed reopen clears the verification timestamp. Webhook providers use the returned status string and must expose a same-origin status URL.

Ticket evidence includes the source provider, finding/access/access-path identifiers when available, recommendation type, current and proposed state, impact, rationale, and evidence snapshot. Native ticket descriptions and signed-webhook payloads also include a `yask-ciem://recommendations/{id}` backlink. MSI and MSIX installs register this protocol and open the matching recommendation in Desktop. The deep link resolves only where that recommendation exists in the local workspace; ticket viewers on other devices still have the embedded evidence snapshot, but there is no hosted/shared recommendation permalink yet.

The local Desktop recommendation workflow above remains unchanged. The shared operational workflow below is separate and uses organization-scoped server persistence and credentials; it does not migrate the local recommendation ticket index or expose local tickets on other devices.

## P1.4 Shared Operational Integrations

The first prioritized connectors are **SignedWebhook** (monitoring/SIEM routing) and **Jira** (Jira Software Cloud REST v3 issue creation). This is not a new broad connector catalog. Existing Jira Service Management, ServiceNow, and other local Desktop connectors remain local.

### Event contract and atomic producers

The existing `SharedDriftEventDto` and signed v1 body are extended additively with `eventType` and optional `subjectId`. The body remains `{ version, tenantId, organizationId, deliveryId, event }`. Existing event IDs, delivery IDs, signing bytes/protocol, event/channel deduplication, and replay verification remain intact. The webhook also carries `X-Yask-Event-Type`; consumers must use the **signed body's** eventType as authoritative, not an unsigned header. Use `/operational-events` for the combined stream; `/drift-events` remains a compatible alias returning that same stream.

| Event type | Meaning |
|---|---|
| `finding.created` | Finding added to a comparable complete snapshot |
| `finding.resolved` | Finding absent from a comparable complete snapshot, or transitions to explicit Resolved status (including numeric domain status 1) |
| `finding.updated` | Other finding state change |
| `access.added`, `access.removed`, `access.changed` | Effective-access drift |
| `review.overdue` | A review item escalates after its deadline or exception expiry; subjectId is campaign ID, entityKey is item ID |
| `scan.failed` | Scheduled job reaches terminal Failed after bounded retries, including exhausted worker-lease recovery; subjectId/entityKey identify the job |

Drift events retain scan IDs, prior/current state hashes and source/provider/account scope. Review/scan events use empty scan IDs when not applicable. They include identifiers, severity, UTC detection time, and scope, not raw review evidence, comments, provider errors, or credentials. Review overdue and terminal scan failure have High severity. Retries of an individual scan attempt, cancellation, and Partial outcomes do not generate `scan.failed`. Events and matched delivery rows are inserted in the source transaction, even with no enabled routing rule, so later **explicit** ticket requests can reference them. Review events are keyed to immutable escalation event IDs; scan failures are keyed to terminal job IDs. Repeated producer execution cannot duplicate events or deliveries.

Legacy rules retain their original source/change filters and default to SignedWebhook. Optional `eventTypes` is an exact, case-sensitive allowlist; when nonempty it replaces legacy source/change filtering, while enabled/minimum severity still apply. Creating/editing rules requires `ManagePolicies`. New rules affect future events only; they do not bulk-create historical tickets. Legacy stored drift events derive eventType from kind/change; old historical changed events cannot retrospectively distinguish a resolved status from another update.

### Ticket policy and server configuration

Ticket creation is **explicit by default**. Select an event in Organizations, enter a configured Jira channel alias, and choose **Request Jira ticket for selected event**. This requires `ManageRemediations`, `ViewInventory`, and `ViewFindings`. POST accepts `{ "channel": "jira" }`, stamps the authenticated requester, queues work, and returns 202 with a status Location. Repeated requests return the existing event/channel delivery, including dead letters, rather than creating another ticket.

Automatic Jira creation requires a notification rule with `connectorKind: "Jira"`, `enabled: true`, `allowAutomaticTickets: true`, and a **nonempty** eventTypes allowlist. Disabling policy suppresses queued policy-generated sends when the worker checks eligibility; explicit operator requests do not depend on a policy rule. A request already in flight cannot be recalled. Deliveries snapshot connector kind so changing a server channel's kind cannot silently turn queued webhook sends into ticket creation. Use distinct webhook and Jira aliases.

Configure organization-scoped channels under:

```text
CentralNotifications:Organizations:<tenant-guid>:<organization-guid>:Channels:<alias>
```

For signed webhooks, keep the existing `Url` and `SigningKey` settings; `Kind` defaults to `SignedWebhook`. Preserve HTTPS, disabled redirect/cookie behavior, minimum 32-byte HMAC key, timestamp freshness verification, signed body verification, and receiver-side durable idempotency/replay tracking described in [alerting](alerting-notification.md). Native Jira is not HMAC signed: it uses its own authenticated REST API over HTTPS and does not weaken or replace webhook signing.

For Jira, set:

| Setting | Value |
|---|---|
| `Kind` | `Jira` |
| `Url` | Organization-approved HTTPS Jira Cloud origin, e.g. `https://example.atlassian.net` (no path, query, userinfo, or fragment) |
| `UserName` | Dedicated Jira service account email |
| `ApiToken` | API token from the server secret provider |
| `ProjectKey` | Approved project key, e.g. `SEC` |
| `IssueType` | Existing issue type name, e.g. `Task` |

Keep tokens/signing keys out of settings JSON, source control, Desktop, and API responses. The Jira service account needs permission to create issues in the selected project; projects requiring additional mandatory fields are not supported by this initial adapter. Issue creation uses `/rest/api/3/issue`, a mapped project/type, Atlassian Document Format description, and a stable `yask-delivery-<delivery-guid>` label. Description includes event/scope identifiers and an explicit no-cloud-mutation statement. No raw event evidence or exception rationale is copied. This initial connector does not read/store Jira response bodies, issue keys, links, or status; Jira remains authoritative and operators find issues by the delivery label.

### Health, retries, dead letters and reconciliation

All delivery/attempt history is shared by organization. Failed terminal deliveries expose `deadLettered: true`; database/API `status: "Failed"` is preserved for compatibility. Attempt `latencyMilliseconds` is elapsed UTC time from claim to recorded completion, not a pure network benchmark. `/connectors/health` aggregates lifetime pending, sending, retrying, failed/dead-lettered and delivered totals, last attempt/success, and mean completed-attempt latency. It includes configured aliases with no attempts as **Unknown**, reports invalid/missing configuration without secret values, and reports **Degraded** while retrying or dead-lettered records remain. It is passive health, **not** a live connectivity/authentication probe or SLA. Historical dead letters keep the channel degraded until administratively reconciled; this release has no purge/replay UI.

Signed webhooks retain bounded exponential retries (up to five attempts), lease recovery, and delivery-ID idempotency. Jira retries only a definitive HTTP 429 rejection, bounded to five attempts. Jira does not guarantee Idempotency-Key support: network errors, timeouts, HTTP 408/5xx and abandoned send leases can mean an issue was already created. They become terminal dead letters (`outcome_unknown` or `lease_expired`), with no blind retry. HTTP redirects are not followed, and error/response bodies and credentials are never persisted. A Jira 201 marks Delivered without reading the body.

For ambiguous Jira outcomes, search Jira for label `yask-delivery-<delivery-guid>` and reconcile with the operations team before any manual creation. Do **not** change aliases or manipulate queue rows to force a retry: doing so can duplicate tickets. The dedupe registry deliberately retains the terminal event/channel record. Permanent rejection/configuration failures likewise remain visible for operator remediation. Local Desktop ticketing is a separate workflow and has its existing explicit submission behavior.

Apply DBA migration [004-operational-integrations.sql](../../infra/postgres/004-operational-integrations.sql) after 003 before upgrading the server. It adds delivery connector kind and authenticated requester metadata; readiness requires both columns. Existing deliveries remain SignedWebhook. See [OperationalIntegrations.http](../../src/CloudEntitle.LicenseServer/OperationalIntegrations.http) for executable API examples. No live Jira or webhook endpoint is required by automated connector tests.
