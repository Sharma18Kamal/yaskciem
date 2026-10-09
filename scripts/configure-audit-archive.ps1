[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$SettingsPath
)

$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'The License Server credential wizard requires Windows Credential Manager.' }

$credentialWriterSource = @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security;

public static class YaskCredentialWriter
{
    private const uint GenericCredential = 1;
    private const uint LocalMachinePersistence = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string UserName;
    }

    [DllImport("Advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    public static void Write(string targetName, string userName, SecureString secret)
    {
        IntPtr secretBuffer = IntPtr.Zero;
        try
        {
            secretBuffer = Marshal.SecureStringToGlobalAllocUnicode(secret);
            var credential = new NativeCredential
            {
                Type = GenericCredential,
                TargetName = targetName,
                Comment = "Configured by YASK-CIEM License Server setup",
                CredentialBlobSize = checked((uint)(secret.Length * sizeof(char))),
                CredentialBlob = secretBuffer,
                Persist = LocalMachinePersistence,
                UserName = userName
            };
            if (!CredWrite(ref credential, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not write a secret to Windows Credential Manager.");
        }
        finally
        {
            if (secretBuffer != IntPtr.Zero) Marshal.ZeroFreeGlobalAllocUnicode(secretBuffer);
        }
    }
}
'@
Add-Type -TypeDefinition $credentialWriterSource

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
$region = (Read-Host 'S3 SigV4 signing region configured on the object store').Trim()
if ([string]::IsNullOrWhiteSpace($region) -or $region -match '\s') { throw 'Signing region is required and cannot contain whitespace.' }
$bucket = (Read-Host 'Object-Lock-enabled bucket name').Trim()
if ([string]::IsNullOrWhiteSpace($bucket) -or $bucket.Contains('/')) { throw 'Enter a bucket name, not a path.' }
$accessKey = (Read-Host 'S3 access key ID').Trim()
if ([string]::IsNullOrWhiteSpace($accessKey)) { throw 'S3 access key ID is required.' }

$databaseTarget = 'YASK-CIEM/LicenseServer/AuditArchive/Postgres'
$s3Target = 'YASK-CIEM/LicenseServer/AuditArchive/S3'
$databaseConnectionString = Read-Host 'Archive PostgreSQL connection string (stored securely)' -AsSecureString
$s3Secret = Read-Host 'S3 secret access key (stored securely)' -AsSecureString
if ($databaseConnectionString.Length -eq 0 -or $s3Secret.Length -eq 0) { throw 'Both secret values are required.' }

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
    Provider = 'S3ObjectLock'
    Endpoint = $endpoint
    Region = $region
    Bucket = $bucket
    ObjectPrefix = 'yask-ciem/audit/v1'
    SegmentEventCount = 500
    PollIntervalSeconds = 60
    MaxPendingEvents = 5000
    MaxLagMinutes = 60
    DatabaseCredentialTarget = $databaseTarget
    S3CredentialTarget = $s3Target
}
if ($settings.PSObject.Properties['AuditArchive']) { $settings.AuditArchive = $archiveSettings }
else { $settings | Add-Member -MemberType NoteProperty -Name AuditArchive -Value $archiveSettings }

$tempPath = "$settingsPath.tmp"
try {
    $json = $settings | ConvertTo-Json -Depth 32
    [IO.File]::WriteAllText($tempPath, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $tempPath -Destination $settingsPath -Force
    [YaskCredentialWriter]::Write($databaseTarget, 'ConnectionString', $databaseConnectionString)
    [YaskCredentialWriter]::Write($s3Target, $accessKey, $s3Secret)
}
catch {
    Remove-Item -LiteralPath $tempPath -Force -ErrorAction SilentlyContinue
    throw
}
finally {
    $databaseConnectionString.Dispose()
    $s3Secret.Dispose()
}

Write-Host "Archive endpoint, signing region, and bucket saved to $settingsPath."
Write-Host "Database and S3 secrets stored in Windows Credential Manager for $currentIdentity."
Write-Warning 'Restart the License Server under this same Windows identity, then verify /api/v1/readiness. Do not copy the appsettings file to an untrusted host; although it contains no credentials, it identifies the archive endpoint.'
