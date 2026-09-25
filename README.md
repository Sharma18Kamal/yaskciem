# YASK-CIEM

This repository was initialized from the project brief in `CIEM-COmpact-prompt.docx` and reflects the requested Windows-first CloudEntitle CIEM architecture.

## Scope

- Cloud entitlement and privilege visibility for Azure, AWS, and GCP
- Read-only discovery and analysis in v1
- WPF desktop client using .NET 10, MVVM, EF Core, SQLite, and Serilog
- Clean Architecture with provider-specific adapters and a shared normalization/risk model
- Local 14-day trial enforcement using normalized email and MAC-derived hardware hashes
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
- Graceful reporting when a CLI is missing or unauthenticated
- JSON report export under `%LocalAppData%\\YASK-CIEM\\reports`

Provider adapters default to deterministic mock data in tests. The desktop composition enables live CLI discovery.

## Prerequisites

- Windows 10/11
- .NET 10 SDK is the production target from the project brief
- The current local environment only has .NET 9.0.318 installed, so the repo scaffold was aligned to `net9.0-windows` for a compile-ready local build until .NET 10 is installed
- Git
- Azure CLI, AWS CLI, and GCP SDK or equivalent auth tooling
- Visual Studio 2022 or VS Build Tools with .NET desktop development workloads
- Access to test cloud accounts for read-only validation

## Running locally

1. Install the .NET 9 SDK for the current local scaffold, or update the target framework when .NET 10 is available.
2. Install and authenticate the required read-only tools: `az`, `aws`, and `gcloud`.
3. Restore with `dotnet restore CloudEntitle.sln --configfile .nuget/NuGet.Config`.
4. Build with `dotnet build src/CloudEntitle.Desktop/CloudEntitle.Desktop.csproj`.
5. Test with `dotnet test CloudEntitle.sln`.

The production release still requires installer packaging/signing, remote HTTPS license validation, and validation against real cloud tenants. No cloud write operations are performed by the current adapters.
