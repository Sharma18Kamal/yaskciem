# YASK-CIEM Privacy Policy

**Effective date:** October 11, 2026
**Publisher:** [Insert the publisher's full legal name]
**Privacy contact:** [Insert a monitored privacy contact email or web form]
**Postal address:** [Insert the publisher's business address]

This Privacy Policy explains how the publisher of YASK-CIEM ("we," "us," or "our") handles information when you install or use the YASK-CIEM Windows desktop application (the "App"). It covers the App's local operation and optional connections that you or your organization configure.

## At a Glance

- Cloud discovery is initiated by you and uses the official Azure, AWS, Google Cloud, or Kubernetes command-line tools already configured on your device. Scans are read-only.
- Scan results, reports, review records, and most settings are stored on the device. They are not automatically uploaded by the App.
- Optional organization, ticketing, and alert features send the information needed for those features to the service you configure.
- The App does not include advertising or product-analytics SDKs.
- The App does not currently provide online license activation or payment processing. Its trial record is stored locally.

## Information the App Processes

### Cloud and Kubernetes information

When you run a scan, the App queries the cloud accounts and Kubernetes contexts you select. Depending on the provider, permissions, and scan options, results may include account and resource identifiers, names, locations, users, groups, service identities, owners, roles, permissions, access assignments, policy conditions, credential-expiration metadata, activity timestamps, and risk or coverage findings. This information can identify people or reveal details about your organization's systems and access controls.

The App uses the provider's CLI session on your device. It does not ask you to enter your Azure, AWS, Google Cloud, or Kubernetes password into the App and does not intentionally collect or store the CLI's authentication tokens. The provider's CLI and cloud services process the requests under their own terms and privacy notices. Scan results are stored locally unless you use an optional sharing feature described below.

### Trial and device information

The current trial implementation reads the Windows MachineGuid, or a network adapter identifier as a fallback, to derive a device identifier. It stores hashes of the trial identifier and device identifier, together with trial start, end, and last-observed times, in the current Windows user's local application data. The App does not request an email address or name to start this local trial. This identifier is not sent to a licensing server by the current App.

### Organization sign-in information

If you use organization features, the App can sign you in through Microsoft Entra ID. It processes tenant and user identifiers and a work email address or user principal name to identify the signed-in member and match invitations. It requests the delegated Microsoft Graph `User.Read` permission to retrieve your profile. Sign-in tokens are held in the current app process and are not written to the local organization directory by the App.

### Feedback and diagnostics

Feedback that you submit through the App, including the free-text feedback and suggestions and submission time, is stored locally on your device. Do not include passwords, access tokens, private keys, or other secrets in feedback.

The App writes rolling diagnostic logs under your Windows local application data. Logs can contain application errors and cloud command names, limited command arguments, duration, and exit status. The App does not intentionally log cloud command output or credentials. Review logs before sharing them with support because error details or command arguments may contain environment-specific identifiers.

## Where Information Is Stored

By default, scan inventory and findings are stored in a local SQLite database. Scan history is stored locally and automatically limited to the most recent seven days. Other local records can include organization information, access-review campaigns and attestations, remediation plans, ticket references, compliance assessment history, settings, and feedback. Compliance assessment history retains up to 200 assessments per organization and framework. These records generally remain until you remove them or delete the application data; uninstalling the App does not automatically delete all local data.

The local database and workflow files are stored in the Windows user's application-data folders. The App does not currently encrypt the entire SQLite database or all local workflow files. Protect the Windows account and device with appropriate access controls and full-disk encryption. Optional API credentials and the posture-alert endpoint are stored in Windows Credential Manager. CLI credentials remain under the relevant CLI's own storage and policies.

You can create a local backup archive. It contains the database and supported workflow records, but excludes ticketing and webhook credentials, CLI tokens, logs, and generated reports. Treat the archive as sensitive because it may contain identity and access information.

## When Information Is Shared

The App does not automatically upload scan results, feedback, or diagnostics. Information is sent externally only when needed for a feature you or your organization configures or invokes:

- **Cloud providers and Kubernetes clusters:** The selected CLI sends read-only discovery requests to the provider or cluster. The provider processes those requests under the credentials and permissions configured on your device.
- **Microsoft Entra and Microsoft Graph:** If you sign in to organization features, Entra authenticates you and Graph provides the profile fields needed to match your organization membership.
- **YASK-CIEM License Server:** If your organization configures a License Server, organization records, membership and access-review operations may be synchronized to that server. When you explicitly publish a shared scan summary, the App sends the bound account identifiers and names, scan times, aggregate counts, and source-coverage details. That summary does not include resource-level findings. When you explicitly synchronize central audit events, the selected organization's audit events and associated metadata are sent to the server.
- **Ticketing systems:** When you raise a ticket, the selected ticketing provider receives the recommendation title and description, current and proposed access state, rationale, impact, and an evidence snapshot. This can include identity, resource, permission, or access-path identifiers. Ticket status checks also contact that provider.
- **Posture-alert webhooks:** If you configure an HTTPS alert endpoint, eligible high-impact scan changes are sent to that endpoint. Alert data may include the provider, change type, entity type, severity, summary, previous and current state, and detection time. The endpoint operator handles that information under its own policies.
- **Reports and evidence exports:** Exports are saved to the destination you choose. You control whether and with whom you share those files.

We do not control the privacy or security practices of cloud providers, Microsoft, ticketing vendors, webhook recipients, or your organization's License Server. Review their terms and configure only destinations approved by your organization. The License Server's retention and hosting settings are controlled by its operator; ask your administrator for the applicable retention period and hosting location. The current License Server configuration has a default minimum central-audit retention of 2,555 days, which may be changed by the operator.

## Purposes and Legal Basis

The App processes information to perform read-only cloud access analysis, display findings and evidence, support locally managed access reviews and remediation planning, operate optional organization and ticketing features, deliver configured posture alerts, maintain the local trial, and diagnose errors. Where required by applicable law, the organization that configures and uses the App is responsible for identifying its legal basis and providing any employee or administrator notices required for cloud-account analysis.

## Retention and Deletion

Scan history is automatically limited to seven days. Compliance assessment history is limited to 200 records per organization and framework. Other local records are retained until you delete them or remove the local application data. Uninstalling the App may leave local data behind; use Windows storage controls to delete it if desired. Credential Manager entries can be removed from Windows Credential Manager or through the App's Settings where supported.

If information has been sent to an organization server, ticketing provider, or webhook recipient, deletion from your device does not delete the recipient's copy. Contact your organization administrator or that provider to request access, correction, or deletion. Central audit records may be subject to append-only controls and configured retention obligations.

## Security

The App is designed to use read-only cloud discovery and not to store cloud CLI credentials. It validates HTTPS for configured remote endpoints and uses Windows Credential Manager for supported ticketing credentials and posture-alert endpoints. Local database and workflow-file protection otherwise depends on Windows account permissions and device security. No software or transmission method can be guaranteed completely secure.

## Children's Privacy

YASK-CIEM is a business security application and is not designed for children. We do not knowingly seek to collect children's personal information.

## Your Choices and Privacy Requests

You can choose which providers, accounts, and Kubernetes contexts to scan; whether to enable scheduled scans; whether to configure ticketing or alert destinations; and whether to publish shared summaries or synchronize central audit events. You can refrain from using optional sharing features. Your organization may control some settings and data when it manages the App or License Server.

To ask questions or make a privacy request, contact **[insert privacy contact email or web form]**. Include the App version, your organization (if applicable), and a description of the request. Do not include passwords, license keys, or access tokens. We may need to verify your identity and coordinate with your organization before responding.

Depending on your location, you may have rights to request access, correction, deletion, restriction, portability, or to object to certain processing. Your organization may be the controller of cloud-account information processed on its behalf; in that case, direct requests to your organization first.

## International Processing

Cloud providers, Microsoft, ticketing providers, webhook recipients, or an organization-operated License Server may process information in countries different from yours. Their locations and transfer terms depend on the accounts and services your organization configures. Consult the relevant provider and organization notices for details.

## Changes to This Policy

We may update this policy when the App's data practices change or legal requirements change. The effective date above identifies the latest version. Material changes should be communicated through the product or the publisher's distribution page.

## Publisher Contact

**Legal entity:** [Insert publisher's full legal name]
**Privacy contact:** [Insert monitored email address or web form]
**Postal address:** [Insert business address]
**Public policy URL:** [Insert the permanent HTTPS URL to this policy]

---

**Publisher publication checklist:** Replace every bracketed field, confirm the non-sale statement and legal-basis language against the publisher's actual practices, confirm the hosted License Server retention/location and current deployment configuration, and have counsel or the responsible privacy officer review this policy before submitting it to the Microsoft Store.
