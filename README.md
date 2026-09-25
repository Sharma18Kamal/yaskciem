# YASK-CIEM

This repository was initialized from the project brief in `CIEM-COmpact-prompt.docx` and reflects the requested Windows-first CloudEntitle CIEM architecture.

## Scope

- Cloud entitlement and privilege visibility for Azure, AWS, and GCP
- Read-only discovery and analysis in v1
- WPF desktop client using .NET 10, MVVM, EF Core, SQLite, and Serilog
- Clean Architecture with provider-specific adapters and a shared normalization/risk model
- Licensing service API with a 7-day trial and secure validation model

## Initial structure

- `src/` — application, domain, infrastructure, cloud providers, licensing
- `tests/` — unit and integration tests
- `installer/` — MSI and MSIX packaging
- `docs/` — architecture, security, deployment, development, cloud provider docs, licensing
- `.github/workflows/` — CI/CD workflow

## Prerequisites

- Windows 10/11
- .NET 10 SDK is the production target from the project brief
- The current local environment only has .NET 9.0.318 installed, so the repo scaffold was aligned to `net9.0-windows` for a compile-ready local build until .NET 10 is installed
- Git
- Azure CLI, AWS CLI, and GCP SDK or equivalent auth tooling
- Visual Studio 2022 or VS Build Tools with .NET desktop development workloads
- Access to test cloud accounts for read-only validation

## Next steps

1. Create the .NET solution and project scaffolding.
2. Implement the domain model and database schema.
3. Implement the provider adapter abstractions and normalization layer.
4. Build the WPF dashboard and settings pages.
5. Add risk engine, reporting, licensing API, and installer packaging.
