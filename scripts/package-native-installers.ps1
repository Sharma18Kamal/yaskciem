[CmdletBinding()]
param(
    [string]$PackageVersion = '1.0.0.64',
    [string]$OutputPath,
    [string]$WixExePath,
    [string]$CertificateThumbprint = '537C64CF83CF2C6C1EBC89553B7B4EDD13E7F104'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$version = $null
if (-not [Version]::TryParse($PackageVersion, [ref]$version) -or $version.Revision -lt 0) {
    throw 'PackageVersion must have four numeric components, for example 1.0.0.21.'
}
[xml]$buildProperties = Get-Content -LiteralPath (Join-Path $repositoryRoot 'Directory.Build.props') -Raw
if ($PackageVersion -cne $buildProperties.Project.PropertyGroup.Version) {
    throw 'PackageVersion must match the source Version in Directory.Build.props.'
}

$msiThirdVersion = ($version.Build * 1000) + $version.Revision
if ($msiThirdVersion -gt 65535) { throw 'The encoded MSI version component cannot exceed 65535.' }
$msiVersion = "$($version.Major).$($version.Minor).$msiThirdVersion"
$buildTimestamp = (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd\THH:mm\Z')
$outputDirectory = if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    Join-Path $repositoryRoot "artifacts\native-installers-$PackageVersion"
}
elseif ([IO.Path]::IsPathRooted($OutputPath)) {
    $OutputPath
}
else {
    Join-Path $repositoryRoot $OutputPath
}
if (Test-Path $outputDirectory) { throw "Output directory already exists; choose a new versioned path or remove it first: $outputDirectory" }

if (-not $WixExePath) {
    $wixCommand = Get-Command wix.exe -ErrorAction SilentlyContinue
    if ($wixCommand) { $WixExePath = $wixCommand.Source }
}
if (-not $WixExePath -or -not (Test-Path $WixExePath)) {
    throw 'WiX Toolset CLI v5 was not found. Install WiX 5 or pass -WixExePath.'
}

$signtoolCommand = Get-Command signtool.exe -ErrorAction SilentlyContinue
if ($signtoolCommand) {
    $signtoolPath = $signtoolCommand.Source
}
else {
    $signtoolPath = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Filter signtool.exe -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\' } |
        Sort-Object FullName -Descending |
        Select-Object -First 1 -ExpandProperty FullName
}
if (-not $signtoolPath) { throw 'SignTool.exe was not found. Install the Windows 10/11 SDK.' }

$publishDirectory = Join-Path $outputDirectory 'publish'
$setupPublishDirectory = Join-Path $outputDirectory 'setup-publish'
$msiPath = Join-Path $outputDirectory "YASK-CIEM-$PackageVersion.msi"
$setupPath = Join-Path $outputDirectory "YASK-CIEM-Setup-$PackageVersion.exe"
$setupProject = Join-Path $repositoryRoot 'installer\YaskCiemNativeSetup\YaskCiemNativeSetup.csproj'
$wixSource = Join-Path $repositoryRoot 'installer\native\YaskCiemMsi.wxs'
$publishScript = Join-Path $PSScriptRoot 'publish-windows.ps1'
New-Item $outputDirectory -ItemType Directory -Force | Out-Null

& $publishScript -Configuration Release -Runtime win-x64 -SelfContained $true -OutputPath $publishDirectory
if ($LASTEXITCODE -ne 0) { throw "Desktop publish failed with exit code $LASTEXITCODE." }

& $WixExePath build -arch x64 -d "ProductVersion=$msiVersion" -d "FullVersion=$PackageVersion" -d "BuildTimestamp=$buildTimestamp" -b "PublishedApp=$publishDirectory" -o $msiPath $wixSource
if ($LASTEXITCODE -ne 0) { throw "WiX MSI build failed with exit code $LASTEXITCODE." }

& $signtoolPath sign /fd SHA256 /s My /sha1 $CertificateThumbprint $msiPath
if ($LASTEXITCODE -ne 0) { throw 'MSI signing failed.' }
& $signtoolPath verify /pa $msiPath
if ($LASTEXITCODE -ne 0) { throw 'MSI signature verification failed.' }
& dotnet restore $setupProject --runtime win-x64 --configfile (Join-Path $repositoryRoot '.nuget\NuGet.Config')
if ($LASTEXITCODE -ne 0) { throw "Setup project restore failed with exit code $LASTEXITCODE." }
& dotnet publish $setupProject `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --no-restore `
    --output $setupPublishDirectory `
    "-p:MsiPath=$msiPath" `
    "-p:Version=$PackageVersion" `
    "-p:BuildTimestamp=$buildTimestamp"
if ($LASTEXITCODE -ne 0) { throw "Setup EXE publish failed with exit code $LASTEXITCODE." }

$publishedSetupExe = Join-Path $setupPublishDirectory 'YASK-CIEM-Setup.exe'
if (-not (Test-Path $publishedSetupExe)) { throw "Setup executable was not produced: $publishedSetupExe" }
Copy-Item -LiteralPath $publishedSetupExe -Destination $setupPath

& $signtoolPath sign /fd SHA256 /s My /sha1 $CertificateThumbprint $setupPath
if ($LASTEXITCODE -ne 0) { throw 'Setup EXE signing failed.' }
& $signtoolPath verify /pa $setupPath
if ($LASTEXITCODE -ne 0) { throw 'Setup EXE signature verification failed.' }

Write-Host "Created signed MSI: $msiPath"
Write-Host "Created signed setup EXE: $setupPath"
