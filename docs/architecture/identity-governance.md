# Identity Governance and Lifecycle

Identity Governance consumes normalized principals, activity records, EffectiveAccess, and existing access-path evidence. It classifies lifecycle state and creates review campaigns without making cloud changes.

## Lifecycle States

- Active: recent activity evidence exists.
- Inactive: provider metadata marks the identity inactive.
- Dormant: activity exceeds the configured threshold.
- Disabled: explicit disabled status.
- External: reliable external classification exists.
- ServiceIdentity: service principal, service account, managed identity, or application identity.
- Unknown: activity/status evidence is unavailable.

Unknown activity is never converted into Dormant.

## Workload Identity Evidence

The normalized identity inventory persists owners, credential rotation/expiry, federation trust metadata, last activity, current workload observation, and per-source completeness flags. The Identities workspace shows these fields; unavailable or incomplete evidence is shown as unknown rather than as an empty/clean result.

- Microsoft Entra service principals and managed identities: Graph owner IDs and password/key credential rotation/expiry are collected. App federated credentials are read for application service principals. Service-principal sign-in history is not currently collected, so absence of activity is not treated as evidence of no use.
- AWS IAM workload roles: roles with a service principal or `AssumeRoleWithWebIdentity` trust are included as workload identities. `RoleLastUsed` provides up to 400 days of role-level activity evidence. Owner tags recognized are `owner`, `owner-email`, and `business-owner`; IAM roles that can only be assumed by AWS principals remain ordinary roles.
- Google Cloud service accounts: user-managed key creation/expiry metadata is collected. This is not a complete view of Google-managed credentials or API activity. Owner and federated-pool evidence remains unknown unless associated Kubernetes workload trust was verified.
- Kubernetes ServiceAccounts: current Pod presence is recorded separately from historical activity. A Running Pod is an observation, not proof that its token was used. Owner inventory is considered complete only when the `yask-ciem.io/owner` annotation is present; an empty value or `unowned` explicitly means no owner. Federation issuer/subject/audience metadata from app and IAM trust documents is recorded, but marked verified only when the AKS, EKS, or GKE cluster-to-provider relationship was directly checked.

## Workload Findings

- `CIEM-018` Orphaned Workload Identity is confirmed only when a complete observation window at least as long as the dormancy threshold has no observed use and the identity has no effective grants.
- `CIEM-019` Unowned Workload Identity requires a complete owner inventory with no owner identifiers.
- `CIEM-020` Overprivileged Workload Identity reports effective privileged grants for service principals, managed identities, service accounts, and application identities.

These findings are read-only review signals. They do not disable identities, rotate credentials, alter federation trust, or remove grants. Provider API permissions and source coverage determine which records can be evaluated; incomplete evidence does not produce a clean-state assertion.

## Access Reviews

The Access Reviews workspace creates campaigns from the current live scan or Demo Mode. Each item retains an evidence snapshot and links to its EffectiveAccess and optional AccessPath records. Built-in templates cover all access, privileged workload identities, and external identities. A known identity owner is assigned as reviewer first; the configured fallback reviewer is used when no owner was observed. Owner identity is retained as an assignment-time snapshot.

Unresolved items receive recurring in-app reminders during the configured lead window and escalate after their deadline. Reminders are evaluated when review campaigns are refreshed; this does not send email or require background access to provider systems. Decisions create immutable attestations containing reviewer, action actor, decision, rationale, UTC timestamp, exact evidence snapshot, and its SHA-256 digest. Shared-mode decisions and attestations are authored by the License Server from validated Entra claims. Neither review decisions nor attestations grant, revoke, or alter cloud access.

Reviewers can certify, reject, request investigation, or grant a temporary exception. Exceptions require a future expiry date; expired exceptions become unresolved and escalate on the next campaign refresh. Overdue pending items also escalate. A reviewer and a separate action actor are recorded for assignments and decisions.

Campaigns are stored locally under `%LOCALAPPDATA%\YASK-CIEM\governance\access-reviews.json` when shared API mode is not configured; shared mode stores them in the tenant-scoped License Server workspace. A refresh reevaluates reminder windows, deadlines, and exception expiry status. Decisions and exceptions are governance records only; they do not revoke permissions, remove memberships, disable identities, or call cloud APIs.
