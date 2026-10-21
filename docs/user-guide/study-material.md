# YASK-CIEM Study Material

A step-by-step guide to every screen, button and option in YASK-CIEM (Cloud Infrastructure Entitlement Management).

---

## Part 1 - Concepts

### 1.1 What the application does

YASK-CIEM is a Windows desktop application that answers one question: **who can do what in my cloud, and is that access safe?**

It connects to Azure, AWS and Google Cloud through the cloud CLI you are already signed in with, collects identities, roles, permissions and resources, and then:

- shows the effective access each identity has,
- flags risky access (for example, a dormant administrator),
- proposes least-privilege improvements,
- supports access reviews, evidence exports and ticketing.

**All scans are read-only.** The application never grants, removes or changes cloud access. Remediation is a planning and approval workflow only; cloud changes are disabled in this build.

### 1.2 Key terms

| Term | Meaning |
|---|---|
| Identity / principal | A user, group, service principal, managed identity or service account |
| Role | A named bundle of permissions (for example Owner, Global Administrator) |
| Permission | A single allowed action (for example `Microsoft.Storage/*/read`) |
| Entitlement | A grant that gives an identity a role or permission at a scope |
| Scope | Where a grant applies (subscription, resource group, account, project, directory) |
| Effective access | What an identity can really do after direct, inherited and group-based grants are combined |
| Access path | The chain that connects an identity to a resource (identity, group, role, scope, resource) |
| Blast radius | How much could be affected if one identity or path were abused |
| Privileged identity | An identity holding administrator-level access |
| Workload identity | A non-human identity (service principal, managed identity, service account) |
| PIM | Privileged Identity Management; roles can be Active or Eligible (not yet activated) |
| Access review (campaign) | A scheduled check where reviewers certify or reject access |
| Attestation | The recorded decision of a reviewer, with an evidence hash |
| Evidence bundle | A signed export of controls, evidence and gaps (not a compliance certification) |

---

## Part 2 - Installing and starting

### 2.1 Requirements

- Windows 10 or 11.
- At least one cloud CLI, signed in:
  - Azure: Azure CLI (`az`)
  - AWS: AWS CLI v2 (`aws`)
  - Google Cloud: Google Cloud CLI (`gcloud`)
- A cloud account with read access (see Part 3).

### 2.2 Installing

Two installers are provided. Install only one on a machine.

1. **Native installer** - run `YASK-CIEM-Setup-<version>.exe` (or the `.msi`). Accept the Windows administrator prompt. A box shows the version and build time; click Yes to install, then Yes to launch.
2. **MSIX installer** - run `YASK-CIEM-Setup-<version>.exe` from the MSIX folder. It asks you to type `TRUST` to trust the publisher certificate (this is a development-signed package), then installs for your account.

Notes:
- The installer writes `%TEMP%\YASK-CIEM-Setup.log` (native). Send this file if an install fails.
- Re-running the native setup over an installed copy removes the old copy first, then installs.

### 2.3 First launch and the trial

- On first launch a **14-day trial** starts. The header pill shows the days left.
- When the trial expires, cloud and workspace options are disabled; your local data is not deleted.
- The trial survives reinstalling under the same Windows account.

### 2.4 Window layout

| Area | What it contains |
|---|---|
| Header, left | Logo, application name and tagline |
| Header, right | Trial pill, **Connect Cloud**, **Disconnect Cloud**, **Settings**, minimize, maximize, close |
| Left menu | Dashboard, Cloud Accounts, Identities, Resources, Entitlements, Effective Access, Access Paths, Risk Findings, Recommendations, Remediation, Organizations, Access Reviews, Scan History (and Feedback, Settings) |
| Security workspace card | Shows which provider is connected |
| Main area | The selected page |
| Status bar | Current page, database status, cloud status, version |

You can drag the header to move the window.

---

## Part 3 - Connecting a cloud

