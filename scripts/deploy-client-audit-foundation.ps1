[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [Guid]$ClientSubscriptionId,
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$ParametersFile,
    [ValidateNotNullOrEmpty()]
    [string]$Location = 'eastus2',
    [switch]$Apply,
    [string]$ConfirmSubscriptionId
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$template = Join-Path $repositoryRoot 'infra\audit-foundation\main.bicep'
$parametersPath = if ([IO.Path]::IsPathRooted($ParametersFile)) { $ParametersFile } else { Join-Path $repositoryRoot $ParametersFile }

if (-not (Test-Path $template)) { throw "Audit foundation template not found: $template" }
if (-not (Test-Path $parametersPath)) { throw "Client parameter file not found: $parametersPath" }

$activeSubscription = & az account show --query id --output tsv --only-show-errors
if ($LASTEXITCODE -ne 0) { throw 'Azure CLI is not signed in. Sign in with the client-approved identity before running this script.' }
if ($activeSubscription.Trim() -ne $ClientSubscriptionId.ToString('D')) {
    throw "Selected Azure subscription '$($activeSubscription.Trim())' does not match the specified client subscription '$ClientSubscriptionId'. No deployment was attempted."
}

$deploymentArguments = @(
    '--subscription', $ClientSubscriptionId.ToString('D'),
    '--location', $Location,
    '--template-file', $template,
    '--parameters', "@$parametersPath"
)
& az deployment sub what-if @deploymentArguments --name 'yask-ciem-audit-foundation' --only-show-errors
if ($LASTEXITCODE -ne 0) { throw 'Audit foundation what-if failed; no deployment was attempted.' }

if (-not $Apply) {
    Write-Output 'Preview only. Re-run with -Apply and -ConfirmSubscriptionId set to the exact client subscription ID to deploy.'
    return
}

if ($ConfirmSubscriptionId -ne $ClientSubscriptionId.ToString('D')) {
    throw 'Apply requires -ConfirmSubscriptionId to exactly match the client subscription ID shown above.'
}

& az deployment sub create @deploymentArguments --name 'yask-ciem-audit-foundation' --only-show-errors
if ($LASTEXITCODE -ne 0) { throw 'Audit foundation deployment failed.' }
