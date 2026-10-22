# YASK-CIEM

This repository was initialized from the project brief in `CIEM-COmpact-prompt.docx` and reflects the requested Windows-first CloudEntitle CIEM architecture.

## Scope

- Cloud entitlement and privilege visibility for Azure, AWS, and GCP
- Read-only discovery and analysis in v1
- WPF desktop client using .NET 10, MVVM, EF Core, SQLite, and Serilog
- Clean Architecture with provider-specific adapters and a shared normalization/risk model
- Local fourteen-day trial enforcement bound to a stable Windows device identifier, with no email or username prompt
- JSON and CSV report export from the persisted findings database

## Current implementation

- `src/` — application, domain, infrastructure, cloud providers, licensing
- `tests/` — unit and integration tests
- `src/` — application, domain, infrastructure, cloud providers, licensing, and WPF desktop client
- `tests/` — domain, application, infrastructure, Azure provider, and licensing tests
- `.github/workflows/ci.yml` — Windows restore, desktop build, and test validation

The desktop client currently supports:

- SQLite persistence for risk rules, normalized inventory, and findings
- Provider-filtered findings and identity views
- Read-only Azure CLI, AWS CLI, and gcloud discovery adapters
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
- The current projects target .NET 9. [global.json](global.json) selects the latest installed stable .NET 9 feature band, preventing newer preinstalled SDKs from silently changing compiler and LINQ binding behavior in local or CI builds. Moving to .NET 10 requires a separately tested upgrade.
- Git
- Azure CLI, AWS CLI, and GCP SDK or equivalent auth tooling
- Visual Studio 2022 or VS Build Tools with .NET desktop development workloads
- Access to test cloud accounts for read-only validation

## Running locally

For installation instructions for released Windows packages, see [Install YASK-CIEM](docs/deployment/installation.md). For development setup, testing, and first-run details, see [Getting Started](docs/development/getting-started.md).

1. Install the .NET 9 SDK for the current local scaffold, or update the target framework when .NET 10 is available.
2. Install and authenticate the required read-only tools: `az`, `aws`, and `gcloud`.
3. Restore with `dotnet restore CloudEntitle.sln --configfile .nuget/NuGet.Config`.
4. Build with `dotnet build src/CloudEntitle.Desktop/CloudEntitle.Desktop.csproj`.
5. Test with `dotnet test CloudEntitle.sln`.

On first launch, the desktop app starts a fourteen-day trial and shows the remaining days in the header. The trial continues from its saved start time when the app is reinstalled under the same Windows account and Windows installation. Trial status is rechecked every 30 seconds; after expiry, cloud and workspace options are disabled without deleting local data. Trial state is local at `%LOCALAPPDATA%\YASK-CIEM\trial-state.json` and is not tamper-proof. Production release still requires remote HTTPS license validation and validation against real cloud tenants. No cloud write operations are performed by the current adapters.