Connect Azure, AWS, and Google Cloud independently with their existing CLI identities. Select **Connect** for each provider you intend to use in the current app session; cached CLI sign-ins do not automatically enable other providers. You may keep multiple explicitly selected providers connected; only one interactive sign-in flow runs at a time. Each provider retains its own account scope and scan coverage.

### 3.1 Step by step

1. Click **Connect Cloud** in the header (or open **Cloud Accounts**).
2. On the provider card, click **Connect** to open its CLI sign-in or profile-configuration terminal:
   - Azure: `az login`
   - AWS IAM user: `aws configure --profile yask-ciem`; enter credentials directly in the terminal. Other profile names and the default profile are unchanged.
   - AWS IAM Identity Center: configure externally with `aws configure sso --profile audit`, then `aws sso login --profile audit`.
   - Google Cloud: `gcloud auth login`
3. Finish any browser sign-in and all CLI configuration prompts. For AWS, prefer IAM Identity Center with a dedicated read-only permission set and a short-lived SSO profile; use IAM-user credentials only when required by your organization's policy. Enter credentials only in the provider CLI terminal, never in YASK-CIEM.
4. Return to the app after completing the terminal prompts. **Refresh sign-in status** rechecks selected providers without connecting others. AWS configuration waits can be canceled and have a three-minute deadline; canceling the wait does not close the terminal.
5. Use **Discover accessible accounts** on Cloud Accounts to check which account scopes are visible to the selected CLI identities. This confirms discovery, not every permission required by CIEM.
6. Run a read-only scan, then review **Latest permission and source coverage** on Cloud Accounts. Resolve `Denied`, `Partial`, or `Unavailable` sources using the per-source details. `Unsupported` means the connector does not collect that data; granting broader cloud roles will not enable it.
7. Bind only the intended, discovered account in the Organizations workspace. Repeat for each account or permission set that should be included.

To sign out of one provider, use **Disconnect** on that provider card. **Disconnect Cloud** opens Cloud Accounts; use the provider-specific Disconnect action. A pending sign-in can be canceled separately.

For AWS, **Disconnect** stops AWS use in the current app session without deleting or revoking CLI credentials. Subsequent discovery and scans exclude AWS until **Connect** is selected; **Refresh sign-in status** does not reconnect it. Select **Connect** again in a new app session. Use **Stop Scan** separately if a scan is already running.

### 3.2 Permissions needed

| Provider | Recommended access |
|---|---|
| Azure | Reader on intended subscriptions. Entra enrichment needs tenant-approved Graph read permissions. Optional PIM schedules require separate Graph read consent. Do not grant Owner, Contributor, or User Access Administrator for scans. |
| AWS | Prefer IAM Identity Center SSO with SecurityAudit or an approved least-privilege equivalent. Each target account requires a working profile; an Organizations roster does not grant member-account access. |
| Google Cloud | Project Viewer and the connector's IAM, service-account, and Cloud Asset Inventory read permissions at intended project scopes. Inherited policy discovery additionally needs read access on ancestor folders/organization. |

The exact permissions and optional data-source requirements are listed in [Azure provider setup](../providers/azure.md), [AWS provider setup](../providers/aws.md), and [Google Cloud provider setup](../providers/gcp.md). For Azure PIM data, a tenant administrator must grant admin consent for `RoleAssignmentSchedule.Read.Directory` and `RoleEligibilitySchedule.Read.Directory`. If it is not granted, YASK-CIEM falls back to permanent directory role assignments and reports PIM as unavailable.

### 3.3 Cloud Accounts page

- **Connect a cloud provider** cards with least-privilege guidance, CLI authentication status, and the latest scan-permission validation summary.
- **Discover accessible accounts** lists visible account scopes before a scan. Account discovery is not a full permission check.
- **Discovered accounts and permission sets** table: provider, account, account ID, CLI profile, permission set, and scan access.
- **Latest permission and source coverage** lists the last scan's collection status by provider/source, including details for denied, partial, unavailable, unsupported, and skipped sources. The results are time-bounded to the latest scan, not a live health check.

