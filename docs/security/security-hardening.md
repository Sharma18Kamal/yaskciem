# Security Hardening

Security hardening is applied at shared boundaries:

- Sensitive audit metadata and log text are redacted for passwords, tokens, secrets, private keys, credentials, API keys, access keys, refresh tokens, and authorization values.
- API responses receive `X-Content-Type-Options`, `X-Frame-Options`, `Referrer-Policy`, `Permissions-Policy`, and a restrictive CSP. HSTS is enabled outside development when configured.
- Production security configuration requires HTTPS/HSTS and sensitive-log redaction.
- The API exposes contracts and capability metadata rather than raw EF entities or credentials.
- Cloud connectors remain read-only; security services never receive or store cloud credentials.

Security controls are defensive defaults and do not replace deployment-level TLS termination, secret rotation, dependency scanning, least-privilege IAM, network restrictions, or organization identity policies.
