[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Backup', 'Restore')]
    [string]$Mode,
    [Parameter(Mandatory)]
    [string]$BackupDirectory,
    [ValidateRange(1, 3650)]
    [int]$RetentionDays = 35,
    [string]$PgDumpPath,
    [string]$PgRestorePath,
    [string]$CreatedbPath,
    [string]$DropdbPath,
    [string]$PsqlPath
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$backupScript = Join-Path $PSScriptRoot 'backup-postgres.ps1'
$restoreScript = Join-Path $PSScriptRoot 'test-postgres-restore.ps1'
$webhookUrl = $env:YASK_CIEM_ALERT_WEBHOOK_URL

function Send-MaintenanceAlert([string]$Status, [string]$Summary) {
    if ([string]::IsNullOrWhiteSpace($webhookUrl)) { return }
    $payload = @{
        source = 'YASK-CIEM PostgreSQL maintenance'
        mode = $Mode
        status = $Status
        summary = $Summary
        timestampUtc = [DateTime]::UtcNow.ToString('O')
        computer = $env:COMPUTERNAME
    } | ConvertTo-Json -Compress
    try {
        Invoke-RestMethod -Uri $webhookUrl -Method Post -ContentType 'application/json' -Body $payload -TimeoutSec 15 | Out-Null
    }
    catch {
        Write-Warning 'PostgreSQL maintenance alert delivery failed; inspect the protected task log and webhook configuration.'
    }
}

try {
    foreach ($name in @('PGHOST', 'PGDATABASE', 'PGUSER')) {
        if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($name))) {
            throw "The maintenance task requires the libpq variable $name."
        }
    }
    if ([string]::IsNullOrWhiteSpace($webhookUrl) -or -not $webhookUrl.StartsWith('https://', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Configure YASK_CIEM_ALERT_WEBHOOK_URL as an HTTPS endpoint before running scheduled maintenance.'
    }

    if ($Mode -eq 'Backup') {
        $backupParameters = @{ BackupDirectory = $BackupDirectory; RetentionDays = $RetentionDays }
        if ($PgDumpPath) { $backupParameters.PgDumpPath = $PgDumpPath }
        if ($PgRestorePath) { $backupParameters.PgRestorePath = $PgRestorePath }
        & $backupScript @backupParameters
        if ($LASTEXITCODE -ne 0) { throw 'The PostgreSQL backup script failed.' }
        Send-MaintenanceAlert 'success' "Backup completed and retention applied ($RetentionDays days)."
    }
    else {
        $restoreUser = $env:YASK_CIEM_RESTORE_PGUSER
        $restorePassfile = $env:YASK_CIEM_RESTORE_PGPASSFILE
        if ([string]::IsNullOrWhiteSpace($restoreUser) -or [string]::IsNullOrWhiteSpace($restorePassfile)) {
            throw 'Configure YASK_CIEM_RESTORE_PGUSER and protected YASK_CIEM_RESTORE_PGPASSFILE for the quarterly restore drill.'
        }
        if (-not (Test-Path -LiteralPath $restorePassfile -PathType Leaf)) {
            throw 'The configured quarterly restore-drill PGPASSFILE does not exist.'
        }

        $backup = Get-ChildItem -LiteralPath $BackupDirectory -Filter 'yask-ciem-postgres-*.dump' -File |
            Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
        if (-not $backup) { throw 'No PostgreSQL backup archive is available for the restore drill.' }
        $restoreParameters = @{
            BackupFile = $backup.FullName
            RestoreDatabaseName = 'yask_ciem_restore_' + [guid]::NewGuid().ToString('N')
        }
        if ($PgRestorePath) { $restoreParameters.PgRestorePath = $PgRestorePath }
        if ($CreatedbPath) { $restoreParameters.CreatedbPath = $CreatedbPath }
        if ($DropdbPath) { $restoreParameters.DropdbPath = $DropdbPath }
        if ($PsqlPath) { $restoreParameters.PsqlPath = $PsqlPath }
        $previousRestoreFlag = $env:YASK_CIEM_ALLOW_RESTORE_TEST
        $previousPgUser = $env:PGUSER
        $previousPgPassfile = $env:PGPASSFILE
        $env:YASK_CIEM_ALLOW_RESTORE_TEST = 'YES'
        $env:PGUSER = $restoreUser
        $env:PGPASSFILE = $restorePassfile
        try {
            & $restoreScript @restoreParameters
            if ($LASTEXITCODE -ne 0) { throw 'The PostgreSQL restore drill failed.' }
        }
        finally {
            if ($null -eq $previousRestoreFlag) { Remove-Item Env:YASK_CIEM_ALLOW_RESTORE_TEST -ErrorAction SilentlyContinue }
            else { $env:YASK_CIEM_ALLOW_RESTORE_TEST = $previousRestoreFlag }
            if ($null -eq $previousPgUser) { Remove-Item Env:PGUSER -ErrorAction SilentlyContinue }
            else { $env:PGUSER = $previousPgUser }
            if ($null -eq $previousPgPassfile) { Remove-Item Env:PGPASSFILE -ErrorAction SilentlyContinue }
            else { $env:PGPASSFILE = $previousPgPassfile }
        }
        Send-MaintenanceAlert 'success' 'Restore drill completed: backup checksum, archive restore, schemas, and audit counts verified.'
    }
}
catch {
    Send-MaintenanceAlert 'failure' "PostgreSQL $Mode task failed. Inspect the protected task log for details."
    throw
}
