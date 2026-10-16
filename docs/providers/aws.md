# AWS Read-Only Connector

The AWS adapter lives in `CloudEntitle.Cloud.Aws` and maps AWS IAM and account metadata into the shared normalized Domain model. WPF does not call AWS APIs or the AWS CLI directly.

## Authentication

The connector enumerates AWS CLI profiles, including IAM Identity Center SSO profiles, and verifies each with STS. Every working profile/account pair is scanned separately, even when multiple permission sets target the same AWS account. Access keys, secret keys, session tokens, and private keys are never stored or logged.

Configure an IAM-user profile with `aws configure --profile audit`, or configure and authenticate an Identity Center profile with `aws configure sso --profile audit` and `aws sso login --profile audit`. Use the actual profile name in place of `audit`. Verify it with `aws sts get-caller-identity --profile audit`, then select **Connect** in YASK-CIEM. AWS Connect verifies existing credentials and does not automatically start an SSO setup wizard. A `SecurityAudit` policy grants permissions but does not authenticate the CLI.

Connection checks have a 15-second cancellation deadline. AWS identity checks disable CLI paging, use five-second connection/read timeouts, and probe default and named profiles concurrently. The first successful identity cancels slower probes. Connecting one provider does not wait for other providers' CLI checks.

AWS **Disconnect**, including inactivity disconnection, applies only to the current YASK-CIEM app session. It excludes AWS from subsequent account discovery and scans, and refreshing sign-in status does not reconnect it. **Connect** resumes use of existing valid CLI credentials without reopening SSO configuration. Restarting the app checks existing CLI credentials again. Disconnection does not cancel an already-running scan; use **Stop Scan** for that.

YASK-CIEM does not remove CLI profiles, sign out shared AWS SSO sessions, or deactivate IAM access keys when disconnecting AWS. `aws sso logout` does not invalidate IAM-user access keys. To revoke an IAM key, deactivate it separately in AWS IAM after checking which other tools depend on it.

## Required Permissions

Use read-only IAM permissions appropriate to each configured account/profile. Discovery uses STS caller identity, IAM users/groups/roles/policies/account authorization details, plus `access-analyzer:ListAnalyzers` and `access-analyzer:ListFindings` for already-configured analyzers. `cloudtrail:LookupEvents` enables recent IAM change timestamps. `organizations:ListAccounts` can add the organization roster to Cloud Accounts, but does not grant member-account access or trigger role assumption; only accounts with a configured, working profile are scanned. Permission-set names are inferred from assumed-role ARNs. No analyzer is created or modified.

## Discovery and Normalization

- AWS account identity becomes `CloudAccount` with the account ID as opaque `ExternalId`.
- IAM users and workload identities become `CloudPrincipal`; workload roles remain distinguishable as `IdentityType.Role` or service-oriented identities where evidence supports it.
- Groups map to `CloudGroup`; direct memberships map to `GroupMembership` when persisted by the application layer.
- Roles and policies map to `CloudRole` and `CloudPolicy`.
- Policy statements map to `CloudPermission`, preserving Effect, Action, NotAction, Resource, NotResource, service prefixes, and wildcard values such as `s3:*`.
- Trust-policy principals are returned as `AwsTrustRelationship` records for future access-path analysis.
- Active findings from existing IAM Access Analyzer instances are added to Risk Findings.
- Each profile/account appears separately in the Cloud Accounts inventory, with its CLI profile and inferred permission set.
- Organization members without a configured working profile appear as roster-only rows and are not represented as scanned.
- IAM role usage dates/regions and IAM user's console-password last-use time are shown with their specific meanings.
- Recent CloudTrail IAM management events can populate `Last changed` for matching users, groups, roles, or policies; this is separate from IAM's last-used signals. The lookup uses `us-east-1`, where IAM global-service events are recorded.
- AWS ARNs are preserved as resource and scope identifiers.

## Reliability

AWS CLI pagination is enabled by default for supported list operations, so the adapter does not force a first-page-only response. Command failures are retried up to three times with bounded exponential backoff. Cancellation flows through every command and scan operation. Account-level failures are recorded in the scan result without discarding other successful discoveries.

## Activity Limitations

`RoleLastUsed` is limited to IAM's supported tracking period and describes role use, while `PasswordLastUsed` describes console-password use only. CloudTrail event history lookup covers events within the last 90 days and is limited to 50 returned events per query. Missing or denied audit data leaves `Last changed` unreported; it is not evidence that access was never changed or used. Access Analyzer requires a pre-existing analyzer and read permission.

## Read-Only Guarantee

The connector invokes only STS, IAM, Organizations, and CloudTrail read operations. It does not create users, policies, roles, access keys, or assignments, and it does not modify trust relationships or permissions.

## Troubleshooting

- Authentication failures: verify the active AWS profile with `aws sts get-caller-identity`.
- Access denied: grant the minimum read-only IAM actions, including `cloudtrail:LookupEvents` for change history and `organizations:ListAccounts` at organization scope when roster discovery is required.
- Throttling or network failures: retry with the bounded connector backoff and review logs for the failed operation.
- Missing member accounts: configure and authenticate an AWS CLI/IAM Identity Center profile for each account and permission set to be scanned; organization rosters do not grant access to member accounts.
