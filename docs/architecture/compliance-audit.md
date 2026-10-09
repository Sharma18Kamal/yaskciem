# Compliance and Audit

The Compliance module assesses evidence already collected by CIEM. It does not call cloud APIs or decide provider authorization. Controls consume read-only accounts, EffectiveAccess, AccessPaths, findings, identity governance records, and audit events.

Supported assessment profiles:

- CIEM baseline: read-only posture, evidence-backed access, high-risk visibility.
- Least privilege: fully evaluated allowed access and privileged access paths.
- Identity governance: explicit activity/status knowledge and external identity visibility.
- Access review: privileged-access evidence and audit-event availability.

Control results are `Pass`, `Fail`, `Partial`, or `NotAssessed`; evidence and gaps are retained for review. Missing data is disclosed rather than treated as a pass.

Assessments can crosswalk the internal control set to CIS Controls v8.1, NIST SP 800-53 Rev. 5, ISO/IEC 27001:2022, and SOC 2 Trust Services Criteria (2017). Each mapping carries its source CIEM control ID, framework version, mapping-catalog version, relationship, reference, and analyst-authored rationale. Control evidence lineage includes source type and ID, cloud-account ID where applicable, collection time when known, and a SHA-256 digest of the evidence description used by the assessment. Existing history entries without mapping metadata remain labeled unversioned rather than being assigned the current catalog version.

Assessments may include explicit control exceptions with a reference, rationale, recorder, recorded time, expiry, and optional evidence reference. Active and expired exceptions are exported and attached to control lineage, but do not change the control's Pass/Fail/Partial/NotAssessed state. These are recorded exception statements, not proof of an independently approved waiver.

`AuditEventService` creates audit records while filtering sensitive metadata keys such as passwords, tokens, secrets, private keys, and credential keys. Audit configuration must never contain credentials.

The desktop Dashboard can export an organization-scoped compliance evidence bundle for CIEM Baseline, Least Privilege, Identity Governance, or Access Review and select one or more standards for crosswalk. The ZIP contains `manifest.json`, `assessment.json`, spreadsheet-safe `controls.csv`, `control-mappings.csv`, `evidence-lineage.json`, and `signature.json`. The manifest includes SHA-256 digests for each payload and is signed with ECDSA P-256/SHA-256. The persistent non-exportable signing key is stored in the current Windows user's CNG key store. Recipients can verify the signature and payload digests with `ComplianceBundleSignatureVerifier`; they must trust the public-key fingerprint out of band to establish signer identity. Account and evidence selection is enforced by the export workflow using the authenticated organization membership and stable provider plus external-account bindings; a Viewer cannot export.

The signed bundle uses manifest schema 1.2 and includes `assessment.json`, `controls.csv`, versioned `control-mappings.csv`, `evidence-lineage.json`, `control-lineage.json`, `exceptions.json`, and `signature.json`. The manifest contains each artifact digest, mapping-catalog version, exception counts, and an explicit `isCertification: false` declaration. The Dashboard labels exports as evidence snapshots, not certification.

The dashboard retains up to 200 assessment snapshots per framework and organization in `%LOCALAPPDATA%\YASK-CIEM\compliance\assessments.json`; trend history includes the mapping-catalog version and exception count. History is local to that Windows profile and is not shared between devices.

The shared License Server exposes authenticated `POST /api/v1/audit/events` and `GET /api/v1/audit/events` routes. Tenant and actor IDs are derived from validated Entra `tid`/`oid` claims, not accepted from the request body. Append requires `access_as_user` or `Ciem.Api.Access`; reads additionally require the `audit.read` scope or `Ciem.Audit.Read` role. Events have stable IDs for retry deduplication, sensitive metadata and secret-like description values are redacted, and records carry sequence numbers, a minimum-retention deadline, and an HMAC-SHA256 chain. Chain validation fails closed on read or append after detected corruption. `CiemApiClient` exposes append/read methods for authenticated clients.

To enable the explicit Organizations-workspace sync action in Desktop, set `YASK_CIEM_API_BASE_URL` to the License Server HTTPS base URL and `YASK_CIEM_API_SCOPE` to the delegated scope (for example `api://<api-client-id>/access_as_user`). Entra sign-in requests Graph `User.Read` and, separately, the configured API scope so each access token has one resource audience. The signed-in user must have report-export permission in the selected local organization. Sync uploads only that organization's local audit rows; retrying sends the same event IDs and is deduplicated by the server. Sync is manual and requires network access; failed uploads remain in the local database for a later retry.

Production central audit storage uses `AuditRetention__Provider=Postgres`, `ConnectionStrings__Postgres`, `AuditRetention__MinimumRetentionDays`, and a Base64 `AuditRetention__IntegrityKey` supplied through the host's secret provider. Apply `infra/postgres/001-schema.sql` as a DBA-managed migration. It installs the workspace/audit tables, rejects ordinary audit-row updates/deletes with a trigger, and grants the `yask_ciem_runtime` role only the operations required by the application. Create a login role separately and add it to that runtime role; do not run the application as the schema owner. Readiness remains HTTP 503 until both schemas and the database connection are available.

The PostgreSQL audit store serializes tenant writes transactionally, deduplicates event IDs, and validates a per-tenant HMAC chain. The trigger and runtime grants are defense in depth, not storage-enforced WORM: database owners/superusers can bypass them. Where regulations require immutable retention, export sealed audit segments to an independently administered on-premises WORM archive and test backup/restore and key rotation. The local file backend remains Development-only and is never a production fallback. Existing SQLite workspace data is not automatically migrated; export and validate it before cutover.

The bundles and central audit service are evidence mechanisms, not an audit certification or provider authorization decision. Organization membership remains local to the desktop, while the central audit API scopes records only to the authenticated Entra tenant; it is not a shared organization-membership or findings API.
