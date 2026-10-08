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

The desktop Settings workspace stores an HTTPS Jira site URL locally and can open that site in the user's browser. Credentials, query strings, and fragments are rejected in the URL. This is not Jira API authentication and does not create or update tickets.

Future endpoints can add posture summaries, findings, recommendations, audit events, scheduled reports, and review workflows behind the same `/api/v1` boundary. Authorization and tenant scope must be enforced server-side before exposing organization data. Ticketing integration is not implemented; choose the target system and its service-auth model before adding a connector.
