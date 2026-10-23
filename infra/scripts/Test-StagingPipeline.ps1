[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$PSModuleAutoLoadingPreference = 'None'
$runner = Join-Path $PSScriptRoot 'Invoke-StagingInfrastructure.ps1'
$root = Join-Path ([IO.Path]::GetTempPath()) ('yask-pipeline-test-' + [guid]::NewGuid().ToString('N'))
$savedEnvironment = @{}
foreach ($name in @(
    'YASK_VM_ADMIN_PASSWORD', 'YASK_POSTGRES_ADMIN_PASSWORD', 'YASK_DEPLOYER_OBJECT_ID', 'GITHUB_STEP_SUMMARY'
)) {
    $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name)
}
$state = @{}
function Assert-Test([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}
function Get-AzContext {
    [CmdletBinding()]param()
    [pscustomobject]@{
        Subscription = @{ Id = $state.subscription }
        Tenant = @{ Id = '56c9fa43-26a9-40e2-80b1-899780a2d78f' }
        Account = @{ Id = $state.clientId }
    }
}
function Get-AzResourceGroup {
    [CmdletBinding()]param($Name)
    [pscustomobject]@{ ResourceGroupName = $Name }
}
function Get-AzResourceProvider {
    [CmdletBinding()]param($ProviderNamespace)
    [pscustomobject]@{ RegistrationState = $state.providerState }
}
function Get-AzVMUsage {
    [CmdletBinding()]param($Location)
    foreach ($name in @('cores', 'standardDASv5Family')) {
        [pscustomobject]@{ Name = @{ Value = $name }; Limit = $state.quota; CurrentValue = 0 }
    }
}
function Get-AzResource {
    [CmdletBinding()]param()
    $state.inventoryCalls++
    if ($state.inventoryCalls -eq $state.inventoryFailureCall) { throw 'Mock inventory failure' }
    if ($state.inventoryCalls -gt 1 -and $state.deploymentCalls -gt 0) {
        [pscustomobject]@{
            ResourceId = '/subscriptions/test/resourceGroups/Yask-Ciem-Staging/providers/Test/resource'
            Name = 'resource'; ResourceType = 'Test'; ResourceGroupName = 'Yask-Ciem-Staging'; Location = 'centralus'
        }
    }
}
function Get-AzSubscriptionDeploymentWhatIfResult {
    [CmdletBinding()]param($Location, $TemplateFile, $TemplateParameterObject, $ValidationLevel)
    Assert-Test ($TemplateParameterObject.vmAdminPassword -ceq $env:YASK_VM_ADMIN_PASSWORD) 'VM password must be passed in memory to the SDK.'
    Assert-Test ($TemplateParameterObject.postgresAdminPassword -ceq $env:YASK_POSTGRES_ADMIN_PASSWORD) 'Database password must be passed in memory to the SDK.'
    Assert-Test ($TemplateParameterObject.deployerPrincipalType -eq 'ServicePrincipal') 'OIDC principal type was not wired.'
    $state.previewCalls++
    $state.validationLevel = $ValidationLevel
    [pscustomobject]@{
        Status = $state.previewStatus
        Error = [pscustomobject]@{
            Code = 'InvalidTemplateDeployment'
            Message = $env:YASK_POSTGRES_ADMIN_PASSWORD
            Details = @([pscustomobject]@{ Code = 'RequestDisallowedByPolicy'; Message = $env:YASK_VM_ADMIN_PASSWORD })
        }
        Changes = @([pscustomobject]@{
            FullyQualifiedResourceId = $state.resourceId
            ChangeType = $state.changeType
            Before = @{ secret = $env:YASK_VM_ADMIN_PASSWORD }
            After = @{ secret = $env:YASK_POSTGRES_ADMIN_PASSWORD }
        })
    }
}
function New-AzSubscriptionDeployment {
    [CmdletBinding()]param($Name, $Location, $TemplateFile, $TemplateParameterObject, [switch]$SkipTemplateParameterPrompt)
    $state.deploymentCalls++
    if ($state.deploymentFailure) { throw "Mock SDK secret: $env:YASK_POSTGRES_ADMIN_PASSWORD" }
    [pscustomobject]@{ ProvisioningState = 'Succeeded' }
}
function Test-AzSubscriptionDeployment {
    [CmdletBinding()]param($Location, $TemplateFile, $TemplateParameterObject, $ValidationLevel,
        [switch]$SkipTemplateParameterPrompt, [switch]$SuppressDiagnostics)
    $state.validationCalls++
    Assert-Test ($Location -eq 'centralus' -and $ValidationLevel -eq 'Provider') 'Validation must use Provider-level checks in Central US.'
    Assert-Test ($SkipTemplateParameterPrompt -and $SuppressDiagnostics) 'Validation must suppress prompts and provider diagnostics.'
    Assert-Test ($TemplateParameterObject.vmAdminPassword -ceq $env:YASK_VM_ADMIN_PASSWORD -and
        $TemplateParameterObject.postgresAdminPassword -ceq $env:YASK_POSTGRES_ADMIN_PASSWORD) 'Validation must pass the actual protected parameters in memory.'
    if ($state.validationThrows) { throw "Mock validation SDK secret: $env:YASK_VM_ADMIN_PASSWORD" }
    if ($state.validationErrors) {
        [pscustomobject]@{ Code = 'InvalidTemplateDeployment'; Message = $env:YASK_POSTGRES_ADMIN_PASSWORD
            Details = @([pscustomobject]@{ Code = 'RequestDisallowedByPolicy'; Message = $env:YASK_VM_ADMIN_PASSWORD }) }
    }
}

function Invoke-Case([string]$Name, [string]$Phase, [bool]$ShouldFail, [hashtable]$Overrides = @{}, [string]$Approval) {
    $state.Clear()
    foreach ($pair in @{
        subscription = '455b906b-799c-4c50-9d04-b092ea12116d'
        providerState = 'Registered'; quota = 20; inventoryCalls = 0; inventoryFailureCall = -1
        deploymentCalls = 0; previewCalls = 0; deploymentFailure = $false; changeType = 'Create'; previewStatus = 'Succeeded'
        resourceId = '/subscriptions/test/resource'
        clientId = '5bc90f84-57b2-4487-866f-bc86cf96f671'
        validationCalls = 0; validationThrows = $false; validationErrors = $false
    }.GetEnumerator()) { $state[$pair.Key] = $pair.Value }
    foreach ($pair in $Overrides.GetEnumerator()) { $state[$pair.Key] = $pair.Value }
    $directory = Join-Path $root $Name
    $failed = $false
    $output = ''
    try {
        $output = (& $runner -Phase $Phase -TemplateFile $template -EvidenceDirectory $directory -ApprovedPreview $Approval *>&1 | Out-String)
    }
    catch {
        $failed = $true
        $output += $_.ToString()
    }
    Assert-Test ($failed -eq $ShouldFail) "Unexpected outcome: $Name"
    $result = Get-Content -LiteralPath (Join-Path $directory 'result.json') -Raw | ConvertFrom-Json
    foreach ($file in Get-ChildItem -LiteralPath $directory -File) {
        $output += Get-Content -LiteralPath $file.FullName -Raw
    }
    Assert-Test (-not $output.Contains($env:YASK_VM_ADMIN_PASSWORD)) "VM secret leaked in $Name"
    Assert-Test (-not $output.Contains($env:YASK_POSTGRES_ADMIN_PASSWORD)) "Database secret leaked in $Name"
    Write-Host "PASS: $Name"
    return $result
}

try {
    $infra = Split-Path -Parent $PSScriptRoot
    $workflow = Get-Content -LiteralPath (Join-Path (Split-Path -Parent $infra) '.github\workflows\staging.yml') -Raw
    Assert-Test ($workflow -match '(?m)^      validate_only:\r?\n(?:.*\r?\n){1,3}        default: false') 'Validation-only mode must be opt-in.'
    Assert-Test ($workflow.Contains("if: inputs.provision && !inputs.validate_only")) 'Validation-only mode must exclude the provision job.'
    Assert-Test ($workflow.Contains("`$env:PROVISION -eq 'true' -and `$env:VALIDATE_ONLY -eq 'true'")) 'Workflow must reject conflicting dispatch modes before processing secrets.'
    Assert-Test ($workflow.Contains("`$env:PROVISIONING_ENABLED -cne 'false'")) 'Validation-only dispatch must require provisioning explicitly disabled.'
    Assert-Test ($workflow.Contains("`$phase = if (`$env:VALIDATE_ONLY -eq 'true') { 'Validate' } else { 'Preview' }")) 'Validation-only input must select the non-deploying runner phase.'
    $role = Get-Content -LiteralPath (Join-Path $infra 'github-preview-role.json') -Raw | ConvertFrom-Json
    Assert-Test (@($role.Actions).Count -eq 2 -and '*/read' -in $role.Actions -and 'Microsoft.Resources/deployments/whatIf/action' -in $role.Actions) 'Preview role must not grant deployment or RBAC writes.'
    Assert-Test (@($role.DataActions).Count -eq 0) 'Preview role must not grant secret/data access.'
    $federation = Get-Content -LiteralPath (Join-Path $infra 'github-preview-federation.json') -Raw | ConvertFrom-Json
    Assert-Test ($federation.subject -ceq 'repo:Sharma18Kamal@317556502/yaskciem@1387494170:environment:staging-preview') 'Federation must be preview-only and bind immutable repository/owner IDs.'
    Assert-Test ($federation.issuer -ceq 'https://token.actions.githubusercontent.com' -and @($federation.audiences).Count -eq 1 -and $federation.audiences[0] -ceq 'api://AzureADTokenExchange') 'Federation issuer and audience must be exact.'
    New-Item -ItemType Directory -Path $root | Out-Null
    $template = Join-Path $root 'template.json'
    '{"parameters":{"vmAdminPassword":{"type":"securestring"},"postgresAdminPassword":{"type":"securestring"}}}' | Set-Content -LiteralPath $template
    $env:YASK_VM_ADMIN_PASSWORD = 'mock-vm-secret-not-for-deployment'
    $env:YASK_POSTGRES_ADMIN_PASSWORD = 'mock-postgres-secret-not-for-deployment'
    $env:YASK_DEPLOYER_OBJECT_ID = '11111111-1111-1111-1111-111111111111'
    $env:GITHUB_STEP_SUMMARY = ''

    $null = Invoke-Case 'preview' Preview $false
    Assert-Test ($state.deploymentCalls -eq 0) 'Preview must never deploy.'
    Assert-Test ($state.validationLevel -eq 'ProviderNoRbac') 'Preview must not require deployment write permissions.'
    $emptyInventory = Get-Content -LiteralPath (Join-Path $root 'preview\inventory-before.json') -Raw
    Assert-Test ($emptyInventory.Trim() -eq '[]') 'Empty inventory must be a JSON array.'
    $approval = Join-Path $root 'preview\preview.json'
    $result = Invoke-Case 'deploy' Deploy $false @{} $approval
    Assert-Test ($state.validationLevel -eq 'Provider') 'Deployment must check resource write permissions.'
    Assert-Test ($result.status -eq 'infrastructure-succeeded' -and $state.deploymentCalls -eq 1) 'Successful deployment evidence is missing.'
    Assert-Test ($result.deploymentNames.Count -eq 1 -and $state.inventoryCalls -eq 2) 'Actual attempt and inventories must be recorded.'
    $null = Invoke-Case 'delete' Deploy $true @{ changeType = 'Delete' } $approval
    Assert-Test ($state.deploymentCalls -eq 0) 'Delete preview must block deployment.'
    $null = Invoke-Case 'unresolved' Deploy $true @{ changeType = 'Ignore' } $approval
    Assert-Test ($state.deploymentCalls -eq 0) 'Unresolved preview must block deployment.'
    $null = Invoke-Case 'drift' Deploy $true @{ changeType = 'Modify' } $approval
    Assert-Test ($state.deploymentCalls -eq 0) 'Changed preview must block deployment.'
    $null = Invoke-Case 'wrong-subscription' Deploy $true @{ subscription = 'wrong' } $approval
    Assert-Test ($state.previewCalls -eq 0 -and $state.deploymentCalls -eq 0) 'Wrong subscription must fail closed.'
    $null = Invoke-Case 'provider' Deploy $true @{ providerState = 'NotRegistered' } $approval
    Assert-Test ($state.deploymentCalls -eq 0) 'Unregistered provider must block deployment.'
    $null = Invoke-Case 'quota' Deploy $true @{ quota = 0 } $approval
    Assert-Test ($state.deploymentCalls -eq 0) 'Insufficient quota must block deployment.'
    $null = Invoke-Case 'baseline-failure' Deploy $true @{ inventoryFailureCall = 1 } $approval
    Assert-Test ($state.previewCalls -eq 0 -and $state.deploymentCalls -eq 0) 'Baseline failure must block preview and deployment.'
    $result = Invoke-Case 'deployment-failure' Deploy $true @{ deploymentFailure = $true } $approval
    Assert-Test ($result.deploymentAttempted -and $result.inventoryVerified) 'Failed deployment must still capture inventory.'
    $result = Invoke-Case 'capture-failure' Deploy $true @{ inventoryFailureCall = 2 } $approval
    Assert-Test (-not $result.inventoryVerified -and $result.status -eq 'failed') 'Failed inventory must not report success.'
    $null = Invoke-Case 'missing-approval' Deploy $true
    Assert-Test ($state.deploymentCalls -eq 0) 'Missing approved preview must block deployment.'
    $env:YASK_DEPLOYER_OBJECT_ID = '22222222-2222-2222-2222-222222222222'
    $null = Invoke-Case 'configuration-change' Deploy $true @{} $approval
    Assert-Test ($state.deploymentCalls -eq 0) 'Changed public configuration must block deployment.'
    $env:YASK_DEPLOYER_OBJECT_ID = '11111111-1111-1111-1111-111111111111'
    '{"changed":true,"parameters":{"vmAdminPassword":{"type":"secureString"},"postgresAdminPassword":{"type":"secureString"}}}' | Set-Content -LiteralPath $template
    $null = Invoke-Case 'template-change' Deploy $true @{} $approval
    Assert-Test ($state.deploymentCalls -eq 0) 'Changed template must block deployment.'
    $result = Invoke-Case 'arm-failure' Preview $true @{ previewStatus = 'Failed' }
    Assert-Test ('RequestDisallowedByPolicy' -in $result.armErrorCodes -and 'InvalidTemplateDeployment' -in $result.armErrorCodes) 'Sanitized ARM error codes must be preserved without messages.'
    $null = Invoke-Case 'existing-group-ignore' Preview $false @{
        changeType = 'Ignore'
        resourceId = '/subscriptions/455b906b-799c-4c50-9d04-b092ea12116d/resourceGroups/Yask-Ciem-Staging'
    }
    $null = Invoke-Case 'other-group-ignore' Preview $true @{
        changeType = 'Ignore'
        resourceId = '/subscriptions/455b906b-799c-4c50-9d04-b092ea12116d/resourceGroups/Other'
    }
    $env:YASK_DEPLOYER_OBJECT_ID = 'e5f3aecd-613d-4a2c-95b3-209ed796b6cf'
    $result = Invoke-Case 'validate' Validate $false
    Assert-Test ($state.validationCalls -eq 1 -and $state.previewCalls -eq 1 -and $state.deploymentCalls -eq 0) 'Validation must validate and preview exactly once, never deploy.'
    Assert-Test ($state.validationLevel -eq 'Provider' -and $result.templateValidationSucceeded -and
        $result.status -eq 'validation-succeeded' -and -not $result.deploymentAttempted -and
        $result.deploymentNames.Count -eq 0 -and $result.inventoryVerified) 'Validation success evidence must distinguish validation from deployment.'
    $result = Invoke-Case 'validation-errors' Validate $true @{ validationErrors = $true }
    Assert-Test ($state.previewCalls -eq 0 -and $state.deploymentCalls -eq 0 -and -not $result.templateValidationSucceeded) 'Validation error objects must block what-if and deployment.'
    Assert-Test ('RequestDisallowedByPolicy' -in $result.armErrorCodes) 'Validation must retain sanitized nested policy codes, not error messages.'
    $result = Invoke-Case 'validation-throws' Validate $true @{ validationThrows = $true }
    Assert-Test ($state.previewCalls -eq 0 -and $state.deploymentCalls -eq 0) 'Thrown validation failures must block further ARM operations.'
    $null = Invoke-Case 'validation-wrong-client' Validate $true @{ clientId = 'administrator-not-deployer' }
    Assert-Test ($state.validationCalls -eq 0 -and $state.previewCalls -eq 0) 'Validation must reject the wrong client before any ARM validation request.'
    $env:YASK_DEPLOYER_OBJECT_ID = '11111111-1111-1111-1111-111111111111'
    $null = Invoke-Case 'validation-wrong-principal' Validate $true
    Assert-Test ($state.validationCalls -eq 0 -and $state.previewCalls -eq 0) 'Validation must reject the wrong service principal.'
    $env:YASK_DEPLOYER_OBJECT_ID = 'e5f3aecd-613d-4a2c-95b3-209ed796b6cf'
    $null = Invoke-Case 'validation-what-if-fails' Validate $true @{ previewStatus = 'Failed' }
    Assert-Test ($state.validationCalls -eq 1 -and $state.deploymentCalls -eq 0) 'Successful validation must not hide a failed what-if.'
    $result = Invoke-Case 'validation-inventory-fails' Validate $true @{ inventoryFailureCall = 2 }
    Assert-Test ($result.status -eq 'failed' -and -not $result.inventoryVerified -and $state.deploymentCalls -eq 0) 'Validation must fail closed on missing post-run inventory.'
    '{"parameters":{"vmAdminPassword":{"type":"string"},"postgresAdminPassword":{"type":"secureString"}}}' | Set-Content -LiteralPath $template
    $null = Invoke-Case 'validation-insecure-template' Validate $true
    Assert-Test ($state.validationCalls -eq 0 -and $state.previewCalls -eq 0 -and $state.deploymentCalls -eq 0) 'Validation must reject insecure secret declarations before any ARM validation request.'
    $null = Invoke-Case 'insecure-template' Preview $true
    Assert-Test ($state.previewCalls -eq 0 -and $state.deploymentCalls -eq 0) 'Insecure credential declarations must block all ARM requests.'
    Write-Host 'All 26 offline staging pipeline tests passed. No Azure calls were made.'
}
finally {
    foreach ($entry in $savedEnvironment.GetEnumerator()) {
        [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value)
    }
    Remove-Item -LiteralPath $root -Recurse -Force
}
