[CmdletBinding()]
param([string]$PsqlPath)

$ErrorActionPreference = 'Stop'
foreach ($name in @('PGHOST', 'PGDATABASE', 'PGUSER')) {
    if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($name))) {
        throw "Configure the libpq environment variable $name for the operations check."
    }
}
if (-not $PsqlPath) {
    $command = Get-Command psql.exe -ErrorAction SilentlyContinue
    if ($command) { $PsqlPath = $command.Source }
}
if (-not $PsqlPath -or -not (Test-Path $PsqlPath)) { throw 'psql.exe was not found.' }

$sql = @"
WITH expected(name) AS (VALUES
  ('ix_sharedscanjobs_queue'),
  ('ix_sharedscanjobs_org_queued'),
  ('ix_sharedscanjobs_retention'),
  ('ix_sharednotificationdeliveries_queue'),
  ('ix_sharednotificationattempts_retention'),
  ('ix_sharednotificationattempts_failuretime')
)
SELECT CASE WHEN count(*)=6 THEN 'OK' ELSE 'MISSING_OR_INVALID_INDEX' END
FROM expected e JOIN pg_class i ON i.relname=e.name
JOIN pg_index x ON x.indexrelid=i.oid AND x.indisvalid AND x.indisready;
EXPLAIN (FORMAT TEXT)
SELECT JobId FROM SharedScanJobs
WHERE Status='Queued' AND NOT CancellationRequested
  AND (NextAttemptAtUtc IS NULL OR NextAttemptAtUtc <= now())
ORDER BY QueuedAtUtc LIMIT 1;
EXPLAIN (FORMAT TEXT)
SELECT DeliveryId FROM SharedNotificationDeliveries
WHERE Status='Pending' AND NextAttemptAtUtc <= now()
ORDER BY CreatedAtUtc LIMIT 1;
SELECT relname, n_live_tup, last_autovacuum, last_autoanalyze
FROM pg_stat_user_tables
WHERE relname IN ('sharedscanjobs','sharednotificationdeliveries','sharednotificationattempts',
                  'sharedciemscanrecords','shareddriftevents')
ORDER BY relname;
"@
$output = & $PsqlPath --no-psqlrc --set ON_ERROR_STOP=1 --tuples-only --no-align --dbname=$env:PGDATABASE --command $sql
if ($LASTEXITCODE -ne 0) { throw "PostgreSQL operations checks failed with exit code $LASTEXITCODE." }
if ($output[0].Trim() -ne 'OK') { throw 'One or more required operations indexes are missing or invalid.' }
Write-Output 'Required queue, tenant-read and retention indexes are valid.'
Write-Output 'Queue query plans and PostgreSQL autovacuum/analyze statistics follow (small tables may correctly use sequential scans):'
$output | Select-Object -Skip 1
