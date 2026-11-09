# Continuous client bridge change feeds

## Capability boundary

This is a **durable, bounded server poller of client-configured authenticated normalized HTTPS bridges**, not native Event Hub/EventBridge/Pub/Sub SDK polling and not proof of full cloud IAM coverage. No cloud resources, subscriptions, trails, sinks or credentials are created. Desktop sign-in never starts a source poll. Existing on-demand trust/credential snapshots remain independent partial observations; feed metadata does not establish effective authorization or authoritative escalation.

Desktop is a client, not the scheduler: scheduled inventory scans, continuous change-feed polling, and JIT grant/revocation processing run as independent License Server hosted workers. Configure account scan schedules in the Organization workspace; keep the License Server deployed and healthy for background work.

Both server `Enabled` + `Connected` configuration **and** explicit organization administrator enable/connect are required. Inventory/findings read access is required for reads; writes additionally require ManageConnections and ManagePolicies. The organization must be active and the configured provider/account must remain bound at claim and commit. The worker runs without a Desktop session only after durable opt-in.

Source completeness is machine-readable for all six categories: `IamPolicy`, `RoleAssignment`, `WorkloadTrust`, `ServiceAccountPermission`, `CredentialMetadata`, `ServiceAccountKeyMetadata`. Every batch must include each category, even unsupported ones (`Unavailable` + gap code). States are `Unknown`, `Current`, `Backfill`, `Gap`, `Unavailable`; interval bounds, source event IDs/revision IDs, original source and provider-observation times, source watermark, source identity and epoch are retained. `SourceReportedComplete` is **only a bridge claim**; `VerifiedComplete` is always false. No native coverage verification is implemented. Empty successful responses are not completeness evidence. A previously observed `Gap` cannot be cleared by a later source claim: status preserves it until an explicit epoch reset. Batch history preserves the original claims, including gaps; resets start Unknown, never VerifiedComplete.

## Client-owned cloud setup (only on demand)

Client cloud/security administrators must review cost, permissions, regional delivery, retention and data residency before creating these resources. YASK does not run these instructions or deploy anything:

