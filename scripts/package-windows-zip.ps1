[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$OutputPath = '.\artifacts\windows-zip'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$publishPath = Join-Path $repositoryRoot 'artifacts\windows-zip\publish'
$outputDirectory = Join-Path $repositoryRoot $OutputPath
$archivePath = Join-Path $repositoryRoot "$OutputPath\YASK-CIEM-$Runtime.zip"

if (Test-Path $outputDirectory) {
    Remove-Item $outputDirectory -Recurse -Force
}
New-Item $outputDirectory -ItemType Directory -Force | Out-Null

& (Join-Path $PSScriptRoot 'publish-windows.ps1') `
    -Configuration $Configuration `
    -Runtime $Runtime `
    -OutputPath '.\artifacts\windows-zip\publish'
if ($LASTEXITCODE -ne 0) { throw "Publish failed with exit code $LASTEXITCODE." }

Compress-Archive -Path (Join-Path $publishPath '*') -DestinationPath $archivePath -Force
Write-Host "Created Windows ZIP package: $archivePath"
