# Multi-Account Organization Management

An organization owns members, role assignments, audit entries, and cloud-account bindings. Organization records are persisted at `%LOCALAPPDATA%\YASK-CIEM\organizations\directory.json`. The local store uses atomic replacement and refuses to treat malformed JSON as an empty directory.

## Entra SSO Setup

Register the desktop as a native/public-client application in Microsoft Entra ID and configure its loopback redirect URI. Grant the delegated Microsoft Graph `User.Read` permission. Set `YASK_CIEM_ENTRA_CLIENT_ID` to the application (client) ID before launching the app. Sign-in is initiated against the tenant ID selected for the organization.

The application validates the returned `tid`, `oid`, and `sub` claims against the requested tenant, then calls `/me` with the delegated token to resolve the user's current mail or UPN for invitations. Tokens are not persisted by the organization store. The signed-in session exists only in the current app process.

Organization creation requires an authenticated user from the configured tenant and makes that user the first admin. Invited members remain pending until they authenticate in that same Entra tenant with the invited email. Acceptance links the member to the authenticated Entra object ID; authorization never grants access based on email alone. Switching to a different tenant requires a new sign-in.

## Roles

### Administrator invitation and reviewer workflow

In Organizations, an authorized administrator selects the organization, enters the
user's Entra sign-in email, selects a built-in or custom role, and selects
**Invite member**. **Pending sign-in** remains checked until acceptance; **Active**
alone does not mean the invitation was accepted. No email is sent automatically:
the administrator must tell the user to sign in to the same organization/tenant.
A suspended invitation cannot be accepted until an administrator reactivates it.

For shared access reviews, the invited user must accept and have
`ManageRemediations` permission (for example SecurityAnalyst or an appropriately
configured custom role). The administrator then assigns the reviewer from
Access Reviews; invitation does not assign campaigns automatically. Pending and
inactive members are excluded from reviewer selection, and the shared API
excludes the campaign creator from reviewing their own items. Assignment changes
require a reason and use the existing governance audit/concurrency path.
Local-only access review mode retains its existing scanned-cloud reviewer rules.

Application invitations are unrelated to GitHub deployment reviewers. They do not
grant cloud permissions, trigger access grants/revocations, or enable deployment.

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

## Administrator-led cloud connection verification

Cloud permissions are granted externally by the cloud administrator, not by
organization roles or member invitations in YASK-CIEM. Prefer short-lived,
federated or assumed-role credentials; never enter administrator passwords in
the application.

In **Cloud Accounts**, follow the provider card's least-privilege instructions,
configure the approved CLI identity externally, connect, and discover accounts.
Select a discovered Azure subscription, AWS account/profile, or GCP project to
prefill the **Your administrator-approved scope** form, or enter the identifiers
directly. Each user supplies their own approved scope at runtime; no shared
subscription or tenant is hardcoded:

- Azure: subscription GUID and expected tenant GUID.
- AWS: 12-digit account ID and an existing CLI profile name (`default` must be
  explicitly entered when intended).
- GCP: project ID and expected active CLI account.

Choose **Verify entered account identity**. Required fields and provider-specific
ID formats are checked before CLI execution. A different returned tenant or GCP
identity fails verification, as does an account/provider mismatch. Editing the
form or changing the discovered selection invalidates the displayed evidence;
late responses for a changed scope are discarded. Scope entry is held only in
the viewmodel, not written to the organization directory or credential storage.
Verification does not start a scan, launch authentication, change CLI defaults,
or bind an account.

The read-only check displays the
CLI identity, actual account, Azure tenant where applicable, and check time. It
rejects a returned account that differs from the selection. AWS verification
uses the selected named profile, not another working profile. Azure additionally
performs a live subscription read and compares the returned subscription and
tenant with the selection. GCP pins the project read to the displayed active CLI
account; impersonation and token-file overrides are explicitly unsupported until
effective-principal verification is available. Kubernetes contexts and
organization roster rows are not supported by this identity check.

After verification, choose **Scan verified account** to explicitly collect that
account/profile without manually reselecting it in Dashboard. This action:

- Rechecks provider, account, tenant and identity after acquiring the shared scan
  lock, and again after account discovery before collecting inventory.
- Requires exactly one discovered match. AWS profile names are matched with exact
  casing; the provider's implicit default profile corresponds to entered `default`.
  Missing/ambiguous matches and identity changes do not fall back to other accounts.
- Invokes inventory only for the matching account in the selected provider.
  Provider account discovery may still list other scopes/profiles. Existing
  account adapters may read ancestor IAM evidence needed to model inherited
  access for the selected account.
- Skips separate provider-wide Entra directory/PIM, organization roster, and
  Kubernetes discovery/scan passes, with explicit `Skipped` coverage.
- Shows collection status and the source coverage table, retaining denied and
  partial results rather than presenting a completed collection as complete
  permission coverage. A missing collected scope is an explicit failure.
- Requires verification again before another scan, prevents duplicate clicks
  while running, and never creates an organization binding.

Do not change external CLI profiles/credentials during collection: these
point-in-time checks cannot lock configuration owned by the CLI. Dashboard's
existing scan selection/behavior remains independent and unchanged.

Verification is not a cloud authorization grant or a global scan gate.
The Organizations desktop binding action repeats verification immediately before
submitting a binding and rejects mismatched provider/account results or changed
organization/sign-in/account selection. This client-side check is not server-side
proof of cloud control; the shared API still enforces organization authorization.
CLI credentials can change after verification;
reverify after changing them. Azure CLI subscription metadata and AWS STS
identity do not prove inventory or Graph permissions. Run the explicit verified
account scan (or select the intended accounts separately in Dashboard) and inspect **Latest
permission and source coverage** for denied, partial, unavailable, unsupported,
and skipped sources. A completed collection or zero findings must not be
interpreted as complete successful coverage. Coverage timestamps describe
the last collection in this app session. Provider cards separately retain the
last collection observed this session where all reported sources were collected
or returned no data. A later partial/denied/skipped scan does not advance that
timestamp. This is not a durable last-success ledger or evidence that every
possible cloud data source was queried.

Verification errors do not display raw CLI response/error bodies. Request the
administrator's help with missing read permissions; do not broaden access to
Owner or Contributor to make onboarding pass. Invite application analysts and
reviewers separately; application roles never implicitly grant cloud IAM.
