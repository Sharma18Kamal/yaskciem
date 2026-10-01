# API and Integration Layer

The solution now exposes a versioned integration contract through `CloudEntitle.Integration` and the existing ASP.NET License Server host.

## Endpoints

- `GET /health`: host health check.
- `GET /api/v1/health`: versioned API health response.
- `GET /api/v1/readiness`: API/configuration readiness response.
- `GET /api/v1/providers`: read-only Azure/AWS/GCP capability declarations.

Responses use `ApiEnvelope<T>` where appropriate and carry a correlation ID and contract version. The desktop/API client uses `CiemApiClient` with `HttpClient` and JSON contracts rather than raw EF entities.

Provider capability declarations describe read-only authentication modes only. Cloud credentials, access tokens, secrets, and provider SDK objects are not exposed through the API contract.

Future endpoints can add posture summaries, findings, recommendations, audit events, scheduled reports, and review workflows behind the same `/api/v1` boundary. Authorization and tenant scope should be enforced before exposing organization data.
