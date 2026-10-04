[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [bool]$SelfContained = $true,
    [string]$PackageVersion = '1.0.0.0',
    [string]$Publisher = 'CN=YASK-CIEM',
    [string]$CertificatePath,
    [string]$CertificatePassword,
    [string]$AppInstallerUri,
    [string]$PackageUri,
    [ValidateRange(1, 168)]
    [int]$UpdateCheckHours = 8,
    [string]$OutputPath = '.\artifacts\msix'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$manifestTemplate = Join-Path $repositoryRoot 'installer\AppxManifest.xml'
$publishScript = Join-Path $repositoryRoot 'scripts\publish-windows.ps1'
$outputDirectory = Join-Path $repositoryRoot $OutputPath
$packageRoot = Join-Path $outputDirectory 'package'
$manifestPath = Join-Path $packageRoot 'AppxManifest.xml'
$packagePath = Join-Path $outputDirectory "YASK-CIEM-$PackageVersion.msix"
$hasAppInstallerUri = -not [string]::IsNullOrWhiteSpace($AppInstallerUri)
$hasPackageUri = -not [string]::IsNullOrWhiteSpace($PackageUri)

if ($hasAppInstallerUri -xor $hasPackageUri) {
    throw 'Specify both -AppInstallerUri and -PackageUri to generate an auto-update feed.'
}

if ($hasAppInstallerUri) {
    foreach ($uriValue in @($AppInstallerUri, $PackageUri)) {
        $parsedUri = $null
        if (-not [Uri]::TryCreate($uriValue, [UriKind]::Absolute, [ref]$parsedUri) -or $parsedUri.Scheme -ne 'https') {
            throw "Auto-update URLs must be absolute HTTPS URLs. Invalid URL: $uriValue"
        }
    }

    if (-not $CertificatePath) {
        throw 'App Installer auto-update feeds require a signed MSIX. Specify -CertificatePath.'
    }
}

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

New-Item $packageRoot -ItemType Directory -Force | Out-Null
New-Item $outputDirectory -ItemType Directory -Force | Out-Null

& $publishScript -Configuration $Configuration -Runtime $Runtime -SelfContained $SelfContained -OutputPath $packageRoot
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

if ($hasAppInstallerUri) {
    $manifestDocument = [xml](Get-Content $manifestPath -Raw)
    $namespaceManager = [System.Xml.XmlNamespaceManager]::new($manifestDocument.NameTable)
    $namespaceManager.AddNamespace('appx', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
    $identity = $manifestDocument.SelectSingleNode('/appx:Package/appx:Identity', $namespaceManager)
    $architecture = switch ($Runtime) {
        'win-x64' { 'x64' }
        'win-arm64' { 'arm64' }
        'win-x86' { 'x86' }
        default { throw "Unsupported MSIX runtime architecture: $Runtime" }
    }

    $appInstallerPath = Join-Path $outputDirectory 'YASK-CIEM.appinstaller'
    $appInstallerNamespace = 'http://schemas.microsoft.com/appx/appinstaller/2018'
    $appInstallerDocument = [System.Xml.XmlDocument]::new()
    $appInstaller = $appInstallerDocument.CreateElement('AppInstaller', $appInstallerNamespace)
    $appInstaller.SetAttribute('Uri', $AppInstallerUri)
    $appInstaller.SetAttribute('Version', $PackageVersion)
    $appInstallerDocument.AppendChild($appInstaller) | Out-Null

    $mainPackage = $appInstallerDocument.CreateElement('MainPackage', $appInstallerNamespace)
    $mainPackage.SetAttribute('Name', $identity.GetAttribute('Name'))
    $mainPackage.SetAttribute('Publisher', $identity.GetAttribute('Publisher'))
    $mainPackage.SetAttribute('Version', $PackageVersion)
    $mainPackage.SetAttribute('ProcessorArchitecture', $architecture)
    $mainPackage.SetAttribute('Uri', $PackageUri)
    $appInstaller.AppendChild($mainPackage) | Out-Null

    $updateSettings = $appInstallerDocument.CreateElement('UpdateSettings', $appInstallerNamespace)
    $onLaunch = $appInstallerDocument.CreateElement('OnLaunch', $appInstallerNamespace)
    $onLaunch.SetAttribute('HoursBetweenUpdateChecks', [string]$UpdateCheckHours)
    $onLaunch.SetAttribute('ShowPrompt', 'false')
    $onLaunch.SetAttribute('UpdateBlocksActivation', 'true')
    $updateSettings.AppendChild($onLaunch) | Out-Null
    $updateSettings.AppendChild($appInstallerDocument.CreateElement('AutomaticBackgroundTask', $appInstallerNamespace)) | Out-Null
    $appInstaller.AppendChild($updateSettings) | Out-Null

    $appInstallerDocument.Save($appInstallerPath)
    Write-Host "Created App Installer auto-update feed: $appInstallerPath"
}

Write-Host "Created MSIX package: $packagePath"