---

## Part 4 - Running a scan (Dashboard)

### 4.1 Buttons

| Button | What it does |
|---|---|
| **Scan Now** | Scans the connected provider using read-only CLI calls |
| **Stop Scan** | Cancels the running scan |
| **Generate Report** | Saves findings as JSON or CSV to a folder you choose |
| **Discover accounts** | Lists the accounts the connected provider can see, so you can choose the scope |
| **Export evidence bundle** | Exports controls, evidence and gaps (needs an organization sign-in with report-export permission) |

### 4.2 Step by step

1. Connect a cloud (Part 3).
2. Optional: click **Discover accounts**, open the account list, tick the cloud accounts and Kubernetes contexts to scan (**Select all** / **Select none**). If you do not pick, all discovered scopes are scanned.
3. Click **Scan Now**. The status reports the current provider, account and data source as discovery proceeds.
4. Wait for completion. A summary line shows accounts, identities, resources and effective-access assignments, and how many sources need attention. Failed or unavailable sources remain visible in **Source coverage**.
5. Review the cards below and open the other pages.

The scan includes all connected providers and selected accounts/contexts for each. Kubernetes context discovery reads local kubeconfig; RBAC calls run only for selected contexts. Typical duration is about a minute for a small tenant and longer for large tenants. Entra lookups are capped (directory discovery stops after 4 minutes and results are marked partial). **Stop Scan** cancels the current scan; the last completed scan remains available.

### 4.3 Dashboard cards

| Card | Meaning |
|---|---|
| Cloud accounts / Identities / Resources / Entitlements | Counts from the latest scan |
| Privileged identities | Identities with administrator-level access |
| Risk findings / Critical findings | Total and critical risk findings |
| Last scan | Completion time of the newest scan |
| Evidence age | How old the evidence is |
| Source coverage | Which data sources were collected, partial, denied or unavailable |
| Unsupported permission sources | Permission types the app cannot evaluate yet |
| Attribution confidence | Share of assignments that show who changed them and when |
| Unknown / Unsupported evaluations | Access the engine could not decide |
| Risk by provider / by severity / Access decisions | Breakdown charts |

Hover over a card to see details.

### 4.4 Standards and assessment history

Open the expander **Standards and assessment history**:

1. Tick the standards for the assessment crosswalk: CIS Controls v8.1, NIST SP 800-53 Rev. 5, ISO/IEC 27001:2022, SOC 2 Trust Services Criteria.
2. **Control exceptions** - record an accepted gap: fill Control, Reference, Recorded by, Expires (date) and Rationale, then **Add exception**. Select a row and **Remove selected** to delete one.
3. **Load assessment history** shows saved assessments for the selected framework (requires report-export permission).

Exports are signed evidence snapshots, not a compliance certification or auditor attestation.

---

## Part 5 - Exploring the results

All grid pages have a search box. Type to filter; sort by clicking column headers.

### 5.1 Identities
Users, groups and service identities. Useful columns: Type, Email, Owner, **Privileged**, Last activity, Credential rotated / expires, Federation trust and whether it is verified.

Use it to find privileged users, ownerless workload identities and expiring credentials.

### 5.2 Resources
Normalized resources with name, type, resource ID, scope and location. Use it to see what the scan could enumerate.

### 5.3 Entitlements
Role and permission grants: provider, subject, grant, scope, assignment type and source. Also shows discovered roles and permissions.

### 5.4 Effective Access
Modeled access per principal, role, permission and resource, with decision (Allow, Conditional, Deny, Unknown), path, source, last used, last changed and privileged flag.

**Permission simulator**

1. Choose a **Principal**, a **Resource** and a **Permission**.
2. Click **Evaluate**.
3. Read the result and the status line. It explains why access is allowed or not and what evidence was used.

