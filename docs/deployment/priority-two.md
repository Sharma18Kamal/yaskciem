# Priority Two: privileged access and security operations

This release extends existing ticketing and SIEM delivery rather than adding a
parallel ticket system. All live connections are client-configured; sign-in and
saved evidence reads never provision endpoints or activate cloud privileges.

## Deployment and capabilities

Apply every numbered migration shipped with the matching release, in filename
order, with the DBA identity before deploying. Use the package manifest; sequence
gaps are allowed and must not be filled by renaming or inventing scripts. Startup
does not migrate. The staging installer packages schema files but neither
applies them nor starts services.
Readiness checks JIT tables, the valid status/time index and enabled audit guards.

| Capability | Implemented boundary |
|---|---|
| Multi-cloud JIT | Azure native PIM, client-owned AWS human-session broker protocol (not a native AWS adapter), GCP native PAM with explicit human mapping; two non-requester approvals, activation consent, exact-grant early revocation and durable audit |
| Emergency access | Online emergency JIT retains the configured approval threshold and shorter duration; separate client-owned post-outage break-glass reporting/review does not issue access |
| Change monitoring | Existing scan drift, saved comparisons, on-demand trust/credential observations and opt-in durable Azure/AWS/GCP client-bridge feeds; source-reported coverage/gaps remain unverified |
| Behavior | First-observed configured privileged actions, source-enriched unusual countries, configured permission-change bursts and privileged calls outside business hours |
| Decision transitions | Native GCP v3beta Preview capture for project setIamPolicy only; exact-context NOT_GRANTED-to-GRANTED IAM decision confirmation when complete provider evidence passes the gate; AWS/Azure remain Unsupported |
| SIEM/tickets | Existing Sentinel/Splunk/QRadar/Jira plus central ServiceNow incidents and Teams Workflows notifications |
| Compliance | Informative frameworks plus source-verified Azure Foundations v2.0.0 (1.5, 1.23, 5.1.2) and AWS Foundations v3.0.0 (1.12, 1.15) crosswalks; GDPR access-evidence crosswalk; catalog 2026.11.3, bundle schema 1.4; manual evidence remains unverified |

Offline credential issuance, continuous all-source trust/credential collection,
complete activity/geographic coverage and fully confirmed effective privilege
escalation remain outside the implemented boundary. Potential signals are not
confirmed escalation or malicious behavior. CIS Controls are not CIS Benchmarks;
the pinned recommendation subset is not latest-version or comprehensive
benchmark coverage, and compliance mappings are not certification.

See [client-owned emergency access reporting](emergency-access.md),
[continuous client-bridge change feeds](../continuous-change-feeds.md),
[on-demand trust/credential monitoring](trust-credential-monitoring.md),
[native IAM decision transition boundaries](authoritative-decisions.md), and
[compliance evidence and benchmark boundaries](../architecture/compliance-audit.md).

## Multi-cloud JIT and Azure PIM configuration

The client prepares a same-tenant Azure subscription, enables applicable PIM
licensing/policies, and grants the License Server's **system-assigned managed
identity** only the required subscription read and
`Microsoft.Authorization/roleAssignmentScheduleRequests/read,write` and
`roleAssignmentSchedules/read` operations for the intended scopes. Test the
native API in a disposable client scope before production use. Native Azure
policies may reject an assignment or duration; the app never substitutes an
unbounded ordinary role assignment. Azure national clouds and external tenants
are not supported by this adapter.
Deploy that managed identity in the client's Entra tenant; cross-tenant SaaS
managed-identity impersonation is not supported.

Configure a bounded target through the server configuration/secret provider:

```json
{
  "PrivilegedAccess": {
    "Organizations": {
      "<tenant-guid>": {
        "<organization-guid>": {
          "TenantId": "<same-tenant-guid>",
          "Enabled": false,
        "RequiredApprovals": 2,
        "ApproverIds": ["<manager-object-id>", "<security-object-id>"],
          "Targets": {
            "investigation": {
              "SubscriptionId": "<bound-subscription-guid>",
              "Scope": "/subscriptions/<guid>/resourceGroups/<client-group>",
              "RoleDefinitionId": "/subscriptions/<guid>/providers/Microsoft.Authorization/roleDefinitions/<role-guid>",
              "MaximumMinutes": 60,
              "EmergencyMaximumMinutes": 15,
              "Connected": false
            }
          }
        }
      }
    }
  }
}
```

