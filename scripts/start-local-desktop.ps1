[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
foreach ($name in @('YASK_CIEM_API_BASE_URL', 'YASK_CIEM_ENTRA_CLIENT_ID', 'YASK_CIEM_API_SCOPE')) {
    $value = [Environment]::GetEnvironmentVariable($name, 'User')
    if ([string]::IsNullOrWhiteSpace($value)) { throw "Local Desktop configuration is missing: $name" }
    [Environment]::SetEnvironmentVariable($name, $value, 'Process')
}
if ($env:YASK_CIEM_API_BASE_URL -ne 'https://localhost:7443/') {
    throw 'The local Desktop launcher expects https://localhost:7443/. Existing settings were not changed.'
}
$response = Invoke-WebRequest -Uri 'https://localhost:7443/health/live' -TimeoutSec 10
if ($response.StatusCode -ne 200) { throw 'Start the local License Server before Desktop.' }
$root = Split-Path -Parent $PSScriptRoot
& dotnet run --project (Join-Path $root 'src\CloudEntitle.Desktop\CloudEntitle.Desktop.csproj') --no-launch-profile
if ($LASTEXITCODE -ne 0) { throw "Desktop exited with code $LASTEXITCODE." }
