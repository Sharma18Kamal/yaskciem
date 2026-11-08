# Client-owned emergency access and post-outage reconciliation

YASK-CIEM does **not** issue offline cloud credentials, cache approval bypasses or
claim to grant access when Entra, the server or the cloud is unavailable.
Clients own emergency identities and their tested runbooks outside the app.
This implementation records completed use and independent security review after
connectivity returns. It does not implement an offline emergency-account vault.

## Client preparation

Before an outage, the client security team must provision and protect independent
emergency identities using each provider's supported emergency-access guidance.
Keep secrets in the client's controlled vault, separate from this app. Establish
dual custody, tested authentication/recovery, minimum necessary scopes, monitoring,
periodic exercises and documented cleanup/credential rotation. A designated client
incident commander remains responsible for emergency authorization.
For Entra, follow the client's reviewed
[Microsoft emergency-access guidance](https://learn.microsoft.com/en-us/entra/identity/role-based-access-control/security-emergency-access)
and its availability/monitoring requirements; this app does not automatically
create or assign the accounts described there.
Never create a new standing privileged identity merely to satisfy this reporting UI.

After recovery, the client must verify actual cloud permission/session cleanup,
token/credential rotation and audit collection. Saved successful API calls are
partial evidence of use; an ended incident time, missing activity, accepted
revocation request or reviewer attestation does not prove access was revoked.
If the provider or session cannot be verified, escalate through the client's
incident workflow rather than marking cleanup successful.

## Deployment

Apply migrations through [012-break-glass-reports.sql](../../infra/postgres/012-break-glass-reports.sql)
with the DBA identity before deploying. Reports and reviews reject runtime
UPDATE/DELETE/TRUNCATE. Production startup does not migrate. Back up both tables;
there is no automatic retention/pruning and DBAs can alter guards (not WORM).

Configure current security reviewer Entra object IDs on the server:

```json
{
  "EmergencyAccessReview": {
    "Organizations": {
      "<tenant-guid>": {
        "<organization-guid>": {
          "ReviewerIds": ["<security-reviewer-object-id>"]
        }
      }
    }
  }
}
```

Between one and ten explicitly configured reviewers are accepted. Each must be an
active organization member with ManageRemediations, ViewInventory and ViewFindings,
and use delegated `access_as_user` authentication. The reviewer must differ from
the incident reporter. Reporter writes require the same organization permissions
and delegated authentication; listing reads require inventory/finding access.
Account bindings are checked before report or review creation. No role, emergency
account, ticket endpoint or cloud privilege is automatically provisioned.

## Workflow

1. Reconnect/sign in after the outage. Choose a bound activity account and,
   optionally, load a saved activity snapshot for that exact account.
2. Open **Client-owned emergency access: post-outage reports**. Enter the exact
   principal reported by cloud logs, completed start/end UTC timestamps
   (`yyyy-MM-ddTHH:mm:ssZ`), business reason and an opaque client incident ID.
   The interval is bounded to seven days; split longer incidents into linked
   reports. Never enter passwords, keys, tokens, signed URLs or confidential
   incident details; use opaque client-system references.
3. Submit explicitly. The server stores an immutable report, optionally matching
   at most 2,000 distinct successful source event IDs within the incident interval.
   Without matches it remains ClientAttestationOnly; with matches it is
   PartialActivityObservedNotRevocationProof. Audit actor names are not guessed
   or mapped from an email to a different cloud identity.
4. A configured independent security reviewer refreshes reports, selects the
   incident, enters rationale and a cleanup/rotation evidence reference, and
   records a review. ClientVerifiedClosure is explicitly a client attestation,
   not verified provider access removal. Leave it false when follow-up is needed.
   A report permits one immutable review; corrections require a new linked report.

Sign-in never submits reports or triggers cloud reads/writes. Context changes and
sign-out cancel pending UI operations and discard late responses. No report or
credential is queued offline. Refresh after an uncertain HTTP response before
repeating a submission. API callers should retain the client-generated report ID;
an exact same-reporter/body replay is idempotent and a conflicting replay returns
409. New evidence or corrections need a new report ID.

Reports list the newest 100 entries; a scoped selected GET remains available for
older IDs. Report/review and operational-event publication share a transaction.
Existing notification/ticket/SIEM rules can explicitly allow `breakglass.reported`,
`breakglass.clientclosurereviewed` and `breakglass.followuprequired`; this may send
client-configured notifications, but cannot authorize emergency access or approve
JIT. Notification bodies use incident IDs, not raw principals or client references.

[REST examples](../../src/CloudEntitle.LicenseServer/BreakGlass.http).
