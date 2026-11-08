# Continuous Monitoring and Drift Detection

The monitoring layer compares immutable scan snapshots after discovery, normalization, effective-access evaluation, access-path construction, and risk evaluation. It does not call cloud APIs, recalculate authorization, or mutate resources.

## Drift Model

The service reports Added, Removed, and Changed events for:

- Normalized entitlements
- Effective access decisions
- Access paths and their status/shape
- Risk findings and lifecycle/severity changes

Each event retains current/previous scan IDs, provider, stable entity key, summary, evidence, timestamp, and a review severity. Newly added effective access is elevated for review; first snapshots are treated as additions with no previous scan.

The comparison is deterministic for stable entity keys and cancellation-aware. Future persistence can store reports alongside scan history for trend queries and the existing recommendation/remediation review workflow.

The desktop access-change timeline separately compares comparable live scan snapshots retained locally for seven days. It shows scan detection time separately from provider-reported audit time and actor. Attribution depends on provider log permissions and retention; empty provider results are excluded rather than treated as proof of removal.

The Scan History coverage report retains per-provider, per-source statuses (`Collected`, `NoData`, `Partial`, `Denied`, `Unavailable`, `Unsupported`, `Skipped`) for the same seven-day local retention period. A successful empty query is distinct from a denied or unsupported source, and one source failure does not prevent other sources for that account from being scanned.

## Shared central monitoring (P1.2)

Shared scan uploads and server-scheduled scans now maintain PostgreSQL baselines, drift events, and a notification outbox in the same transaction as scan persistence. An organization-scoped transaction lock serializes concurrent scan/rule writes; a replay of the same scan cannot create new events or deliveries. Older/equal completion timestamps cannot rewind a baseline.

The central comparison covers `finding`, `resource-finding`, and `effective-access` records. Each scan explicitly declares `MonitoringScopes` with provider, external account ID, kind, and comparison profile. Supported profiles are `desktop-risk-v1`, `desktop-resource-risk-v1`, and `effective-access-v1`, respectively. Sources must have complete `Collected`/`NoData` coverage; successful empty snapshots can produce removals. Missing, partial, denied, unsupported, or unavailable scopes do not replace a baseline or imply removals. A scan from an older client without monitoring scopes is still stored but is not compared. The next comparable complete scan compares against the last complete baseline, not against an intervening partial scan.

Desktop publishing declares risk scopes only when all supported provider sources completed, and supplies explicit risk-evaluation coverage. Resource findings use their existing stable fingerprints; identity findings use rule/principal/resource/permission/title keys. Effective access uses principal/role/scope/source keys. Transient scan-local record IDs and activity timestamps are excluded. Meaningful severity, finding lifecycle/evaluation/recommendation, privilege, and assignment-type changes produce `Changed` events. Role/scope changes are separate removals/additions. External IDs/natural identifiers must remain stable; connector identity renames are not inferred.

Each account/kind/profile's first complete snapshot establishes a quiet baseline. Later events retain current/previous scan IDs, provider/account, kind, change type, severity, stable hashed entity key, and before/after state hashes. Full scan evidence remains available through the existing authorized scan-record API rather than being copied into notifications. Event IDs are deterministic for a scan transition/entity. The outbox is unique per event/channel, including overlapping rules, and unchanged subsequent scans are quiet. A genuine recurring transition after intervening changes is a new event, not permanently suppressed.

The scheduled executor currently declares only effective-access scopes when collection is complete. It still does not evaluate risk findings; its zero finding count cannot erase a Desktop finding baseline or imply that risks were resolved. Local/offline monitoring remains unchanged.

## On-demand cloud activity collection

Continuous change feeds are a separate, client-configured server worker described
in [continuous feed setup](../continuous-change-feeds.md). Azure/AWS/GCP cloud
exports are normalized by a client-owned authenticated HTTPS bridge; this is not
native Event Hubs, EventBridge or Pub/Sub consumption. Durable source epochs,
cursors, bounded atomic batches, immutable event revisions, retry health and
coverage/gap metadata survive restarts. Both server configuration and explicit
organization enable/connect consent are required; Desktop sign-in never creates
cloud resources or connects a feed. Coverage remains source-reported and
unverified, so a working bridge does not prove comprehensive all-cloud coverage
or effective privilege escalation. Saved feed reads do not contact that bridge.

