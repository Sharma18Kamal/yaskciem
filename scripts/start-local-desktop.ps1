[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
foreach ($name in @('YASK_CIEM_API_BASE_URL', 'YASK_CIEM_ENTRA_CLIENT_ID', 'YASK_CIEM_API_SCOPE')) {
    $value = [Environment]::GetEnvironmentVariable($name, 'User')
    if ([string]::IsNullOrWhiteSpace($value)) { throw "Local Desktop configuration is missing: $name" }
    [Environment]::SetEnvironmentVariable($name, $value, 'Process')
}
$serverUrl = [uri]$env:YASK_CIEM_API_BASE_URL
if (-not $serverUrl.IsAbsoluteUri -or $serverUrl.Scheme -ne 'https' -or $serverUrl.UserInfo -or $serverUrl.Query -or $serverUrl.Fragment -or $serverUrl.AbsolutePath -ne '/') {
    throw 'YASK_CIEM_API_BASE_URL must be an HTTPS origin, for example https://susa-pal:7443/.'
}
$response = Invoke-WebRequest -Uri ([uri]::new($serverUrl, 'health/live')) -TimeoutSec 10
if ($response.StatusCode -ne 200) { throw 'Start the local License Server before Desktop.' }
$root = Split-Path -Parent $PSScriptRoot
& dotnet run --project (Join-Path $root 'src\CloudEntitle.Desktop\CloudEntitle.Desktop.csproj') --no-launch-profile
if ($LASTEXITCODE -ne 0) { throw "Desktop exited with code $LASTEXITCODE." }
