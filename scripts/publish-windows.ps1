[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$OutputPath = '.\artifacts\publish'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$solutionPath = Join-Path $repositoryRoot 'CloudEntitle.sln'
$projectPath = Join-Path $repositoryRoot 'src\CloudEntitle.Desktop\CloudEntitle.Desktop.csproj'
$nugetConfigPath = Join-Path $repositoryRoot '.nuget\NuGet.Config'
$output = Join-Path $repositoryRoot $OutputPath

if (Test-Path $output) {
    Remove-Item $output -Recurse -Force
}

& dotnet restore $solutionPath --configfile $nugetConfigPath
if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed with exit code $LASTEXITCODE." }

& dotnet publish $projectPath `
    --configuration $Configuration `
    --runtime $Runtime `
    --self-contained false `
    --no-restore `
    --output $output
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

Write-Host "Published YASK-CIEM to $output"
