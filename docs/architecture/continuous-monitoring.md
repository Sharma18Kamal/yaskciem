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
