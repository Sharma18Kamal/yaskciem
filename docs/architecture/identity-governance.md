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

Campaigns contain review items linked to EffectiveAccess and optional AccessPath records. Reviewers can certify, reject, or mark access for investigation. Decisions are governance records only; they do not revoke permissions, remove memberships, disable identities, or call cloud APIs.
