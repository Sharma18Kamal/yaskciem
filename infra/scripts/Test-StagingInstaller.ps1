[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) ('yask-installer-test-' + [guid]::NewGuid().ToString('N'))
$installer = Join-Path $PSScriptRoot 'Install-StagingPackage.ps1'
New-Item -ItemType Directory -Path $root | Out-Null
try {
    $bundle = Join-Path $root 'bundle'
    New-Item -ItemType Directory -Path $bundle | Out-Null
    $files = @(
        'publish\CloudEntitle.LicenseServer.exe', 'publish\wwwroot\console\index.html',
        'infra\postgres\001-schema.sql', 'infra\postgres\002-central-monitoring.sql',
        'infra\postgres\003-governance-deadlines.sql', 'infra\postgres\004-operational-integrations.sql',
        'infra\postgres\005-operations-scale.sql', 'infra\postgres\backup-roles.sql'
    )
    foreach ($name in $files) {
        $path = Join-Path $bundle $name
        $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))
        'offline fixture only' | Set-Content -LiteralPath $path
    }
    $manifest = @{
        runtime = 'win-x64'; version = '1.0.0.1'; includesWebConsole = $true
        files = @($files | ForEach-Object {
            @{ path = $_; sha256 = (Get-FileHash -LiteralPath (Join-Path $bundle $_)).Hash }
        })
    }
    $manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $bundle 'package-manifest.json')
    $zip = Join-Path $root 'release.zip'
    Compress-Archive -Path (Join-Path $bundle '*') -DestinationPath $zip
    $hash = (Get-FileHash -LiteralPath $zip).Hash
    $target = Join-Path $root 'installed'
    & $installer -PackagePath $zip -ExpectedSha256 $hash -Destination $target -WhatIf
    if (Test-Path -LiteralPath $target) { throw 'WhatIf created a destination.' }
    & $installer -PackagePath $zip -ExpectedSha256 $hash -Destination $target
    foreach ($name in $files) {
        if (-not (Test-Path -LiteralPath (Join-Path $target $name))) { throw 'Installation omitted a required file.' }
    }
    function Assert-Rejected([scriptblock]$Action) {
        $rejected = $false
        try { & $Action } catch { $rejected = $true }
        if (-not $rejected) { throw 'Unsafe package was accepted.' }
    }
    Assert-Rejected { & $installer -PackagePath $zip -ExpectedSha256 $hash -Destination $target }
    Assert-Rejected { & $installer -PackagePath $zip -ExpectedSha256 ('0' * 64) -Destination (Join-Path $root 'wrong-hash') }
    # Probe the actual verified stream while the installer holds it open.
    function Invoke-LockedPackageTest {
        $installerText = Get-Content -LiteralPath $installer -Raw
        $probeText = @'
        Assert-Rejected { $writer = [IO.File]::Open($package, [IO.FileMode]::Open, [IO.FileAccess]::Write, [IO.FileShare]::ReadWrite); $writer.Dispose() }
        Assert-Rejected { [IO.File]::Move($package, ($package + '.replaced')) }
'@
        $instrumented = $installerText.Replace("    `$entries = ", "$probeText`n    `$entries = ")
        if ($instrumented -eq $installerText) { throw 'Installer lock probe was not injected.' }
        & ([scriptblock]::Create($instrumented)) -PackagePath $zip -ExpectedSha256 $hash -Destination (Join-Path $root 'locked') -WhatIf
    }
    Invoke-LockedPackageTest
    foreach ($badName in @('../escape.txt', 'publish/CON.txt', 'publish/appsettings.Production.json', 'unlisted.txt')) {
        $badZip = Join-Path $root ([guid]::NewGuid().ToString('N') + '.zip')
        Copy-Item -LiteralPath $zip -Destination $badZip
        $archive = [IO.Compression.ZipFile]::Open($badZip, [IO.Compression.ZipArchiveMode]::Update)
        try { $null = $archive.CreateEntry($badName) } finally { $archive.Dispose() }
        $badHash = (Get-FileHash -LiteralPath $badZip).Hash
        $badTarget = Join-Path $root ([guid]::NewGuid().ToString('N'))
        Assert-Rejected { & $installer -PackagePath $badZip -ExpectedSha256 $badHash -Destination $badTarget }
        if (Test-Path -LiteralPath $badTarget) { throw 'Rejected archive wrote output.' }
    }
    $tampered = Join-Path $root 'tampered.zip'
    'changed content' | Set-Content -LiteralPath (Join-Path $bundle $files[0])
    Compress-Archive -Path (Join-Path $bundle '*') -DestinationPath $tampered
    Assert-Rejected { & $installer -PackagePath $tampered -ExpectedSha256 (Get-FileHash $tampered).Hash -Destination (Join-Path $root 'tampered') }
    Write-Output 'Installer tests passed: WhatIf, extraction, overwrite refusal, checksum, archive write/replacement lock, traversal, reserved names, production config, unlisted files and file tampering.'
}
finally { Remove-Item -LiteralPath $root -Recurse -Force }
