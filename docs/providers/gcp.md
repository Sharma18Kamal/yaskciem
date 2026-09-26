# GCP Read-Only Connector

The GCP adapter lives in `CloudEntitle.Cloud.Gcp` and maps Google Cloud projects and IAM policy data into the common CIEM Domain model. WPF does not call Google APIs or `gcloud` directly.

## Authentication

The connector supports the authenticated `gcloud` path and includes a `GcpCredentialFactory` using Google Application Default Credentials. OAuth/browser authentication and explicitly configured service-account credentials can be added through the Google SDK without persisting private keys or tokens.

## Required Read-Only Permissions

At project scope, use read-only permissions for project metadata and IAM policy inspection. Organization/folder discovery requires Resource Manager list/get permissions at those hierarchy scopes. Service-account discovery requires service-account read permissions. Role permission details and Cloud Asset/Audit activity require additional read-only permissions and are optional.

## Discovery and Normalization

- Projects map to `CloudAccount` with `ProjectId` preserved as the provider identifier and `AccountType.Project`.
- Project resources map to `CloudResource` with the original `projects/{projectId}` scope.
- `user:`, `group:`, and `serviceAccount:` IAM members map to `CloudPrincipal` with the matching identity type.
- Groups map to `CloudGroup`.
- IAM roles map to `CloudRole`; role identifiers remain unchanged.
- IAM bindings map to `CloudPolicy`, `CloudPermission`, and `Entitlement`.
- Permission identifiers such as `storage.objects.get` are preserved as the normalized Action.
- IAM conditions are preserved in the policy document and permission source marker; they are not discarded.
- Project inheritance is retained through the original project scope and assignment source. Effective-access calculation remains a later milestone.

## Reliability

`GcpProvider.ScanAsync` supports cancellation, bounded exponential retry, partial project errors, and normalized `Scan` status. `gcloud` list commands provide pagination support through the CLI; SDK-backed expansion should continue tokens until exhaustion.

## Activity Limitations

Cloud Audit Logs and Cloud Asset Inventory activity are optional. If activity metadata is unavailable, the connector leaves last-activity information unknown and does not claim identities are dormant.

## Read-Only Guarantee

The connector only invokes authentication, project listing, IAM policy, service-account listing, and read-only metadata commands. It does not create or modify projects, service accounts, roles, credentials, policies, or bindings.

## Troubleshooting

- Authentication failure: run `gcloud auth application-default login` or verify the active `gcloud` account.
- Permission failure: grant the minimum Resource Manager, IAM, and service-account read permissions at the relevant scope.
- Quota/rate limit: retry with the bounded connector backoff and reduce scan scope.
- Missing organization/folder data: verify hierarchy permissions; project-level discovery can still succeed independently.