* **Azure:** create/select an Event Hubs namespace, event hub and dedicated consumer group; authorize the client bridge consumer with **Azure Event Hubs Data Receiver** and appropriate checkpoint storage access. For each bound subscription, configure **Azure Monitor → Activity log → Export Activity Logs / Diagnostic settings** to send `Administrative` activity to the event hub. Include successful `Microsoft.Authorization/roleAssignments/write|delete`, `roleDefinitions/write|delete` and relevant policy operations. Separately configure **Microsoft Entra ID → Monitoring & health → Diagnostic settings** to stream `AuditLogs` to Event Hubs for application/service-principal federation, credential metadata and directory role changes. Entra licensing, tenant diagnostic administration and destination configuration permissions are client prerequisites. Subscription Activity Log alone does **not** cover Entra credentials/trust; missing tenant categories must be `Unavailable`/`Gap`. Normalize event identity (`eventDataId`/audit ID), `eventTimestamp`/activity time, subscription/tenant scope and safe object identifiers.
* **AWS:** configure a client-owned multi-Region/account or organization **CloudTrail trail** with management write events and **global service events**; retain delivered records for replay/backfill. IAM global-service events require a correctly placed **EventBridge default-bus rule in US East (N. Virginia)** (or the partition's IAM event Region); validate actual delivery, do not assume every Region's rule receives IAM events. Match `source: aws.iam`, `detail-type: AWS API Call via CloudTrail`, `detail.eventSource: iam.amazonaws.com`; select `CreatePolicyVersion`, `SetDefaultPolicyVersion`, `DeletePolicyVersion`, `Put/Delete*Policy`, `Attach/Detach*Policy`, `UpdateAssumeRolePolicy`, `Create/Update/DeleteAccessKey`, `Create/DeleteOpenIDConnectProvider`, `UpdateOpenIDConnectProviderThumbprint` and relevant role/user/group/trust changes. Route through a client-owned durable queue/bridge target with scoped rule-target authorization and dead-letter handling. Use `eventID`, `eventTime`, `recipientAccountId`, normalized ARN/object IDs. EventBridge delivery without an initial inventory/backfill does not prove policy/key inventory completeness. SCPs, permission boundaries, resource policies and cross-account effects need explicit additional category coverage or gaps.
* **GCP:** create/select a **Pub/Sub topic and subscription**. Configure a project (or explicitly approved aggregated organization/folder) **Cloud Logging sink** for `cloudaudit.googleapis.com/activity`, including IAM policy writes, `iam.googleapis.com` service-account/key administration and resource-specific `SetIamPolicy` calls. Grant the sink's `writerIdentity` **Pub/Sub Publisher** on the topic; grant the bridge identity **Pub/Sub Subscriber** on its subscription. Enable relevant **Data Access** audit logs where needed: enabling them incurs client-owned cost and may be required for permission/use evidence; Admin Activity alone is not full permissions coverage. Verify sink filter/exclusions and grant scope before connection. Preserve `insertId`, `timestamp`, `receiveTimestamp`, full resource scope and safe service-account/key identifiers. Pub/Sub acknowledgements and Logging retention/backfill are the bridge's responsibility.

## Azure administrator setup and test

This is an operator-run setup. YASK-CIEM does not create Azure resources, configure diagnostic settings, or read Event Hubs directly. The Azure administrator owns the subscription and tenant configuration; a client-owned bridge must consume the exported events and implement the HTTPS [bridge protocol](#bridge-protocol). Without that bridge, an Event Hub endpoint alone cannot be connected to the current feed worker.

1. **Confirm scope and approvals.** Record the exact Azure subscription ID already bound to the YASK-CIEM organization, its Entra tenant, the selected Azure region, and the organization-approved retention/cost limits. Subscription Activity Logs cover control-plane operations; they are not a complete record of resource data-plane access. Entra directory audit logs are a separate tenant-scoped source.
2. **Create the Event Hubs destination.** In the Azure portal, create or select an Event Hubs namespace and event hub in an approved region. Use the default consumer group unless another consumer needs an isolated group. Set retention and throughput according to the expected log volume and replay requirements. Give the bridge consumer only the read permissions it needs (prefer Microsoft Entra ID with the Event Hubs Data Receiver role at the narrowest applicable scope). Do not give the bridge send/manage rights or use a namespace-wide root key for consumption.
3. **Export the subscription Activity Log.** For the bound subscription, open **Monitor → Activity log → Export Activity Logs** (or the subscription's **Diagnostic settings**) and create a diagnostic setting targeting the selected Event Hubs namespace. Select the applicable control-plane categories, at minimum **Administrative**; include **Policy** and **Security** where relevant to the client's scope. Save and verify the destination configuration. Activity Log export is subscription-scoped, so repeat for every subscription that is intentionally monitored. A successful export is not proof that all identity, trust, or data-plane changes are covered.
4. **Export Entra directory audit events separately.** In the Microsoft Entra admin center, open **Entra ID → Monitoring & health → Diagnostic settings**, add a setting, select **AuditLogs**, and target an Event Hubs destination associated with the same Entra tenant. The operator requires the appropriate tenant diagnostic-settings role (Microsoft currently documents Security Administrator for general Entra diagnostic settings). Include sign-in logs only if the organization explicitly wants them and has a use case; they are not a substitute for audit events. Entra license and tenant policy constraints may apply.
5. **Operate the client bridge.** Deploy and own a consumer that reads the selected hub(s), checkpoints safely, handles replay/retention gaps, and maps source event identifiers, principal/resource IDs, operation, provider event time and outcome to the normalized contract. Keep Azure subscription events and Entra tenant events distinguishable in source identity and coverage. Omit raw records, request bodies, tokens, secrets, policy documents and credential values. Mark categories `Unavailable` or `Gap` unless the bridge can substantiate their source coverage; do not report all categories `Current` just because the consumer is healthy.
6. **Configure the server alias.** A server operator configures the HTTPS bridge URL, exact DNS allowlist, source/account IDs and secret through the server's secure configuration provider (see [Server alias configuration](#server-alias-configuration)). The endpoint must be publicly reachable by the server over HTTPS on port 443, use no URL query string, and implement the authenticated GET/cursor protocol below. Do not put its bearer secret in the Desktop, source control, screenshots or logs.
7. **Connect from YASK-CIEM.** Sign in to the intended organization, refresh feed status, select the configured alias, explicitly consent, then enable/connect. Load saved changes to confirm the bridge returned valid normalized batches. Health `Partial`, `Gap`, `Stale`, `Error`, or a category marked `Unavailable` is not a successful coverage validation.
8. **Validate the pipeline safely.** First use the bridge's own health/checkpoint diagnostics and Event Hubs metrics to confirm records are arriving and being consumed. To test transport, use an approved non-production subscription/resource and a benign control-plane event (for example, updating a test resource's tag) or an existing authorized Activity Log event; never change production IAM merely to make a test event. To test IAM event normalization, use only a dedicated non-production test identity and an explicitly approved, reversible role-assignment/policy change. Confirm the event reaches the Event Hub, is normalized by the bridge, appears in YASK-CIEM saved revisions with the expected source time and scope, and is not duplicated after replay. A generic resource event may verify transport without mapping to one of the feed's IAM categories.
9. **Test a fresh inventory scan separately.** The **Rescan this account** button queues the existing server scan schedule for the feed's provider/account; it does not read the Event Hub or trigger toxic-combination/attack-path analysis. Create a server scan schedule for the same bound subscription first and ensure the server scan worker has separately authorized Azure CLI credentials for that subscription. A completed scan can refresh the existing inventory/effective-access baseline, subject to source coverage; it does not prove complete feed coverage.

The repository's automated tests use fake bridges and PostgreSQL test databases; they do not require a live Azure subscription. A client live smoke test requires the administrator-created Azure destinations **and** a running client bridge. The app cannot complete that test by itself.

Each bridge must strip raw request/response payloads **before** output, normalize only safe identifiers, timestamps, key/credential metadata IDs and SHA-256 metadata digests, and maintain a durable bounded cursor. Metadata identifiers also reject base64 padding and recognizable JWT/AWS-session/access-key/Google-OAuth/GitHub-token prefixes. These checks are defense-in-depth, not proof that arbitrary identifier-shaped strings are non-secret: bridges must supply typed object identities or non-secret metadata-derived digest identifiers, never secret-derived digests or opaque credential values. Never forward `secretText`, access tokens, credential values, private keys, certificate bodies, policy bodies, arbitrary user text or raw CloudTrail/audit records. Hash only normalized safe metadata, not secret values. Credential metadata IDs are object identifiers, never credentials. Identifiers must obey the documented bounded grammar; the schema rejects unknown JSON fields. Client bridges must attest/validate provenance themselves; the server authenticates the configured bridge, not individual provider events.

## Server alias configuration

Configure through the server's secure configuration provider/environment, not Desktop, the organization API, source-controlled files or logs. Example **shape only**, with no real secret:

```text
ContinuousChangeFeeds:Organizations:<tenant-guid>:<organization-guid>:Sources:client-iam:
  Enabled = true
  Connected = true
  Provider = Azure | Aws | Gcp
  AccountId = <exact active-bound subscription/account/project>
  SourceId = <stable safe source identity>
  SourceVersion = <safe revision e.g. v1>
  Endpoint = https://bridge.client.example/normalized-changes
  AllowedHosts:0 = bridge.client.example
  BearerToken = <from server secure configuration provider>
  PollSeconds = 60
```

Environment keys use the normal ASP.NET Core `__` hierarchy separator. Aliases use lowercase ASCII letters/digits, hyphens or underscores (1–64 characters); case variants cannot create duplicate cursors. Server operators must control exact trusted DNS names, public HTTPS endpoints and outbound network restrictions; host allowlisting does not itself prevent DNS rebinding or a compromised allowed host. DNS/network trust remains operator-owned. IP-literal/loopback endpoints, non-HTTPS, non-443, wildcard hosts, embedded user info, fragments and endpoint query strings are rejected. The named HTTP client disables redirects and cookies. The secret is used only in the Authorization header to that alias's endpoint; safe status never contains the endpoint, secret or configuration fingerprint. Rotating the secret, changing endpoint/source/version or provider identity invalidates the old fingerprint; an explicit source epoch reset/reconnect is required. Changing provider/account requires a separate alias so old evidence cannot inherit another account's authorization.

Refresh status in **Continuous cloud IAM / trust / key-metadata change feeds**, select the alias, consent and **Enable and connect**. No network connectivity success is fabricated: initial state is Pending until a valid batch is committed. SourceChanged/NotConfigured/Blocked/Error/Stale/Gap are surfaced. Pause fences in-flight attempts and keeps immutable evidence. Refresh status and Load saved changes are database-only reads. Source resets retain old epochs and clear the new cursor/coverage; they do not repair previous evidence.

## Bridge protocol

Server sends authenticated `GET <configured-endpoint>?epoch=<guid>&sourceId=<id>&limit=500[&cursor=<opaque>]`. Response must be HTTP **200 application/json**, at most **2 MiB**, and match `ContinuousChangeBatch`:

```json
{
  "batchId": "33333333-3333-3333-3333-333333333333",
  "sourceEpoch": "<the epoch in the request>",
  "sourceId": "client-cloudtrail",
  "provider": 1,
  "accountId": "123456789012",
  "previousCursor": null,
  "cursor": "checkpoint-1",
  "sourceWatermarkUtc": "2026-11-08T18:00:00Z",
  "coverage": [
    {"category":"IamPolicy","state":"Backfill","sourceReportedComplete":false,"coveredFromUtc":"2026-11-08T17:00:00Z","coveredThroughUtc":"2026-11-08T18:00:00Z","gapCode":null},
    {"category":"RoleAssignment","state":"Unavailable","sourceReportedComplete":false,"coveredFromUtc":null,"coveredThroughUtc":null,"gapCode":"not_configured"},
    {"category":"WorkloadTrust","state":"Unavailable","sourceReportedComplete":false,"coveredFromUtc":null,"coveredThroughUtc":null,"gapCode":"not_configured"},
    {"category":"ServiceAccountPermission","state":"Unavailable","sourceReportedComplete":false,"coveredFromUtc":null,"coveredThroughUtc":null,"gapCode":"not_configured"},
    {"category":"CredentialMetadata","state":"Unavailable","sourceReportedComplete":false,"coveredFromUtc":null,"coveredThroughUtc":null,"gapCode":"not_configured"},
    {"category":"ServiceAccountKeyMetadata","state":"Unavailable","sourceReportedComplete":false,"coveredFromUtc":null,"coveredThroughUtc":null,"gapCode":"not_configured"}
  ],
  "changes": [{
    "sourceEventId":"cloudtrail-event-id",
    "revisionId":"normalized-v1",
    "category":"IamPolicy",
    "operation":"Changed",
    "principalId":"arn:aws:iam::123456789012:role/example",
    "resourceId":"arn:aws:iam::123456789012:policy/example",
    "sourceEventAtUtc":"2026-11-08T17:59:00Z",
    "providerObservedAtUtc":"2026-11-08T17:59:01Z",
    "beforeSha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
    "afterSha256":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
  }]
}
```

Existing provider numeric enum JSON is preserved: Azure=0, Aws=1, Gcp=2. Identifiers/cursors match `[A-Za-z0-9][A-Za-z0-9._:@/+=,-]*`; source IDs ≤128, cursors ≤512, event/principal/resource IDs ≤256, revision and credential metadata IDs ≤128. Categories and operations are fixed allowlists. Added requires only after digest, Removed only before digest, Changed both. Source/provider times cannot be future or before Unix epoch; provider-observation time must follow source event time and not exceed the batch watermark. Coverage through must not exceed watermark. Digests are exactly 64 hex characters. For provider events without an exact safe object identity, use a stable non-secret digest-based identifier and report the normalization gap.

Return **empty changes with a fresh durable checkpoint and explicit coverage** for heartbeats; never return 204, redirect, fabricated data or an unchanged cursor. Bridge must return identical batch content/ID when replaying an uncertain response; do not discard provider events merely because a request was made. The bridge must support restarting from the **server committed** cursor, retain source revisions, handle overlapping events and preserve source epochs. ACK/provider delivery semantics, Event Hub partitions, EventBridge ordering, Pub/Sub ack/replay, retention gaps and initial state backfill are client bridge-owned. A batch ID or `(source-event-ID,revision-ID)` reused with different metadata is a conflict, not success.

## Durability and operations

Migration `013-continuous-change-feeds.sql` stores persistent consent, source fingerprint/epoch, committed cursor, watermark, health and attempts. Claim uses row locks/SKIP LOCKED; leases expire after 90 seconds and fence stale completions. Transport is bounded to 20 seconds/500 changes/2 MiB; one feed is processed at a time per worker process. A single transaction inserts immutable normalized revisions and batch receipt/coverage, deduplicates overlaps, records operational events/outbox and advances the cursor. Exact replay returns the original receipt without regressing a later cursor or duplicating outbox entries. Conflicting revisions/cursors/source scope, stale/paused leases and failed persistence roll back; no silent empty-success fallback. Expired attempts become Expired on reclaim, paused/reconfigured ones Superseded. Failure retries are 30, 60, 120… seconds capped at one hour, with safe failure codes and preserved cursor. Staleness is derived from last successful poll versus configured interval.

Notification rules can explicitly select `cloud-change.observed`, `cloud-change.coverage-gap`, `cloud-change.poll-failed`; events are observed changes, not verified privilege escalation. Delivery uses the existing durable outbox and channel opt-in. Notification rules with only legacy finding/access filters do not silently subscribe to feed events. Worker readiness requires the `continuous-change-feeds` heartbeat; the heartbeat reflects actual idle/finished polling loop progress, not a fake success timer.

No live provider/bridge calls or deployments were performed while implementing this feature. Deployment validation must include actual client delivery, least-privilege permissions, bridge TLS/DNS/egress controls, backlog/retention/backfill and category provenance. Saved UI revisions are the latest 100; API is bounded to 500 and is not an unbounded export API.

Official setup references:
* [Azure Activity Log / export](https://learn.microsoft.com/en-us/azure/azure-monitor/fundamentals/activity-log)
* [Azure Monitor diagnostic settings](https://learn.microsoft.com/en-us/azure/azure-monitor/data-collection/diagnostic-settings)
* [Stream Azure Monitor data to Event Hubs](https://learn.microsoft.com/en-us/azure/azure-monitor/data-collection/stream-monitoring-data-event-hubs)
* [Stream Microsoft Entra logs to Event Hubs](https://learn.microsoft.com/en-us/entra/identity/monitoring-health/howto-stream-logs-to-event-hub)
* [IAM and STS CloudTrail events](https://docs.aws.amazon.com/IAM/latest/UserGuide/cloudtrail-integration.html)
* [CloudTrail events in EventBridge](https://docs.aws.amazon.com/eventbridge/latest/userguide/eb-service-event-cloudtrail.html)
* [Cloud Logging sinks and destination authorization](https://docs.cloud.google.com/logging/docs/export/configure_export_v2)
