[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$BackupDirectory,
    [string]$PgDumpPath,
    [string]$PgRestorePath
)

$ErrorActionPreference = 'Stop'

foreach ($name in @('PGHOST', 'PGDATABASE', 'PGUSER')) {
    if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($name))) {
        throw "Configure the libpq environment variable $name for the backup process."
    }
}

if (-not $PgDumpPath) {
    $command = Get-Command pg_dump.exe -ErrorAction SilentlyContinue
    if ($command) { $PgDumpPath = $command.Source }
}
if (-not $PgRestorePath) {
    $command = Get-Command pg_restore.exe -ErrorAction SilentlyContinue
    if ($command) { $PgRestorePath = $command.Source }
}
if (-not $PgDumpPath -or -not (Test-Path $PgDumpPath)) { throw 'pg_dump.exe was not found.' }
if (-not $PgRestorePath -or -not (Test-Path $PgRestorePath)) { throw 'pg_restore.exe was not found.' }

$directory = [IO.Path]::GetFullPath($BackupDirectory)
New-Item -ItemType Directory -Path $directory -Force | Out-Null
$timestamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ')
$backupPath = Join-Path $directory "yask-ciem-postgres-$timestamp.dump"
$temporaryPath = "$backupPath.partial"

try {
    & $PgDumpPath --format=custom --no-owner --no-privileges --file $temporaryPath
    if ($LASTEXITCODE -ne 0) { throw "pg_dump failed with exit code $LASTEXITCODE." }
    if (-not (Test-Path $temporaryPath) -or (Get-Item $temporaryPath).Length -eq 0) { throw 'pg_dump produced an empty backup.' }

    & $PgRestorePath --list $temporaryPath *> $null
    if ($LASTEXITCODE -ne 0) { throw 'pg_restore could not read the generated backup archive.' }

    Move-Item -LiteralPath $temporaryPath -Destination $backupPath
    $hash = Get-FileHash -LiteralPath $backupPath -Algorithm SHA256
    Set-Content -LiteralPath "$backupPath.sha256" -Value "$($hash.Hash) *$(Split-Path -Leaf $backupPath)" -Encoding ascii
    Write-Output "Backup verified: $backupPath"
    Write-Output "SHA256: $($hash.Hash)"
}
catch {
    Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
    throw
}