The [Priority Two extension](../deployment/priority-two.md) compares selected saved
permission documents and analyzes behavior using explicitly configured action,
baseline and business-hours policy. Deduplicated potential review events reuse
the existing central SIEM/ticketing outbox. This does not automatically schedule
document/activity polling or establish complete IAM trust/credential change
coverage; source collection still requires client setup and consent.

Inbound SIEM evidence is separate from scan drift and cloud activity. The [normalized client bridge](../deployment/security-connectors.md#7-inbound-normalized-client-bridge) stores organization/product/alias-scoped immutable alert revisions and advances ingestion cursors transactionally. It does not poll products, update drift baselines, resolve findings or authorize responses. Reading the Desktop's saved-alert panel never contacts a live SIEM.

The Organizations workspace now provides a separate, explicitly consented 90-day audit read for a selected bound AWS account, Azure subscription or GCP project. It does not run at sign-in, during normal inventory scans, or in scheduled jobs. The user prepares read access/logging, selects AWS region/profile where applicable, and authorizes one read. Changing the account/region/profile resets consent; sign-out clears account evidence and cancels activity work. Loading saved evidence calls only the organization API, not a cloud endpoint.

- AWS: verifies `sts get-caller-identity` against the bound account, then pages regional `cloudtrail lookup-events`. Event history covers management events, not data events or other regions. API event names are displayed as source/name rather than invented IAM action mappings.
- Azure default source: reads the public-cloud subscription Activity Log API, validates same-subscription continuation links and returned event scope, and counts terminal Succeeded/Failed records. Permission evidence uses `authorization.action`, never the operation name alone. Entra sign-ins and resource data-plane logs are not included in this default source.
- GCP: queries project Cloud Audit Logs for the explicit 90-day UTC window. Observed method names and actual `authorizationInfo.permission` evidence remain separate. Authorization checks do not by themselves establish that an operation succeeded; permission observations count successful only when the operation succeeds and the check was granted.

Collection is bounded to 5,000 source events (Azure stops at page boundaries), 20,000 normalized rows, five minutes total, and a 16 MiB-character check per CLI response. GCP requests one extra event to detect truncation. Oversized/malformed/failed queries return an explicit error, not a successful empty collection. Continuation loops, foreign scopes and unsafe Azure next links are rejected. A truncated successful collection is explicitly marked truncated. All supported sources retain **Partial** coverage even when the query returns no events.

Normalized snapshots retain provider/account, source event ID, principal, observed action, permission-action evidence where available, resource where available, success and UTC time. Raw request/response bodies, provider errors and credentials are excluded. The shared API requires RunScan, ViewInventory and ViewFindings for upload, inventory/findings access for reads, and an exact organization account binding. Snapshots are client-supplied audit observations, not independently attested cloud evidence; they cannot approve or trigger cloud remediation.

PostgreSQL retains scoped snapshots with idempotent IDs; conflicting reuse returns 409. The latest snapshot is loaded for the account, not combined with previous overlapping collections. The report deduplicates source event/principal/action/permission observations and counts successful actions in exact inclusive 30/60/90-day windows, with last successful time and separate failed counts. Multiple resource checks for one action/event do not inflate its frequency. The [saved history API and Desktop selector](../deployment/cloud-activity.md#saved-historical-browsing) page snapshot summaries and load one historical snapshot under the same organization/account authorization. Historical activity is not historical entitlement attribution. No automatic retention job is implemented.

This UI reports **observed actions**, not a complete used/unused entitlement report. `PermissionUsageAnalyticsService` still needs authoritative action-level EffectiveAccess and complete attribution coverage. Blank actions, missing permission evidence, and unattributed successful events cannot produce a reliable unused conclusion. Role-level assignment names are not matched to API events, and missing logs never authorize permission removal.

### Explicitly selected data-plane destinations

The source selector also supports client-provisioned CloudTrail Lake (existing customers), CloudTrail S3 archives, Azure Log Analytics workspaces and Azure diagnostic Blob archives. GCP's existing project reader includes enabled Data Access logs. Source/destination/container/prefix changes reset consent and cancel pending activity work. These adapters do not create destinations or enable logging. CloudTrail Lake `StartQuery` creates a **billable query**, not an IAM mutation; local cancellation does not cancel that server-side query. Lake is no longer available to new customers after 31 May 2026, so use S3 archives for new clients.

Lake validates the event data store's exact account/region ARN and uses a fixed account/time-scoped scalar query with at most 5,001 results. It waits for `FINISHED`, pages results, rejects failed statuses/repeated tokens and retains query/page digest provenance. API event names are not IAM actions; resource/role attribution remains unavailable from this scalar query.

Log Analytics validates the workspace ARM resource ID within the bound subscription, resolves its workspace GUID and sends a fixed read-only query via the Logs REST API. It reads `StorageBlobLogs`, `StorageFileLogs`, `StorageQueueLogs`, `StorageTableLogs` and `AzureDiagnostics`, filters returned resources to the subscription, and rejects query errors/partial failures. Missing tables, other resource-specific schemas, retention and identities remain partial coverage. Azure operation names are not translated into permissions. Unknown outcomes are retained as unknown, not counted as failures.

Archives list only an explicitly selected prefix; S3 enforces same-account bucket ownership and account-specific CloudTrail prefixes. Azure verifies the storage-account ARM scope and uses Entra data access (`--auth-mode login`), never storage-account keys. Downloads use listing etags as conditional reads, with 128 files maximum, 8 MiB per downloaded object, 16 MiB decoded per object, 32 MiB total decoded, 20,000 normalized rows and the existing five-minute timeout. CloudTrail `.json.gz` `Records` and Azure diagnostic `.json` `records` formats are supported. Broad or old prefixes may return no recent evidence; they are not proof of inactivity. Downloaded raw files are transient and deleted in `finally`; a process crash can leave a `yask-audit-*.json` file in the current user's temporary directory.

Snapshots retain selected source scope and SHA-256 content digests with source record counts, without persisting raw audit payloads. Digests demonstrate consistency of client-supplied evidence, **not provider attestation**. Payload/provenance validation uses the existing scoped PostgreSQL snapshot store; no new database migration is required beyond 007. Snapshots remain separate: there is no cross-source merge or durable ingestion cursor yet.

### Conservative current-entitlement correlation

The UI correlates against persisted **non-demo**, exact provider/account inventory for the selected binding. It resolves exact principal IDs (or provider GCP emails), exact resource IDs and explicit successful permission evidence to compatible fully evaluated, unconditional, action-level Allow edges. It does not match role display names, expand wildcards, invent resource IDs or infer a role-assumption/federation relationship. Multiple edges are marked ambiguous; missing identities/resources/actions/evidence are explicitly unresolved. Missing matching inventory shows an attribution-unavailable notice.

**Compatible current grants are not proven-used historical grants.** The report preserves source observations and candidate edge IDs; it does not feed optimistic grant-use evidence into least-privilege removal. Historical entitlement versions, complete provider/category/resource coverage and trustworthy grant attribution remain prerequisites. Full service-schema coverage, durable continuous cursors, historical browsing, inbound SIEM, complete attack/lifecycle detection and approved SOAR remediation remain pending.

See [data-plane client setup](../deployment/cloud-activity.md) for required permissions, source fields and acceptance tests.

Apply [007-cloud-activity.sql](../../infra/postgres/007-cloud-activity.sql) after migration 006 before deployment. See [CloudActivity.http](../../src/CloudEntitle.LicenseServer/CloudActivity.http) and the [client endpoint setup guide](../deployment/security-connectors.md) for authorized requests and guided provisioning.

Vendor references: [CloudTrail event-history limitations](https://docs.aws.amazon.com/awscloudtrail/latest/userguide/view-cloudtrail-events.html), [Azure Activity Logs REST API](https://learn.microsoft.com/en-us/rest/api/monitor/activity-logs/list?view=rest-monitor-2015-04-01), [GCP audit-entry format](https://cloud.google.com/logging/docs/audit/understanding-audit-logs), and [gcloud logging read](https://cloud.google.com/sdk/gcloud/reference/logging/read).