Simulation uses scan data only; it does not call the cloud.

### 5.5 Access Paths
Select a path to see its evidence (each hop from identity to resource) and its **blast radius**: related assignments, related roles and observed resources. A caveat reminds you counts are a lower bound.

### 5.6 Risk Findings
Findings with provider, severity, priority score, title, principal, resource, evidence and recommendation. Examples of what the rules detect:

- privileged identities, privileged service accounts and privileged group memberships,
- excessive or wildcard permissions and broad scope,
- dormant or external privileged identities,
- unowned, orphaned and overprivileged workload identities,
- unused entitlements and unused role assignments,
- exploitable identity-to-workload attack paths, redundant and long privilege chains.

Work through Critical and High first.

### 5.7 Scan History
Previous scans (retained for 7 days) with coverage details and access changes observed between scans. Use it to see what changed since the last scan and to read scan messages (for example, which data source was denied).

---

## Part 6 - Acting on findings

### 6.1 Recommendations
Evidence-backed least-privilege proposals: type, title, provider, state, current and suggested values, and priority.

1. Select a recommendation.
2. Read **Current policy**, **Rationale**, **Proposed policy** and **Impact assessment** in the policy draft workbench.
3. **Export Review Draft** saves a draft for your change process.
4. **Raise ticket** creates one ticket for the recommendation (an existing link is reused). **Refresh ticket status** updates it.

### 6.2 Remediation (approval-gated)

Cloud changes are disabled in this build; the workflow produces approvals and drafts only.

1. Select a recommendation and click **Create plan**.
2. Select the plan and review **Proposed changes**.
3. **Approve** or **Reject** (the approver is recorded).
4. **Run dry run** to simulate the change.
5. **Verify** to check the result.
6. **Prepare rollback** to create a rollback draft.
7. Check the **Audit trail** at the bottom.

### 6.3 Access Reviews

**Create a campaign**

1. Choose a template:
   - All effective access (30 days)
   - Privileged workload identities (7 days)
   - External identities (14 days)
2. Enter a **campaign name** and **duration** (1-365 days).
3. Choose a **fallback reviewer**. Only eligible reviewers are listed (see below).
4. In local mode, confirm the **actor name**. Shared mode records the signed-in organization identity instead.
5. Click **Create from Current Scan**. Each item is assigned to the identity owner when the owner is an eligible reviewer, otherwise to the fallback reviewer if eligible, otherwise left unassigned.

**Who can be a reviewer**

- In local/offline mode: an active Azure, AWS, or Google Cloud user with an active qualifying privileged grant. Eligible-only PIM roles do not count; the list is built from the latest scan.
- In shared mode: an active organization member with `ManageRemediations` authorization (Organization Admin, Security Analyst, or an approved custom role). The campaign creator is excluded to enforce separation of duties; only the assigned reviewer can decide an item.
- The License Server verifies membership, role, assignment, and actor from authenticated Entra identity. Client-supplied reviewer or actor names do not authorize a decision.

**Review an item**

Shared campaigns and decisions are saved centrally for authorized team members and remain available across devices. Each decision records the authenticated actor, UTC time, rationale, audit correlation ID, and SHA-256 digest of the captured evidence. The evidence snapshot is locked when the campaign is created and cannot be changed after a decision. Without shared API configuration, reviews remain in the local store and preserve offline behavior.

1. Select a campaign, then an item. Read **Review evidence**.
2. To change the reviewer: pick an eligible reviewer, set the deadline, then **Assign reviewer**.
3. To decide: choose Certified, NeedsInvestigation, Rejected or ExceptionGranted.
   - Anything other than Certified needs a comment.
   - ExceptionGranted also needs an expiry date.
4. Click **Record decision**. An attestation with an evidence hash is stored.
5. Review **Attestations** and the **Audit history**.

