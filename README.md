# YASK-CIEM

This repository was initialized from the project brief in `CIEM-COmpact-prompt.docx` and reflects the requested Windows-first CloudEntitle CIEM architecture.

## Scope

- Cloud entitlement and privilege visibility for Azure, AWS, and GCP
- Read-only discovery and analysis in v1
- WPF desktop client using .NET 10, MVVM, EF Core, SQLite, and Serilog
- Clean Architecture with provider-specific adapters and a shared normalization/risk model
- Free standalone desktop edition, without a trial expiry or license-activation requirement
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
- The projects target .NET 10 LTS. [global.json](global.json) requires SDK 10.0.402 or a later stable .NET 10 feature band. Self-contained packages include their runtime; framework-dependent builds need matching .NET 10 runtimes.
- Git
- Azure CLI, AWS CLI, and GCP SDK or equivalent auth tooling
- Visual Studio 2022 or VS Build Tools with .NET desktop development workloads
- Access to test cloud accounts for read-only validation

## Running locally

For installation instructions for released Windows packages, see [Install YASK-CIEM](docs/deployment/installation.md). For development setup, testing, and first-run details, see [Getting Started](docs/development/getting-started.md).

1. Install the .NET 10 SDK selected by `global.json`.
2. Install and authenticate the required read-only tools: `az`, `aws`, and `gcloud`.
3. Restore with `dotnet restore CloudEntitle.sln --configfile .nuget/NuGet.Config`.
4. Build with `dotnet build src/CloudEntitle.Desktop/CloudEntitle.Desktop.csproj`.
5. Test with `dotnet test CloudEntitle.sln`.

The desktop starts as **Free standalone edition**. It does not create or read local trial state, collect a device identifier for licensing, or lock workspace/cloud controls after fourteen days. Legacy trial files from older versions are left untouched and ignored. Existing scan data, settings and cloud permissions are preserved. Central organization features still require their separately configured services; free desktop access does not grant cloud or organization permissions. Public release still requires the installed-package and real-cloud acceptance in the [release checklist](docs/deployment/release.md). No cloud write operations are performed by the current discovery adapters.
