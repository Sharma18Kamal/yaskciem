# YASK-CIEM Getting Started

This guide explains how to download, install, test, and run YASK-CIEM on Windows 10 or Windows 11.

## 1. Download the application

### Option A: Clone the repository

Install Git, then run PowerShell:

```powershell
git clone <repository-url>
Set-Location .\YASK-CIEM
```

Replace `<repository-url>` with the repository URL supplied by your team.

### Option B: Download a ZIP

1. Download the repository ZIP from the project hosting service.
2. Extract it to a local folder, for example `C:\YASK-CIEM`.
3. Open PowerShell in the extracted folder.

The repository currently builds locally with .NET 9. The production brief targets .NET 10; install .NET 10 and retarget the projects when the production SDK is adopted.

## 2. Install prerequisites

Install the following tools:

- .NET 9 SDK
- Git
- PowerShell 7 or Windows PowerShell
- Azure CLI for Azure discovery
- AWS CLI for AWS discovery
- Google Cloud CLI (`gcloud`) for GCP discovery

The cloud CLIs are required for live discovery. The automated tests use deterministic fake command responses and do not require cloud credentials.

## 3. Authenticate cloud providers

Authenticate only against accounts that the application is allowed to inspect. The application uses read-only discovery commands.

### Azure

```powershell
az login
az account show
```

### AWS

```powershell
aws configure
aws sts get-caller-identity
```

Use your organization-approved AWS credential method instead of storing long-lived credentials where possible.

### GCP

```powershell
gcloud auth login
gcloud auth application-default login
gcloud projects list
```

If a CLI is missing or not authenticated, YASK-CIEM reports that provider as unavailable instead of terminating the scan.

## 4. Restore dependencies

From the repository root:

```powershell
dotnet restore .\CloudEntitle.sln --configfile .\.nuget\NuGet.Config
```

The repository NuGet configuration is required because it defines the package source used by the solution.

## 5. Run the automated tests

```powershell
dotnet test .\CloudEntitle.sln --configuration Release --no-restore
```

A successful run reports zero failed tests.

## 6. Run the desktop application from source

```powershell
dotnet run --project .\src\CloudEntitle.Desktop\CloudEntitle.Desktop.csproj --configuration Release --no-restore
```

When the window opens:

1. Review the provider readiness message.
2. Select `All Providers`, `Azure`, `Aws`, or `Gcp` from the provider filter.
3. Select `Scan Now`.
4. Review risk findings, identities, roles, and permissions in the tabs.
5. Use `Export JSON` or `Export CSV` to save the current findings.

## 7. Create a publishable Windows artifact

For provider-specific Azure, AWS, and GCP execution steps, see [Provider Execution Guide](../cloud/provider-execution.md).

Run PowerShell from the repository root:

```powershell
.\scripts\publish-windows.ps1
```

The default output directory is:

```text
artifacts\publish
```

The script restores the solution and publishes the WPF desktop client for `win-x64`. It produces a framework-dependent artifact, so the target machine must have the matching .NET desktop runtime installed.

Custom options are supported:

```powershell
.\scripts\publish-windows.ps1 -Configuration Release -Runtime win-x64 -OutputPath .\artifacts\publish
```

MSI/MSIX packaging and code signing are not included yet. Follow [the installer handoff](../../installer/README.md) for those release steps.

## 8. Data and report locations

The application stores local data under:

```text
%LocalAppData%\YASK-CIEM
```

This includes:

- `yask-ciem.db` for SQLite inventory, rules, and findings
- `trial-state.json` for hashed trial identity records
- `reports\` for exported JSON and CSV reports

Do not manually edit these files while the application is running.

## 9. Troubleshooting

### Restore fails

Confirm that the command is run from the repository root and that the repository NuGet configuration is supplied:

```powershell
dotnet restore .\CloudEntitle.sln --configfile .\.nuget\NuGet.Config
```

### A provider is unavailable

Install the matching CLI and authenticate it. Then verify the command independently:

```powershell
az account show
aws sts get-caller-identity
gcloud projects list
```

### The application opens but shows no findings

Run `Scan Now` after authenticating at least one provider. Findings are generated only from discovered permissions that match enabled risk rules.

### The trial cannot be started

A trial can be used only once per normalized email address and once per normalized hardware/MAC identity. This is intentional. MAC addresses can be changed or spoofed and are a trial deterrent, not a cryptographic hardware identity.

### Build targets the wrong framework

The local scaffold currently targets .NET 9 because that SDK is installed in the development environment. Install the intended production SDK and update the project target framework as part of the release migration.
