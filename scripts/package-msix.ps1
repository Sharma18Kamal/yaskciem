[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$PackageVersion = '1.0.0.0',
    [string]$Publisher = 'CN=YASK-CIEM',
    [string]$CertificatePath,
    [string]$CertificatePassword,
    [string]$OutputPath = '.\artifacts\msix'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$manifestTemplate = Join-Path $repositoryRoot 'installer\AppxManifest.xml'
$publishScript = Join-Path $repositoryRoot 'scripts\publish-windows.ps1'
$packageRoot = Join-Path $repositoryRoot 'artifacts\msix\package'
$outputDirectory = Join-Path $repositoryRoot $OutputPath
$manifestPath = Join-Path $packageRoot 'AppxManifest.xml'
$packagePath = Join-Path $outputDirectory "YASK-CIEM-$PackageVersion.msix"

function Find-MakeAppx {
    $command = Get-Command makeappx.exe -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }

    $kits = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Filter makeappx.exe -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\' } |
        Sort-Object FullName -Descending
    if ($kits) { return $kits[0].FullName }
    throw 'MakeAppx.exe was not found. Install the Windows 10/11 SDK.'
}

function Find-SignTool {
    $command = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }

    $kits = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Filter signtool.exe -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\' } |
        Sort-Object FullName -Descending
    if ($kits) { return $kits[0].FullName }
    throw 'SignTool.exe was not found. Install the Windows 10/11 SDK.'
}

if (Test-Path $outputDirectory) { Remove-Item $outputDirectory -Recurse -Force }
New-Item $packageRoot -ItemType Directory -Force | Out-Null
New-Item $outputDirectory -ItemType Directory -Force | Out-Null

& $publishScript -Configuration $Configuration -Runtime $Runtime -OutputPath '.\artifacts\msix\package'
if ($LASTEXITCODE -ne 0) { throw "Publish failed with exit code $LASTEXITCODE." }

$manifest = Get-Content $manifestTemplate -Raw
$manifest = $manifest.Replace('Publisher="CN=YASK-CIEM"', ('Publisher="' + $Publisher + '"'))
$manifest = $manifest.Replace('Version="1.0.0.0"', ('Version="' + $PackageVersion + '"'))
Set-Content $manifestPath $manifest -Encoding UTF8

# Placeholder assets are required by AppX metadata; replace them with branded PNGs before release signing.
$assetDirectory = Join-Path $packageRoot 'Assets'
New-Item $assetDirectory -ItemType Directory -Force | Out-Null
foreach ($asset in 'StoreLogo.png', 'Square150x150Logo.png', 'Square44x44Logo.png') {
    $assetPath = Join-Path $assetDirectory $asset
    [IO.File]::WriteAllBytes($assetPath, [Convert]::FromBase64String('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII='))
}

$makeAppx = Find-MakeAppx
& $makeAppx pack /d $packageRoot /p $packagePath /o
if ($LASTEXITCODE -ne 0) { throw "MakeAppx failed with exit code $LASTEXITCODE." }

if ($CertificatePath) {
    $signtool = Find-SignTool
    & $signtool sign /fd SHA256 /a /f $CertificatePath /p $CertificatePassword $packagePath
    if ($LASTEXITCODE -ne 0) { throw "Signing failed with exit code $LASTEXITCODE." }
}

Write-Host "Created MSIX package: $packagePath"
