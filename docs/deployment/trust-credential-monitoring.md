# Trust and credential monitoring

## Scope and operating model

This feature is an explicitly requested, client-side read for one selected, bound Azure subscription, AWS account, or GCP project. It does not poll in the background, create cloud resources, alter identities or permissions, or run cloud writes. The client must be connected to the selected account and the user must authorize each collection. A snapshot is uploaded only after the collector finishes successfully.

Each provider has a bounded source:

* **Azure:** Azure CLI verifies the selected subscription and reads Microsoft Graph Entra service-principal password/key metadata and federated credentials on locally owned applications. Collection is bounded to 5,000 service principals, 20 Graph pages, 20,000 observations, four minutes, and a 16 MiB response. Failed federated-credential reads mark trust inventory incomplete. Azure RBAC trust and external application-owner federation are not covered.
* **AWS:** AWS CLI verifies the selected 12-digit account, reads IAM role `AssumeRolePolicyDocument` trust statements, and attempts the read-only `iam get-credential-report`. It never generates or refreshes a credential report. A `COMPLETE` report is required for credential inventory to be marked complete; the report's `GeneratedTime` is retained as a limitation because its metadata can be stale. Missing reports leave credential inventory incomplete. Collection is bounded to 5,000 roles, 20,000 observations, four minutes, and a 16 MiB response. SCPs, resource policies, service-specific trust, external identity-provider configuration, and organization-wide scope are not covered.
* **GCP:** gcloud verifies the selected project and reads all direct IAM bindings at the project and service-account levels, including impersonation grants, plus **user-managed** service-account key metadata. Collection is bounded to 1,000 service accounts, 100 user-managed keys per service account, 20,000 observations, four minutes, and a 16 MiB response. A failed per-account key or IAM-policy read marks the corresponding inventory incomplete. System-managed keys, inherited organization/folder policies, workforce identity pools, key use, and other projects are not covered. Custom role definitions are not resolved.

All provider coverage remains **Partial** at the feature level. Completeness flags apply only to the explicitly stated bounded inventories; they do not imply full cloud or organization coverage. These are point-in-time observations, not provider change feeds. Snapshot timestamps describe observation windows, not the time a provider changed an object. Secret values, access-key secret material, private keys, tokens, certificate bytes, and other credential contents are neither requested nor stored.

## Client configuration and consent

1. Bind the exact Azure subscription, AWS account, or GCP project in the organization workspace.
2. On the client, connect the corresponding CLI to that exact account only when preparing to collect: Azure CLI, AWS CLI, or gcloud.
3. Grant only the provider read permissions required for the selected source. Azure requires Microsoft Graph `Application.Read.All` admin consent. AWS requires IAM reads for role trust policies and `get-credential-report`; do not grant this feature report-generation permissions. GCP requires reads for service-account inventory, user-managed keys, project IAM policy, and service-account IAM policy.
4. Insert `CloudEntitle.Desktop.Views.TrustCredentialMonitoringView` into the organization workspace at the monitoring section. Select a bound account, check the per-run consent box, and choose **Collect and save**. No live source call occurs until this explicit action.
5. Review the saved observations and potential changes. The first snapshot establishes a baseline. Later analyses use the latest nonoverlapping prior snapshot among at most 50 saved summaries.

If source consent, CLI connection, or account verification is missing, collection fails without publishing a successful snapshot. Some per-resource AWS/GCP reads may be incomplete while successful evidence is saved; those snapshots clearly mark incomplete source dimensions, and missing entries do not imply removal. Do not enter cloud credentials into the UI.

## Monitoring and interpretation

The collector compares stable provider credential/trust identities and hashes observed metadata. Complete, comparable observations can report `Added`, `Changed`, and `NoLongerObserved`; incomplete inventory suppresses removal claims and marks uncertain differences as potential. AWS report age and provider-specific scope limits remain visible as limitations. A difference does not prove a provider-side change time, credential use, or a resulting permission change.

For effective privilege monitoring, select two chronological saved scans of the same bound account. The implementation compares newly observed or newly privileged assignment records. Those records are not authoritative provider authorization decisions, so every current signal is `Potential`; none is `Verified`. A `Verified` escalation requires authoritative, comparable effective before/after provider decision evidence. A trust or credential change alone is never treated as proof of effective escalation. Partial scans cannot establish absence or removal.

Potential monitoring events (`trust-credentials.observedchange` and `privilege-escalation.potential`) are written to the existing operational-event stream and can be delivered only through the organization’s existing notification rules, outbox, and connector configuration. No new SIEM, ticketing, or automatic-remediation configuration is enabled.

## Server rollout

Apply `infra/postgres/011-trust-credential-monitoring.sql` as the DBA identity after migration 010 and before migration 012. The migration's logical resource name is `CloudEntitle.LicenseServer.TrustCredentialSchema.sql`. It creates `SharedTrustCredentialSnapshots`, index `IX_SharedTrustCredentialSnapshots_AccountTime`, triggers `TR_SharedTrustCredentialSnapshots_AppendOnly` and `TR_SharedTrustCredentialSnapshots_NoTruncate`, and grants the runtime role only `SELECT` and `INSERT`; row update/delete and table truncation are guarded by the shared evidence-mutation triggers.

Before deploying, include migration 011 as an embedded resource with that logical name in the license-server project and test project, add it to the shared test migration initialization sequence and staging-package manifest, and map the public extension `MapTrustCredentialMonitoringEndpoints(this WebApplication app, SharedWorkspaceStore store)` from `MapSharedWorkspaceEndpoints`. The Desktop registration uses `MultiCloudTrustCredentialEvidenceCollector(ICloudCommandRunner)`. The UserControl namespace is `CloudEntitle.Desktop.Views`, class `TrustCredentialMonitoringView`. Endpoints require `CiemApiAccess`; saving and analyzing require `ManagePolicies`, `ViewInventory`, `ViewFindings`, and an exact bound account. History and snapshot reads require the existing inventory/finding monitoring authorization.

The API exposes:

* `POST /api/v1/organizations/{organizationId}/trust-credential-evidence`
* `GET /api/v1/organizations/{organizationId}/trust-credential-evidence?provider=Azure|Aws|Gcp&accountId=...`
* `GET /api/v1/organizations/{organizationId}/trust-credential-evidence/{snapshotId}?provider=Azure|Aws|Gcp&accountId=...`
* `POST /api/v1/organizations/{organizationId}/trust-credential-monitoring/analyze`

Analysis reads saved snapshots and scan records only. The API does not connect to a cloud provider or perform a cloud mutation.

`src/CloudEntitle.LicenseServer/TrustCredentialMonitoring.http` contains placeholder-host examples for every route, saved-history reads for all three providers, and invalid/unbound evidence requests. These are documentation examples, not live-source calls; replace illustrative payloads with genuine consented collector output.
