[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$infra = Split-Path -Parent $PSScriptRoot
if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    throw 'Azure CLI is required for Bicep validation.'
}
function Get-CompiledTemplate([string]$FileName) {
    $diagnosticsPath = [IO.Path]::GetTempFileName()
    try {
        $compiledText = & az bicep build --file (Join-Path $infra $FileName) --stdout 2> $diagnosticsPath
        $exitCode = $LASTEXITCODE
        $diagnostics = Get-Content -LiteralPath $diagnosticsPath -Raw
        if ($exitCode -ne 0 -or $diagnostics -match '(?im)(?:Error BCP\d+|Warning BCP(?!081\b)\d+)') {
            throw "Bicep validation failed for $FileName ($exitCode): $diagnostics"
        }
        if (-not [string]::IsNullOrWhiteSpace($diagnostics)) {
            Write-Warning $diagnostics.Trim()
        }
        return ($compiledText | Out-String) | ConvertFrom-Json -Depth 100
    }
    finally {
        Remove-Item -LiteralPath $diagnosticsPath -Force
    }
}
$template = Get-CompiledTemplate 'main.bicep'
$bootstrap = Get-CompiledTemplate 'bootstrap.bicep'
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
Assert-Artifact (-not $resourceTypes.Contains('Microsoft.Authorization/roleDefinitions')) 'The application deployment must never create or modify custom role definitions.'
$bootstrapRoles = @($bootstrap.resources | Where-Object type -eq 'Microsoft.Authorization/roleDefinitions')
$accessModules = @($bootstrap.resources | Where-Object type -eq 'Microsoft.Resources/deployments')
Assert-Artifact ($accessModules.Count -eq 1 -and $accessModules[0].resourceGroup -eq "[parameters('resourceGroupName')]") 'Bootstrap must nest group access only in the approved resource group.'
$accessModule = $accessModules[0]
$accessTemplate = $accessModule.properties.template
$groupGrants = @($accessTemplate.resources)
$bootstrapAssignments = @($bootstrap.resources | Where-Object type -eq 'Microsoft.Authorization/roleAssignments') + $groupGrants
Assert-Artifact ($bootstrapRoles.Count -eq 3 -and $bootstrapAssignments.Count -eq 4 -and @($bootstrap.resources).Count -eq 6 -and $groupGrants.Count -eq 2) 'Bootstrap must contain only three administrator-managed roles and four deployer assignments.'
Assert-Artifact (@($bootstrap.parameters.resourceGroupName.allowedValues).Count -eq 1 -and $bootstrap.parameters.resourceGroupName.allowedValues[0] -ceq 'Yask-Ciem-Staging') 'Bootstrap must restrict the resource group to the approved staging group.'
$backupRole = @($bootstrapRoles | Where-Object { $_.properties.roleName -eq 'YASK staging immutable backup writer' })
$deploymentRole = @($bootstrapRoles | Where-Object { $_.properties.roleName -eq 'YASK staging subscription deployment submitter' })
$assignmentRole = @($bootstrapRoles | Where-Object { $_.properties.roleName -eq 'YASK staging role assignment writer' })
Assert-Artifact ($backupRole.Count -eq 1 -and $deploymentRole.Count -eq 1 -and $assignmentRole.Count -eq 1) 'Bootstrap role identities must be unique and stable.'
$expectedBackupActions = @(
    'Microsoft.Storage/storageAccounts/blobServices/containers/blobs/read',
    'Microsoft.Storage/storageAccounts/blobServices/containers/blobs/write',
    'Microsoft.Storage/storageAccounts/blobServices/containers/blobs/add/action'
)
Assert-Artifact (@($backupRole[0].properties.permissions).Count -eq 1 -and @($backupRole[0].properties.permissions[0].actions).Count -eq 0) 'Backup role must not grant control-plane actions.'
Assert-Artifact (@(Compare-Object $expectedBackupActions @($backupRole[0].properties.permissions[0].dataActions)).Count -eq 0) 'Backup role must retain its exact non-delete data permissions.'
$expectedDeploymentActions = @(
    'Microsoft.Resources/deployments/write',
    'Microsoft.Resources/deployments/validate/action',
    'Microsoft.Resources/deployments/whatIf/action'
)
Assert-Artifact (@($deploymentRole[0].properties.permissions).Count -eq 1 -and @(Compare-Object $expectedDeploymentActions @($deploymentRole[0].properties.permissions[0].actions)).Count -eq 0) 'Subscription deployment role must grant only submission, validation and what-if.'
Assert-Artifact (@($assignmentRole[0].properties.permissions).Count -eq 1 -and @($assignmentRole[0].properties.permissions[0].actions).Count -eq 1 -and $assignmentRole[0].properties.permissions[0].actions[0] -ceq 'Microsoft.Authorization/roleAssignments/write') 'Assignment writer must not grant delete, role-definition writes or wildcard actions.'
foreach ($role in @($deploymentRole[0], $assignmentRole[0])) {
    Assert-Artifact (@($role.properties.permissions[0].dataActions).Count -eq 0) 'Deployer control-plane roles must not grant data-plane access.'
}
Assert-Artifact (@($assignmentRole[0].properties.assignableScopes).Count -eq 1 -and $assignmentRole[0].properties.assignableScopes[0] -eq "[subscriptionResourceId('Microsoft.Resources/resourceGroups', parameters('resourceGroupName'))]") 'Assignment writer must be assignable only in the approved resource group.'
foreach ($assignment in $bootstrapAssignments) {
    Assert-Artifact ($assignment.properties.principalId -eq "[parameters('deployerObjectId')]" -and $assignment.properties.principalType -eq 'ServicePrincipal') 'Bootstrap must grant access only to the selected deployment service principal.'
}
foreach ($assignment in $groupGrants) {
    Assert-Artifact ($assignment.type -eq 'Microsoft.Authorization/roleAssignments' -and -not $assignment.PSObject.Properties['scope']) 'Group grants must inherit only the approved module scope.'
}
$conditionedGrant = @($groupGrants | Where-Object { $_.properties.PSObject.Properties['condition'] })
Assert-Artifact ($conditionedGrant.Count -eq 1 -and $conditionedGrant[0].properties.conditionVersion -eq '2.0') 'Exactly one assignment-writer grant must have a version 2.0 condition.'
Assert-Artifact ($conditionedGrant[0].properties.roleDefinitionId -eq "[parameters('assignmentWriterRoleId')]" -and $accessModule.properties.parameters.assignmentWriterRoleId.value -eq "[subscriptionResourceId('Microsoft.Authorization/roleDefinitions', guid(subscription().id, 'yask-staging-role-assignment-writer'))]") 'The condition must protect the role-assignment writer grant.'
Assert-Artifact ($conditionedGrant[0].properties.condition -eq "[variables('assignmentCondition')]") 'The restricted grant must use the reviewed condition expression.'
$contributorGrant = @($groupGrants | Where-Object { -not $_.properties.PSObject.Properties['condition'] })
Assert-Artifact ($contributorGrant.Count -eq 1 -and $contributorGrant[0].properties.roleDefinitionId -eq "[subscriptionResourceId('Microsoft.Authorization/roleDefinitions', variables('contributorRoleId'))]" -and $accessTemplate.variables.contributorRoleId -eq 'b24988ac-6180-42a0-ab88-20f7382dd24c') 'The only unconditioned group grant must be Contributor.'
$subscriptionGrants = @($bootstrap.resources | Where-Object type -eq 'Microsoft.Authorization/roleAssignments')
Assert-Artifact (@($subscriptionGrants | Where-Object { $_.properties.roleDefinitionId -eq "[subscriptionResourceId('Microsoft.Authorization/roleDefinitions', variables('readerRoleId'))]" }).Count -eq 1 -and $bootstrap.variables.readerRoleId -eq 'acdd72a7-3385-48ef-bd42-f606fba81ae7') 'Subscription read access must use Reader.'
Assert-Artifact (@($subscriptionGrants | Where-Object { $_.properties.roleDefinitionId -eq "[subscriptionResourceId('Microsoft.Authorization/roleDefinitions', guid(subscription().id, 'yask-staging-subscription-deployer'))]" }).Count -eq 1) 'The other subscription grant must use the submission-only custom role.'
$condition = $accessTemplate.variables.assignmentCondition
$conditionMatch = [regex]::Match($condition, "(?s)^\[format\('(?<body>(?:[^']|'')*)', variables\('secretsOfficerRoleId'\), parameters\('deployerObjectId'\), variables\('secretsUserRoleId'\), variables\('blobReaderRoleId'\), parameters\('backupWriterRoleId'\)\)\]$")
Assert-Artifact ($conditionMatch.Success) 'Condition must interpolate only the reviewed roles and principal parameters.'
$renderedCondition = [string]::Format(
    [Globalization.CultureInfo]::InvariantCulture,
    $conditionMatch.Groups['body'].Value.Replace("''", "'"),
    [object[]]@(
        $accessTemplate.variables.secretsOfficerRoleId, '11111111-1111-1111-1111-111111111111',
        $accessTemplate.variables.secretsUserRoleId, $accessTemplate.variables.blobReaderRoleId,
        '22222222-2222-2222-2222-222222222222'
    )
)
$expectedCondition = @'
(
  (!(ActionMatches{'Microsoft.Authorization/roleAssignments/write'}))
  OR
  (
    @Request[Microsoft.Authorization/roleAssignments:PrincipalType] StringEqualsIgnoreCase 'ServicePrincipal'
    AND
    (
      (
        @Request[Microsoft.Authorization/roleAssignments:RoleDefinitionId] GuidEquals b86a8fe4-44ce-4948-aee5-eccb2c155cd7
        AND
        @Request[Microsoft.Authorization/roleAssignments:PrincipalId] GuidEquals 11111111-1111-1111-1111-111111111111
      )
      OR
      (
        @Request[Microsoft.Authorization/roleAssignments:RoleDefinitionId] ForAnyOfAnyValues:GuidEquals {4633458b-17de-408a-b874-0445c86b69e6, 2a2b9908-6ea1-4ae2-8e65-a410df84e7d1, 22222222-2222-2222-2222-222222222222}
      )
    )
  )
)
'@
Assert-Artifact (($renderedCondition -replace '\s', '') -ceq ($expectedCondition -replace '\s', '')) 'Rendered condition must preserve the exact role/principal allowlist and boolean grouping.'
Assert-Artifact ($accessTemplate.variables.secretsOfficerRoleId -eq 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7' -and $accessTemplate.variables.secretsUserRoleId -eq '4633458b-17de-408a-b874-0445c86b69e6' -and $accessTemplate.variables.blobReaderRoleId -eq '2a2b9908-6ea1-4ae2-8e65-a410df84e7d1') 'The condition must use only the reviewed Key Vault and blob roles.'
Assert-Artifact ($accessModule.properties.parameters.backupWriterRoleId.value -eq $backupRole[0].name -and $accessModule.properties.parameters.deployerObjectId.value -eq "[parameters('deployerObjectId')]") 'Access condition must use the bootstrapped backup role and selected deployer.'
$vmModule = @($template.resources | Where-Object { $_.type -eq 'Microsoft.Resources/deployments' -and $_.name -eq 'windows-vm' })
Assert-Artifact ($vmModule.Count -eq 1 -and $vmModule[0].properties.parameters.backupWriterRoleDefinitionId.value -eq $backupRole[0].name) 'VM backup grant must reference the exact administrator-managed role ID.'
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
