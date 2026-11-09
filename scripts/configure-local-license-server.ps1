[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)][guid]$ApiClientId,
    [Parameter(Mandatory)][guid]$DesktopClientId,
    [Parameter(Mandatory)][guid]$TenantId,
    [Parameter(Mandatory)]
    [ValidatePattern('^/subscriptions/[0-9a-fA-F-]{36}/resourceGroups/[^/?#]+/providers/Microsoft\.Storage/storageAccounts/[a-z0-9]{3,24}$')]
    [string]$StorageAccountResourceId,
    [ValidatePattern('^[a-z0-9](?:[a-z0-9]|-(?!-)){1,61}[a-z0-9]$')]
    [string]$ArchiveContainer = 'ciem-audit',
    [string]$PsqlPath = 'C:\Program Files\PostgreSQL\15\bin\psql.exe',
    [string]$AdminUser = 'postgres'
)

$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Local setup requires PowerShell 7 on Windows and Windows Credential Manager.' }
if ($ApiClientId -eq [guid]::Empty -or $DesktopClientId -eq [guid]::Empty -or $TenantId -eq [guid]::Empty) {
    throw 'Valid Entra API, Desktop and tenant IDs are required.'
}
if (-not (Test-Path -LiteralPath $PsqlPath -PathType Leaf)) { throw 'PostgreSQL psql was not found.' }
$root = Split-Path -Parent $PSScriptRoot
$directory = Join-Path $env:LOCALAPPDATA 'YASK-CIEM\LocalLicenseServer'
$settingsPath = Join-Path $directory 'appsettings.Local.json'
if (Test-Path -LiteralPath $settingsPath) { throw 'Local settings already exist. Do not overwrite credentials or trial history; use start-local-license-server.ps1.' }
$database = 'yask_ciem_local'
$runtimeUser = 'yask_ciem_local_runtime'
$archiveUser = 'yask_ciem_local_archive'

function Invoke-AdminSql([string]$Sql, [string]$Database = 'postgres') {
    $start = [Diagnostics.ProcessStartInfo]::new($PsqlPath)
    foreach ($argument in @('-X', '-w', '-h', 'localhost', '-U', $AdminUser, '-d', $Database, '-v', 'ON_ERROR_STOP=1', '-q', '-t', '-A')) {
        $start.ArgumentList.Add($argument)
    }
    $start.UseShellExecute = $false
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $process = [Diagnostics.Process]::Start($start)
    try {
        $output = $process.StandardOutput.ReadToEndAsync()
        $errorOutput = $process.StandardError.ReadToEndAsync()
        $process.StandardInput.WriteLine($Sql)
        $process.StandardInput.Close()
        $process.WaitForExit()
        $result = $output.GetAwaiter().GetResult()
        $null = $errorOutput.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) {
            throw 'Local PostgreSQL setup failed. Verify administrator access and server logs; SQL and credentials are not printed. Existing data has not been reset.'
        }
        return $result.Trim()
    }
    finally { $process.Dispose() }
}

