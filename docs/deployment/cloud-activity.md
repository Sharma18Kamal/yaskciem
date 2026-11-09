# Client-owned cloud activity sources

In Organizations, select a bound account and activity source, prepare the indicated
logging/read access, enter its identifiers and explicitly authorize one read.
Destinations are provisioned **by the client on demand**. Neither sign-in nor
normal scans create endpoints, enable logs, grant permissions or start this read.
Shared mode requires RunScan, ViewInventory and ViewFindings and migration 007.
Loading saved evidence uses only the organization API.

| Source | Destination and fields | Client setup/read access |
|---|---|---|
| AWS default | Region; optional CLI profile | `sts:GetCallerIdentity`, `cloudtrail:LookupEvents`; management history only |
| AWS CloudTrail Lake | Existing event data store UUID, region/profile | Required management/data event selectors and 90-day retention; `cloudtrail:GetEventDataStore`, `StartQuery`, `GetQueryResults` |
| AWS S3 archive | Bucket NAME, region/profile, account-specific object prefix | Client CloudTrail trail with required data events; `s3:ListBucket`, `GetObject`, KMS decrypt if applicable; bucket owner must equal the bound account |
| Azure default | Bound subscription | Activity Log read; control-plane only |
| Azure Log Analytics | Exact workspace ARM resource ID in the bound subscription | Client resource diagnostic settings and retention; workspace read plus Logs query access |
| Azure Blob archive | Exact storage-account ARM resource ID, `insights-logs-*` container, resource/date prefix | Client resource diagnostics, storage-account read and Storage Blob Data Reader; Entra login only |
| GCP default | Bound project | Enable appropriate Data Access logs and retention; `logging.logEntries.list` and private-log access (`roles/logging.privateLogViewer` as appropriate) |

Do not enter tokens, account keys, passwords or signed URLs. Native cloud policies
still enforce scope; organization membership does not grant cloud access.
Clients must independently verify enabled categories, resource coverage, routing,
exclusions, retention and audit delivery health.

## AWS source notes

CloudTrail Lake is restricted to existing customers after **31 May 2026**.
New clients should use S3 archives. Lake starts a billable query with fixed
provider/account/time scope. Explicit consent includes client query charges.
Cancelling or timing out local collection does **not** cancel the AWS query:
monitor it in the CloudTrail console and cancel there if needed.

Example S3 prefix:
`optional-base/AWSLogs/123456789012/CloudTrail/us-east-1/2026/11/07/`.
For organization trails include the existing organization base path. Prefer a
narrow date prefix; broad prefixes can hit the 128-file limit before recent logs
are reached. Cross-account central buckets are not supported by this reader.
Only native CloudTrail `.json.gz` archives are decoded; digest files and custom
formats are not imported.

API event source/name is preserved, not translated into an IAM action.
Lake's scalar query cannot reliably attribute resources/roles. S3 retains resource
ARNs explicitly present in source records. Missing resource/identity evidence is
not filled with a guessed value.

## Azure source notes

Workspace resource ID:
`/subscriptions/<subscription-guid>/resourceGroups/<group>/providers/Microsoft.OperationalInsights/workspaces/<workspace>`.

Storage resource ID:
`/subscriptions/<subscription-guid>/resourceGroups/<group>/providers/Microsoft.Storage/storageAccounts/<account>`.

The storage container must be a client diagnostic container (`insights-logs-*`).
Use its real resource/date prefix, for example
`resourceId=/SUBSCRIPTIONS/<guid>/RESOURCEGROUPS/<group>/PROVIDERS/MICROSOFT.KEYVAULT/VAULTS/<vault>/y=2026/m=11/d=07/`.
Only native `.json` documents containing `records` are supported, not arbitrary
JSONL/CSV/custom exports. ARM resource IDs in every imported record must belong
to the selected subscription.

Workspace queries use the Logs REST API, so its query operation uses HTTP POST
but does not mutate cloud permissions. The fixed schema currently supports
Storage Blob/File/Queue/Table logs and AzureDiagnostics; other resource-specific
tables, including dedicated Key Vault tables, are not yet included. Unsupported
identity/result schemas remain unknown. A denied/failed/partial-error query does
not publish a successful empty snapshot. Azure Activity Log permission evidence
comes from `authorization.action`; data-plane operation names are not assumed
to equal IAM permissions.

## Evidence, limits and safety

All sources report **Partial**. A selected destination does not establish complete
90-day audit coverage. Action frequency uses inclusive 30/60/90-day windows and
last successful time. Unknown outcomes are not counted as failures. Downloads
use conditional etags and explicit account checks; raw files are deleted after
processing, but a process crash may leave a transient file in the user's temp
directory. Do not treat that directory as an encrypted evidence vault.

Limits: 5,000 source query rows (one extra detects truncation), 128 archive files and 64 listing pages,
8 MiB per downloaded object, 16 MiB decompressed per object, 32 MiB decompressed
total, 20,000 normalized rows, and five minutes per collection. Azure default
Activity Log stops at page boundaries. Over-limit, invalid and foreign-scope
evidence fails explicitly. File-count/query-result truncation is displayed.
Retained source scopes and SHA-256 content digests are client provenance, not
provider-signed attestations.

