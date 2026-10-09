[CmdletBinding()]
param(
    [string]$PackageVersion = '1.0.0.48',
    [string]$OutputPath,
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$version = $null
if (-not [Version]::TryParse($PackageVersion, [ref]$version) -or $version.Revision -lt 0) {
    throw 'PackageVersion must have four numeric components, for example 1.0.0.48.'
}
if ($Runtime -ne 'win-x64') { throw 'The self-hosted License Server bundle currently supports win-x64 only.' }

$outputDirectory = if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    Join-Path $repositoryRoot "artifacts\license-server-$PackageVersion"
}
elseif ([IO.Path]::IsPathRooted($OutputPath)) {
    [IO.Path]::GetFullPath($OutputPath)
}
else {
    Join-Path $repositoryRoot $OutputPath
}
if (Test-Path $outputDirectory) { throw "Output directory already exists; choose a new versioned path: $outputDirectory" }

$bundleRoot = Join-Path $outputDirectory 'bundle'
$publishDirectory = Join-Path $bundleRoot 'publish'
$schemaDirectory = Join-Path $bundleRoot 'infra\postgres'
$configDirectory = Join-Path $bundleRoot 'config'
$scriptsDirectory = Join-Path $bundleRoot 'scripts'
$zipPath = Join-Path $outputDirectory "YASK-CIEM-LicenseServer-$Runtime-$PackageVersion.zip"
$projectPath = Join-Path $repositoryRoot 'src\CloudEntitle.LicenseServer\CloudEntitle.LicenseServer.csproj'
$nugetConfig = Join-Path $repositoryRoot '.nuget\NuGet.Config'
$schemaPath = Join-Path $repositoryRoot 'infra\postgres\001-schema.sql'
$settingsTemplate = Join-Path $repositoryRoot 'infra\postgres\license-server.settings.example.json'
$archiveWizard = Join-Path $PSScriptRoot 'configure-audit-archive.ps1'
$deploymentGuide = Join-Path $repositoryRoot 'docs\deployment\license-server-hosting.md'

try {
    New-Item -ItemType Directory -Path $publishDirectory,$schemaDirectory,$configDirectory,$scriptsDirectory -Force | Out-Null
    & dotnet publish $projectPath --configuration Release --runtime $Runtime --self-contained true `
        --configfile $nugetConfig --output $publishDirectory
    if ($LASTEXITCODE -ne 0) { throw "License Server publish failed with exit code $LASTEXITCODE." }

    Copy-Item -LiteralPath $schemaPath -Destination $schemaDirectory
    Copy-Item -LiteralPath $settingsTemplate -Destination (Join-Path $configDirectory 'license-server.settings.example.json')
    Copy-Item -LiteralPath $archiveWizard -Destination $scriptsDirectory
    Copy-Item -LiteralPath $deploymentGuide -Destination (Join-Path $bundleRoot 'README.md')

    $manifest = [pscustomobject]@{
        Product = 'YASK-CIEM Self-Hosted License Server'
        Version = $PackageVersion
        Runtime = $Runtime
        Includes = @('Self-contained License Server publish output', 'PostgreSQL schema', 'Non-secret configuration template', 'Archive Credential Manager setup wizard', 'Deployment guide')
        ExternalPrerequisites = @('PostgreSQL 15+', 'S3-compatible Object Lock COMPLIANCE archive', 'Microsoft Entra API app registration')
        ContainsSecrets = $false
    } | ConvertTo-Json -Depth 4
    [IO.File]::WriteAllText((Join-Path $bundleRoot 'package-manifest.json'), $manifest + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))

    Compress-Archive -Path (Join-Path $bundleRoot '*') -DestinationPath $zipPath -CompressionLevel Optimal
    if (-not (Test-Path $zipPath) -or (Get-Item $zipPath).Length -eq 0) { throw 'License Server ZIP package was not created.' }
    $hash = Get-FileHash -LiteralPath $zipPath -Algorithm SHA256
    Set-Content -LiteralPath "$zipPath.sha256" -Value "$($hash.Hash) *$(Split-Path -Leaf $zipPath)" -Encoding ascii
    Remove-Item -LiteralPath $bundleRoot -Recurse -Force
    Write-Output "Created self-hosted License Server package: $zipPath"
    Write-Output "SHA256: $($hash.Hash)"
}
catch {
    if (Test-Path $outputDirectory) { Remove-Item -LiteralPath $outputDirectory -Recurse -Force }
    throw
}
*** End Patch
