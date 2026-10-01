# Multi-Tenant Organization Management

Organization Management provides the tenant boundary for CIEM data and operations. An organization owns active members, role assignments, and cloud-account/tenant bindings. Every provider connection can be scoped to an organization without storing credentials in the organization model.

## Roles

- OrganizationAdmin: manage members, connections, policies, and organization settings.
- SecurityAnalyst: run read-only scans and export/review findings.
- Auditor: review inventory, findings, and evidence.
- Viewer: read-only visibility.

Authorization decisions are explicit and deny unknown/non-member users. Organization status is checked before actions are authorized.

## Tenant Scope

`TenantBinding` preserves provider, cloud-account ID, external tenant/account identifier, and display name. `OrganizationScope` provides the account/provider filter used by future repositories and UI queries. The service does not call provider APIs, mutate cloud resources, or handle credentials.

The current implementation is an application boundary. Persistent organization/member/binding tables and SSO integration can be added behind these contracts when the licensing and identity backend is implemented.
