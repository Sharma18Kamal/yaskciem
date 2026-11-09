[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$SettingsPath,
    [ValidateSet('S3ObjectLock', 'AzureBlob')]
    [string]$Provider = 'S3ObjectLock'
)

$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'The License Server credential wizard requires Windows Credential Manager.' }

. (Join-Path $PSScriptRoot 'credential-writer.ps1')

$currentIdentity = [Security.Principal.WindowsIdentity]::GetCurrent().Name
Write-Host "This wizard writes secrets to Windows Credential Manager for '$currentIdentity'."
Write-Host 'Run it as the same Windows identity that will run the License Server.'
$continue = Read-Host 'Continue with archive setup? Type YES'
if ($continue -cne 'YES') { throw 'Archive setup cancelled.' }

$endpoint = (Read-Host 'S3-compatible HTTPS endpoint URL').Trim().TrimEnd('/')
$endpointUri = $null
$endpointValid = [Uri]::TryCreate($endpoint, [UriKind]::Absolute, [ref]$endpointUri)
switch ($endpointValid) {
    $false { throw 'Endpoint must be an absolute HTTPS URL.' }
    $true {
        switch ($endpointUri.Scheme) {
            { $_ -ne 'https' } { throw 'Endpoint must use HTTPS.' }
        }
        switch ($endpointUri.UserInfo.Length) {
            { $_ -gt 0 } { throw 'Endpoint must not embed credentials.' }
        }
    }
}
$region = $null
$bucket = $null
$accessKey = $null
$s3Secret = $null
$container = $null
$storageAccountResourceId = $null
$managedIdentityClientId = $null
if ($Provider -eq 'AzureBlob') {
    if ($endpointUri.AbsolutePath -ne '/' -or $endpointUri.Query -or $endpointUri.Fragment -or -not $endpointUri.IsDefaultPort) {
        throw 'Use the Blob service endpoint only, without paths, ports, SAS tokens or fragments.'
    }
    $storageAccountResourceId = (Read-Host 'Azure storage account ARM resource ID').Trim()
    if ($storageAccountResourceId -notmatch '^/subscriptions/[0-9a-fA-F-]{36}/resourceGroups/[^/?#]+/providers/Microsoft\.Storage/storageAccounts/([a-z0-9]{3,24})$') {
        throw 'Provide a valid storage account ARM resource ID.'
    }
    if ($endpointUri.Host -ne "$($Matches[1]).blob.core.windows.net") {
        throw 'The Blob endpoint must match the public-cloud storage account in the resource ID.'
    }
    $container = (Read-Host 'Private, version-level immutable audit container name').Trim()
    if ($container -cnotmatch '^[a-z0-9](?:[a-z0-9]|-(?!-)){1,61}[a-z0-9]$') {
        throw 'Provide a valid Azure Blob container name.'
    }
    $managedIdentityClientId = (Read-Host 'User-assigned managed identity client GUID (leave empty for system-assigned)').Trim()
    $identityGuid = [guid]::Empty
    if ($managedIdentityClientId -and (-not [guid]::TryParse($managedIdentityClientId, [ref]$identityGuid) -or $identityGuid -eq [guid]::Empty)) {
        throw 'The managed identity client ID must be a non-empty GUID.'
    }
}
else {
    $region = (Read-Host 'S3 SigV4 signing region configured on the object store').Trim()
    if ([string]::IsNullOrWhiteSpace($region) -or $region -match '\s') { throw 'Signing region is required and cannot contain whitespace.' }
    $bucket = (Read-Host 'Object-Lock-enabled bucket name').Trim()
    if ([string]::IsNullOrWhiteSpace($bucket) -or $bucket.Contains('/')) { throw 'Enter a bucket name, not a path.' }
    $accessKey = (Read-Host 'S3 access key ID').Trim()
    if ([string]::IsNullOrWhiteSpace($accessKey)) { throw 'S3 access key ID is required.' }
}

$databaseTarget = 'YASK-CIEM/LicenseServer/AuditArchive/Postgres'
$s3Target = 'YASK-CIEM/LicenseServer/AuditArchive/S3'
$databaseConnectionString = Read-Host 'Archive PostgreSQL connection string (stored securely)' -AsSecureString
if ($Provider -eq 'S3ObjectLock') {
    $s3Secret = Read-Host 'S3 secret access key (stored securely)' -AsSecureString
    if ($s3Secret.Length -eq 0) { throw 'S3 secret access key is required.' }
}
if ($databaseConnectionString.Length -eq 0) { throw 'Archive PostgreSQL connection string is required.' }

$settingsPath = [IO.Path]::GetFullPath($SettingsPath)
$settingsDirectory = Split-Path -Parent $settingsPath
New-Item -ItemType Directory -Path $settingsDirectory -Force | Out-Null
if (Test-Path $settingsPath) {
    $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
}
else {
    $settings = [pscustomobject]@{}
}
$archiveSettings = [pscustomobject]@{
    Provider = $Provider
    Endpoint = $endpoint
    ObjectPrefix = 'yask-ciem/audit/v1'
    SegmentEventCount = 500
    PollIntervalSeconds = 60
    MaxPendingEvents = 5000
    MaxLagMinutes = 60
    DatabaseCredentialTarget = $databaseTarget
}
if ($Provider -eq 'AzureBlob') {
    $archiveSettings | Add-Member -NotePropertyName Container -NotePropertyValue $container
    $archiveSettings | Add-Member -NotePropertyName StorageAccountResourceId -NotePropertyValue $storageAccountResourceId
    $archiveSettings | Add-Member -NotePropertyName Authentication -NotePropertyValue 'ManagedIdentity'
    if ($managedIdentityClientId) {
        $archiveSettings | Add-Member -NotePropertyName ManagedIdentityClientId -NotePropertyValue $managedIdentityClientId
    }
}
else {
    $archiveSettings | Add-Member -NotePropertyName Region -NotePropertyValue $region
    $archiveSettings | Add-Member -NotePropertyName Bucket -NotePropertyValue $bucket
    $archiveSettings | Add-Member -NotePropertyName S3CredentialTarget -NotePropertyValue $s3Target
}
if ($settings.PSObject.Properties['AuditArchive']) { $settings.AuditArchive = $archiveSettings }
else { $settings | Add-Member -MemberType NoteProperty -Name AuditArchive -Value $archiveSettings }

$tempPath = "$settingsPath.tmp"
try {
    $json = $settings | ConvertTo-Json -Depth 32
    [IO.File]::WriteAllText($tempPath, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $tempPath -Destination $settingsPath -Force
    [YaskCredentialWriter]::Write($databaseTarget, 'ConnectionString', $databaseConnectionString)
    if ($Provider -eq 'S3ObjectLock') { [YaskCredentialWriter]::Write($s3Target, $accessKey, $s3Secret) }
}
catch {
    Remove-Item -LiteralPath $tempPath -Force -ErrorAction SilentlyContinue
    throw
}
finally {
    $databaseConnectionString.Dispose()
    if ($null -ne $s3Secret) { $s3Secret.Dispose() }
}

Write-Host "$Provider archive configuration saved to $settingsPath."
Write-Host "Required database/S3 secrets stored in Windows Credential Manager for $currentIdentity. Azure Blob uses managed identity, not a storage key."
Write-Warning 'Restart the License Server under this same Windows identity, then verify /api/v1/readiness. Do not copy the appsettings file to an untrusted host; although it contains no credentials, it identifies the archive endpoint.'
