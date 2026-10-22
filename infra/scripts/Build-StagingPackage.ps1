[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')]
    [string]$Version,
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $repositoryRoot "artifacts\staging-$Version"
}
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) {
    throw "Refusing to overwrite existing package directory: $output"
}

foreach ($tool in @('dotnet', 'node', 'npm')) {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
        throw "Required build tool not found: $tool"
    }
}
$nodeVersion = (& node --version | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $nodeVersion -notmatch '^v(\d+\.\d+\.\d+)$') {
    throw 'Could not determine the Node.js version.'
}
if ([Version]$Matches[1] -lt [Version]'22.18.0') {
    throw 'Building the operations console requires Node.js 22.18.0 or later.'
}

$bundle = Join-Path $output 'bundle'
$publish = Join-Path $bundle 'publish'
New-Item -ItemType Directory -Path $publish -Force | Out-Null
$project = Join-Path $repositoryRoot 'src\CloudEntitle.LicenseServer\CloudEntitle.LicenseServer.csproj'
& dotnet publish $project --configuration Release --runtime win-x64 --self-contained true `
    --configfile (Join-Path $repositoryRoot '.nuget\NuGet.Config') `
    -p:BuildWebConsole=true -p:Version=$Version --output $publish
if ($LASTEXITCODE -ne 0) {
    throw "Publishing failed ($LASTEXITCODE). The incomplete package remains at $output for diagnosis."
}
foreach ($requiredFile in @('CloudEntitle.LicenseServer.exe', 'wwwroot\console\index.html')) {
    if (-not (Test-Path -LiteralPath (Join-Path $publish $requiredFile) -PathType Leaf)) {
        throw "Publish output is incomplete: $requiredFile"
    }
}
if (Test-Path -LiteralPath (Join-Path $publish 'appsettings.Production.json')) {
    throw 'Refusing to package appsettings.Production.json; configure production secrets on the host.'
}

$schema = Join-Path $bundle 'infra\postgres'
$scripts = Join-Path $bundle 'scripts'
$config = Join-Path $bundle 'config'
New-Item -ItemType Directory -Path $schema, $scripts, $config -Force | Out-Null
foreach ($name in @(
    '001-schema.sql', '002-central-monitoring.sql', '003-governance-deadlines.sql',
    '004-operational-integrations.sql', '005-operations-scale.sql', 'backup-roles.sql'
)) {
    Copy-Item -LiteralPath (Join-Path $repositoryRoot "infra\postgres\$name") -Destination $schema
}
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'infra\postgres\license-server.settings.example.json') -Destination $config
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'infra\staging.settings.example.json') -Destination $config
foreach ($name in @('configure-audit-archive.ps1', 'backup-postgres.ps1', 'test-postgres-restore.ps1')) {
    Copy-Item -LiteralPath (Join-Path $repositoryRoot "scripts\$name") -Destination $scripts
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install-StagingPackage.ps1') -Destination $scripts

$manifest = [ordered]@{
    product = 'YASK-CIEM staging License Server and operations console'
    version = $Version
    runtime = 'win-x64'
    createdUtc = [DateTime]::UtcNow.ToString('o')
    includesWebConsole = $true
    migrations = @('001', '002', '003', '004', '005')
    configuration = 'Host-supplied; no staging credentials are included.'
    files = @(Get-ChildItem -LiteralPath $bundle -Recurse -File | ForEach-Object {
        [ordered]@{
            path = [IO.Path]::GetRelativePath($bundle, $_.FullName)
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        }
    })
}
$manifest | ConvertTo-Json -Depth 6 |
    Set-Content -LiteralPath (Join-Path $bundle 'package-manifest.json') -Encoding utf8
$zip = Join-Path $output "YASK-CIEM-Staging-win-x64-$Version.zip"
Compress-Archive -Path (Join-Path $bundle '*') -DestinationPath $zip -CompressionLevel Optimal
if ((Get-Item -LiteralPath $zip).Length -eq 0) {
    throw 'The staging ZIP is empty.'
}
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
"$hash *$(Split-Path -Leaf $zip)" | Set-Content -LiteralPath "$zip.sha256" -Encoding ascii
Write-Output "Package created: $zip"
Write-Output "SHA256: $hash"
