# Multi-Account Organization Management

An organization owns members, role assignments, audit entries, and cloud-account bindings. Organization records are persisted at `%LOCALAPPDATA%\YASK-CIEM\organizations\directory.json`. The local store uses atomic replacement and refuses to treat malformed JSON as an empty directory.

## Entra SSO Setup

Register the desktop as a native/public-client application in Microsoft Entra ID and configure its loopback redirect URI. Grant the delegated Microsoft Graph `User.Read` permission. Set `YASK_CIEM_ENTRA_CLIENT_ID` to the application (client) ID before launching the app. Sign-in is initiated against the tenant ID selected for the organization.

The application validates the returned `tid`, `oid`, and `sub` claims against the requested tenant, then calls `/me` with the delegated token to resolve the user's current mail or UPN for invitations. Tokens are not persisted by the organization store. The signed-in session exists only in the current app process.

Organization creation requires an authenticated user from the configured tenant and makes that user the first admin. Invited members remain pending until they authenticate in that same Entra tenant with the invited email. Acceptance links the member to the authenticated Entra object ID; authorization never grants access based on email alone. Switching to a different tenant requires a new sign-in.

## Roles

- OrganizationAdmin: manage members, connections, policies, and organization settings; approve remediation and export compliance evidence.
- SecurityAnalyst: run read-only scans, review findings, approve remediation, and export compliance evidence.
- Auditor: review inventory, findings, and evidence.
- Viewer: read-only visibility.

Organization administrators can also create reusable custom roles, select any combination of the supported organization actions, and assign those roles to members. Custom role changes take effect immediately for assigned members. Roles in use cannot be deleted; built-in roles remain available and retain their existing authorization behavior.

Authorization is enforced by `OrganizationManagementService`, not only by UI state. Unknown, inactive, pending, cross-tenant, and non-member identities are denied. Analysts can run read-only scans and export reports; auditors can review; viewers have read-only visibility; only admins can manage memberships and account bindings. The final active admin cannot be demoted or deactivated. Organization changes are recorded with actor, timestamp, action, target, and details; only admins can read the audit trail.

## Tenant Scope

`TenantBinding` stores provider, the scan's cloud-account GUID, the stable provider account identifier, display name, and bind time. Resolution and duplicate prevention use the provider plus stable external account ID because provider adapters generate new internal GUIDs on each scan. A given provider/account pair can be bound only once across local organizations.

The Organizations workspace displays consolidated account, identity, resource, finding, critical-finding, and effective-access counts from the latest live scan. Metrics are filtered by the organization's stable provider/account bindings; provider findings are attributed to their originating scan account. Synthetic Demo Mode data is excluded. Bound accounts not present in the latest scan are called out as unmatched. Dashboard compliance evidence bundles use the same provider plus external-account binding scope and require `ExportReports` authorization.

## Deployment Boundary

This desktop build stores the organization directory in the current Windows user profile and keeps the SSO session in process memory. It does not provide a shared server-side directory, cross-device synchronization, centralized invitation delivery, or a production multi-user audit authority. Those require a trusted backend and server-side authorization before this local directory can be treated as an enterprise control plane. Cloud CLI credentials and scans remain read-only and are not stored in organization records.
