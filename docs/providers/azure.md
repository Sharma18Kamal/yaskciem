# Azure Read-Only Connector

The Azure adapter lives in `CloudEntitle.Cloud.Azure` and returns the shared normalized Domain model. The WPF layer does not call Azure SDKs or Azure CLI directly.

## Authentication

The connector supports the authenticated Azure CLI path used by the desktop provider (`az account show`) and includes an `AzureCredentialFactory` for `DefaultAzureCredential` with interactive browser support for future SDK-backed discovery. Tokens and secrets are never persisted or logged.

## Required Permissions

Use the least-privileged read-only access needed for the selected scope. Typical discovery requires permission to read subscriptions, resource groups, resources, role definitions, and role assignments. Microsoft Entra user/group/service-principal discovery and activity data require additional directory or audit read permissions where permitted by the tenant. Do not grant Owner, Contributor, or User Access Administrator for scanning.

## Discovery

The adapter normalizes:

- Azure subscription and tenant metadata into `CloudAccount`.
- Resources from `az resource list` into `CloudResource` with opaque IDs, type, location, and scope.
- Role-assignment principals into `CloudPrincipal`.
- RBAC role definitions into `CloudRole` and `CloudPermission`.
- Role assignments into `RoleAssignment` and `Entitlement`.
- Azure scope strings into `CloudScope`, preserving the original value.

Azure `actions` and `dataActions` become Allow permissions. `notActions` and `notDataActions` become Deny permissions with a source marker identifying the original Azure field. Wildcards are preserved as supplied.

## Scan and Resilience

`AzureProvider.ScanAsync` supports cancellation, bounded three-attempt exponential retry for failed CLI calls, partial subscription errors, and a normalized `Scan` status. A missing activity or directory permission does not produce a false last-activity or external-identity claim.

The current scan result is returned to the Application layer for persistence; the connector does not manipulate SQLite directly. Full effective-access calculation and risk analysis remain later milestones.

## Limitations

The current implementation uses Azure CLI discovery as the production path and provides the Azure Identity/Resource Manager packages for the SDK-backed expansion. Tenant management-group enumeration, Microsoft Entra directory enrichment, and activity-log collection depend on permissions and are not fabricated when unavailable.

## Troubleshooting

- `Authentication Failed`: run `az login` and verify the selected subscription with `az account show`.
- `Insufficient Permissions`: grant read-only subscription/resource/RBAC visibility at the required scope.
- `Scan Failed`: inspect the application log for the subscription-level error; successful subscriptions are retained.
- `Scan Cancelled`: retry the scan; cancellation does not modify Azure resources.
