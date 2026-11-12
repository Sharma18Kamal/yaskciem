[CmdletBinding()]
param(
    [uri]$ServerUrl = $( $configuredUrl = [Environment]::GetEnvironmentVariable('YASK_CIEM_API_BASE_URL', 'User'); if ($configuredUrl) { $configuredUrl } else { 'https://localhost:7443/' } ),
    [string]$CertificateThumbprint = $([Environment]::GetEnvironmentVariable('YASK_CIEM_LOCAL_TLS_THUMBPRINT', 'User'))
)

$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'The local server requires Windows. Run it under the Windows user who configured its Credential Manager secrets.'
}
$settings = Join-Path $env:LOCALAPPDATA 'YASK-CIEM\LocalLicenseServer\appsettings.Local.json'
if (-not (Test-Path -LiteralPath $settings -PathType Leaf)) {
    throw 'Run configure-local-license-server.ps1 first under this Windows user.'
}
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\CloudEntitle.LicenseServer\CloudEntitle.LicenseServer.csproj'
if ($ServerUrl.Scheme -ne 'https' -or $ServerUrl.UserInfo -or $ServerUrl.Query -or $ServerUrl.Fragment -or $ServerUrl.AbsolutePath -ne '/') {
    throw 'ServerUrl must be an HTTPS origin, for example https://susa-pal:7443/.'
}
$certificateArguments = @()
if (-not $ServerUrl.IsLoopback -or $CertificateThumbprint) {
    if ($CertificateThumbprint -notmatch '^[A-Fa-f0-9]{40}$') {
        throw 'Network HTTPS requires CertificateThumbprint for a dedicated trusted server certificate in CurrentUser\My.'
    }
    $certificate = Get-Item -LiteralPath "Cert:\CurrentUser\My\$CertificateThumbprint"
    if (-not $certificate.HasPrivateKey -or $certificate.NotAfter -le (Get-Date) -or $certificate.NotBefore -gt (Get-Date) -or
        $ServerUrl.DnsSafeHost -notin @($certificate.DnsNameList | ForEach-Object { $_.Unicode })) {
        throw 'The server certificate must have a private key, be currently valid and include the exact ServerUrl hostname in its DNS names.'
    }
    if (-not $certificate.Verify()) { throw 'The server certificate is not trusted by this Windows user.' }
    $subject = $certificate.GetNameInfo([Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false)
    $matches = @(Get-ChildItem Cert:\CurrentUser\My | Where-Object {
        $_.GetNameInfo([Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false) -eq $subject
    })
    if ($matches.Count -ne 1) { throw 'Use a uniquely named dedicated server certificate; its subject matches multiple certificates.' }
    $certificateArguments = @(
        "--Kestrel:Certificates:Default:Subject=$subject",
        '--Kestrel:Certificates:Default:Store=My',
        '--Kestrel:Certificates:Default:Location=CurrentUser',
        '--Kestrel:Certificates:Default:AllowInvalid=false'
    )
    Write-Warning 'Network hosting listens beyond loopback. Restrict inbound access to approved clients using Windows Firewall. No firewall rules are changed by this script.'
}
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:ASPNETCORE_URLS = $ServerUrl.AbsoluteUri.TrimEnd('/')
$env:DOTNET_ENVIRONMENT = 'Development'
& dotnet run --project $project --no-launch-profile -- --LocalSettingsPath=$settings @certificateArguments
if ($LASTEXITCODE -ne 0) { throw "Local License Server exited with code $LASTEXITCODE." }
