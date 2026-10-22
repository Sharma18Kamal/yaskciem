[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)]
    [string]$PackagePath,
    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Fa-f0-9]{64}$')]
    [string]$ExpectedSha256,
    [Parameter(Mandatory)]
    [string]$Destination
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$package = (Resolve-Path -LiteralPath $PackagePath).Path
$target = [IO.Path]::GetFullPath($Destination)
if (Test-Path -LiteralPath $target) {
    throw 'Destination already exists. Use a new versioned directory; existing releases are never overwritten.'
}
Add-Type -AssemblyName System.IO.Compression
$packageStream = [IO.File]::Open($package, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
try {
    if ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($packageStream)) -ine $ExpectedSha256) {
        throw 'Package SHA256 does not match the independently approved checksum.'
    }
    $packageStream.Position = 0
    $archive = [IO.Compression.ZipArchive]::new($packageStream, [IO.Compression.ZipArchiveMode]::Read, $true)
    try {
    $entries = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $archive.Entries) {
        $name = $entry.FullName.Replace('\', '/')
        if ($name.EndsWith('/')) { continue }
        $parts = $name.Split('/')
        if ($name.StartsWith('/') -or $parts -contains '..' -or $parts -contains '.' -or
            $parts -contains '' -or $name -match '[:<>|"*?\x00-\x1f]' -or
            @($parts | Where-Object { $_ -match '[. ]$|^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)' }).Count -gt 0) {
            throw 'Archive contains an unsafe Windows path.'
        }
        if (-not $entries.TryAdd($name, $entry)) { throw 'Archive contains duplicate paths.' }
        if ($name -match '(?i)(^|/)appsettings\.Production\.json$') {
            throw 'Release packages must not include host-specific production configuration.'
        }
    }
    if (-not $entries.ContainsKey('package-manifest.json')) { throw 'Package manifest is missing.' }
    $reader = [IO.StreamReader]::new($entries['package-manifest.json'].Open())
    try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    if ($manifest.runtime -cne 'win-x64' -or $manifest.includesWebConsole -ne $true -or
        $manifest.version -notmatch '^\d+\.\d+\.\d+\.\d+$') {
        throw 'Manifest is not a supported Windows staging release.'
    }
    $listed = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $manifest.files) {
        $name = $file.path.Replace('\', '/')
        if ($name -eq 'package-manifest.json' -or -not $listed.Add($name) -or -not $entries.ContainsKey($name)) {
            throw 'Manifest contains duplicate, reserved or missing entries.'
        }
        $stream = $entries[$name].Open()
        try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) }
        finally { $stream.Dispose() }
        if ($file.sha256 -notmatch '^[A-Fa-f0-9]{64}$' -or $hash -ine $file.sha256) {
            throw 'Package file integrity verification failed.'
        }
    }
    if ($listed.Count -ne $entries.Count - 1) { throw 'Archive contains files absent from the manifest.' }
    foreach ($name in @(
        'publish/CloudEntitle.LicenseServer.exe', 'publish/wwwroot/console/index.html',
        'infra/postgres/001-schema.sql', 'infra/postgres/002-central-monitoring.sql',
        'infra/postgres/003-governance-deadlines.sql', 'infra/postgres/004-operational-integrations.sql',
        'infra/postgres/005-operations-scale.sql', 'infra/postgres/backup-roles.sql'
    )) {
        if (-not $listed.Contains($name)) { throw "Required release file is missing: $name" }
    }
    if ($PSCmdlet.ShouldProcess($target, 'Extract verified staging release without starting services')) {
        # A caller must supply a protected parent directory; do not inherit permissions from an arbitrary share.
        $parent = Split-Path -Parent $target
        if (-not (Test-Path -LiteralPath $parent -PathType Container)) { throw 'Destination parent must already exist with approved access controls.' }
        $null = New-Item -ItemType Directory -Path $target -ErrorAction Stop
        foreach ($pair in $entries.GetEnumerator()) {
            $path = Join-Path $target $pair.Key.Replace('/', [IO.Path]::DirectorySeparatorChar)
            $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))
            $source = $pair.Value.Open()
            try {
                $output = [IO.File]::Open($path, [IO.FileMode]::CreateNew)
                try { $source.CopyTo($output) } finally { $output.Dispose() }
            }
            finally { $source.Dispose() }
        }
        Write-Output "Verified release $($manifest.version) staged at $target. Service activation has NOT been performed."
    }
    }
    finally { $archive.Dispose() }
}
finally { $packageStream.Dispose() }
