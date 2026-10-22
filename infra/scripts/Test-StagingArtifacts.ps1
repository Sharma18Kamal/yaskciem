[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$infra = Split-Path -Parent $PSScriptRoot
if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    throw 'Azure CLI is required for Bicep validation.'
}
$bicep = Join-Path $infra 'main.bicep'
$diagnosticsPath = [IO.Path]::GetTempFileName()
try {
    $compiledText = & az bicep build --file $bicep --stdout 2> $diagnosticsPath
    $exitCode = $LASTEXITCODE
    $diagnostics = Get-Content -LiteralPath $diagnosticsPath -Raw
    if ($exitCode -ne 0 -or $diagnostics -match '(?im)(?:Error BCP\d+|Warning BCP(?!081\b)\d+)') {
        throw "Bicep validation failed ($exitCode): $diagnostics"
    }
    if (-not [string]::IsNullOrWhiteSpace($diagnostics)) {
        Write-Warning $diagnostics.Trim()
    }
}
finally {
    Remove-Item -LiteralPath $diagnosticsPath -Force
}
$template = ($compiledText | Out-String) | ConvertFrom-Json -Depth 100
$parameters = Get-Content -LiteralPath (Join-Path $infra 'main.parameters.json') -Raw | ConvertFrom-Json

function Assert-Artifact([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

Assert-Artifact ($parameters.parameters.location.value -eq 'centralus') 'The approved region must remain Central US.'
Assert-Artifact ($template.parameters.deployerPrincipalType.defaultValue -eq 'User') 'Interactive deployments must retain the User principal default.'
Assert-Artifact ('ServicePrincipal' -in $template.parameters.deployerPrincipalType.allowedValues) 'OIDC deployments must support a service principal.'
$resourceTypes = [Collections.Generic.List[string]]::new()
$resources = [Collections.Generic.List[object]]::new()
function Test-TemplateResources($TemplateNode) {
    foreach ($parameter in $TemplateNode.parameters.PSObject.Properties) {
        if ($parameter.Value.type -in @('secureString', 'secureObject')) {
            Assert-Artifact (-not $parameter.Value.PSObject.Properties['defaultValue']) "Secure parameter $($parameter.Name) must not have a default."
        }
    }
    foreach ($resource in $TemplateNode.resources) {
        $resourceTypes.Add($resource.type)
        $resources.Add($resource)
        if ($resource.type -eq 'Microsoft.Resources/deployments') {
            Test-TemplateResources $resource.properties.template
        }
    }
}
Test-TemplateResources $template
$deployerAssignments = @($resources | Where-Object {
    $_.type -eq 'Microsoft.Authorization/roleAssignments' -and
    $_.properties.principalId -eq "[parameters('deployerObjectId')]"
})
Assert-Artifact ($deployerAssignments.Count -eq 1 -and $deployerAssignments[0].properties.principalType -eq "[parameters('deployerPrincipalType')]") 'The Key Vault deployer role must use the selected principal type.'
foreach ($requiredType in @(
    'Microsoft.Compute/virtualMachines', 'Microsoft.Network/virtualNetworks',
    'Microsoft.Network/networkSecurityGroups', 'Microsoft.Network/publicIPAddresses',
    'Microsoft.Network/networkInterfaces', 'Microsoft.DBforPostgreSQL/flexibleServers',
    'Microsoft.DBforPostgreSQL/flexibleServers/databases', 'Microsoft.Storage/storageAccounts',
    'Microsoft.Storage/storageAccounts/blobServices/containers', 'Microsoft.KeyVault/vaults',
    'Microsoft.OperationalInsights/workspaces', 'Microsoft.Insights/components',
    'Microsoft.Authorization/roleAssignments'
)) {
    Assert-Artifact ($resourceTypes.Contains($requiredType)) "The approved resource is missing: $requiredType"
}
$vm = @($resources | Where-Object type -eq 'Microsoft.Compute/virtualMachines')
Assert-Artifact ($vm.Count -eq 1) 'Exactly one Windows VM must be present.'
Assert-Artifact ($vm[0].properties.storageProfile.osDisk.createOption -eq 'FromImage') 'The Windows OS disk must be created from its image.'
Assert-Artifact ($vm[0].properties.storageProfile.osDisk.diskSizeGB -eq 128) 'The approved OS disk must be 128 GiB.'
Assert-Artifact ($vm[0].properties.securityProfile.uefiSettings.secureBootEnabled -and $vm[0].properties.securityProfile.uefiSettings.vTpmEnabled) 'Trusted Launch controls must be enabled.'
$workspace = @($resources | Where-Object type -eq 'Microsoft.OperationalInsights/workspaces')
Assert-Artifact ($workspace.Count -eq 1 -and $workspace[0].properties.sku.name -eq 'PerGB2018') 'Log Analytics SKU must be set inside properties.'
$storage = @($resources | Where-Object type -eq 'Microsoft.Storage/storageAccounts')
Assert-Artifact ($storage.Count -eq 1 -and $storage[0].properties.allowSharedKeyAccess -eq $false -and $storage[0].properties.allowBlobPublicAccess -eq $false) 'Backup storage must disable shared keys and public blobs.'
Assert-Artifact ($storage[0].properties.networkAcls.defaultAction -eq 'Deny') 'Backup storage must restrict network access.'
$firewalls = @($resources | Where-Object type -eq 'Microsoft.DBforPostgreSQL/flexibleServers/firewallRules')
Assert-Artifact ($firewalls.Count -eq 1 -and $firewalls[0].properties.startIpAddress -eq '0.0.0.0' -and $firewalls[0].properties.endIpAddress -eq '0.0.0.0') 'The only database firewall rule must be the approved Azure-services rule, not a full-internet range.'
Assert-Artifact ($parameters.parameters.operatorIpv4.value -eq '') 'RDP must remain disabled in the checked-in parameters.'
$postgres = @($resources | Where-Object type -eq 'Microsoft.DBforPostgreSQL/flexibleServers')
Assert-Artifact ($postgres.Count -eq 1 -and $postgres[0].properties.version -eq '15') 'PostgreSQL must select the ARM-supported major version 15.'
foreach ($parameter in $parameters.parameters.PSObject.Properties) {
    Assert-Artifact ($null -ne $template.parameters.PSObject.Properties[$parameter.Name]) "Unknown parameter in main.parameters.json: $($parameter.Name)"
    Assert-Artifact ($template.parameters.($parameter.Name).type -notin @('secureString', 'secureObject')) "Secure parameter $($parameter.Name) must not be persisted in main.parameters.json."
}
foreach ($script in Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.ps1' -File) {
    $tokens = $null
    $parseErrors = $null
    [Management.Automation.Language.Parser]::ParseFile($script.FullName, [ref]$tokens, [ref]$parseErrors) | Out-Null
    Assert-Artifact ($parseErrors.Count -eq 0) "PowerShell syntax errors in $($script.Name): $($parseErrors.Message -join '; ')"
}
Write-Output 'Staging artifacts passed compilation, approved-resource coverage, parameter hygiene, and PowerShell syntax checks.'
Write-Output 'This is local validation only. No Azure provisioning, live sign-in, backup, restore, or load tests were performed.'
