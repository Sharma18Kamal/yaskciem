# YASK-CIEM user manual

Step-by-step operator and administrator instructions for the current Desktop
source.

**Audience:** cloud-security analysts, reviewers, organization administrators and
deployment administrators. This manual describes available workflows, not a
certificate of production readiness. An older installed package may not contain
the current source features or fixes.

## Contents

1. [Safety, terminology and operating modes](#1-safety-terminology-and-operating-modes)
2. [Installation and first sign-in](#2-installation-and-first-sign-in)
3. [Header, navigation and common controls](#3-header-navigation-and-common-controls)
4. [Cloud Accounts](#4-cloud-accounts)
5. [Dashboard, reports and compliance evidence](#5-dashboard-reports-and-compliance-evidence)
6. [Identities, Resources and Entitlements](#6-identities-resources-and-entitlements)
7. [Effective Access](#7-effective-access)
8. [Permission Usage](#8-permission-usage)
9. [Toxic Combinations](#9-toxic-combinations)
10. [What-if Simulator](#10-what-if-simulator)
11. [Access Paths](#11-access-paths)
12. [Risk Findings and Recommendations](#12-risk-findings-and-recommendations)
13. [Remediation](#13-remediation)
14. [Organizations: identity, members, roles and accounts](#14-organizations-identity-members-roles-and-accounts)
15. [Organizations: shared scans and schedules](#15-organizations-shared-scans-and-schedules)
16. [Organizations: activity and permission documents](#16-organizations-activity-and-permission-documents)
17. [Organizations: JIT access](#17-organizations-jit-access)
18. [Organizations: behavior, trust and credential observations](#18-organizations-behavior-trust-and-credential-observations)
19. [Organizations: continuous change feeds](#19-organizations-continuous-change-feeds)
20. [Organizations: authoritative IAM decisions](#20-organizations-authoritative-iam-decisions)
21. [Organizations: alerts, connectors and tickets](#21-organizations-alerts-connectors-and-tickets)
22. [Organizations: live cloud administration](#22-organizations-live-cloud-administration)
23. [Organizations: emergency-access reports](#23-organizations-emergency-access-reports)
24. [Access Reviews](#24-access-reviews)
25. [Scan History](#25-scan-history)
26. [Settings, backup and restore](#26-settings-backup-and-restore)
27. [Feedback](#27-feedback)
28. [Daily workflow, troubleshooting and acceptance](#28-daily-workflow-troubleshooting-and-acceptance)

## 1. Safety, terminology and operating modes

### 1.1 What the app does

YASK-CIEM collects cloud identity/access information, presents evidence-linked
findings, supports offline analysis, and provides access-governance workflows.
It supports Azure, AWS and Google Cloud, plus selected local Kubernetes contexts.

| Workflow type | Meaning | Examples |
| --- | --- | --- |
| Saved/local analysis | Reads existing evidence; no live cloud collection | What-if, saved usage analysis, path inspection |
| Live read | Queries cloud APIs using an authorized identity | Discovery, scans, activity and document collection |
| Local/central write | Saves records or sends data, without changing cloud IAM | Publishing, review decisions, rules, tickets |
| Live cloud write | Can change cloud access; requires explicit approvals/consent | Supported administration/remediation, JIT activation or early ending |

**Scanning is read-only. The whole application is not read-only.** Never select a
LIVE apply/rollback or JIT activation/end option without an approved change.
Live reads can incur provider charges. Exports, tickets and webhooks can disclose
sensitive identity/access information to their configured destinations.

### 1.2 Terms and result meanings

| Term | How to interpret it |
| --- | --- |
| Identity/principal | User, group, workload role, service principal, managed identity or service account |
| Entitlement | One permission/role grant at a scope |
| Scope | Subscription, account, project, folder, namespace or resource where evidence applies |
| Effective access | An evaluated/modelled result; review its coverage and conditions before treating it as actionable |
| Inherited | Access evidence coming through an ancestor or membership relationship |
| Allow / Deny | The model's decision for the supplied evidence/context, not necessarily a live provider decision |
| Conditional | Required context is missing or the condition cannot be evaluated |
| NotApplicable | No applicable model contribution; **not** provider-confirmed denial |
| Unknown / Unsupported | Insufficient evidence or unsupported semantics; never a clean bill of health |
| Partially evaluated | Some relevant evidence or semantics are unresolved |
| Potential | A candidate requiring investigation, not a confirmed exploit or escalation |
| Source coverage | Whether a source was collected, empty, skipped, denied or unavailable |
| Evidence hash | Identifies captured content; does not by itself prove completeness, origin or historical validity |

Conditions are not assumed true. Condition explanations can identify matched,
not-matched or unresolved expressions. Full provider authorization, all policy
constraints and workload dependencies are not comprehensively modeled.
See [effective-access boundaries](../effective-access/effective-access.md).

### 1.3 Local, shared and demo data

- **Local:** Desktop stores SQLite inventory and supported local workflow records.
- **Shared:** the HTTPS API stores organizations, members, shared evidence and
  jobs in PostgreSQL. Features require the appropriate organization permission.
- **Demo:** synthetic data for training, with no cloud collection. Do not mix
  demo screenshots or reports into customer acceptance evidence.
- New live collection needs successful online trial/license validation.
  Existing local results remain readable/exportable after expiry or outages.
- Scheduled scans use the **server identity's** credentials; interactive
  Desktop scans use the **operator's** CLI credentials.

## 2. Installation and first sign-in

### 2.1 Before opening Desktop

1. Follow the [installation and cloud onboarding guide](../deployment/customer-installation-guide.md).
2. Obtain a trusted, current Desktop package and the matching server configuration.
3. Ask the administrator for the API HTTPS URL, Entra tenant GUID, Desktop client
   GUID and delegated API scope. A Blob URL is not an API URL.
4. Install only the required CLIs: Azure CLI, AWS CLI v2, Google Cloud CLI,
   optionally `kubectl` and its authentication plugin.
5. Use an approved read-only identity for initial scans.
6. Confirm the License Server is available. Full deployment acceptance also
   requires healthy PostgreSQL, workers and immutable archive.

### 2.2 Sign in and start the trial

1. Have the administrator configure `YASK_CIEM_API_BASE_URL`,
   `YASK_CIEM_ENTRA_CLIENT_ID` and `YASK_CIEM_API_SCOPE` for your Windows user.
2. Close Desktop and reopen it with the updated environment.
3. Open **Organizations**, enter the tenant GUID and select **Sign in**.
4. Finish browser authentication and approved consent prompts.
5. Select **Start 30-day trial**, then **Refresh license**.
6. Confirm the header shows an active entitlement before collecting data.
7. Select/create the authorized organization if using shared features.

The trial is registered once per Entra tenant for 720 hours. Reinstalling does not
reset it. Cloud CLI login is separate from this API sign-in, even for AWS/GCP-only
users. Do not delete database history to resolve a licensing problem.

### 2.3 Safe first exercise

1. Use **Settings > Load Demo Data** for training on a separate test profile.
2. Inspect Dashboard, Identities, Effective Access and What-if without cloud calls.
3. Reset the demo when finished and verify which data is displayed.
4. For a live test, connect one provider and explicitly select one approved
   non-production account. Follow sections 4 and 5.

### 2.4 Optional web operations console

Administrators can use the separately configured
[web operations console](../deployment/web-operations-console.md) to inspect
supported central operational status. It is not a full replacement for Desktop
scanning, simulation or governance workflows.

## 3. Header, navigation and common controls

| Option | Step-by-step use | Result/caution |
| --- | --- | --- |
| License status | Read the status before scans; use the two license buttons to resolve it | Unavailable validation blocks new licensed operations |
| **Start 30-day trial** | Sign in under Organizations, then select once | Persistent tenant registration; repeated requests retain original dates |
| **Refresh license** | Select after sign-in, trial registration or an administrator-approved entitlement update | Reads current server status; does not purchase a license |
| **Connect Cloud** | Select to open Cloud Accounts, then use a provider's Connect | Does not automatically choose every cached CLI session |
| **Disconnect Cloud** | Open Cloud Accounts/disconnect the selected provider; if a sign-in is pending, use **Cancel Sign-in** | Canceling the wait does not close an external CLI terminal |
| **Settings** | Select to open configuration/backup options | See section 26 |
| Minimize / maximize / close | Use the window buttons; drag the header to move the window | Closing Desktop does not stop independent server jobs |
| Left navigation | Select a page; scroll the menu for lower entries | Advanced organization panels are inside Organizations, not separate main-menu entries |
| Status/error text | Read the page status after every operation | A button click alone is not proof of success |

On grid pages, use available search/filter boxes and sortable column headers.
Select a row to display its details where provided. Expand collapsed sections
and scroll horizontally to inspect long IDs/evidence. **Refresh** reloads the
page's data; it is not necessarily a new cloud scan.

Disabled controls normally need a selection, valid fields, permission, license,
configured server capability or completed preceding step. Read the tooltip/status;
do not grant broader cloud roles merely to enable an unsupported feature.

Starting with Desktop 1.0.0.68, the mouse wheel scrolls the table or multiline
editor under the pointer first, then the containing page when that control reaches
its top/bottom edge. Empty tables and horizontal-only panels do not trap vertical
scrolling. Over a closed dropdown, the wheel scrolls the page without changing
the selection; open the dropdown to scroll its choices. Sidebar scrolling remains
independent. Scrolling respects Windows wheel line/page settings; this does not
add middle-button auto-scroll.

## 4. Cloud Accounts

### 4.1 Provider connection cards

1. Select **Cloud Accounts** or **Connect Cloud**.
2. Read the provider's permission guidance.
3. Select **Connect** for each provider you intend to use this session.
4. Complete any external authentication and return to Desktop.
5. Select **Refresh sign-in status** to recheck selected providers.
6. Select **Discover accessible accounts** to list scopes and profile metadata.
7. Check account ID, CLI profile, permission set and scan-access status.

| Provider | Recommended preparation | Important boundary |
| --- | --- | --- |
| Azure | `az login --tenant '<tenant-guid>'`; verify `az account show` | ARM Reader does not automatically grant Graph/PIM reads |
| AWS | `aws configure sso --profile ciem-audit`, then `aws sso login --profile ciem-audit`; verify STS identity | Connect can launch `aws configure --profile yask-ciem`; this is key configuration, not SSO setup. Prepare SSO externally; do not enter long-lived keys unnecessarily |
| GCP | `gcloud auth login`; verify `gcloud auth list` and visible projects | Active CLI identity and project/ancestor permissions are separate requirements |
| Kubernetes | Prepare authorized kubeconfig contexts externally | No provider card grants cluster access; select contexts explicitly in Dashboard |

Provider details: [Azure](../providers/azure.md),
[AWS](../providers/aws.md), [GCP](../providers/gcp.md),
[Kubernetes](../providers/kubernetes.md).

Use the individual **Disconnect** button to exclude a provider. AWS disconnect
does not delete profiles, deactivate keys or log out shared SSO credentials.
It is not a way to revoke cloud access. For other providers, account for the
effect of CLI sign-out on other tools. Use **Stop Scan** separately for an
already-running collection. Inactivity can disconnect active app connections;
recheck status before starting another operation.

### 4.2 Verify an exact administrator-approved scope

Use this before broader onboarding:

1. Select a discovered account row, or fill the scope fields manually.
2. Choose **Provider** and enter **Subscription / account / project ID**.
3. For Azure, enter the **Expected Azure tenant ID**.
4. For AWS, enter the **Existing AWS CLI profile**, including `default` when
   that is the approved profile.
5. For GCP, enter the **Expected active CLI account**. Impersonation and
   credential/token-file overrides are not supported by this check.
6. Select **Verify entered account identity**.
7. Check the returned identity/scope. If mismatched, correct external CLI
   configuration and verify again.
8. Select **Scan verified account** only after successful verification.
9. Review **Latest permission and source coverage**.

This scan rechecks the verified identity and collects only that account/profile.
It excludes other providers, provider-wide directory/organization collection and
Kubernetes contexts. It does not create an organization account binding.
Do not change CLI identity/configuration while collection is running.

## 5. Dashboard, reports and compliance evidence

### 5.1 Discover and run a scan

1. Confirm API entitlement and intended provider connection.
2. Select **Discover accounts**.
3. Expand **Discover accounts to choose the scope for this scan**.
4. Inspect the provider/account/profile/context rows and their **Scan** selections.
5. Use **Select none**, then select the intended rows; **Select all** selects
   every available row. Do not rely on defaults for your first scan.
6. Select **Scan Now**.
7. Watch the progress/status indicator. Use **Stop Scan** to request cancellation.
8. Check final status, source coverage and Scan History before interpreting results.

No collected data, canceled collection and successful empty queries are different
outcomes. Partial or denied sources must not become a conclusion of no risk.

### 5.2 Dashboard cards and charts

| Display | Use |
| --- | --- |
| Accounts, identities, resources, entitlements | Check whether the expected inventory was observed |
| Privileged identities and severity counts | Triage candidate high-impact access; inspect supporting evidence |
| Last scan / evidence age | Establish freshness before making a decision |
| Source coverage | Find denied, unavailable, skipped and partial sources |
| Unsupported permission sources | Identify analysis that cannot be enabled merely by granting permission |
| Attribution confidence | Inspect how much matching change-time/actor evidence is available |
| Unknown / unsupported evaluations | Investigate unresolved evidence instead of treating it as safe |
| Provider/severity/decision breakdowns | Prioritize investigation; not a certification or exploitation probability |

### 5.3 Generate a report

| Category option | Intended focus |
| --- | --- |
| Executive | High-level posture and priorities |
| IdentityRisk | Identity-focused risk evidence |
| PermissionUsage | Observed usage and its coverage |
| ToxicCombination | Dangerous permission combinations |
| AttackPath | Modeled paths and relationships |
| Remediation | Review plans and proposed changes |
| ComplianceAssessment | Evidence assessment and gaps |
| AuditHistory | Recorded audit history |
| Technical | Detailed technical inventory |
| Identity | Identity inventory |
| Entitlement | Observed grants |
| Risk | Risk findings |
| Recommendation | Proposed improvements |

1. Choose a **report category** in the selector beside **Generate Report**.
2. Select **Generate Report**.
3. In the save dialog choose the offered format and destination. The UI supports
   JSON, CSV, HTML, PDF or Excel reporting; available output depends on the
   selected report workflow.
4. Open the saved file and verify its scope, evidence age, gaps and counts.
5. Distribute only through approved channels.

### 5.4 Standards and assessment history

The framework selector offers **CiemBaseline**, **LeastPrivilege**,
**IdentityGovernance** and **AccessReview**. These are internal assessment
frameworks, not provider authorization modes.

The standards crosswalk checkboxes are **CIS Controls v8.1**, **NIST SP 800-53
Rev. 5**, **ISO/IEC 27001:2022**, **SOC 2 Trust Services Criteria**, **NIST CSF
2.0**, **PCI DSS v4.0.1**, **GDPR access-evidence crosswalk**, **CIS Microsoft
Azure Foundations Benchmark v2.0.0** and **CIS AWS Foundations Benchmark v3.0.0**.
Selecting one does not run every check in that external standard.

1. Expand **Standards and assessment history**.
2. Select the framework checkboxes relevant to your assessment. Use the
   framework selector beside **Export evidence bundle** for its export/history.
3. To add manual evidence, enter the internal **Control ID**, **Summary**,
   **Reference** and **Recorded by**, then **Add manual evidence**.
4. Select an incorrect manual-evidence row and **Remove selected** to remove
   it from the local draft.
5. To record a control exception, enter **Control**, **Reference**, **Recorded
   by**, a future **Expires** date and **Rationale**; add optional approval
   evidence reference, then **Add exception**.
6. Select an exception and **Remove selected** only when appropriate for the draft.
7. Sign in/select an organization with **Export reports** permission.
8. Select **Export evidence bundle**, choose the destination and inspect controls,
   evidence, gaps and assessment metadata.
9. Select **Load assessment history** for saved results of the chosen framework.

Manual evidence is classified, not independently verified, and cannot by itself
turn an unassessed control into a pass. An exception is not a technical fix or
permission grant. Evidence bundles are signed snapshots,
not auditor attestations, full benchmark coverage or compliance certification.
Control exceptions are separate from Access Review exceptions.

## 6. Identities, Resources and Entitlements

### 6.1 Identities

1. Run/load a scan, then select **Identities**.
2. Search by visible identity name, type/provider or other supported text.
3. Inspect human and workload identities, privileged flags, owner and account.
4. Review activity, credential dates and federation fields where present.
5. Use the identity to investigate related rows in Effective Access, Access Paths
   and Risk Findings.

A missing owner/date is missing evidence, not proof of inactivity or a bad
credential. Eligible PIM access is not the same as active access.

### 6.2 Resources

1. Select **Resources**.
2. Search/sort by provider, type, scope or resource ID.
3. Confirm the account and scope before linking a finding to a resource.
4. If resources are absent, check collection coverage before concluding none exist.

Resource inventory does not include every data-plane object or runtime behavior.

### 6.3 Entitlements

1. Select **Entitlements**.
2. Inspect provider, subject, grant, scope, assignment type and source.
3. Inspect the discovered role/permission inventories beneath the assignments.
4. Distinguish assignments, role definitions, policy statements and usage records.
5. Use Effective Access to inspect modeled decisions rather than treating every
   displayed role as fully evaluated resource access.

These pages display evidence; editing a grid does not implement cloud changes.

## 7. Effective Access

### 7.1 Inspect access rows

1. Select **Effective Access** and filter for the identity or scope of interest.
2. Inspect direct/inherited assignment type, role/permission, source and decision.
3. Read evaluation status, path/evidence and condition limitations.
4. Treat usage/change timestamps according to their source-specific meaning.

Live provider summaries can be display-oriented rather than complete native
authorization evidence. An Allow with partial status is not a guarantee of
successful cloud access.

### 7.2 Permission simulator: Evaluate

1. Load a supported Demo Mode snapshot; this single-permission simulator is not
   a general live-provider authorization API.
2. Select **Principal**, **Resource** and **Permission**.
3. Select **Evaluate**.
4. Read decision, evaluation status, source evidence, diagnostics and access path.
5. If Conditional/Unknown, retain the limitation; do not interpret it as Deny.

This uses normalized saved evidence and makes no live cloud call.
Use **What-if Simulator** for before/after changes rather than this tuple check.

## 8. Permission Usage

### 8.1 Analyze saved evidence

1. Open **Permission Usage**.
2. Use **Refresh accounts**, select the account, provider filter and identity.
3. Select an observation period: **30**, **60** or **90** days.
4. Enter search text if needed, then select **Analyze saved evidence**.
5. For training only, **Load Demo Mode** loads synthetic evidence.
6. Inspect all four tabs:

   | Tab | What to do |
   | --- | --- |
   | **Permission usage** | Inspect permission/resource, period states, successful action counts, first/last use and attribution |
   | **Identity / unused analysis** | Inspect not-observed and inconclusive results plus correlation limitations |
   | **Historical activity** | Set **From UTC** and **Through UTC** dates and inspect matching saved events |
   | **Risk findings & recommendations** | Review proposals together with evidence quality and limitations |

7. Select **Export usage CSV** after analysis; protect the exported evidence.

**Not observed is not proven unused.** Missing audit coverage, limited retention,
truncation and ambiguous action-to-grant correlation prevent safe automatic removal.

### 8.2 Collect and analyze live activity

1. Prepare client-owned logs and read permissions before enabling this option.
2. Select the activity source for the account.
3. Fill **Region**, **Profile**, **Destination**, **Container** and **Prefix**
   only as required by that source.
4. Review costs and scope, then check **I authorize a read-only audit query for
   the selected client account**.
5. Select **Collect and analyze** and inspect source status/coverage.
6. Reanalyze saved results without repeating live collection when possible.

Sources and identifiers are explained in section 16 and the
[activity setup guide](../deployment/cloud-activity.md). The app does not enable
provider logging, create destinations or grant their permissions for you.

## 9. Toxic Combinations

### 9.1 Review findings

1. Open **Toxic Combinations**.
2. Select **Refresh saved findings**, or **Load Demo Mode** for training.
3. Use search to narrow the results.
4. Inspect the tabs in order:

| Tab | Purpose |
| --- | --- |
| **Toxic combination dashboard** | Summary of findings and priorities |
| **Identity risk analysis** | Which identities contribute to the observed candidates |
| **Dangerous permission combinations** | Permission pairs and applicable scope/resource evidence |
| **Finding evidence** | Why the combination was reported, with confidence/limitations |
| **Cross-account trust paths** | Relationships and their feasibility/evidence, not automatic exploit proof |
| **Remediation recommendations** | Review proposals; no automatic removal |
| **Rule management** | Configure supported detection rules and author additional local rules |

### 9.2 Configure or create a rule

1. In **Rule management**, review existing editable rule settings, then use
   **Save rule configuration** to persist intended changes.
2. For a new rule fill **Key**, **Name**, **Provider**, **Resource requirement**,
   **Severity**, **Risk score**, **Description** and **Recommendation**.
3. Enter the first and second permission patterns.
4. Select the applicable constraints: **Require same resource**, **Require
   evidenced trust path**, **First permission is privileged**, **Second
   permission is privileged**.
5. Select **Preview against local inventory**.
6. Inspect matches and unexpected/missing results before selecting
   **Save previewed rule**.
7. If changing inputs invalidates the preview, preview again before saving.

Rule edits affect analysis configuration, not cloud IAM. Validate custom rules
against known fixtures before treating their findings as accepted detections.

## 10. What-if Simulator

This is offline hypothetical analysis. It never directly edits cloud policies.

### 10.1 Choose an evidence source

1. Select **Refresh saved accounts**.
2. Choose an account and **Load saved inventory**; or select
   **Import offline JSON** and supply the documented schema 1.0 bundle.
3. Inspect provider and limitations before building the scenario.
4. Do not import arbitrary native policy JSON as though it were a complete bundle;
   native export projections need explicit resource/identity mapping.

### 10.2 Configure a scenario

1. Choose **Remove**, **Add** or **Replace**.
2. Choose the **target kind** (**Role**, **Policy** or **Identity**), then the
   target available for that kind. Select the affected identity when relevant.
3. Select or enter the exact **Action**.
4. For Replace, enter the exact **Replacement action**.
5. Set **Resource scope** when narrowing a role/policy assignment; a direct
   identity addition requires exactly one inventoried resource scope.
6. Select **Run isolated simulation**.

Role/policy edits include every inventoried assignment of that target, not only
the identity currently visible in a selector.

### 10.3 Interpret and export

1. Read status, summary, operational impact and analysis limitations first.
2. Inspect added/removed attack paths and the access-change grid.
3. Select a row to inspect removed and added origins, source/scope/digests,
   and alternative grants of the **same action**.
4. Compare Before/After **and** their evaluation statuses.
5. Select **Export simulation report** to save JSON for review.

`ModeledAccessLost` is not verified cloud denial. A different action is not
automatically an equivalent substitute. Exposure-reduction percentage concerns
affected modeled pairs, not overall enterprise risk or exploit probability.
Operational dependencies remain unknown without supporting usage evidence.
Unsupported wildcard subtraction and work-budget limits can reject a scenario
or withhold scores. Retain those limitations in any approval record.

Full input schema, examples and boundaries:
[What-if Simulator](../architecture/what-if-simulator.md).

## 11. Access Paths

1. Open **Access Paths** after a scan with relevant evidence.
2. Search for identity, role, scope, provider or path evidence.
3. Select a path to inspect nodes, edges and identity-to-resource relationships.
4. Inspect workload-to-ServiceAccount-to-cloud-identity relationships where available.
5. Review the **blast radius** display as observed inventory coverage, not an
   exhaustive list of everything an attacker could reach.
6. In attack-path results, select a row and inspect edge evidence, feasibility,
   source/reference, scope and condition evidence.
7. Review graph diagnostics and coverage before prioritizing the path.

Potential, mismatched and unavailable federation/trust evidence have different
meanings. Even a verified configuration relationship is not proof of a successful
runtime token exchange or all downstream operational permissions.

## 12. Risk Findings and Recommendations

### 12.1 Risk Findings

1. Open **Risk Findings**.
2. Search/filter and sort by severity/priority as available.
3. Inspect provider, principal, resource, source evidence and evaluation state.
4. Confirm the account/scope and evidence freshness.
5. Read the recommendation and investigate gaps before raising a change.

Prioritize Critical/High findings, but include confidence, exposure and business
context. Zero findings with incomplete coverage is not a clean assessment.

### 12.2 Recommendation workbench

1. Open **Recommendations**, refresh the list with its refresh control where
   available, then search/filter for the target.
2. Select a recommendation.
3. Read **Current policy**, **Rationale**, **Proposed policy** and **Impact assessment**.
4. Check whether the proposal is supported by complete enough usage/evidence.
5. Select **Export Review Draft** for an offline review artifact.
6. With ticketing configured, select **Raise ticket**; inspect success or an
   existing linked request before trying again.
7. Use **Refresh ticket status** for supported provider status updates.

Exporting a draft or creating a ticket does not change cloud permissions.
Local recommendation ticket settings are distinct from shared organization
notification channels.

## 13. Remediation

### 13.1 Review/dry-run workflow

1. Open **Remediation** and select a recommendation.
2. Verify **Signed in as** and enter the approval/rejection rationale.
   The workflow uses authenticated identity, not an editable actor name.
3. Select **Create plan**.
4. Select the saved plan and inspect proposed changes, safety notice and fingerprint.
5. Use **Approve** (or the displayed approval-stage label) or **Reject**.
   Approval alone does not write to the cloud.
6. Select **Run dry run** to model changes against saved evidence. After a
   qualifying successful dry run, the control becomes **Approve for execution**;
   complete the required independent final approval before live execution.
7. Review results and the **Audit trail**.
8. **Prepare rollback** creates a review draft; it does not apply rollback.
9. **Verify provider state** performs the supported read comparison; it is not
   proof of workload safety.

The creator cannot approve their own plan. Execution approval must also come
from an authorized reviewer different from the preview approver. Fresh evidence
and confirmed recommendation state are additional gates; a typed name does not
bypass authenticated-user separation.

### 13.2 Provider preview and guarded execution

Use only for an explicitly supported, confirmed `RemoveUnusedPermission`
recommendation and an approved change. A generic/potential recommendation is
not sufficient.

The supported operation choices are Azure role-assignment removal, AWS
managed-policy detachment or inline-policy deletion, and GCP project allow-policy
replacement. They are not a universal per-action editor.

1. Fill **Profile**, supported **Operation**, exact **Scope**, **Subject kind**,
   **Subject**, **Role/policy**, required native **Policy JSON** and **Reason**.
2. Select **Prepare provider before/after preview**. This reads provider state.
3. Inspect Before/After, the what-if snapshot and preview fingerprint.
4. Complete preview approval, dry run and required independent execution approval
   for that exact preview. A changed preview needs a fresh review.
5. Check **I reviewed workloads, operational dependencies, and break-glass access**.
6. Select **Apply approved change** only with approved write credentials and
   complete the separate confirmation.
7. Inspect outcome and use **Verify provider state**.
8. If an outcome is unknown, select **Reconcile unknown result** before any retry.
9. **Apply approved rollback** is a real write, requires its own approved/valid
   state and confirmation, and can be refused if current state has changed.

Do not blindly reapply after timeouts. Never assume a dry-run score guarantees
safe removal. For operation-specific fields and guards, see
[cloud administration](../deployment/cloud-administration.md).

## 14. Organizations: identity, members, roles and accounts

### 14.1 Select or create an organization

1. Open **Organizations**, enter tenant GUID and **Sign in** with Entra.
2. Select an existing organization to load its workspace.
3. To create one, enter **Organization name**, **Tenant key** and **Tenant ID**
   matching the authenticated identity, then **Create and become admin**.
4. Read **Signed in as**, current role, consolidated inventory and status.
5. Use **Sign out** to end the organization UI session; this is not provider
   credential revocation or cancellation of server work.
6. **Sync central audit events** explicitly uploads local events to the tenant's
   central log; check status rather than assuming it happens automatically.

### 14.2 Bind cloud accounts

1. Discover the intended cloud account under Cloud Accounts.
2. Select it in the Organizations binding controls.
3. Select **Bind account** with authorized **Manage connections** access.
4. Confirm provider, external account ID and tenant mapping in the bindings list.

A binding authorizes organization scope; it does not grant cloud IAM permissions
or copy the operator's CLI credentials to the server.

### 14.3 Members and custom roles

1. Enter the member email and choose the intended built-in/custom role.
2. Select **Invite member**. The user must sign in with a matching authorized identity.
3. Select a member, choose another role and **Update role** when required.
4. **Toggle active** enables/disables that member's organization membership;
   it is not a cloud-account disable operation.
5. For custom roles, select **New role**, name the role and check only needed
   permissions, then **Save role**.
6. Select an unused custom role and **Delete role** only after reviewing assignments;
   assigned roles cannot simply be removed.

| Role option | What it authorizes in the organization |
| --- | --- |
| View inventory | Read authorized organization inventory |
| Run scans | Submit permitted shared scan operations |
| View findings | Read organization findings; some panels also require inventory |
| Manage members | Invite/change authorized organization members |
| Manage connections | Manage account bindings |
| Manage policies | Manage supported policy/routing configuration and evidence imports |
| Manage remediation | Authorized remediation/review/JIT-related workflow actions, subject to further checks |
| Export reports | Organization-scoped report/evidence export |

These are API permissions, not Azure/AWS/GCP IAM grants. Native cloud writes
require separate cloud permissions and workflow-specific approvals.

## 15. Organizations: shared scans and schedules

### 15.1 Publish Desktop results

1. Complete a Desktop scan for selected organization-bound accounts.
2. Select the organization and **Publish latest scan data**.
3. Check publication status, shared scan history and findings.

Publication uploads summaries, coverage and normalized records. Review data
handling approval before publishing; not all local-only results automatically
appear on another operator's PC.

### 15.2 Scheduled full scans

1. Ask the server administrator to configure read-only cloud CLI credentials
   for the server identity and ensure workers/readiness are healthy.
2. In **Scheduled full scans**, select a bound account.
3. Enter the interval in minutes within the supported range (5 minutes to 7 days).
4. Select **Create schedule**.
5. Select **Refresh**, then choose the saved schedule.
6. **Run selected now** queues an immediate full scan.
7. **Pause / resume** changes future scheduling.
8. Inspect recent account scan jobs: queued/running/terminal status, attempts,
   duration and last outcome.
9. Select an eligible job and **Cancel selected job** to request cancellation.

The server runs independently of Desktop. Cancellation is a request, not
instant termination proof. These are full scans, not incremental resume.
Current documented server scans do not run the full Desktop risk pipeline;
zero scheduled-scan findings alone must not be interpreted as clean posture.

## 16. Organizations: activity and permission documents

### 16.1 On-demand cloud activity

| Activity source option | Purpose |
| --- | --- |
| Default: Azure | Subscription Activity Log; control-plane evidence only |
| AzureLogAnalytics | Approved workspace resource logs; requires the exact workspace ARM resource ID |
| AzureBlobArchive | Existing diagnostic container on the approved storage account |
| Default: AWS | CloudTrail management event history |
| AwsCloudTrailLake | Existing compatible event data store; explicitly billable query |
| AwsS3Archive | Native client CloudTrail `.json.gz` archives with a narrow account/date prefix |
| Default: GCP | Project Cloud Logging; Data Access coverage depends on client logging settings |

These are offered according to the selected provider. Current readers report
Partial coverage; choosing a destination never establishes complete 90-day
coverage. Archive reads are bounded, including a 128-file limit per collection.

1. Select a bound **activity account**.
2. Select the source and read its setup instructions.
3. Enter the identifiers applicable to that source:

   | Field | Enter |
   | --- | --- |
   | Client destination | Existing CloudTrail Lake store UUID, S3 bucket name, Azure workspace ARM resource ID or storage-account ARM resource ID as instructed |
   | Azure archive container | Existing diagnostic `insights-logs-*` container for the archive source |
   | Archive resource/date prefix | Narrow approved prefix, not a secret-bearing URL |
   | AWS region | Region containing the intended source |
   | AWS profile | Existing CLI profile name; blank uses the default where supported |

4. Confirm logging/read access is prepared and review possible query charges.
5. Check **I have prepared logging/read access and authorize one live read
   (including client query charges)**.
6. Select **Collect and save 90-day activity**.
7. Review status, event rows, truncation, provenance and 30/60/90-day counts.
8. **Cancel read** requests cancellation. It does not necessarily cancel a
   provider-side billable query already started.
9. Use **Refresh saved history**, select a snapshot, then
   **Load selected/latest saved** to inspect saved evidence without new cloud reads.
10. Use **Older saved history** to retrieve the next page when available.

Activity dates, detection dates and observation dates are different. A matching
event may show successful use without proving which grant authorized it.
See [source-specific setup and limits](../deployment/cloud-activity.md).

### 16.2 Provider permission documents

1. Connect the approved account and select the appropriate bound activity account.
2. Check **I configured/connected the account and authorize one live
   permission-document read**.
3. Select **Read and save permission documents**.
4. Check effect, action/pattern, scope, exclusions, conditions and source version.
5. Use **Refresh saved document history**, select a snapshot and
   **Load selected documents (saved)** for subsequent inspection.
6. With activity loaded, **Compare historical observations (saved)** compares
   compatible earlier observations; it does not reconstruct full historical IAM.
7. Select **Evaluate document risk candidates** to review potential risks.

Collection requires the relevant organization permissions and native read access.
An observation timestamp is not a policy valid-from timestamp; a compatible
document is not proof of the actual historical authorizing grant.

## 17. Organizations: JIT access

JIT can grant/end real temporary access. Server-allowlisted targets and native
Azure PIM, GCP PAM or an AWS client-owned human-session broker must be configured.

1. Enter **Target alias** supplied by the administrator, not an arbitrary URL,
   role or principal.
2. Enter **Duration minutes** and a business **Reason**.
3. Standard requests support 1-60 minutes; **Emergency** limits to at most
   15 minutes. Server/native policy may impose tighter limits.
4. Select **Request JIT access**. This creates a request, not an immediate grant.
5. **Refresh JIT requests**, select the request and inspect risk screening,
   limitations, saved state and approval requirements.
6. Configured non-requester approvers use **Approve selected** or
   **Reject selected** with rationale. Default approval configuration requires
   two distinct approvers; configured thresholds/native approval policy apply.
7. After approval, the requester selects the exact request, checks
   **I authorize live activation of my selected, approved JIT request**, then
   **Activate selected JIT request**.
8. Refresh and inspect actual native outcome/expiry. Pending/unknown is not active.
9. For active AWS requests, the requester can **Open my AWS broker session**
   and authenticate directly to the client-managed broker.
10. To end/cancel, enter the reason, select the exact request, check
    **I authorize ending/cancelling the selected grant now**, then
    **End / cancel selected JIT access** as the requester/authorized approver.
11. Refresh until the native result is known; queued revocation is not verified removal.
12. Use **Read JIT audit** for the selected request's recorded history.

Emergency does not bypass approvals. The app does not return raw AWS session
credentials. Native expiry is independent of Desktop. Low-confidence screening
is not a full provider permission-risk evaluation.
See [JIT configuration and boundaries](../deployment/priority-two.md).

## 18. Organizations: behavior, trust and credential observations

### 18.1 Saved behavior/document analysis

1. Load the relevant saved activity/document observations.
2. Select **Analyze saved behavior and document changes**.
3. Inspect behavior anomalies and document-change rows.
4. Investigate event scope, observation windows, missing baseline and confidence.

This is analysis of supplied/saved evidence, not complete continuous threat
detection or proof that an identity was compromised.

### 18.2 Trust and credential monitoring

1. In **Trust and credential monitoring**, select an approved bound account.
2. Read setup instructions and select the one-read consent checkbox.
3. Select **Collect and save**; inspect coverage details.
4. **Refresh history**, choose a saved observation, then **Load selected snapshot**.
5. Inspect metadata: principal, evidence ID, kind, issuer, subject and validity.
6. Review observed changes with their evidence boundary.
7. Select **Compare effective-access scans** to load saved scan options.
8. Choose the bound account, earlier baseline scan and later current scan.
9. Select **Analyze selected scans** and inspect confidence/evidence.

Only credential/trust metadata is collected: no passwords, private keys or tokens.
An apparent privilege increase normally remains Potential unless comparable
authoritative decision evidence satisfies the narrower confirmation rules.
See [trust/credential setup](../deployment/trust-credential-monitoring.md).

## 19. Organizations: continuous change feeds

This requires a separately deployed, approved client-owned bridge and
organization-specific server configuration. It is not automatically enabled.

1. Select **Refresh saved status**.
2. Choose the configured feed alias and inspect provider/account/source.
3. Check **I consent to continuous server bridge reads** only after approval.
4. Leave **Reset source epoch (retain old evidence)** unchecked for normal use.
   Use it only for an approved source-reset/rebaselining operation; old evidence
   remains, and resetting is not proof that historical gaps are resolved.
5. Select **Enable and connect**.
6. Inspect health, last success, source watermark and failure/gap.
7. Select **Load saved changes** to inspect revisions without live cloud calls
   from Desktop.
8. Select **Pause** to stop future feed polling; retained evidence remains.
9. **Rescan this account** queues the existing server scan for that account.

Review coverage per category. **Source reports complete** is not independently
verified completeness; **Verified complete** remains false for these bridge
claims. Gaps and source epochs must be retained in analysis.
See [continuous change-feed configuration](../continuous-change-feeds.md).

## 20. Organizations: authoritative IAM decisions

The current native profile is narrowly scoped GCP v3beta Preview project IAM
evaluation, not a universal Azure/AWS/GCP authorization oracle.

1. Select **Load configured connections**.
2. Choose a server-allowlisted **Source**, **Principal** and **Permission**.
3. Inspect the displayed full resource name and setup limitations.
4. Check **I authorize one read-only native IAM evaluation**.
5. Select **Capture and save**, or **Cancel** for an in-progress request.
6. Select **Refresh saved history** to inspect recorded decisions and coverage.
7. Choose an earlier **Before** snapshot and later **After** snapshot.
8. Select **Analyze saved decisions**.
9. Read status, explanation and blockers, including context comparability.

`ConfirmedDecisionEscalation` is a qualifying exact-tuple
NOT_GRANTED-to-GRANTED transition under captured context. It does not prove
abuse, causation, successful workload access or all-action privilege escalation.
Azure/AWS simulations or partial policy observations cannot substitute for this
profile. See [authoritative decision setup](../deployment/authoritative-decisions.md).

## 21. Organizations: alerts, connectors and tickets

### 21.1 Create or edit routing

1. Have the administrator provision the receiver and configure the organization
   channel alias on the server. Never enter receiver credentials in the rule.
2. Select **Refresh alerts** to load events, rules, deliveries and connector health.
3. Select **New rule**, or select an existing notification rule to edit.
4. Choose **Connector kind**, matching the configured channel.
5. Read receiver instructions and confirm **The client-created receiver and this
   organization server alias are configured; enable delivery** when true.
6. Enter **Rule name**, **Channel alias** and **Minimum severity**.
7. Choose **Enabled**, **Findings**, **Effective access**, **Added**, **Removed**
   and **Changed** as appropriate for legacy drift filtering.
8. Alternatively enter exact comma-separated **event types**. A nonempty
   allowlist replaces those legacy source/change filters; severity still applies.
9. For approved Jira/ServiceNow automation only, check **Policy: auto-create
   tickets** and supply its required nonempty event allowlist.
10. Select **Save notification rule**.
11. Inspect subsequent matching events and deliveries. Rules affect future
    events, not automatic historical backfill.
12. To disable, select the rule, clear **Enabled** and save.

| Kind | Purpose and prerequisite |
| --- | --- |
| SignedWebhook | Signed HTTPS delivery to a client-managed receiver/bridge |
| Jira | Shared Jira issue creation; explicit or policy-enabled |
| ServiceNow | Shared incident creation; configured channel and approved policy |
| TeamsWorkflow | Notifications through a client-managed Teams Workflow |
| SplunkHec | Outbound HTTPS HEC events; receiver/token configured on server |
| QRadarTls | Outbound TLS syslog/LEEF with required client certificate |
| SentinelLogs | Azure Monitor Logs Ingestion; configured DCR/table and authorized managed identity |

Example event types include `finding.created`, `finding.resolved`,
`finding.updated`, `access.added`, `access.removed`, `access.changed`,
`review.overdue` and `scan.failed`. Match the approved server event contract.

### 21.2 Delivery inspection and explicit tickets

1. Select an event, enter the configured ticket channel alias and select
   **Request ticket for selected event** when authorized.
2. Confirm reuse of an existing event/channel request rather than creating duplicates.
3. Select a delivery and **Inspect delivery attempts**.
4. Review attempt time, latency, HTTP status, safe failure code and retry state.
5. Treat terminal failed deliveries as dead letters requiring administrator
   reconciliation; there is no general replay button.

Configured/unused connector health is not proof of delivery. Acknowledgement
is not proof of a downstream incident or SOAR action. After an ambiguous ticket
send, search/reconcile the destination before manual recreation.

### 21.3 Saved inbound SIEM alerts

1. Have the client deploy/authorize its normalized inbound bridge separately.
2. Select **Refresh saved SIEM alerts**.
3. Inspect product, external alert ID, title, severity, SIEM status and update time.
4. Investigate in the authoritative SIEM where needed.

This reads saved evidence; Desktop does not poll Splunk/QRadar/Sentinel on sign-in.
No automatic SOAR or cloud response is authorized by receiving an alert.
Full setup: [security connectors](../deployment/security-connectors.md).

## 22. Organizations: live cloud administration

**Administrator-only change workflow. These controls can modify real IAM.**
Use a non-production target first, an approved change window, independent
review and a tested recovery path.

1. Select **Enable administrator read/write mode for this session**.
2. Select the organization-bound account and supported **Operation**.
3. Read the operation-specific instructions; do not reuse field values blindly:

   | Input | Meaning |
   | --- | --- |
   | AWS CLI profile | Existing profile used for the exact target |
   | Azure scope | Exact subscription/resource-group/resource scope |
   | Subject | Azure principal object ID or AWS IAM name, as instructed |
   | Target kind/ID | Azure assignment GUID or AWS user/role/group kind |
   | Role/policy | Azure role GUID, AWS policy ARN/name or GCP policy ID as instructed |
   | Policy JSON | Native provider schema for operations that edit policies; no credentials |
   | Change reason | Required rationale for the exact action |

4. Select **Prepare live preview (read only)**.
5. Select the prepared plan and review captured **Before** and proposed **After**.
6. Check unrelated grants, inherited access, conditions, logging, operational
   dependencies and break-glass access.
7. Check **I approve this exact change and have reviewed workloads, inherited
   access, logging and break-glass dependencies**.
8. Select **Approve and apply LIVE** only after approval and complete the confirmation.
9. Inspect recorded state and verify using the supported read workflow.
10. If submission outcome is unknown, select **Reconcile (read only)**; never
    assume a timeout means no change.
11. **Approve rollback LIVE** restores captured state only when the guard permits
    it and after separate confirmation; a concurrent change can block rollback.

Operations include supported Azure role assignments/custom roles, AWS policy
attachments/inline policies and GCP project IAM/deny policies. Only the displayed
supported operation list is authoritative for the selected provider.
An organization role is not a substitute for native write permission.
See [operation-specific administration instructions](../deployment/cloud-administration.md).

## 23. Organizations: emergency-access reports

This is retrospective reporting, not offline credential issuance or emergency
approval bypass.

1. During an outage, use the client's independently tested emergency-account runbook.
2. After reconnecting, select the bound activity account and load relevant saved
   activity when available.
3. Expand **Client-owned emergency access: post-outage reports**.
4. Enter exact audit **Principal**, **Start UTC**, **End UTC**, **Reason** and
   opaque **Incident reference**.
5. Use UTC format `yyyy-MM-ddTHH:mm:ssZ`; the completed interval must be no more
   than seven days.
6. Select **Report completed emergency access**, then **Refresh emergency reports**.
7. Inspect evidence matching and limitations.
8. An authorized configured security reviewer other than the reporter selects
   the report and enters review rationale and cleanup/rotation reference.
9. Check **Client verified cleanup/rotation** only after client verification.
10. Select **Record independent security review**.

Do not enter passwords, tokens or signed URLs. Closure is an attestation, not
provider-verified revocation. See [emergency reporting](../deployment/emergency-access.md).

## 24. Access Reviews

### 24.1 Create a campaign

1. Complete/load the intended scan and select the authorized organization when shared.
2. Open **Access Reviews** and choose a template:
   - All effective access: default 30 days.
   - Privileged workload identities: default 7 days.
   - External identities: default 14 days.
3. Enter campaign name and duration, within 1-365 days.
4. Choose an eligible fallback reviewer.
5. In local mode, provide the actor name; shared mode uses authenticated identity.
6. Select **Create from Current Scan**, then inspect assigned/unassigned items.
7. Use **Refresh** to reload campaigns and reevaluate supported deadline state.

Local reviewer eligibility comes from supported active privileged cloud users.
Shared reviewers must be active organization members with review authorization;
the creator is excluded. Owner assignment is used only when the owner is eligible;
otherwise fallback/unassigned rules apply.

### 24.2 Assignment and decision

1. Select a campaign and an item; read its captured **Review evidence**.
2. To reassign, choose reviewer/deadline. In shared mode also review current
   owner, account, team and required reassignment reason.
3. Select **Update assignment**. Refresh after a concurrency/stale-version error.
4. Select the decision:

   | Decision | Required follow-up |
   | --- | --- |
   | Certified | Record acceptance of the reviewed evidence |
   | NeedsInvestigation | Supply a comment; work remains unresolved |
   | Rejected | Supply rationale and route any required access removal through a separate approved workflow |
   | ExceptionGranted | Supply rationale and a future expiry; monitor renewal/expiry |

5. Select **Record decision** as the authorized assigned reviewer.
6. Inspect **Attestations** and audit history for actor/time/evidence hash.
7. To renew an exception, record a new ExceptionGranted decision with future
   expiry and rationale; history remains.

A review decision does not itself grant or revoke cloud access. Captured evidence
is not rewritten by changing current owner/team labels.

### 24.3 Governance monitoring

1. Inspect open/unresolved/escalated totals.
2. Review organization completion/overdue groups by provider/account/team.
3. Inspect active/expired exceptions and age buckets.
4. Expand **Deadlines and overdue work**.
5. Expand **Organization escalation and audit history** for recent persisted events.

Shared server workers process reminders/expiry independently of Desktop.
Local mode reevaluates on refresh. Reminders are in-app records, not automatically
emails. Older events may remain in individual histories beyond the latest
organization summary page.

## 25. Scan History

1. Open **Scan History**.
2. Search by supported provider/status text and sort by time.
3. Inspect scan outcome and inventory/finding/access counts.
4. Read latest detailed source coverage and messages.
5. Inspect access-change timeline: Added, Changed or Removed across retained
   observations, plus available provider change time/actor/source.
6. Inspect posture trends, drift alerts and recorded notification status.

Local live history is retained for seven days. Detection time is not necessarily
provider change time. Missing/partial observations must not be treated as proof
that access was removed; inspect comparability and coverage.
Central retention and external provider log retention are separate policies.

## 26. Settings, backup and restore

### 26.1 Application and data location

Open **Settings** to inspect version and data location. For support, record
the actual running version, not just the installer filename.

### 26.2 Local Data Backup

1. Select **Create backup** and choose the `.yaskbackup` destination.
2. Protect the file as sensitive identity/access evidence.
3. Before restore, create a current backup and verify the intended restore file.
4. Select **Restore backup**, review replacement warning and confirm only when intended.
5. Restart Desktop after successful restore.

Restoring replaces the supported local database/workflow records. It does not
restore the central PostgreSQL server or external cloud IAM. CLI credentials,
ticketing/webhook secrets, logs and generated reports are not included.
Retain them through their separate approved backup/export procedures.

### 26.3 Demo Mode

1. Use **Load Demo Data** to load synthetic Azure/AWS/GCP data through SQLite.
2. Inspect the Demo Mode status so training data is not mistaken for live evidence.
3. Use **Reset Demo Data** when finished; read any confirmation and refresh pages.
4. Use a separate test profile/database and back up real local data first.

No cloud credentials are required for demo collection.

### 26.4 Continuous monitoring and cloud connections

The Settings **Continuous monitoring** panel directs you to organization/server
schedules. It is not a separate always-on Desktop scan service.
Use section 15 to create schedules. Use header **Connect Cloud** for CLI sessions.

### 26.5 Posture alerts

1. Have the receiver approved before entering its secret-bearing HTTPS URL.
2. Enter it in the protected endpoint field and **Save alert endpoint**.
3. Check credential/status text and inspect delivery evidence after eligible changes.
4. Use **Remove endpoint** to remove this user's stored endpoint.

The endpoint is stored in Windows Credential Manager. An administrator-provided
environment fallback may remain active after removal. Local posture alerts and
central notification rules are separate; configuring both may notify twice.

### 26.6 Ticketing connection

1. Choose the **Ticketing system**.
2. Enter the HTTPS **Site URL**, without embedded credentials/query/fragment.
3. Enter **Project key / service desk ID** where required.
4. Enter **Issue type / request type / category** according to that provider.
5. For a non-native provider, configure the approved **Signed webhook URL**
   fallback and its secret as required.
6. Enter **API username / email** and **API token / secret** where needed.
7. Select **Save URL** to persist non-secret settings.
8. Select **Save credential** to store the secret in Windows Credential Manager.
9. Use the provider's displayed request/open button to open its request form.
10. Return to Recommendations to **Raise ticket** and **Refresh ticket status**.

Native local request adapters include Jira, Jira Service Management and ServiceNow.
Other selections use the configured signed webhook. Do not confuse these local
settings with organization server channel aliases.

### 26.7 Data and diagnostics

| Item | Location or owner |
| --- | --- |
| Local SQLite database | `%LOCALAPPDATA%\CloudEntitle\Data\cloudentitle.db` |
| Desktop logs | `%LOCALAPPDATA%\CloudEntitle\Logs\application-<date>.log` |
| Default reports | `%LOCALAPPDATA%\YASK-CIEM\reports` |
| Supported local workflow files | `%LOCALAPPDATA%\YASK-CIEM` and supported application data paths |
| Local protected secrets | Current user's Windows Credential Manager |
| Shared records/audit | Organization-managed PostgreSQL and configured archive |

Never send tokens, Authorization headers, raw credential files, signed URLs or
unredacted sensitive exports with a support request.

## 27. Feedback

1. Open **Feedback**.
2. Enter **Your feedback** and **Suggestions**; both are required.
3. Select **Submit Feedback**.
4. Read the result; feedback is stored locally, not automatically submitted to
   an external support service.

For an incident, separately send approved sanitized diagnostics through your
organization's support process.

## 28. Daily workflow, troubleshooting and acceptance

### 28.1 Recommended daily sequence

1. Check build, API license and organization context.
2. Connect only intended providers.
3. Discover accounts and explicitly select scope.
4. Scan, then inspect source coverage and freshness.
5. Triage findings; inspect identity/access/path evidence.
6. Analyze usage and hypothetical changes without assuming operational safety.
7. Create review/ticket/remediation records when justified.
8. Perform live changes only through an approved, separately confirmed workflow.
9. Verify actual outcome, retain evidence and export the required report.
10. Monitor jobs, deliveries, exceptions and backup/restore health.

### 28.2 Troubleshooting

| Symptom | Next step |
| --- | --- |
| No active CLI sign-in | Connect the intended provider explicitly; verify its CLI under the same Windows identity |
| Scan blocked by sign-in | Organizations sign-in is separate from provider login |
| License validation unavailable | Check HTTPS endpoint, server availability, environment and current API authentication |
| HTTP 401 | Administrator checks API audience/issuer/token validity; v2 audience is API client GUID, not scope URI |
| HTTP 403 | Identify whether API authorization or cloud source denied access; inspect scope, original claims and organization role |
| Disabled action | Check selected item, required fields, consent, prior workflow state, permissions and configured capability |
| Empty/zero results | Check scope, filters, source coverage and last successful scan before interpreting |
| Denied/unsupported source | Fix approved missing reads for Denied; Unsupported requires implementation, not broader permissions |
| Unknown apply result | Reconcile/read provider state; do not blindly retry |
| Empty reviewer list | Check current scan/organization membership, reviewer eligibility and separation of duties |
| Receiver configured but no alerts | Verify future event match, enabled rule, exact channel kind/alias, worker state and delivery attempts |
| Readiness 503 with liveness 200 | Inspect database/schema, worker, queue and immutable archive dependencies |
| UI still shows old behavior | Confirm actual running package version; source changes do not replace installed binaries |

### 28.3 Acceptance checklist

- [ ] Trusted exact Desktop/server candidate installed and versions recorded.
- [ ] API authentication/authorization and tenant isolation verified.
- [ ] Trial/license registration survives restarts/reinstall without reset.
- [ ] Explicit selection limits collection to approved providers/accounts.
- [ ] Provider evidence and condition/deny/inheritance expectations verified.
- [ ] Partial/unsupported sources remain visible and are not reported as safe.
- [ ] Saved results, reports, backup and restore verified.
- [ ] Optional connectors/schedules/JIT/writes accepted separately before use.
- [ ] Immutable archive and operational readiness accepted before production sign-off.

See the [installation and staged acceptance guide](../deployment/customer-installation-guide.md),
[release gates](../deployment/release.md) and
[operations runbook](../deployment/operations-runbook.md).
Passing offline tests or following this manual does not complete real-cloud,
clean-machine or production acceptance.
