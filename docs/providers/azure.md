# Azure Read-Only Connector

The Azure adapter lives in `CloudEntitle.Cloud.Azure` and returns the shared normalized Domain model. The WPF layer does not call Azure SDKs or Azure CLI directly.

## Authentication

The connector uses the authenticated Azure CLI session (`az account show`) for Azure Resource Manager and Microsoft Graph read calls. Entra Graph requests use `az rest`; YASK-CIEM does not store a client secret or separate credential.

## Required Permissions

Use the least-privileged read-only access needed for the selected scope. ARM discovery requires permission to read subscriptions, resources, role definitions, and role assignments. Azure Activity Log timestamps require `Microsoft.Insights/eventtypes/values/read` at subscription scope. Entra discovery uses Microsoft Graph delegated read permissions `User.ReadBasic.All`, `Group.Read.All`, and `Application.Read.All`. PIM active and eligible assignments use `RoleAssignmentSchedule.Read.Directory` and `RoleEligibilitySchedule.Read.Directory`. Microsoft Graph documents supported read roles including Global Reader, Security Reader, Security Operator, Security Administrator, and Privileged Role Administrator. A tenant administrator may need to grant consent to these delegated permissions for the Azure CLI application used by `az rest`. Do not grant Owner, Contributor, or User Access Administrator for scanning.

## Discovery

The adapter normalizes:

- Azure subscription and tenant metadata into `CloudAccount`.
- Resources from `az resource list` into `CloudResource` with opaque IDs, type, location, and scope.
- Role-assignment principals into `CloudPrincipal`.
- RBAC role definitions into `CloudRole` and `CloudPermission`.
- Role assignments into `RoleAssignment` and `Entitlement`.
- Microsoft Entra users, groups, and service principals into `CloudPrincipal`.
- Active and eligible Entra PIM role schedules into Effective Access, labeled with assignment state and source.
- Azure scope strings into `CloudScope`, preserving the original value.
- Successful role-assignment writes from the last 90 days can populate `Last changed` when the Activity Log principal, role-definition ID, and scope match the current assignment.

Azure `actions` and `dataActions` become Allow permissions. `notActions` and `notDataActions` become Deny permissions with a source marker identifying the original Azure field. Wildcards are preserved as supplied.

## Scan and Resilience

`AzureProvider.ScanAsync` supports cancellation, bounded three-attempt exponential retry for failed CLI calls, partial subscription errors, and a normalized `Scan` status. Entra and PIM Graph endpoints are isolated: missing consent is reported as partial provider status and does not discard successful ARM discovery. A missing activity or directory permission does not produce a false last-activity or external-identity claim.

The current scan result is returned to the Application layer for persistence; the connector does not manipulate SQLite directly. Full effective-access calculation and risk analysis remain later milestones.

## Limitations

The current implementation uses Azure CLI discovery for ARM, Graph, and subscription Activity Logs. The change timestamp covers matching role-assignment writes in the returned 90-day window; it does not infer last use or attribute unrelated subscription events to an assignment. PIM rows are provider-reported schedules, not a full simulation of nested group membership, conditional access, or authorization evaluation.

## Troubleshooting

- `Authentication Failed`: run `az login` and verify the selected subscription with `az account show`.
- `Insufficient Permissions`: grant ARM read-only subscription/resource/RBAC visibility and approved Graph read consent for Entra/PIM. Successful endpoints are retained when another endpoint is denied.
- `Scan Failed`: inspect the application log for the subscription-level error; successful subscriptions are retained.
- `Scan Cancelled`: retry the scan; cancellation does not modify Azure resources.
