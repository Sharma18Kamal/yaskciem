# AWS Read-Only Connector

The AWS adapter lives in `CloudEntitle.Cloud.Aws` and maps AWS IAM and account metadata into the shared normalized Domain model. WPF does not call AWS APIs or the AWS CLI directly.

## Authentication

The connector supports the AWS CLI credential path and includes an `AwsCredentialFactory` using the AWS SDK default credential chain. Profiles, environment credentials, and role-based credentials are resolved by AWS SDK/CLI mechanisms. Access keys, secret keys, session tokens, and private keys are never stored or logged.

## Required Permissions

Use read-only IAM permissions appropriate to the selected account or organization. Typical discovery needs read access for STS caller identity, IAM users/groups/roles/policies/role versions, role trust policies, and Organizations accounts/OUs where organization discovery is enabled. CloudTrail and access-key last-used metadata are optional and should be granted only when required.

## Discovery and Normalization

- AWS account identity becomes `CloudAccount` with the account ID as opaque `ExternalId`.
- IAM users and workload identities become `CloudPrincipal`; workload roles remain distinguishable as `IdentityType.Role` or service-oriented identities where evidence supports it.
- Groups map to `CloudGroup`; direct memberships map to `GroupMembership` when persisted by the application layer.
- Roles and policies map to `CloudRole` and `CloudPolicy`.
- Policy statements map to `CloudPermission`, preserving Effect, Action, NotAction, Resource, NotResource, service prefixes, and wildcard values such as `s3:*`.
- Trust-policy principals are returned as `AwsTrustRelationship` records for future access-path analysis.
- AWS ARNs are preserved as resource and scope identifiers.

## Reliability

AWS CLI pagination is enabled by default for supported list operations, so the adapter does not force a first-page-only response. Command failures are retried up to three times with bounded exponential backoff. Cancellation flows through every command and scan operation. Account-level failures are recorded in the scan result without discarding other successful discoveries.

## Activity Limitations

Role last-used and access-key last-used metadata can be added when the authenticated principal has permission. CloudTrail is optional. Missing activity data remains unknown; the connector never labels an identity unused without evidence.

## Read-Only Guarantee

The connector invokes only STS/IAM/Organizations read operations. It does not create users, policies, roles, access keys, or assignments, and it does not modify trust relationships or permissions.

## Troubleshooting

- Authentication failures: verify the active AWS profile with `aws sts get-caller-identity`.
- Access denied: grant the minimum read-only IAM actions at the required account/organization scope.
- Throttling or network failures: retry with the bounded connector backoff and review logs for the failed operation.
- Missing organization data: organization APIs may be unavailable outside the management account or without Organizations read permissions.
