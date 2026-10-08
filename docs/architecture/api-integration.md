# API and Integration Layer

The solution now exposes a versioned integration contract through `CloudEntitle.Integration` and the existing ASP.NET License Server host.

## Endpoints

- `GET /health`: host health check.
- `GET /api/v1/health`: versioned API health response.
- `GET /api/v1/readiness`: API/configuration readiness response.
- `GET /api/v1/providers`: read-only Azure/AWS/GCP capability declarations.

Responses use `ApiEnvelope<T>` where appropriate and carry a correlation ID and contract version. The desktop/API client uses `CiemApiClient` with `HttpClient` and JSON contracts rather than raw EF entities.

Provider capability declarations describe read-only authentication modes only. Cloud credentials, access tokens, secrets, and provider SDK objects are not exposed through the API contract.

This host currently provides licensing and provider-capability endpoints only. It does not authenticate organization members, enforce organization scope, or serve shared dashboard data; the readiness route does not imply those enterprise controls exist. Do not expose findings, audit events, remediation, or compliance data through these endpoints until Entra API audience/scopes, server-side membership authorization, tenant isolation, and a shared persistence backend are implemented and configured.

The API now validates multitenant Microsoft Entra bearer tokens for `GET /api/v1/identity`. The endpoint requires either the delegated `access_as_user` scope or the `Ciem.Api.Access` app role and returns only validated tenant (`tid`) and user object (`oid`) identifiers. Health, readiness, and provider-capability routes remain anonymous. Configure the API app registration for organizational accounts, expose the `access_as_user` scope and/or `Ciem.Api.Access` app role, then set `AzureAd__Instance=https://login.microsoftonline.com/`, `AzureAd__TenantId=organizations`, `AzureAd__ClientId=<api-client-id>`, and `AzureAd__Audience=<api-app-id-uri>` in the host environment. Startup fails closed if the API client ID is absent.

This is only the authentication boundary. Organizations, membership, scan summaries, reviews, and audit events are still local to the desktop, and no tenant-scoped data endpoints or shared database are configured yet. The desktop client also needs to request an access token for this API before it can call `/api/v1/identity`.

The desktop Settings workspace offers Jira Software, Jira Service Management, ServiceNow, BMC Helix ITSM, ManageEngine ServiceDesk Plus, Ivanti Neurons for ITSM, Zendesk, SolarWinds Service Desk, Dynamics 365 Customer Service, Azure DevOps Boards, and Other. Each provider has its own HTTPS site URL and project/request-type mapping. Native ticket creation and status refresh are implemented for Jira, Jira Service Management, and ServiceNow. These adapters use HTTPS REST APIs with HTTP Basic authentication; enter the provider username and API token/secret, which are stored per Windows user in Windows Credential Manager. Settings JSON contains provider URLs and non-secret mappings only.

The remaining catalog providers use a generic signed-webhook fallback when a webhook URL and secret are configured. Creation sends a JSON evidence snapshot with an idempotency key, timestamp, and HMAC-SHA256 signature in `X-YASK-CIEM-Signature`. A webhook may return a ticket ID, HTTPS ticket URL, status, and status URL. Status refresh is accepted only when that URL has the same HTTPS origin as the configured webhook endpoint. Provider-specific field mapping and status transitions for these fallback providers belong in the receiving integration.

Ticket creation is explicit and user-triggered from a selected recommendation; scans and recommendation generation never create tickets automatically. Requests are deduplicated by ticketing provider, source cloud provider, and recommendation fingerprint. The request index, external IDs, status, and links are local to this device under `%LOCALAPPDATA%\YASK-CIEM\ticketing`; it is not a shared or cross-device ticket registry. Concurrent clicks within one desktop process are serialized for the same dedupe key. The ticketing provider remains authoritative, and refresh is an explicit user action. Ticket evidence includes the source provider, finding/access/access-path identifiers when available, recommendation type, current and proposed state, impact, rationale, and evidence snapshot.

Future endpoints can add posture summaries, findings, recommendations, audit events, scheduled reports, and review workflows behind the same `/api/v1` boundary. Authorization and tenant scope must be enforced server-side before exposing organization data. The desktop ticket workflow is local and does not add ticket endpoints to the API host; shared organization-wide ticket history requires the same server-side authorization, tenant isolation, and shared persistence boundaries described above.
