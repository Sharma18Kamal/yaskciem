# Cloud Provider Execution Guide

This document shows how YASK-CIEM executes read-only discovery against Azure, AWS, and GCP.

## Execution flow

1. Install the provider CLI.
2. Authenticate the CLI with a read-only cloud identity.
3. Verify the CLI session independently.
4. Start YASK-CIEM.
5. Click `Scan Now`.
6. YASK-CIEM checks provider readiness.
7. The provider adapter runs read-only CLI commands and parses JSON output.
8. Accounts, identities, roles, and permissions are normalized.
9. Risk rules evaluate normalized permissions.
10. Inventory and findings are saved to SQLite.
11. Results appear in the dashboard tabs.

YASK-CIEM does not create, update, delete, or remediate cloud resources in the current release.

## Azure execution

### Install

```powershell
winget install --id Microsoft.AzureCLI
```

Verify:

```powershell
az version
```

### Authenticate

```powershell
az login
az account show
```

Select a subscription when necessary:

```powershell
az account list --output table
az account set --subscription "<subscription-id-or-name>"
az account show
```

### What YASK-CIEM runs

The Azure adapter uses read-only commands equivalent to:

```powershell
az account show --output json
az role assignment list --all --subscription <subscription-id> --output json
```

The output is normalized into:

- Azure subscriptions/accounts
- Principals from role assignments
- Azure RBAC roles
- Permission records

Owner and administrator-style assignments are evaluated by the risk engine.

## AWS execution

### Install

```powershell
winget install --id Amazon.AWSCLI
```

Verify:

```powershell
aws --version
```

### Authenticate

Use the organization-approved credential method. A basic local setup is:

```powershell
aws configure
aws sts get-caller-identity
```

### What YASK-CIEM runs

The AWS adapter uses read-only commands equivalent to:

```powershell
aws sts get-caller-identity --output json
aws iam list-users --output json
aws iam list-policies --scope Local --only-attached --output json
aws iam list-roles --output json
```

The output is normalized into:

- AWS accounts
- IAM users
- IAM managed policies
- IAM roles

Administrator-style policies are evaluated by the risk engine.

## GCP execution

### Install

Install Google Cloud CLI using the official installer or:

```powershell
winget install --id Google.CloudSDK
```

Verify:

```powershell
gcloud version
```

### Authenticate

```powershell
gcloud auth login
gcloud auth application-default login
gcloud projects list
```

### What YASK-CIEM runs

The GCP adapter uses read-only commands equivalent to:

```powershell
gcloud auth list --format=json
gcloud projects list --format=json
gcloud projects get-iam-policy <project-id> --format=json
```

The output is normalized into:

- GCP projects
- IAM policy members
- IAM roles
- Permission records

Owner-level IAM bindings are evaluated by the risk engine.

## Run a provider scan

The desktop scans one connected provider at a time. If more than one CLI is authenticated, disconnect all but the provider you intend to scan.

1. Start YASK-CIEM.
2. Review the cloud connection status on the Dashboard.
3. Select `Discover accounts` to list the connected provider's available accounts.
4. Keep all accounts selected for a full provider scan, or choose a subset in the account scope panel.
5. Click `Scan Now`.
6. Review `Risk Findings`, `Identities`, `Roles`, `Permissions`, and `Scan History` coverage.
7. Export results with `Generate Report`.

Unavailable or unauthenticated providers are skipped and reported in the status message. A scan can continue with the providers that are ready.

## Select accounts for a scan

The Dashboard's **Discover accounts** action lists the accounts visible to the one connected provider. Selection keys use provider, external account ID, and AWS profile where applicable, so a refreshed discovery can be matched even when internal scan GUIDs change. Select a subset in the account scope expander before choosing **Scan Now**; an empty selection is rejected. Unselected accounts are marked `Skipped` in coverage, and partial scans are compared for drift only against scans with the same stable account set. Kubernetes cluster scanning is skipped during partial account scans until cluster accounts are included in the picker; choosing all accounts retains the existing Kubernetes scan behavior.

## Local data and reports

YASK-CIEM stores its local SQLite database and trial state under:

```text
%LocalAppData%\YASK-CIEM
```

Reports are written to:

```text
%LocalAppData%\YASK-CIEM\reports
```

## Troubleshooting

### Provider is unavailable

Run the provider verification command directly:

```powershell
az account show
aws sts get-caller-identity
gcloud projects list
```

Then authenticate again if required.

### No findings appear

Risk findings are generated only when discovered permissions match enabled risk rules. Confirm that the selected account contains IAM/RBAC assignments and that the provider filter is set to the expected provider.

### Permission errors

The CLI identity must have permission to list account metadata and IAM/RBAC data. Grant only the minimum read/list permissions required by your organization. Do not provide write or owner credentials solely for YASK-CIEM discovery.
