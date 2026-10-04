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

## Access Reviews

The Access Reviews workspace creates campaigns from the current live scan or Demo Mode. Each item retains an evidence snapshot and links to its EffectiveAccess and optional AccessPath records. Campaign and item deadlines can be set independently, and every assignment, decision, exception, expiry, and escalation is appended to a local audit trail.

Reviewers can certify, reject, request investigation, or grant a temporary exception. Exceptions require a future expiry date; expired exceptions become unresolved and escalate on the next campaign refresh. Overdue pending items also escalate. A reviewer and a separate action actor are recorded for assignments and decisions.

Campaigns are stored locally under `%LOCALAPPDATA%\YASK-CIEM\governance\access-reviews.json`. A refresh reevaluates deadlines and expiry status. Decisions and exceptions are governance records only; they do not revoke permissions, remove memberships, disable identities, or call cloud APIs.
