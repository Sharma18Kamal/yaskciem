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

The desktop Settings workspace provides selectable Jira Software, Jira Service Management, ServiceNow, BMC Helix ITSM, ManageEngine ServiceDesk Plus, Ivanti Neurons for ITSM, Zendesk, SolarWinds Service Desk, Dynamics 365 Customer Service, Azure DevOps Boards, and Other options. It stores a separate HTTPS site URL per provider. Known create-request routes are deep-linked for Jira, Jira Service Management, ServiceNow, Zendesk, and Azure DevOps Boards; providers without a stable generic create route open the configured portal. Credentials, query strings, and fragments are rejected; sign-in and submission happen at the ticketing site. This is not API authentication or ticket lifecycle synchronization.

Future endpoints can add posture summaries, findings, recommendations, audit events, scheduled reports, and review workflows behind the same `/api/v1` boundary. Authorization and tenant scope must be enforced server-side before exposing organization data. Ticketing integration is not implemented; choose the target system and its service-auth model before adding a connector.
