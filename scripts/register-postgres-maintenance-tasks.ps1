[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$BackupDirectory,
    [ValidateRange(1, 3650)]
    [int]$RetentionDays = 35,
    [ValidatePattern('^([01]?\d|2[0-3]):[0-5]\d$')]
    [string]$BackupTime = '02:00',
    [ValidatePattern('^([01]?\d|2[0-3]):[0-5]\d$')]
    [string]$RestoreTime = '03:00',
    [string]$BackupTaskName = 'YASK-CIEM PostgreSQL Daily Backup',
    [string]$RestoreTaskName = 'YASK-CIEM PostgreSQL Weekly Restore Drill'
)

$ErrorActionPreference = 'Stop'
$runner = Join-Path $PSScriptRoot 'run-postgres-maintenance.ps1'
if (-not (Test-Path $runner)) { throw "Maintenance runner not found: $runner" }
$powerShell = (Get-Command powershell.exe -ErrorAction Stop).Source
$credential = Get-Credential -Message 'Windows account that has access to the local PostgreSQL backup directory and database. The Task Scheduler stores this task credential.'
if (-not $credential) { throw 'A Windows account credential is required to register unattended scheduled tasks.' }

function New-MaintenanceAction([string]$Mode) {
    $quotedRunner = '"' + $runner + '"'
    $quotedDirectory = '"' + [IO.Path]::GetFullPath($BackupDirectory) + '"'
    $arguments = "-NoProfile -ExecutionPolicy RemoteSigned -File $quotedRunner -Mode $Mode -BackupDirectory $quotedDirectory -RetentionDays $RetentionDays"
    New-ScheduledTaskAction -Execute $powerShell -Argument $arguments -WorkingDirectory (Split-Path -Parent $runner)
}

$backupAt = [DateTime]::Today.Add([TimeSpan]::Parse($BackupTime))
$restoreAt = [DateTime]::Today.Add([TimeSpan]::Parse($RestoreTime))
$backupTrigger = New-ScheduledTaskTrigger -Daily -At $backupAt
$restoreTrigger = New-ScheduledTaskTrigger -Weekly -DaysOfWeek Sunday -At $restoreAt
$principal = New-ScheduledTaskPrincipal -UserId $credential.UserName -LogonType Password -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Hours 4) -MultipleInstances IgnoreNew

$backupTask = New-ScheduledTask -Action (New-MaintenanceAction 'Backup') -Trigger $backupTrigger -Principal $principal -Settings $settings -Description 'Creates a verified YASK-CIEM PostgreSQL backup and applies the configured retention window.'
$restoreTask = New-ScheduledTask -Action (New-MaintenanceAction 'Restore') -Trigger $restoreTrigger -Principal $principal -Settings $settings -Description 'Restores the latest YASK-CIEM PostgreSQL backup to a disposable database and validates required schemas and counts.'
$password = $credential.GetNetworkCredential().Password
try {
    Register-ScheduledTask -TaskName $BackupTaskName -InputObject $backupTask -User $credential.UserName -Password $password -Force | Out-Null
    Register-ScheduledTask -TaskName $RestoreTaskName -InputObject $restoreTask -User $credential.UserName -Password $password -Force | Out-Null
}
finally {
    $password = $null
    $credential = $null
}

Write-Output "Registered daily backup task '$BackupTaskName' at $BackupTime."
Write-Output "Registered weekly restore-drill task '$RestoreTaskName' on Sunday at $RestoreTime."
Write-Warning 'Ensure the task account has PGHOST/PGDATABASE/PGUSER, an ACL-protected PGPASSFILE (or approved SSPI mapping), YASK_CIEM_ALERT_WEBHOOK_URL, and access to the backup directory in its user environment.'
