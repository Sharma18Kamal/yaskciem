# API and Integration Layer

The solution now exposes a versioned integration contract through `CloudEntitle.Integration` and the existing ASP.NET License Server host.

## Endpoints

- `GET /health`: host health check.
- `GET /api/v1/health`: versioned API health response.
- `GET /api/v1/readiness`: API/configuration readiness response.
- `GET /api/v1/providers`: read-only Azure/AWS/GCP capability declarations.
- `GET /api/v1/organizations`, `POST /api/v1/organizations`, and `GET /api/v1/organizations/{id}/workspace`: shared organization discovery, creation, and role-filtered workspace data.
- Organization-scoped member, custom-role, cloud-account binding, and `/audit-history` routes: server-authorized membership management and audit history.
- `GET`/`PUT /api/v1/organizations/{id}/access-reviews[/{campaignId}]`: shared review campaigns and decisions.
- `POST`/`GET /api/v1/organizations/{id}/scan-summaries`: aggregate scan upload/history for bound accounts.
- `POST`/`GET /api/v1/audit/events`: Entra-tenant central audit append/read routes; read requires the additional audit scope/role.

Responses use `ApiEnvelope<T>` where appropriate and carry a correlation ID and contract version. The desktop/API client uses `CiemApiClient` with `HttpClient` and JSON contracts rather than raw EF entities.

Provider capability declarations describe read-only authentication modes only. Cloud credentials, access tokens, secrets, and provider SDK objects are not exposed through the API contract.

The License Server validates multitenant Microsoft Entra bearer tokens. Workspace routes require `access_as_user` or `Ciem.Api.Access`; the audit read route additionally requires `audit.read` or `Ciem.Audit.Read`. Tenant and actor IDs come only from validated `tid` and `oid` claims. Organization membership and the requested action are checked by the server for every organization-scoped operation; tenant directory payloads are stored separately by Entra tenant. Scan summaries are limited to account bindings in the organization and contain aggregate counts/coverage, not raw finding or identity records. Review updates preserve original evidence, server-stamp decisions, and append audit entries.

Configure the API app registration for organizational accounts, expose `access_as_user` and/or `Ciem.Api.Access`, and set `AzureAd__Instance=https://login.microsoftonline.com/`, `AzureAd__TenantId=organizations`, `AzureAd__ClientId=<api-client-id>`, and `AzureAd__Audience=<api-app-id-uri>`. Configure `ConnectionStrings__SharedWorkspace` or `SharedData__ConnectionString`; alternatively set `SharedData__Directory` to a durable server directory. The default is `%LOCALAPPDATA%\YASK-CIEM\SharedApi\shared-workspace.db`. This SQLite backend is intended for a single server instance with durable storage; use a shared production database before multi-replica hosting.

To enable the Desktop shared workflow, set `YASK_CIEM_API_BASE_URL` to the License Server HTTPS base URL and `YASK_CIEM_API_SCOPE` to the delegated scope (for example `api://<api-client-id>/access_as_user`). Entra sign-in obtains the API token separately from the Microsoft Graph `User.Read` token. When configured, organizations, member management, review campaigns, and scan summaries use the shared API; without it, the desktop intentionally retains its local/offline stores. Scan summaries are published only for bound accounts and are idempotent by scan ID.

The desktop Settings workspace offers Jira Software, Jira Service Management, ServiceNow, BMC Helix ITSM, ManageEngine ServiceDesk Plus, Ivanti Neurons for ITSM, Zendesk, SolarWinds Service Desk, Dynamics 365 Customer Service, Azure DevOps Boards, and Other. Each provider has its own HTTPS site URL and project/request-type mapping. Native ticket creation and status refresh are implemented for Jira, Jira Service Management, and ServiceNow. These adapters use HTTPS REST APIs with HTTP Basic authentication; enter the provider username and API token/secret, which are stored per Windows user in Windows Credential Manager. Settings JSON contains provider URLs and non-secret mappings only.

The remaining catalog providers use a generic signed-webhook fallback when a webhook URL and secret are configured. Creation sends a JSON evidence snapshot with an idempotency key, timestamp, and HMAC-SHA256 signature in `X-YASK-CIEM-Signature`. A webhook may return a ticket ID, HTTPS ticket URL, status, and status URL. Status refresh is accepted only when that URL has the same HTTPS origin as the configured webhook endpoint. Provider-specific field mapping and status transitions for these fallback providers belong in the receiving integration.

Ticket creation is explicit and user-triggered from a selected recommendation; scans and recommendation generation never create tickets automatically. Requests are deduplicated by ticketing provider, source cloud provider, and recommendation fingerprint. A per-key file lock serializes submissions across Desktop processes for the same Windows user, and the local request index is checked again before provider creation. The request index, external IDs, status, closure verification, and links are local to this device under `%LOCALAPPDATA%\YASK-CIEM\ticketing`; this is not a shared or cross-device ticket registry. The ticketing provider remains authoritative, and refresh is an explicit user action. A fresh Jira status-category/status read or ServiceNow incident state 6 (Resolved) or 7 (Closed) verifies closure; an observed reopen clears the verification timestamp. Webhook providers use the returned status string and must expose a same-origin status URL.

Ticket evidence includes the source provider, finding/access/access-path identifiers when available, recommendation type, current and proposed state, impact, rationale, and evidence snapshot. Native ticket descriptions and signed-webhook payloads also include a `yask-ciem://recommendations/{id}` backlink. MSI and MSIX installs register this protocol and open the matching recommendation in Desktop. The deep link resolves only where that recommendation exists in the local workspace; ticket viewers on other devices still have the embedded evidence snapshot, but there is no hosted/shared recommendation permalink yet.

Future endpoints can add posture summaries, findings, recommendations, audit events, scheduled reports, and review workflows behind the same `/api/v1` boundary. Authorization and tenant scope must be enforced server-side before exposing organization data. The desktop ticket workflow is local and does not add ticket endpoints to the API host; shared organization-wide ticket history requires the same server-side authorization, tenant isolation, and shared persistence boundaries described above.
