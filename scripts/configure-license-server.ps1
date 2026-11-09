[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)][string]$SettingsPath,
    [Parameter(Mandatory)][uri]$ApiBaseUri,
    [Parameter(Mandatory)][guid]$ApiClientId,
    [Parameter(Mandatory)][guid]$DesktopClientId,
    [switch]$ConfigureDesktopForCurrentUser
)

$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'Windows Credential Manager is required.' }
if ($ApiBaseUri.Scheme -ne 'https' -or $ApiBaseUri.UserInfo -or $ApiBaseUri.Query -or $ApiBaseUri.Fragment) {
    throw 'Provide an HTTPS API base URL without embedded credentials, query, or fragment.'
}
if ($ApiClientId -eq [guid]::Empty -or $DesktopClientId -eq [guid]::Empty) { throw 'Real Entra app registration IDs are required.' }
$path = [IO.Path]::GetFullPath($SettingsPath)
if (Test-Path -LiteralPath $path) { throw "Settings already exist; do not overwrite production settings: $path" }
if (-not $PSCmdlet.ShouldProcess($path, 'Create server settings and store database/audit credentials for this Windows identity')) { return }
. (Join-Path $PSScriptRoot 'credential-writer.ps1')
Write-Host 'Run this wizard under the Windows identity that will run the License Server.'
Write-Host 'No database, Entra registration, archive bucket, Windows service, or live endpoint will be created.'
$database = Read-Host 'Runtime PostgreSQL connection string (use TLS VerifyFull and the non-owner runtime login)' -AsSecureString
$key = Read-Host 'Existing/new audit HMAC key (Base64, at least 32 random bytes; retain every historical key separately)' -AsSecureString
try {
    if ($database.Length -eq 0 -or $key.Length -eq 0) { throw 'Database and audit credentials are required.' }
    $databaseTarget = 'YASK-CIEM/LicenseServer/Runtime/Postgres'
    $keyTarget = 'YASK-CIEM/LicenseServer/AuditIntegrity/v1'
    $settings = @{
        AzureAd = @{ Instance = 'https://login.microsoftonline.com/'; TenantId = 'organizations'; ClientId = "$ApiClientId"; Audience = "api://$ApiClientId" }
        DatabaseCredentialTarget = $databaseTarget
        AuditRetention = @{ Provider = 'Postgres'; MinimumRetentionDays = 2555; IntegrityKeyId = 'v1'; IntegrityCredentialTarget = $keyTarget }
        PrivilegedAccess = @{ Organizations = @{} }
        ContinuousChangeFeeds = @{ Organizations = @{} }
        AuthoritativeDecisions = @{ Sources = @() }
    }
    [YaskCredentialWriter]::Write($databaseTarget, 'ConnectionString', $database)
    [YaskCredentialWriter]::Write($keyTarget, 'IntegrityKey', $key)
    New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
    [IO.File]::WriteAllText($path, ($settings | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
    if ($ConfigureDesktopForCurrentUser) {
        [Environment]::SetEnvironmentVariable('YASK_CIEM_API_BASE_URL', $ApiBaseUri.AbsoluteUri.TrimEnd('/') + '/', 'User')
        [Environment]::SetEnvironmentVariable('YASK_CIEM_ENTRA_CLIENT_ID', "$DesktopClientId", 'User')
        [Environment]::SetEnvironmentVariable('YASK_CIEM_API_SCOPE', "api://$ApiClientId/access_as_user", 'User')
    }
}
finally {
    $database.Dispose()
    $key.Dispose()
}
Write-Host "Prepared non-secret server settings: $path"
Write-Host "Desktop: set YASK_CIEM_API_BASE_URL=$($ApiBaseUri.AbsoluteUri), YASK_CIEM_ENTRA_CLIENT_ID=$DesktopClientId, YASK_CIEM_API_SCOPE=api://$ApiClientId/access_as_user, then restart."
Write-Warning 'Apply every numbered PostgreSQL migration, configure the Object Lock archive with configure-audit-archive.ps1, arrange HTTPS/process supervision, and verify /api/v1/readiness before use. This wizard has not deployed or started the server.'