After the client has prepared and verified native access, explicitly set
`Enabled=true` and that target's `Connected=true`. These are opt-in flags, not
a passive configuration health probe or proof of connectivity. The worker makes
no Azure calls without a queued request or a previously issued request needing
reconciliation. Disconnect prevents new activation but reconciliation and exact-grant early
revocation continue for outstanding grants; retain native read/revoke credentials
until outcomes are resolved. Removing the requester from organization membership
does not stop a current configured authorized security approver from revoking.

The approval threshold is configurable from one to two (default two), and the
configured approver list must contain at least that many distinct identities.
Every approver must be an active organization member with
ManageRemediations, ViewInventory and ViewFindings and use delegated
`access_as_user` authentication for decisions. Neither can request access
for their own request. The threshold is snapshotted with each request, so
configuration changes do not silently reinterpret an in-flight approval.
Requesters need ViewInventory/ViewFindings
and delegated `access_as_user` authentication; the target is always their Entra
object ID, never a submitted arbitrary user/device/service principal.
Manager/security relationships are explicitly configured object IDs, not guessed
from titles, email domains or group display names.

In Organizations:

1. Enter the target alias, reason and requested minutes; choose Emergency only
   when appropriate. Standard maximum is 60 minutes; emergency maximum is
   smaller than the standard maximum and no more than 15.
2. Request access. Each required approver refreshes, selects the request and
   records a justified approval. Duplicate/self approvals and stale versions are rejected.
   The request displays a low-confidence screening score based on configured
   scope, duration and emergency status; this is not a full provider permission
   evaluation.
3. The requester refreshes, selects the approved request, ticks one-time
   live consent and activates. All approvals/activation must occur within 30
   minutes of the request. Changed configuration, membership or bindings block
   queued activation when the worker rechecks.
4. Refresh JIT requests and read audit to observe native outcomes. A matching
   provider read that still shows the exact grant active may permit a revocation
   retry with bounded exponential backoff. Unknown, pending, expired or
   not-found observations never trigger another mutation. Revocation remains
   unverified until provider read-back confirms it.
5. To end access early, select the latest request, enter a reason, tick the
   separate **ending/cancelling** consent and choose **End / cancel**. The
   requester may end their own request; either current configured approver with
   the required organization permissions may revoke without a second approval.
   Stale version, missing consent/reason and unauthorized actors are rejected.

The database migration `015-jit-safety-guards.sql` prevents duplicate
outstanding grants for the same requester, provider account, scope and role.
If it detects pre-existing duplicates, it stops without deleting records;
verify provider state and resolve them before reapplying the migration. Failed
grant/revocation reconciliation emits high-severity operational events and
remains visible through existing notification and JIT status surfaces.

The server uses PIM `AdminAssign` to create a temporary **active** Azure resource
role schedule. This is not PIM self-activation of an existing eligible schedule,
and does not promise Azure's self-activation MFA/approval policy enforcement;
the client's own dual-control workflow governs the administrative assignment.
Require Conditional Access/MFA for the delegated app and configure native PIM
administrative-assignment policies as appropriate. No eligibility or permanent
standing access is silently created or removed.

The worker persists an absolute UTC start/end before issuing its deterministic
request-ID PUT. Azure's `AfterDateTime` expiration is independent of Desktop or
server uptime. Acceptance is only Submitted, never Active. Read-only GET
reconciliation verifies principal, role, scope, start and bounded end time.
Local time passing yields ExpiryPending until native Revoked/Expired status is
observed; it never creates a success-shaped "revoked" result.
Network timeouts, crashes and uncertain submissions are reconciled using GET,
**never blindly resubmitted**. If the request cannot be observed, an administrator
must inspect Azure and resolve it; native deletion is not automatically guessed.

Monitor the `privileged-access` worker heartbeat and `jit.expirypending`,
`jit.outcomeunknown`, `jit.grant_failed` and `jit.revocation_unverified` events. Native expiration removes only this
schedule, not independent existing grants or already obtained tokens/sessions.
Clients must test actual authorization expiry, provider propagation and session
behavior. Emergency requests also emit high-severity governance events.

Audit inserts and transitions share a PostgreSQL transaction. Audit tables reject
UPDATE/DELETE/TRUNCATE by runtime roles; DBAs can alter them, so they are not WORM
or provider-attested evidence. Back up both JIT tables; no automatic pruning is
implemented. Request listing returns the newest 100 rows; selected request reads
are available through the API. Audit reads return up to 500 events.