Current-inventory correlation requires a matching non-demo bound account, exact
principal/resource identity and explicit successful permission evidence. It
returns compatible current entitlement IDs or ambiguity/unresolved statuses.
It does not expand wildcard/role names or claim which historical grant authorized
an action. Without matching inventory, attribution is explicitly unavailable.
Saved overlapping sources are not merged and continuous cursors are not yet
implemented.

## Local permission-usage analytics

The Desktop **Permission Usage** workspace analyzes saved effective-access
inventory and normalized activity in the local SQLite database. Use **Analyze
saved evidence** to review existing data. The separate **Collect and analyze**
action requires selecting a non-demo account and explicitly authorizing a
read-only audit query; it never creates or configures a client endpoint or turns
on provider logging. Configure and validate audit sources and their read access
as described above before collection.

The workspace reports used, not observed, insufficient-observation, unknown
mapping, and incomplete-collection states for 30/60/90-day windows. It preserves
provider event IDs for deduplication, displays collection-window status and
truncation, and retains daily/weekly/monthly activity summaries. Azure
`authorization.action` and GCP `authorizationInfo.permission` are exact evidence
when supplied by the source. AWS API operations are not generally IAM actions;
only explicitly curated mappings are candidates. A match does not prove which
grant authorized an event, especially for inherited, delegated, cross-account,
or historical access.

No events never proves a permission is unused. Incomplete source coverage,
unknown mappings, retention gaps, and insufficient observation time remain
inconclusive. Findings are potential, evidence-linked review signals; generated
recommendations are non-executable. This milestone does not modify or revoke
permissions. Demo Mode creates clearly labeled synthetic multi-cloud activity
and coverage; it is not client telemetry.

Historical activity summaries describe observed API usage, not historical
entitlement validity. This module does not reconstruct past grants or attribute
actions to the exact historical role/policy that authorized them. Activity
evidence is retained in local SQLite; configure OS/database protections
appropriate for the sensitivity and retention needs of the client.

### Saved historical browsing

The Desktop's **Refresh saved history** reads snapshot summaries, **Older saved history**
pages older summaries, and **Load selected/latest saved** reads a selected snapshot
(or the latest when none is selected). None of these commands polls a cloud or creates
logging endpoints. Changing account/source/destination or signing out clears selection,
history and pending evidence. Errors are shown explicitly.

The API supports:

```text
GET /api/v1/organizations/<org>/cloud-activity/history?provider=Aws&accountId=<bound-account>&limit=20
GET /api/v1/organizations/<org>/cloud-activity/history?provider=Aws&accountId=<bound-account>&limit=20&beforeSnapshotId=<cursor>
GET /api/v1/organizations/<org>/cloud-activity/history/<snapshot-id>?provider=Aws&accountId=<bound-account>
```

Reads require organization `ViewInventory`/`ViewFindings` and the exact account binding.
Limits are 1-50 summaries per page (default 20), ordered by evidence-through time and
snapshot ID, newest first. Follow `nextBeforeSnapshotId` as an exclusive cursor.
Unknown/foreign cursors return 400; an absent scoped snapshot returns 404. Cursor reads
are stable for existing snapshots with equal timestamps; concurrent uploads can add
evidence, so refresh from the first page to see newer rows. No deletion/retention API
is introduced. Examples: [CloudActivity.http](../../src/CloudEntitle.LicenseServer/CloudActivity.http).

This is **historical activity evidence**, not historical entitlement validity. Selected
snapshots are analyzed independently using their own 90-day anchor; overlapping snapshots
are never summed or merged. Grant correlation still uses current inventory only and
cannot prove the role/policy that authorized a historical action. The separate
permission-document panel below provides prior observed-document compatibility.

## On-demand permission documents and saved observations

Apply [migration 009](../../infra/postgres/009-entitlement-evidence.sql) after 008
using the DBA identity before deploying this version. Startup does not migrate.
Readiness requires the table, account/time index and enabled append-only guards.
The staging bundle includes the migration; installation alone does not apply it.

In Organizations, select the bound account, configure/connect its CLI identity
using the existing cloud connection workflow, review the setup instructions and
tick the separate document-read consent before **Read and save permission documents**.
Organization `ManagePolicies`, `ViewInventory` and `ViewFindings` are required.
Organization permissions never confer cloud permissions. Reads verify the exact
bound account first and do not create endpoints, change policies or enable logs.
Sign-in and normal inventory scans do not trigger this document collector.
Changing the account/source/profile or signing out clears consent and evidence
and cancels pending work.