The top cards show open campaigns, pending items and escalated items. Overdue items receive reminders and are escalated automatically when the workspace refreshes. Decisions are governance records and never change cloud access.

---

## Part 7 - Teams and organizations

### 7.1 Organizations page
Used for multi-user workspaces and tenant membership.

1. **Sign in** with Microsoft Entra SSO.
2. **Create and become admin** to create an organization.
3. **Bind account** to attach a cloud account to the organization.
4. **Add member** and assign a role.
5. View consolidated totals from the latest scan (accounts, identities, resources, findings, critical, effective access).
6. **Publish latest scan data** explicitly uploads summary counts, source coverage, and normalized records for selected, scanned accounts bound to this organization. The organization workspace shows shared scan history and findings to members whose roles allow access.
7. In **Scheduled full scans**, choose a bound account and interval, then create a schedule. Use **Run selected now** to enqueue an immediate job; pause/resume the selected schedule as needed.
8. Review recent account scan jobs for queued/running/succeeded/partial/failed/cancelled outcomes, attempts, duration and errors. Select a queued or running job and request cancellation; the server worker observes the durable request.
9. **Sync central audit events** uploads local audit events to the tenant's central audit log.
10. In **Central drift and alert delivery**, select **New rule**, set a rule name, server-configured channel alias, minimum severity, sources, and change types, then **Save notification rule**. Select an existing rule to edit it or clear **Enabled** to stop future matching delivery.
11. Use **Refresh alerts** to inspect comparable-scan drift and pending/sending/delivered/failed/suppressed notifications. Select a delivery and **Inspect delivery attempts** to see attempt times, HTTP status, safe failure codes, and next retry time. URLs, keys, and response bodies are not displayed.

Central monitoring reads require both inventory and finding access; editing rules additionally requires `ManagePolicies`. Ask the server administrator to configure the channel's HTTPS endpoint and signing secret, following the [shared alerting setup](../architecture/alerting-notification.md#server-channel-configuration). The first complete account/source snapshot is a quiet baseline. Partial or incompatible scans do not imply that access or findings were removed. Rules affect future changes and do not send historical alerts. Transient deliveries retry up to five attempts; exhausted or permanent failures remain visible. The local Desktop posture webhook setting is independent; enabling both local and central notifications can notify through both streams.

Scheduled scans require shared mode (`YASK_CIEM_API_BASE_URL`, HTTPS), an account bound to the organization, the `RunScan` role action, the License Server worker, and read-only cloud CLI credentials configured for that server's service identity. The Desktop user's CLI credentials are not sent to the server. Scans are full per-account scans. Partial source coverage remains visible, retries are bounded, and cancellation is best-effort while a provider command is running. The initial server executor does not yet run the Desktop risk-evaluation pipeline, so scheduled scan summaries report zero findings; do not treat this as a clean risk assessment. Without shared mode, the desktop retains its local workflows; server-side scheduling is unavailable.

---

## Part 8 - Settings

| Section | What to do |
|---|---|
| Application | Shows version and the data location |
| **Demo mode** | **Load Demo Data** fills the app with synthetic Azure, AWS and GCP data (no cloud access). **Reset Demo Data** clears it |
| **Continuous monitoring** | Optional local Desktop schedule; it runs only while the app is open. For cross-device, per-account server schedules and job outcomes, use **Organizations > Scheduled full scans** with shared mode configured. |
| **Posture alerts** | Save an organization-approved HTTPS webhook endpoint. It is protected in Windows Credential Manager; eligible high-impact posture changes are delivered after a scan |
| Cloud connections | Use Connect Cloud in the header |
| **Ticketing connection** | Choose the system (native: Jira, Jira Service Management, ServiceNow; others use a signed webhook). Enter HTTPS site URL, project key or service desk ID, issue/request type, optional signed webhook URL, API user and token. **Save URL**, **Save credential** (stored in Windows Credential Manager) and then the request button |

---

## Part 9 - Local backup and restore

