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
