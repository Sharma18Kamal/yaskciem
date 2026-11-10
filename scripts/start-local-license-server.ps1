[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'The local server requires Windows. Run it under the Windows user who configured its Credential Manager secrets.'
}
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
