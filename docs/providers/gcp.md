# GCP Read-Only Connector

The GCP adapter lives in `CloudEntitle.Cloud.Gcp` and maps Google Cloud projects, hierarchy, Cloud Asset Inventory resources, and IAM policy data into the common CIEM Domain model. WPF does not call Google APIs or `gcloud` directly.

## Authentication

The connector supports the authenticated `gcloud` path and includes a `GcpCredentialFactory` using Google Application Default Credentials. OAuth/browser authentication and explicitly configured service-account credentials can be added through the Google SDK without persisting private keys or tokens.

## Required Read-Only Permissions

At project scope, use read-only permissions for project metadata and IAM policy inspection. Ancestor discovery requires Resource Manager permissions at the relevant project, folder, and organization scopes. Cloud Asset Inventory resource discovery requires the API to be enabled and `cloudasset.assets.searchAllResources` on each project scope. Inherited IAM discovery also requires `cloudasset.assets.searchAllIamPolicies` on each ancestor folder and organization. Project IAM change timestamps require `logging.logEntries.list` for Cloud Audit Logs. Service-account discovery requires service-account read permissions. Role permission details and audit activity require additional read-only permissions and are optional.

## Onboarding and Coverage Validation

Authenticate with `gcloud auth login` using a dedicated read-only identity, and grant access only at the projects and ancestor scopes intended for analysis. Discover accessible projects in YASK-CIEM to confirm visible account scopes, then run a read-only scan and review source coverage. Project discovery or successful IAM inspection does not prove that Cloud Asset Inventory, inherited ancestor policies, or Cloud Audit Logs are available. Revoke onboarding access through your Google Cloud IAM process by removing the scoped role bindings or disabling the dedicated identity.

## Discovery and Normalization

- Projects map to `CloudAccount` with `ProjectId` preserved as the provider identifier and `AccountType.Project`.
- `gcloud projects get-ancestors` adds visible organization and folder hierarchy rows; project rows retain their nearest discovered parent identifier. Hierarchy rows are metadata and are not independently scanned as projects.
- `gcloud asset search-all-resources --scope=projects/{projectId}` discovers project resources with CAI-provided resource name, type, display name, location, labels, and parent name. A CAI permission failure is reported independently and does not stop project IAM inspection.
- Project resources map to `CloudResource` with the original `projects/{projectId}` scope.
- `user:`, `group:`, and `serviceAccount:` IAM members map to `CloudPrincipal` with the matching identity type.
- Groups map to `CloudGroup`.
- IAM roles map to `CloudRole`; role identifiers remain unchanged.
- IAM bindings map to `CloudPolicy`, `CloudPermission`, and `Entitlement`.
- Permission identifiers such as `storage.objects.get` are preserved as the normalized Action.
- IAM conditions are preserved in the policy document and permission source marker; they are not discarded.
- Direct project bindings and organization/folder policies returned by Cloud Asset Inventory are normalized into effective access. Inherited rows preserve the exact policy attachment scope and are labeled `Inherited`; hierarchy membership alone is not treated as proof of a grant.
- If an ancestor policy query is denied or unavailable, direct and any successfully discovered ancestor bindings are retained and the incomplete inherited-policy lookup is reported.

## Reliability

`GcpProvider.ScanAsync` supports cancellation, bounded exponential retry, partial project errors, and normalized `Scan` status. `gcloud` list commands provide pagination support through the CLI; Cloud Asset Inventory pagination is handled by the CLI.

## Activity Limitations

Cloud Audit Logs are optional. Project `SetIamPolicy` events from the last 90 days populate `Last changed` only for matching direct role/member bindings. Inherited folder and organization bindings remain without a change timestamp because ancestor audit logs are not queried. If activity metadata is unavailable, the connector leaves it unknown and does not claim identities are dormant.

## Read-Only Guarantee

The connector only invokes authentication, project and ancestor listing, IAM policy, service-account listing, Cloud Asset Inventory search, and Cloud Logging read commands. It does not create or modify projects, folders, organizations, service accounts, roles, credentials, policies, or bindings.

## Troubleshooting

- Authentication failure: run `gcloud auth application-default login` or verify the active `gcloud` account.
- Permission failure: grant the minimum Resource Manager, IAM, and service-account read permissions at the relevant scope.
- Quota/rate limit: retry with the bounded connector backoff and reduce scan scope.
- Missing organization/folder data: verify hierarchy permissions; project-level discovery can still succeed independently.