| Provider | Read access and observed documents | Exclusions |
|---|---|---|
| Azure | Account read, `Microsoft.Authorization/roleAssignments/read`, `roleDefinitions/read`; subscription role assignments and definition Actions/NotActions/DataActions/NotDataActions | No management-group inheritance, Entra group expansion, PIM, deny-assignment or ABAC evaluation. NotActions are exclusions, not explicit Deny. |
| AWS | `sts:GetCallerIdentity`, `iam:GetAccountAuthorizationDetails`; identity inline/default-version managed policy documents and observed IAM user group membership; optional Activity profile | No trust/resource/session policies, SCP/RCP or permissions-boundary evaluation. NotAction/NotResource statements are not modeled. Public AWS partition only. |
| GCP | Project describe/getIamPolicy plus predefined/custom role get permissions; project IAM etag and included role permissions | No inherited/resource IAM, deny, groups, principal access boundaries or impersonation-control evaluation. |

Source IDs, versions, document SHA-256 digests, Allow/Deny statements and condition
flags are retained. Condition expressions are not retained or evaluated. These
are provider-source documents observed during collection, **not complete effective
access**, atomic snapshots or provider-attested evidence. Coverage remains Partial.
Empty/missing optional AWS inventory lists do not prove absence of effective access.

**Refresh saved document history** returns the newest 50 summaries. Select a summary
and **Load selected documents (saved)** to read saved evidence without a cloud call.
After loading activity, **Compare historical observations (saved)** compares at most five prior
document snapshots from those summaries. Only exact successful Azure
`authorization.action` or GCP `authorizationInfo.permission`, principal and
supported resource scope evidence can yield a compatible prior observed grant.
AWS API event names are never guessed into IAM actions. Future observations are
excluded; timestamp ties and multiple grants remain ambiguous. Missing permissions,
wildcards, exclusions, conditions and unsupported resources remain unresolved.
Collection time is not a grant-valid-from/to interval. A compatible prior document
does not prove it authorized a historical call; no unused/removal inference follows.

**Evaluate document risk candidates** reuses the access-path/risk engine with
matching non-demo inventory and observed permission actions. Matching never uses
principal display names. Toxic-storage combinations and modeled identity/resource
paths are Potential, with partially evaluated authorization. Uncollected denies
or runtime controls may block them. They do not replace confirmed scan findings,
approve remediation or implement cross-account/federated traversal.
Normal live scans additionally run stale/expired-credential detection only when
the provider reports complete credential metadata; this is not complete lifecycle
inventory, orphan or dormant-account detection.

Collection limits: five minutes, 16 MiB characters per CLI response, 128 distinct
Azure/GCP role definitions and 20,000 emitted statements. Exceeding a collection
limit fails rather than publishing truncated success. API upload bodies are
bounded to 8 MiB before JSON deserialization, including chunked requests; saved
payloads have the same bound. Document risk evaluation caps modeled candidates
at 20,000. Failures are shown explicitly. After an uncertain upload, refresh saved
history to reconcile before retrying.

The API provides organization-scoped POST upload and GET history/selected snapshot:
see [EntitlementEvidence.http](../../src/CloudEntitle.LicenseServer/EntitlementEvidence.http).
Upload replay is idempotent; changing a saved snapshot ID's payload returns 409.
Read access requires ViewInventory/ViewFindings and exact account binding.
Runtime storage grants allow SELECT/INSERT only. UPDATE/DELETE/TRUNCATE guards
are checked by readiness; DBAs can alter them, so this is not WORM storage.
Include the table in coordinated backups. No retention/pruning is implemented.

**Automatic least-privilege removal and SOAR response execution remain disabled.**
Complete category/resource/time coverage, authoritative grant attribution and
provider-attested historical entitlement validity are still required. Complete
cross-account/federated paths, lifecycle coverage and approved SOAR execution
remain pending. Separate opt-in [Azure PIM/GCP PAM and client-broker AWS
request/approval workflows](priority-two.md) are available; document/behavior
candidates never approve their grants. [Trust/credential metadata observations](trust-credential-monitoring.md)
remain partial and on-demand, not a complete continuous cloud change feed.

## Client acceptance

Before live use, prepare a disposable scope with client consent:

1. Emit a successful and denied data-plane operation using a known test identity.
2. Confirm the source category and audit delivery, then read the selected source.
3. Verify account/resource IDs, times, known/unknown outcomes, source scope and
   digest provenance. Confirm method names are not invented permission evidence.
4. Verify missing logs, unsupported schemas, narrow prefixes and truncation are
   explicitly partial; they must never produce an unused/removal decision.
5. Change destination/source and confirm consent resets. Sign-out cancels pending
   activity work. Reload saved evidence and confirm no cloud command runs.
6. For Lake, check client query cost/status separately, including local cancellation.

Development validation uses simulated CLI/API responses and local archive files,
not live client destinations or cloud permission changes.

References: [CloudTrail Lake query results](https://docs.aws.amazon.com/cli/latest/reference/cloudtrail/get-query-results.html),
[Azure Storage logs](https://learn.microsoft.com/en-us/azure/storage/blobs/monitor-blob-storage-reference),
[Key Vault diagnostic format](https://learn.microsoft.com/en-us/azure/key-vault/general/logging),
[GCP audit logs](https://cloud.google.com/logging/docs/audit).
