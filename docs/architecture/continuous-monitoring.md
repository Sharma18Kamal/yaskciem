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

The Organizations workspace now provides a separate, explicitly consented 90-day audit read for a selected bound AWS account, Azure subscription or GCP project. It does not run at sign-in, during normal inventory scans, or in scheduled jobs. The user prepares read access/logging, selects AWS region/profile where applicable, and authorizes one read. Changing the account/region/profile resets consent; sign-out clears account evidence and cancels activity work. Loading saved evidence calls only the organization API, not a cloud endpoint.

- AWS: verifies `sts get-caller-identity` against the bound account, then pages regional `cloudtrail lookup-events`. Event history covers management events, not data events or other regions. API event names are displayed as source/name rather than invented IAM action mappings.
- Azure: reads the public-cloud subscription Activity Log API, validates same-subscription continuation links and returned event scope, and counts terminal Succeeded/Failed records. Entra sign-ins and resource data-plane logs are not included.
- GCP: queries project Cloud Audit Logs for the explicit 90-day UTC window. Observed method names and actual `authorizationInfo.permission` evidence remain separate. Authorization checks do not by themselves establish that an operation succeeded; permission observations count successful only when the operation succeeds and the check was granted.

Collection is bounded to 5,000 source events (Azure stops at page boundaries), 20,000 normalized rows, five minutes total, and a 16 MiB-character check per CLI response. GCP requests one extra event to detect truncation. Oversized/malformed/failed queries return an explicit error, not a successful empty collection. Continuation loops, foreign scopes and unsafe Azure next links are rejected. A truncated successful collection is explicitly marked truncated. All supported sources retain **Partial** coverage even when the query returns no events.

Normalized snapshots retain provider/account, source event ID, principal, observed action, permission-action evidence where available, resource where available, success and UTC time. Raw request/response bodies, provider errors and credentials are excluded. The shared API requires RunScan, ViewInventory and ViewFindings for upload, inventory/findings access for reads, and an exact organization account binding. Snapshots are client-supplied audit observations, not independently attested cloud evidence; they cannot approve or trigger cloud remediation.

PostgreSQL retains scoped snapshots with idempotent IDs; conflicting reuse returns 409. The latest snapshot is loaded for the account, not combined with previous overlapping collections. The report deduplicates source event/principal/action/permission observations and counts successful actions in exact inclusive 30/60/90-day windows, with last successful time and separate failed counts. Multiple resource checks for one action/event do not inflate its frequency. Prior snapshots are retained but do not yet have a historical browsing endpoint or automatic retention job.

This UI reports **observed actions**, not a complete used/unused entitlement report. `PermissionUsageAnalyticsService` still needs authoritative action-level EffectiveAccess and complete attribution coverage. Blank actions, missing permission evidence, and unattributed successful events cannot produce a reliable unused conclusion. Role-level assignment names are not matched to API events, and missing logs never authorize permission removal. Full data-plane/log-destination adapters, attribution to entitlement/role/resource edges, continuous ingestion cursors and historical entitlement browsing remain subsequent Priority 1 work.

Apply [007-cloud-activity.sql](../../infra/postgres/007-cloud-activity.sql) after migration 006 before deployment. See [CloudActivity.http](../../src/CloudEntitle.LicenseServer/CloudActivity.http) and the [client endpoint setup guide](../deployment/security-connectors.md) for authorized requests and guided provisioning.

Vendor references: [CloudTrail event-history limitations](https://docs.aws.amazon.com/awscloudtrail/latest/userguide/view-cloudtrail-events.html), [Azure Activity Logs REST API](https://learn.microsoft.com/en-us/rest/api/monitor/activity-logs/list?view=rest-monitor-2015-04-01), [GCP audit-entry format](https://cloud.google.com/logging/docs/audit/understanding-audit-logs), and [gcloud logging read](https://cloud.google.com/sdk/gcloud/reference/logging/read).
