# Effective Access Boundary

Effective access is the normalized result of evaluating assignments, group membership, policy attachments, inheritance, scope, and resource relationships.

The application should preserve an auditable access path for every result, such as:

`User -> Group -> Role -> Permission -> Production Resource`

Direct and inherited access must remain distinguishable. If activity data is unavailable, findings must be reported as potential or unknown rather than confirmed unused access.
