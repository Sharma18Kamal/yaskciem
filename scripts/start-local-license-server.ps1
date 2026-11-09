[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'The local server uses Windows Credential Manager and must run as the setup user.' }
$settings = Join-Path $env:LOCALAPPDATA 'YASK-CIEM\LocalLicenseServer\appsettings.Local.json'
if (-not (Test-Path -LiteralPath $settings -PathType Leaf)) {
    throw 'Run configure-local-license-server.ps1 first under this Windows user.'
}
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\CloudEntitle.LicenseServer\CloudEntitle.LicenseServer.csproj'
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:ASPNETCORE_URLS = 'https://localhost:7443'
$env:DOTNET_ENVIRONMENT = 'Development'
& dotnet run --project $project --no-launch-profile -- --LocalSettingsPath=$settings
if ($LASTEXITCODE -ne 0) { throw "Local License Server exited with code $LASTEXITCODE." }
