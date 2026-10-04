[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [bool]$SelfContained = $false,
    [string]$OutputPath = '.\artifacts\publish'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$solutionPath = Join-Path $repositoryRoot 'CloudEntitle.sln'
$projectPath = Join-Path $repositoryRoot 'src\CloudEntitle.Desktop\CloudEntitle.Desktop.csproj'
$nugetConfigPath = Join-Path $repositoryRoot '.nuget\NuGet.Config'
$output = if ([IO.Path]::IsPathRooted($OutputPath)) { $OutputPath } else { Join-Path $repositoryRoot $OutputPath }

if (Test-Path $output) {
    Remove-Item $output -Recurse -Force
}

& dotnet restore $solutionPath --configfile $nugetConfigPath
if ($LASTEXITCODE -ne 0) { throw "dotnet solution restore failed with exit code $LASTEXITCODE." }

& dotnet restore $projectPath --runtime $Runtime --configfile $nugetConfigPath
if ($LASTEXITCODE -ne 0) { throw "dotnet runtime restore failed with exit code $LASTEXITCODE." }

& dotnet publish $projectPath `
    --configuration $Configuration `
    --runtime $Runtime `
    --self-contained $SelfContained.ToString().ToLowerInvariant() `
    --no-restore `
    --output $output
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

Write-Host "Published YASK-CIEM to $output"
