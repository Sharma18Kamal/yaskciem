[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$BackupFile,
    [Parameter(Mandatory)]
    [ValidatePattern('^yask_ciem_restore_[a-z0-9_]+$')]
    [string]$RestoreDatabaseName,
    [string]$PgRestorePath,
    [string]$CreatedbPath,
    [string]$DropdbPath,
    [string]$PsqlPath
)

$ErrorActionPreference = 'Stop'
if ($env:YASK_CIEM_ALLOW_RESTORE_TEST -ne 'YES') {
    throw 'Set YASK_CIEM_ALLOW_RESTORE_TEST=YES to authorize creation and cleanup of the explicitly named disposable restore database.'
}
foreach ($name in @('PGHOST', 'PGUSER')) {
    if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($name))) {
        throw "Configure the libpq environment variable $name for the restore-test process."
    }
}
if ($RestoreDatabaseName -eq $env:PGDATABASE) { throw 'The restore-test database must not equal PGDATABASE.' }

$backupPath = [IO.Path]::GetFullPath($BackupFile)
if (-not (Test-Path $backupPath)) { throw "Backup file not found: $backupPath" }
$checksumPath = "$backupPath.sha256"
if (-not (Test-Path $checksumPath)) { throw "Backup checksum file not found: $checksumPath" }
$expectedHash = ((Get-Content -LiteralPath $checksumPath -Raw).Trim() -split '\s+')[0]
$actualHash = (Get-FileHash -LiteralPath $backupPath -Algorithm SHA256).Hash
if ($expectedHash -ne $actualHash) { throw 'Backup checksum validation failed.' }

function Resolve-Executable([string]$Path, [string]$Name) {
    if ($Path -and (Test-Path $Path)) { return $Path }
    $command = Get-Command $Name -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    throw "$Name was not found."
}

$PgRestorePath = Resolve-Executable $PgRestorePath 'pg_restore.exe'
$CreatedbPath = Resolve-Executable $CreatedbPath 'createdb.exe'
$DropdbPath = Resolve-Executable $DropdbPath 'dropdb.exe'
$PsqlPath = Resolve-Executable $PsqlPath 'psql.exe'

& $PgRestorePath --no-password --list $backupPath *> $null
if ($LASTEXITCODE -ne 0) { throw 'pg_restore could not read the backup archive.' }

$databaseCreated = $false
try {
    & $CreatedbPath --no-password --maintenance-db=postgres $RestoreDatabaseName
    if ($LASTEXITCODE -ne 0) { throw "Could not create disposable restore database '$RestoreDatabaseName'. It may already exist; refusing to overwrite it." }
    $databaseCreated = $true

    & $PgRestorePath --no-password --exit-on-error --no-owner --no-privileges --dbname=$RestoreDatabaseName $backupPath
    if ($LASTEXITCODE -ne 0) { throw "pg_restore failed with exit code $LASTEXITCODE." }

    $validationSql = @"
SELECT CASE WHEN
    to_regclass('public.sharedorganizationdirectories') IS NOT NULL
    AND to_regclass('public.sharedaccountbindings') IS NOT NULL
    AND to_regclass('public.sharedaccessreviewauditevents') IS NOT NULL
    AND to_regclass('public.sharedscansummaries') IS NOT NULL
    AND to_regclass('public.sharedciemscanrecords') IS NOT NULL
    AND to_regclass('public.sharedscanjobs') IS NOT NULL
    AND to_regclass('public.sharedscanjobattempts') IS NOT NULL
    AND to_regclass('public.sharednotificationdeliveries') IS NOT NULL
    AND to_regclass('public.sharednotificationattempts') IS NOT NULL
    AND to_regclass('public.ix_sharedscanjobs_queue') IS NOT NULL
    AND to_regclass('public.ix_sharedscanjobs_retention') IS NOT NULL
    AND to_regclass('public.ix_sharednotificationattempts_retention') IS NOT NULL
    AND to_regclass('public.ix_sharednotificationattempts_failuretime') IS NOT NULL
    AND to_regprocedure('public.yask_prune_operational_history(timestamp with time zone,integer)') IS NOT NULL
    AND to_regclass('public.ciem_audit_heads') IS NOT NULL
    AND to_regclass('public.ciem_audit_events') IS NOT NULL
    AND to_regclass('public.ciem_audit_archive_segments') IS NOT NULL
THEN 'OK' ELSE 'MISSING_SCHEMA_OR_OPERATIONS_INDEX' END,
(SELECT count(*) FROM ciem_audit_events),
(SELECT count(*) FROM ciem_audit_heads),
(SELECT count(*) FROM ciem_audit_archive_segments),
(SELECT count(*) FROM sharedscanjobs),
(SELECT count(*) FROM sharednotificationdeliveries),
(SELECT count(*) FROM sharedaccessreviewauditevents),
(SELECT count(*) FROM sharedciemscanrecords)
"@
    $resultLine = (& $PsqlPath --no-psqlrc --no-password --set ON_ERROR_STOP=1 --tuples-only --no-align --field-separator='|' --dbname=$RestoreDatabaseName --command $validationSql | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw "Restore validation query failed with exit code $LASTEXITCODE." }
    $results = $resultLine -split '\|'
    if ($results.Count -ne 8 -or $results[0].Trim() -ne 'OK') { throw 'Restored database is missing required audit/workspace/operations schemas or indexes.' }
    if ($results[1..7] | Where-Object { $_.Trim() -notmatch '^\d+$' }) { throw 'Restored audit/archive/operations row counts could not be validated.' }

    Write-Output "Restore drill passed for '$RestoreDatabaseName'."
    Write-Output "Audit events: $($results[1].Trim()); tenant heads: $($results[2].Trim()); archived segments: $($results[3].Trim())."
    Write-Output "Scan jobs: $($results[4].Trim()); notification deliveries: $($results[5].Trim()); review audit events: $($results[6].Trim()); retained scan records: $($results[7].Trim())."
}
finally {
    if ($databaseCreated) {
        & $DropdbPath --no-password --maintenance-db=postgres $RestoreDatabaseName
        if ($LASTEXITCODE -ne 0) { throw "Restore drill passed or failed, but disposable database cleanup failed: $RestoreDatabaseName" }
    }
}