Open **Settings > Local Data Backup**:

1. **Create backup** saves a `.yaskbackup` archive containing the SQLite database and supported local workflow records, including scan history, access reviews and remediation plans.
2. Protect the archive as sensitive identity and access data. Ticketing credentials, webhook endpoints, CLI tokens, logs and generated reports are excluded.
3. **Restore backup** replaces the current local database and supported workflow records. Review the confirmation carefully; ticketing and webhook credentials are not changed.
4. Close and restart YASK-CIEM after a successful restore so screens reload the restored database.

---

## Part 10 - Feedback
Open **Feedback**, fill in **Your feedback** and **Suggestions** (both required), then **Submit Feedback**. It is stored locally.

---

## Part 11 - Where data is stored

| Item | Location |
|---|---|
| Application data, reports, scan history | `%LOCALAPPDATA%\YASK-CIEM\` |
| Database | `%LOCALAPPDATA%\CloudEntitle\Data\cloudentitle.db` |
| Application logs | `%LOCALAPPDATA%\CloudEntitle\Logs\application-<date>.log` |
| Installer log | `%TEMP%\YASK-CIEM-Setup.log` |
| Posture alert endpoint | Windows Credential Manager for the current Windows user |

The application log lists every cloud CLI command with its duration, which helps diagnose slow scans.

---

## Part 12 - Recommended workflow

1. Install and start the app.
2. Connect one or more clouds and run **Scan Now**.
3. Check the Dashboard cards, especially Source coverage.
4. Open **Risk Findings**; handle Critical items first.
5. Use **Effective Access** and **Access Paths** to understand each finding.
6. Create **Recommendations**, raise tickets and create **Remediation** plans.
7. Run an **Access Review** for privileged and external identities.
8. **Generate Report** and, if signed in to an organization, **Export evidence bundle**.
9. Enable **scheduled scans** and compare results in **Scan History**.

---

## Part 13 - Troubleshooting

| Symptom | Likely cause and action |
|---|---|
| "No active CLI sign-in" | Run `az login` (or the AWS/GCP command) and click Refresh sign-in status |
| Scan stays in progress | Follow the current provider/account/source in the dashboard. Check **Source coverage** and the application log for a slow CLI command; use **Stop Scan** if needed |
| Dashboard shows zeros | No scan yet, or the scan found no data; read Scan History messages |
| PIM data unavailable | Tenant admin must grant Graph consent; permanent role assignments are still shown |
| Reviewer list is empty | Scan first; users must be active and hold a qualifying role |
| Export evidence bundle is disabled | Sign in to an organization with report-export permission |
| Install did nothing | Run the setup from PowerShell, read `%TEMP%\YASK-CIEM-Setup.log` |
| Trial expired | Cloud and workspace options are disabled; data is kept |

---

## Part 14 - Review questions

1. Why is a YASK-CIEM scan safe to run in production?
2. What is the difference between an entitlement and effective access?
3. Which providers can be connected at the same time, and how many sign-in flows run at once?
4. What does blast radius tell you?
5. Which decisions in an access review require a comment?
6. Name three conditions a person must meet to be offered as a reviewer.
7. What is the difference between an Active and an Eligible PIM role?
8. Where would you look to find why a data source was missing from a scan?

**Answers**
1. All access is read-only through your existing CLI identity; no write operations exist.
2. An entitlement is one grant; effective access is the combined result after inheritance and groups.
3. Azure, AWS and Google Cloud may all remain connected; only one interactive sign-in flow runs at a time.
4. How much could be affected if the identity or path were abused.
5. Everything except Certified; ExceptionGranted also needs an expiry date.
6. Active cloud user, available (not disabled), holds Global Administrator, Security Administrator or System Analyst (or equivalent).
7. Active is granted now; Eligible must be activated first and does not count for reviewer eligibility.
8. Scan History messages and the Dashboard Source coverage card.