Reference architecture only, not a Britive connector or parity claim:
[Britive JIT permissions](https://www.britive.com/platform/jit-permissioning/).
Native API contract:
[Azure role assignment schedule requests](https://learn.microsoft.com/en-us/rest/api/authorization/role-assignment-schedule-requests/create?view=rest-authorization-2020-10-01).
REST examples: [PriorityTwo.http](../../src/CloudEntitle.LicenseServer/PriorityTwo.http).

### AWS: required client-owned access broker, not a native AWS adapter

This application does **not** issue STS credentials, assume roles directly, or
manufacture IAM Identity Center session expiration guarantees. The client must
operate an HTTPS broker with authenticated **native bounded human sessions**.
Only connect after testing actual human authorization expiration and exact-grant
revocation. A generic STS token vending endpoint is not a conforming broker:
AWS temporary credentials alone do not give this workflow reliable individual
session revocation.

Configure a target under the same organization block:

```json
"aws-investigation": {
  "Provider": "Aws",
  "ExternalTenantId": "123456789012",
  "RoleDefinitionId": "arn:aws:iam::123456789012:role/Investigator",
  "BrokerEndpoint": "https://client-broker.example.invalid/ciem",
  "AuthSecretKey": "ClientSecrets:AwsBroker",
  "RevocationAuthSecretKey": "ClientSecrets:AwsBrokerRevoker",
  "MaximumMinutes": 60, "EmergencyMaximumMinutes": 15, "Connected": false
}
```

The account must be an existing organization AWS binding. Secret **keys** refer
to server configuration/secret-provider entries, never Desktop fields. Keep
those entries available after disconnect; rotate values without changing saved
keys. Redirects and cookies are disabled. Endpoint must be HTTPS without
userinfo/query/fragment; only server-configured endpoints are used.

Broker protocol (paths relative to `BrokerEndpoint`, server Bearer auth):

* `PUT /grants/{requestId}` creates precisely that grant. JSON includes
  `requestId`, `requesterObjectId` (verified Entra oid), `accountId`, `role`,
  `startsAtUtc`, absolute `expiresAtUtc`, `reason`, `emergency`,
  `humanSessionRequired=true`, `requesterOwnedHandoffRequired=true`.
* `GET /grants/{requestId}` returns exact `grantId=requestId`,
  `requesterObjectId`, `accountId`, `role`, `startsAtUtc`, `expiresAtUtc`,
  `status` (`Submitted`, `Active`, `Revoked`, `Expired`, `Failed`) and
  `nativeExpirationGuaranteed=true`, `perGrantRevocationGuaranteed=true`,
  `requesterOwnedHandoff=true`. Never extend the submitted deadline.
* `PUT /grants/{grantId}/revocations/{revokeId}` explicitly removes only this
  native grant. JSON: `grantId`, `revocationId`, `reason`. Support durable
  deterministic IDs even though the application never blindly resends writes.
* `/sessions/{requestId}` is the requester-owned human handoff. Desktop opens
  that stable URL only for its own saved active grant. Broker authenticates the
  human directly and verifies the requester oid, authorization and expiry.
  This URL must not contain bearer credentials or cloud keys.

The broker must return **no raw cloud credentials**. The app parses only bounded
status/identity evidence and never saves responses or logs authentication data.
Acceptance remains Submitted; subsequent GET is needed for Active. GET 404,
timeout, malformed/foreign identity or missing native guarantees is unknown,
not revocation. Native expiration must continue independently of app/broker
availability and native revoke must invalidate the session, not just its
database row. The protocol's guarantee flags are **client broker assertions**,
not independently attested AWS proof; production compliance/session coverage
cannot be inferred from mock adapter tests. Independent grants/tokens are
outside this exact-grant contract.

### GCP: native Privileged Access Manager bounded grants

Configure the native PAM service/entitlement, eligibility, applicable IAM
permissions and authorization in the client GCP project/folder/organization.
The adapter supports an entitlement whose parent resource matches the bound
external account and a single exact configured IAM role binding.

```json
"gcp-investigation": {
  "Provider": "Gcp", "ExternalTenantId": "client-project",
  "Scope": "projects/client-project/locations/global/entitlements/investigation",
  "RoleDefinitionId": "roles/viewer",
  "AuthSecretKey": "ClientSecrets:GcpAdmin",
  "RevocationAuthSecretKey": "ClientSecrets:GcpAdmin",
  "MaximumMinutes": 60, "EmergencyMaximumMinutes": 15, "Connected": false
}
```

Also configure organization `HumanPrincipals:<requester-Entra-oid>:Principal`
as `user:human@client.example` and
`HumanPrincipals:<requester-Entra-oid>:AccessTokenSecretKey` as a secret-provider
key for **that human's native Google OAuth access token**. The app does not
accept caller-submitted principals, service accounts or device identities.
Client authentication must maintain/refresh the human token externally, with
`cloud-platform` plus `openid email` scopes; the client-owned revocation token
requires `privilegedaccessmanager.grants.get` and `.revoke` on the entitlement.
Tokens are loaded from the secret provider at call time, never persisted.

Before grant creation, native OpenID userinfo verifies the mapped human email
and email verification, and PAM entitlement GET verifies the exact configured
resource/role. PAM `requester` is **output-only**; the adapter cannot use a
server service-account token to arbitrarily set a human requester. The mapped
human's native authentication creates the grant using
`POST .../{entitlement}/grants?requestId={requestId}` and
`requestedDuration="<seconds>s"`. The native generated grant name is saved.
Application dual approval never bypasses native PAM eligibility or approver
policies: these can add approval/delay/rejection (`APPROVAL_AWAITED` is not
Active). Duration begins at native access grant time, which can follow native
approval; the worker saves native `auditTrail.accessGrantTime` and bounded
requested duration as observed start/end. Native PAM removes the grant at the
bounded duration independently of the application.

GET exact grant verifies requester, resource/role, duration and native start;
`externallyModified=true` remains unknown because PAM no longer guarantees the
effective access. `POST .../{exact-grant}:revoke` uses client native revoke
authorization and reason. Native `REVOKING`/operation acceptance is not revoked;
GET `REVOKED` or `ENDED` establishes removal. `EXPIRED` is approval-window
expiration (no access), **not** an active grant expiry status.

**Native correlation limit:** PAM generates the grant name and guarantees
create request-ID deduplication only for at least 60 minutes, but GET does not
address that request ID. If create succeeds remotely and its response/name is
lost, this adapter cannot safely associate a grant using user/time/list guesses.
It persists OutcomeUnknown (or RevokeQueued), never resends create and never
claims cancellation; the client must investigate native PAM audit evidence.
No automatic credential refresh/impersonation or production native policy
coverage is claimed by the offline tests.

Public native contracts:
[PAM create](https://cloud.google.com/iam/docs/reference/pam/rest/v1/projects.locations.entitlements.grants/create),
[PAM grant states/audit trail](https://cloud.google.com/iam/docs/reference/pam/rest/v1/folders.locations.entitlements.grants),
[PAM revoke](https://cloud.google.com/iam/docs/reference/pam/rest/v1/projects.locations.entitlements.grants/revoke).

### Durable early revocation and concurrency

`POST /privileged-access/{requestId}/revoke` requires delegated user auth,
`expectedVersion`, nonempty `reason`, and separate `liveConsent=true`. Pending,
Approved and unissued Queued requests become Cancelled with **zero cloud calls**.
Issued grants persist RevokeQueued plus a **separate revoke UUID and reason**
before any write. Worker persists RevokeIssuing before one native write; accepted
or uncertain responses become RevokeOutcomeUnknown. A crashed/unknown revoke is
GET-only thereafter, even when the provider still shows Active; no blind resend
and no success inferred from acceptance, deadline or missing resource.

Azure uses documented `AdminRemove` with the saved exact
`targetRoleAssignmentScheduleId`, principal and role in a separately identified
roleAssignmentScheduleRequest PUT. It never deletes blanket role assignments.
Provider auth and saved target are used even after activation is disabled or
disconnected; current authorized approver validation is still required.
PostgreSQL per-request advisory leases serialize revoke decisions and worker
issuance/reconciliation across processes; row versions reject stale decisions.
Once revocation is queued, worker never creates another grant for that request.
Audit and status writes share a transaction in existing migration 010 tables.
Monitor RevokeQueued/RevokeIssuing/RevokeOutcomeUnknown as unresolved, not success.

## Saved behavior and document change review

Load activity and/or permission-document evidence for a bound account, then
choose **Analyze saved behavior and document changes**. Requires
ManageRemediations, ViewInventory and ViewFindings. It performs no live polling.
The server compares the document snapshot to the newest nonoverlapping prior
observation among at most 50 prior document candidates and uses at most five older activity snapshots from the newest 50
summaries. Equal/future observations cannot become a prior baseline.

Configure per-provider behavior policy, for example:

```json
{
  "BehaviorAnalysis": {
    "Organizations": {
      "<tenant-guid>": {
        "<organization-guid>": {
          "Aws": {
            "Enabled": true,
            "TimeZoneId": "UTC",
            "BusinessStartHour": 9,
            "BusinessEndHour": 18,
            "MinimumBaselineDays": 7,
            "MinimumBaselineEvents": 20,
            "PrivilegedActions": ["iam.amazonaws.com/CreateUser"],
            "PermissionChangeActions": ["iam.amazonaws.com/AttachUserPolicy"],
            "PermissionChangeBurstThreshold": 5
          }
        }
      }
    }
  }
}
```

Use exact normalized source/API action or explicit permission evidence strings;
AWS API names are not mapped into invented IAM permissions. Business hours are
weekdays in the configured OS-supported time zone; no location-based inference,
holiday calendar or overnight shift model is implemented. Unknown outcomes and
unattributed principals are excluded. Baselines count distinct event IDs and
ignore future calls. Permission-change bursts require a trained principal
baseline and the configured distinct-call threshold within one hour; they do
not prove effective privilege escalation.

Geographic anomalies require a two-letter, source-supplied `CountryCode` on saved
events and sufficient prior enriched observations. Native sources do not infer
country from IP; the client must provide authorized enrichment. Source IP is
retained when reported by CloudTrail, Azure Activity Log and GCP request metadata,
but no external geolocation service is contacted. Include this metadata in the
client's data-retention/privacy review.

Document changes are NewlyObservedGrant, NoLongerObservedGrant or
ObservedPolicyOrAssignmentChange, preserving source digest/statement differences.
Disappearance is not deletion proof. Inherited grants and effective conditional
authorization remain outside this permission-document comparison. Separate
[trust/credential monitoring](trust-credential-monitoring.md) collects supported
live documents on demand and compares saved partial observations; it does not
establish all-source continuity or confirmed effective escalation.
Initial observations establish a quiet baseline. Both services fail explicitly
above 2,000 review signals each rather than publishing truncated success.

Results are Potential review signals, never confirmed malicious behavior or
automatic remediation authority. Deduplicated `behavior.anomaly` and
`documents.observedchange` events reference source snapshots and can use existing
SIEM, notifications and ticketing. Re-running an unchanged comparison does not
duplicate events. Event notification bodies omit raw principal/activity data;
authorized analysts can reopen source snapshots and rerun the comparison.

## ServiceNow and Teams on existing delivery infrastructure

Configure separate organization channel aliases alongside Sentinel/Splunk/Jira:

```json
{
  "CentralNotifications": {
    "Organizations": {
      "<tenant-guid>": {
        "<organization-guid>": {
          "Channels": {
            "snow": {
              "Kind": "ServiceNow",
              "Url": "https://<instance>.service-now.com",
              "UserName": "<integration-user>",
              "ApiToken": "<secret-provider-password>"
            },
            "teams": {
              "Kind": "TeamsWorkflow",
              "Url": "<client-owned-Power-Automate-Workflows-signed-webhook>"
            }
          }
        }
      }
    }
  }
}
```

ServiceNow uses Basic authentication to `/api/now/table/incident`, requires 201
acceptance, and sends `correlation_id` equal to the delivery ID. Configure the
client instance/integration user and minimum incident creation permissions.
Only HTTPS `*.service-now.com` instance roots are accepted; custom domains and
OAuth editions are not yet supported. Desktop's existing standalone ticketing
remains unchanged; shared ticket requests now accept Jira or ServiceNow aliases.

Teams uses an Adaptive Card message to a client-created commercial Workflows
webhook under `*.environment.api.powerplatform.com/powerautomate/automations/direct/workflows/`.
Legacy Office 365 connectors, other domains, sovereign clouds and signed-in-user
trigger authentication are not supported. Use a compatible webhook trigger and
co-owners for continuity. The signed URL is a secret; keep it server-side in a
secret provider, never in tickets or Desktop fields. No Teams channel/workflow is
provisioned by the app.

Use the existing organization notification-rule editor and event-type allowlist.
For automated Jira/ServiceNow creation, explicitly opt into **Policy:
auto-create tickets** with nonempty allowed event types. Manual selected-event
ticket creation remains deduplicated and needs ManageRemediations.
Teams rules send notifications, not approval decisions. SIEM/ticket replies
cannot approve JIT or alter cloud access.

All transports reuse passive configuration health, outbox and delivery history.
HTTP 429 is retryable; network uncertainty, 408, server errors and expired
ServiceNow/Teams sending leases become dead letters, not duplicate-prone automatic
retries. Client APIs may ignore idempotency headers; reconcile ambiguous acceptance
by delivery/correlation ID before manual action. Acceptance is not proof that a
ticket was processed or a Teams user saw a message. Redirects are disabled and
response bodies/secrets are not copied into health/audit events.