$existing = Invoke-AdminSql "SELECT count(*) FROM pg_database WHERE datname='$database';"
$roles = Invoke-AdminSql "SELECT count(*) FROM pg_roles WHERE rolname IN ('$runtimeUser','$archiveUser');"
if ($existing -ne '0' -or $roles -ne '0') {
    throw 'The local database or dedicated logins already exist. Refusing to recreate, reset or overwrite them.'
}
if (-not $PSCmdlet.ShouldProcess('localhost PostgreSQL and current Windows user', 'Create isolated local database, apply migrations, store credentials and configure Desktop for localhost HTTPS')) { return }
. (Join-Path $PSScriptRoot 'credential-writer.ps1')
$runtimePassword = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$archivePassword = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$key = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$targets = @{
    Runtime = 'YASK-CIEM/LocalLicenseServer/yask_ciem_local/Runtime'
    Archive = 'YASK-CIEM/LocalLicenseServer/yask_ciem_local/Archive'
    Integrity = 'YASK-CIEM/LocalLicenseServer/yask_ciem_local/Integrity/v1'
}
foreach ($entry in @(
    @{ Target = $targets.Runtime; User = 'ConnectionString'; Value = "Host=localhost;Database=$database;Username=$runtimeUser;Password=$runtimePassword;SSL Mode=Disable" },
    @{ Target = $targets.Archive; User = 'ConnectionString'; Value = "Host=localhost;Database=$database;Username=$archiveUser;Password=$archivePassword;SSL Mode=Disable" },
    @{ Target = $targets.Integrity; User = 'IntegrityKey'; Value = $key }
)) {
    $secure = ConvertTo-SecureString $entry.Value -AsPlainText -Force
    try { [YaskCredentialWriter]::Write($entry.Target, $entry.User, $secure) }
    finally { $secure.Dispose() }
}
$null = Invoke-AdminSql "CREATE DATABASE $database;"
$null = Invoke-AdminSql "CREATE ROLE $runtimeUser LOGIN PASSWORD '$runtimePassword'; CREATE ROLE $archiveUser LOGIN PASSWORD '$archivePassword';"
$runtimePassword = $null
$archivePassword = $null
$key = $null
$migrations = Get-ChildItem -LiteralPath (Join-Path $root 'infra\postgres') -Filter '*.sql' |
    Where-Object Name -Match '^\d{3}-' | Sort-Object Name
foreach ($migration in $migrations) {
    $null = Invoke-AdminSql ("BEGIN;`n" + [IO.File]::ReadAllText($migration.FullName) + "`nCOMMIT;") $database
    Write-Output "Applied $($migration.Name)"
}
$null = Invoke-AdminSql "GRANT yask_ciem_runtime TO $runtimeUser; GRANT yask_ciem_archiver TO $archiveUser; REVOKE CONNECT ON DATABASE $database FROM PUBLIC; GRANT CONNECT ON DATABASE $database TO $runtimeUser, $archiveUser;" $database
$settings = @{
    AzureAd = @{
        Instance = 'https://login.microsoftonline.com/'
        TenantId = "$TenantId"
        ClientId = "$ApiClientId"
        Audience = "$ApiClientId"
    }
    DatabaseCredentialTarget = $targets.Runtime
    AuditRetention = @{
        Provider = 'Postgres'; MinimumRetentionDays = 2555
        IntegrityKeyId = 'v1'; IntegrityCredentialTarget = $targets.Integrity
    }
    AuditArchive = @{
        Provider = 'AzureBlob'
        Endpoint = "https://$($StorageAccountResourceId.Split('/')[-1]).blob.core.windows.net"
        StorageAccountResourceId = $StorageAccountResourceId
        Container = $ArchiveContainer; Authentication = 'AzureCli'
        DatabaseCredentialTarget = $targets.Archive
        ObjectPrefix = 'yask-ciem/audit/v1'; SegmentEventCount = 500
        PollIntervalSeconds = 60; MaxPendingEvents = 5000; MaxLagMinutes = 60
    }
}
$null = New-Item -ItemType Directory -Path $directory -Force
[IO.File]::WriteAllText($settingsPath, ($settings | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
foreach ($entry in @{
    YASK_CIEM_API_BASE_URL = 'https://localhost:7443/'
    YASK_CIEM_ENTRA_CLIENT_ID = "$DesktopClientId"
    YASK_CIEM_API_SCOPE = "api://$ApiClientId/access_as_user"
}.GetEnumerator()) {
    [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'User')
}
Write-Output "Local configuration prepared at $settingsPath. Secrets are in Windows Credential Manager."
Write-Warning 'Local development only: PostgreSQL uses loopback without TLS; the API uses localhost HTTPS. Azure archive readiness remains blocked until its storage and permissions are configured. No cloud storage was changed. Restart Desktop from a fresh environment after starting the server.'
