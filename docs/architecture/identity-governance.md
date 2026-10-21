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

Unresolved items receive recurring in-app reminders during the configured lead window and escalate at their deadline. In shared mode a License Server worker processes a durable PostgreSQL deadline queue, independently of Desktop sessions or API reads. The worker polls every 15 seconds and reschedules relevant campaigns every minute, using batches of 50 locked rows (`FOR UPDATE SKIP LOCKED`). Lifecycle state and append-only audit events commit atomically; overlapping workers cannot duplicate a reminder or expiry transition. Failed transactions retain due work for retry. Reads can also refresh state under the same campaign transaction. Cancelled/draft campaigns are excluded from lifecycle evaluation. Reminders do not send email or contact provider systems.

Decisions create immutable attestations containing reviewer, action actor, decision, rationale, UTC timestamp, exact evidence snapshot, and its SHA-256 digest. In shared mode, the License Server authorizes review changes using organization roles, accepts assignments only to active members authorized to manage reviews, and prevents campaign creators from reviewing their own campaigns. Only the assigned reviewer can decide an item. The server stamps actor email and Entra object ID, timestamp, evidence digest, and a correlation ID; correlated audit events are persisted in an append-only PostgreSQL table. Evidence snapshots cannot be changed after capture. Neither review decisions nor attestations grant, revoke, or alter cloud access.

Reviewers can certify, reject, request investigation, or grant a temporary exception. Pending and NeedsInvestigation are unresolved; certification, rejection, and an unexpired exception count as completed governance decisions. A rejection is not a cloud revocation. Exceptions require a future expiry date and receive recurring pre-expiry reminders. At expiry they become unresolved and escalate. The effective workflow deadline for an exception is its expiry, not the original review deadline; the original deadline is retained separately. The assigned reviewer can renew an exception with a new future expiry and rationale, including when the decision enum remains ExceptionGranted. Renewal creates another immutable attestation and clears current expiry/escalation flags while preserving history. A reviewer and a separate action actor are recorded for assignments and decisions.

Campaigns are stored locally under `%LOCALAPPDATA%\YASK-CIEM\governance\access-reviews.json` when shared API mode is not configured; local lifecycle evaluation still runs on refresh. Shared mode stores campaigns, assignments, evidence snapshots, attestations, and audit events in the organization-scoped License Server workspace; Desktop never saves its own lifecycle reevaluation in shared mode. Decisions and exceptions are governance records only; they do not revoke permissions, remove memberships, disable identities, or call cloud APIs.

## Ownership, Reassignment, and Organization Reporting

This workflow covers temporary **access-review exceptions**. Local compliance-control exceptions remain separate assessment inputs; they are not included in the central governance report.

The shared Access Reviews workspace exposes current owner, reviewer, account, team, deadline, and a required reassignment reason. Current owner is separate from immutable `OwnerSnapshot`. Owners must be active accepted organization members with inventory access; cloud owner identifiers are not automatically treated as organization owners. Reviewer eligibility and separation of duties remain enforced by the server. Team is an explicit governed label, not inferred from cloud groups. Account attribution must match an organization/provider binding; labeling never rewrites evidence.

The assignment API requires the current `AssignmentVersion`. Each actual assignment change increments it and atomically records the authenticated actor, before/after owner/reviewer/account/team/deadline, reason, and correlation ID. A stale request returns 409; full campaign saves also cannot overwrite a newer assignment. Refresh before retrying. Reassignment does not reset attestation or escalation history and cannot extend a temporary exception; only the assigned reviewer may renew that exception.

The organization governance panel and `/governance/report` expose completion, unresolved and overdue work by provider/account/team, plus active/expired exceptions, current exception age buckets **0-29, 30-89, and 90+ whole UTC days**, and the oldest current exception age. Age starts at the most recent exception attestation; renewal begins a new age period. Legacy records without a decision timestamp have unknown age and are not counted in an age bucket. Legacy account/team labels appear as **Unknown account** / **Unassigned**, without guessing from evidence. Draft/cancelled items are excluded from totals; all campaign history is retained, with the latest 500 events included in the report. Individual campaign/item histories remain available in the review workspace. Deadlines are overdue at the exact deadline boundary; exception expiry is likewise inclusive.

Reading reports/assignees requires ViewFindings or ManageRemediations. Reassignment requires ManageRemediations; assigning a new account label additionally requires inventory access to validate the binding. Apply the [governance migration](../../infra/postgres/003-governance-deadlines.sql) before upgrading the server. See [Governance.http](../../src/CloudEntitle.LicenseServer/Governance.http) for API examples.
