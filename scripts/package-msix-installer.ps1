[CmdletBinding()]
param(
    [string]$PackageVersion = '1.0.0.16',
    [string]$PackagePath,
    [string]$PublisherCertificatePath = '.\artifacts\certs\yask-ciem-dev.cer',
    [string]$OutputPath = '.\artifacts\installer'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryRoot 'installer\YaskCiemSetup\YaskCiemSetup.csproj'
$packagePath = if ([string]::IsNullOrWhiteSpace($PackagePath)) {
    Join-Path $repositoryRoot "artifacts\msix\YASK-CIEM-$PackageVersion.msix"
}
elseif ([IO.Path]::IsPathRooted($PackagePath)) {
    $PackagePath
}
else {
    Join-Path $repositoryRoot $PackagePath
}
$certificatePath = Join-Path $repositoryRoot $PublisherCertificatePath
$outputDirectory = if ([IO.Path]::IsPathRooted($OutputPath)) { $OutputPath } else { Join-Path $repositoryRoot $OutputPath }
$publishDirectory = Join-Path $outputDirectory 'publish'
$installerPath = Join-Path $outputDirectory "YASK-CIEM-Setup-$PackageVersion.exe"
$expectedThumbprint = '537C64CF83CF2C6C1EBC89553B7B4EDD13E7F104'

if (-not (Test-Path $packagePath)) { throw "MSIX package not found: $packagePath" }
if (-not (Test-Path $certificatePath)) { throw "Publisher certificate not found: $certificatePath" }

$certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new((Resolve-Path $certificatePath))
if ($certificate.Thumbprint -ne $expectedThumbprint -or $certificate.Subject -ne 'CN=YASK-CIEM') {
    throw 'The supplied public certificate does not match the YASK-CIEM MSIX publisher.'
}

New-Item $outputDirectory -ItemType Directory -Force | Out-Null
New-Item $publishDirectory -ItemType Directory -Force | Out-Null

& dotnet publish $projectPath `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $publishDirectory `
    "-p:MsixPath=$packagePath" `
    "-p:CertificatePath=$certificatePath" `
    "-p:Version=$PackageVersion"
if ($LASTEXITCODE -ne 0) { throw "Setup executable publish failed with exit code $LASTEXITCODE." }

$publishedExecutable = Join-Path $publishDirectory 'YASK-CIEM-Setup.exe'
if (-not (Test-Path $publishedExecutable)) { throw "Setup executable was not produced: $publishedExecutable" }
Copy-Item $publishedExecutable $installerPath -Force

$signtool = Get-Command signtool.exe -ErrorAction SilentlyContinue
if (-not $signtool) {
    $kits = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Filter signtool.exe -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\' } |
        Sort-Object FullName -Descending
    if ($kits) { $signtool = $kits[0].FullName }
}
if (-not $signtool) { throw 'SignTool.exe was not found. Install the Windows 10/11 SDK.' }

$signtoolPath = if ($signtool -is [string]) { $signtool } else { $signtool.Source }
& $signtoolPath sign /fd SHA256 /s My /sha1 $expectedThumbprint $installerPath
if ($LASTEXITCODE -ne 0) { throw "Setup executable signing failed with exit code $LASTEXITCODE." }
& $signtoolPath verify /pa $installerPath
if ($LASTEXITCODE -ne 0) { throw "Setup executable signature verification failed with exit code $LASTEXITCODE." }

Write-Host "Created signed setup executable: $installerPath"
