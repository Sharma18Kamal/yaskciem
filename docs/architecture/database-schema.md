# SQLite Persistence Schema

The Infrastructure layer persists normalized CIEM metadata with EF Core and SQLite. The Domain project remains persistence-ignorant.

## Location

Runtime databases are stored under:

`%LOCALAPPDATA%\CloudEntitle\Data\cloudentitle.db`

The path is supplied by `DatabaseConfiguration`; it is not hard-coded to a developer machine. Automated tests use in-memory SQLite or isolated temporary directories.

## Tables and Relationships

```mermaid
erDiagram
    CloudAccount ||--o{ CloudResource : contains
    CloudAccount ||--o{ CloudPrincipal : contains
    CloudAccount ||--o{ CloudRole : contains
    CloudAccount ||--o{ CloudPermission : contains
    CloudPrincipal ||--o{ Entitlement : receives
    CloudRole ||--o{ Entitlement : grants
    CloudPermission ||--o{ Entitlement : describes
    CloudPrincipal ||--o{ EffectiveAccess : has
    CloudResource ||--o{ EffectiveAccess : protects
    AccessPath ||--o{ AccessPathNode : contains
    RiskFindingModel ||--o{ RecommendationModel : produces
    CloudAccount ||--o{ Scan : records
    CloudPrincipal ||--o{ ActivityRecord : generates
    RemediationPlanRecord ||--o{ RemediationActionRecord : proposes
    RemediationPlanRecord ||--o| RemediationApprovalRecord : reviewed
    RemediationPlanRecord ||--o{ RemediationExecutionRecord : executes
    RemediationPlanRecord ||--o{ RemediationRollbackRecord : recovers
    RemediationPlanRecord ||--o{ RemediationAuditRecord : records
```

The schema also stores policies, assignments, group memberships, audit events, risk rules, and scan history. Security and audit relationships use restrictive delete behavior to avoid accidental historical data loss.

Remediation records are organization-scoped and cascade only with their parent plan. Execution and audit history have independent rows; approval data, action diffs, and provider snapshots are retained in payload columns so historical decisions remain reviewable.

## Indexes

Indexes target provider/account lookups, principal status and privilege, resource ownership, entitlement scope, effective access, risk severity/status, activity timelines, and scan history. JSON fields are stored as SQLite `TEXT` using deterministic `System.Text.Json` conversions for tags, audit metadata, and evidence.

Policy documents are preserved as text so provider-specific policy semantics are not altered by persistence.

## Migrations and Initialization

EF Core migrations are the source of truth. `DbInitializer` creates the app-data directory and applies pending migrations, including the additive remediation-persistence migration. The remediation store expects startup migration to have completed; it does not create tables ad hoc. Database errors are logged with Serilog and surfaced as a useful initialization exception without exposing credentials.

## Security

The database stores authorization metadata and remediation evidence, not credentials. The remediation store rejects common credential/token patterns before saving provider snapshots; this is defense-in-depth and cannot recognize every secret embedded in arbitrary text. Passwords, access tokens, refresh tokens, client secrets, private keys, API keys, and license secrets are intentionally outside this persistence model.
