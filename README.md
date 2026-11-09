# YASK-CIEM

This repository was initialized from the project brief in `CIEM-COmpact-prompt.docx` and reflects the requested Windows-first CloudEntitle CIEM architecture.

## Scope

- Cloud entitlement and privilege visibility for Azure, AWS, and GCP
- Read-only discovery and analysis in v1
- Opt-in desktop administrator role/policy writes with explicit approval, verification and guarded rollback
- WPF desktop client using .NET 10, MVVM, EF Core, SQLite, and Serilog
- Clean Architecture with provider-specific adapters and a shared normalization/risk model
- Server-managed 30-day trial; existing data remains readable/exportable after expiry
- JSON and CSV report export from the persisted findings database

## Current implementation

- `src/` — application, domain, infrastructure, cloud providers, licensing, and WPF desktop client
- `tests/` — domain, application, infrastructure, Azure provider, and licensing tests
- `.github/workflows/ci.yml` — Windows restore, desktop build, and test validation

The desktop client currently supports:

- SQLite persistence for risk rules, normalized inventory, and findings
- Provider-filtered findings and identity views
- Read-only Azure CLI, AWS CLI, and gcloud discovery adapters
- Separate [native cloud administration](docs/deployment/cloud-administration.md) for Azure role assignments/custom roles, AWS policy attachments/inline Allow/Deny policies, and GCP project IAM/deny policies; scans and recommendation simulations remain read-only
- Explicitly consented [cloud activity sources](docs/deployment/cloud-activity.md): CloudTrail history/Lake/S3, Azure Activity Log/Log Analytics/Blob archives, and GCP audit logs; partial evidence and current-grant correlations never authorize automatic permission removal
- On-demand [permission-document observations](docs/deployment/cloud-activity.md#on-demand-permission-documents-and-saved-observations): Azure role definitions, AWS IAM policies and GCP role permissions, saved observation history and potential document-based risk candidates; not complete effective access or historical authorization proof
- [Client SIEM connectors](docs/deployment/security-connectors.md): outbound Splunk HEC, QRadar TLS/LEEF and Sentinel Logs Ingestion; separately enabled inbound normalized client-bridge alerts with durable cursors and a saved-alert Desktop view, not native product polling or SOAR execution
- [Priority Two workflows](docs/deployment/priority-two.md): dual-controlled Azure PIM/GCP PAM and client-broker AWS JIT, early-ending workflows, saved behavior/document review and ServiceNow/Teams delivery; activity/change coverage remains partial and effective-escalation signals remain Potential
- [Continuous change feeds](docs/continuous-change-feeds.md): explicit client-owned Azure/AWS/GCP bridge connections, durable cursors/revisions, coverage gaps and polling health; source-reported coverage is not verified completeness
- [Native IAM decision transitions](docs/deployment/authoritative-decisions.md): opt-in GCP v3beta Preview project setIamPolicy reads with exact-context comparison; confirms only qualifying IAM decision transitions, not causal or effective operational escalation
- [What-if Simulator](docs/architecture/what-if-simulator.md): offline AWS/Azure/GCP role/policy action removal, inheritance and same-action alternatives, evidence-linked impact and selected-action modeled exposure reduction; no live calls or policy changes
- [Compliance mapping](docs/architecture/compliance-audit.md): existing frameworks plus NIST CSF/PCI DSS and source-verified Azure Foundations v2.0.0 and AWS Foundations v3.0.0 recommendation crosswalks; this is an informative subset, not certification or complete Benchmark coverage
- [Client emergency-access reporting](docs/deployment/emergency-access.md): immutable post-outage reports, saved activity correlation and independent security attestations for client-owned emergency accounts; no offline credential issuance or approval bypass
- [Trust and credential observations](docs/deployment/trust-credential-monitoring.md): opt-in read-only Azure/AWS/GCP metadata collection with scoped immutable history and conservative change review; on-demand partial observations, not a complete continuous cloud feed
- Azure Entra users, groups, service principals, and active/eligible PIM schedules through the signed-in Azure CLI's Graph access
- Graceful reporting when a CLI is missing or unauthenticated
- JSON report export under `%LocalAppData%\\YASK-CIEM\\reports`

Provider adapters default to deterministic mock data in tests. The desktop composition enables live CLI discovery.

An optional **read-only web operations console** provides organization-wide
posture, finding evidence, scan coverage, review backlog and snapshot history
without the Windows Desktop client. It uses the existing API permissions and
Entra sign-in. See [web console setup](docs/deployment/web-operations-console.md)
for build, configuration and the 72-hour freshness policy.

## Prerequisites

- Windows 10/11
- .NET 10 SDK is the production target from the project brief
- The projects target .NET 10 LTS. [global.json](global.json) requires SDK 10.0.402 or a later stable .NET 10 feature band. Self-contained packages include their runtime; framework-dependent builds need matching .NET 10 runtimes.
- Git
- Azure CLI, AWS CLI, and GCP SDK or equivalent auth tooling
- Visual Studio 2022 or VS Build Tools with .NET desktop development workloads
- Access to test cloud accounts for read-only validation

## Running locally

For installation instructions for released Windows packages, see [Install YASK-CIEM](docs/deployment/installation.md). For development setup, testing, and first-run details, see [Getting Started](docs/development/getting-started.md).

1. Install the .NET 10 SDK selected by `global.json`.
2. Install and authenticate `az`, `aws`, and `gcloud` with read permissions for discovery. Cloud administration additionally requires explicitly granted native write permissions.
3. Restore with `dotnet restore CloudEntitle.sln --configfile .nuget/NuGet.Config`.
4. Build with `dotnet build src/CloudEntitle.Desktop/CloudEntitle.Desktop.csproj`.
5. Test with `dotnet test CloudEntitle.sln`.

Version **1.0.0.65** replaces the unlimited free edition with a **server-managed 30-day trial**, registered once per authenticated Entra tenant. Configure the HTTPS License Server and Entra public-client/API registrations, sign in under Organizations, then select **Start 30-day trial**. The server persists the original registration and evaluates expiry using its database clock; reinstalling or changing the desktop clock does not reset it. New scans, provider collection, and privileged actions require online validation. Existing local data, reports, Demo Mode, and server reads remain available after expiry; server-side revocation/reconciliation continues. Legacy local trial files are ignored. Trial expiry does not remove cloud permissions or replace organization authorization. See [server setup](docs/deployment/license-server-hosting.md). Public release still requires the installed-package and real-cloud acceptance in the [release checklist](docs/deployment/release.md).
