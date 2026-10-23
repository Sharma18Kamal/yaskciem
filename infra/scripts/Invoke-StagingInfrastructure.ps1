[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Preview', 'Validate', 'Deploy')]
    [string]$Phase,
    [Parameter(Mandatory)]
    [string]$TemplateFile,
    [Parameter(Mandatory)]
    [string]$EvidenceDirectory,
    [string]$ApprovedPreview
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$subscriptionId = '455b906b-799c-4c50-9d04-b092ea12116d'
$tenantId = '56c9fa43-26a9-40e2-80b1-899780a2d78f'
$resourceGroup = 'Yask-Ciem-Staging'
$stage = 'configuration'
$attempted = $false
$baseline = $null
$deploymentName = $null
$result = [ordered]@{
    status = 'failed'
    scope = 'Infrastructure only; application deployment and P2.3 acceptance are not performed.'
    subscriptionId = $subscriptionId
    resourceGroupName = $resourceGroup
    deploymentNames = @()
    deploymentAttempted = $false
    startedUtc = [DateTime]::UtcNow.ToString('o')
    inventoryVerified = $false
}
New-Item -ItemType Directory -Path $EvidenceDirectory -Force | Out-Null

function Save-Evidence([string]$Name, $Value) {
    ConvertTo-Json -InputObject $Value -Depth 30 |
        Set-Content -LiteralPath (Join-Path $EvidenceDirectory $Name) -Encoding utf8
}

function Get-Inventory {
    @(Get-AzResource -ErrorAction Stop | Sort-Object ResourceId | ForEach-Object {
        [ordered]@{
            id = $_.ResourceId.ToLowerInvariant()
            name = $_.Name
            type = $_.ResourceType
            resourceGroup = $_.ResourceGroupName
            location = $_.Location
        }
    })
}

function Get-SafeArmErrorCodes($ErrorNode) {
    if ($null -eq $ErrorNode) { return }
    foreach ($property in $ErrorNode.PSObject.Properties) {
        if ($property.Name -ieq 'code' -and [string]$property.Value -match '^[A-Za-z][A-Za-z0-9_.-]{0,100}$') {
            [string]$property.Value
        }
        elseif ($property.Name -in @('error', 'details', 'innererror')) {
            foreach ($child in @($property.Value)) { Get-SafeArmErrorCodes $child }
        }
    }
}

try {
    foreach ($name in @('YASK_VM_ADMIN_PASSWORD', 'YASK_POSTGRES_ADMIN_PASSWORD', 'YASK_DEPLOYER_OBJECT_ID')) {
        if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($name))) {
            throw "Missing required protected configuration: $name"
        }
    }
    $objectId = [guid]::Parse($env:YASK_DEPLOYER_OBJECT_ID).ToString()
    $context = Get-AzContext -ErrorAction Stop
    if ($context.Subscription.Id -ne $subscriptionId -or $context.Tenant.Id -ne $tenantId) {
        throw 'Azure context does not match the approved target.'
    }
    if ($Phase -eq 'Validate') {
        if ($objectId -ne 'e5f3aecd-613d-4a2c-95b3-209ed796b6cf' -or
            $context.Account.Id -ne '5bc90f84-57b2-4487-866f-bc86cf96f671') {
            throw 'Protected validation must use the approved deployment application and service principal.'
        }
    }
    $null = Get-AzResourceGroup -Name $resourceGroup -ErrorAction Stop
    $stage = 'provider-registration'
    foreach ($namespace in @(
        'Microsoft.Compute', 'Microsoft.Network', 'Microsoft.Storage', 'Microsoft.KeyVault',
        'Microsoft.DBforPostgreSQL', 'Microsoft.OperationalInsights', 'Microsoft.Insights'
    )) {
        $provider = Get-AzResourceProvider -ProviderNamespace $namespace -ErrorAction Stop
        if (-not @($provider | Where-Object RegistrationState -eq 'Registered')) {
            throw "Provider is not registered: $namespace"
        }
    }
    $stage = 'quota'
    $usage = @(Get-AzVMUsage -Location centralus -ErrorAction Stop)
    foreach ($quotaName in @('cores', 'standardDASv5Family')) {
        $quota = @($usage | Where-Object { $_.Name.Value -eq $quotaName })
        if ($quota.Count -ne 1 -or ($quota[0].Limit - $quota[0].CurrentValue) -lt 2) {
            throw "Insufficient or unknown VM quota: $quotaName"
        }
    }
    $stage = 'parameters'
    $infra = Split-Path -Parent $PSScriptRoot
    $parameterDocument = Get-Content -LiteralPath (Join-Path $infra 'main.parameters.json') -Raw |
        ConvertFrom-Json -AsHashtable
    $parameters = @{}
    foreach ($entry in $parameterDocument.parameters.GetEnumerator()) {
        $parameters[$entry.Key] = $entry.Value.value
    }
    $parameters.deployerObjectId = $objectId
    $parameters.deployerPrincipalType = 'ServicePrincipal'
    $parameters.deployedBy = 'GitHub Actions OIDC'
    $templateDefinition = Get-Content -LiteralPath $TemplateFile -Raw | ConvertFrom-Json
    foreach ($name in @('vmAdminPassword', 'postgresAdminPassword')) {
        if ($templateDefinition.parameters.$name.type -ine 'secureString') {
            throw "Credential parameter must be declared secureString: $name"
        }
    }
    # The what-if SDK cannot serialize SecureString instances. ARM security is
    # enforced by the template declaration; values remain in memory, never files.
    $parameters.vmAdminPassword = $env:YASK_VM_ADMIN_PASSWORD
    $parameters.postgresAdminPassword = $env:YASK_POSTGRES_ADMIN_PASSWORD
    $templateHash = (Get-FileHash -LiteralPath $TemplateFile -Algorithm SHA256).Hash
    # The input fingerprint detects configuration changes between protected environments without hashing secrets.
    $publicParameters = $parameters.Clone()
    $null = $publicParameters.Remove('vmAdminPassword')
    $null = $publicParameters.Remove('postgresAdminPassword')
    $publicJson = ($publicParameters.GetEnumerator() | Sort-Object Key | ForEach-Object {
        '{0}={1}' -f $_.Key, $_.Value
    }) -join "`n"
    $parameterHash = [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($publicJson))
    )
    $stage = 'baseline-inventory'
    $baseline = @(Get-Inventory)
    Save-Evidence 'inventory-before.json' $baseline
    if ($Phase -eq 'Validate') {
        $stage = 'template-validation'
        $result.validationLevel = 'Provider'
        $result.templateValidationSucceeded = $false
        $validationErrors = @(Test-AzSubscriptionDeployment -Location centralus -TemplateFile $TemplateFile `
            -TemplateParameterObject $parameters -ValidationLevel Provider -SkipTemplateParameterPrompt `
            -SuppressDiagnostics -WarningAction SilentlyContinue -InformationAction SilentlyContinue -ErrorAction Stop)
        if ($validationErrors.Count -gt 0) {
            $result.armErrorCodes = @($validationErrors | ForEach-Object { Get-SafeArmErrorCodes $_ } | Sort-Object -Unique)
            throw 'ARM template validation returned errors; inspect sanitized codes.'
        }
        $result.templateValidationSucceeded = $true
    }
    $stage = 'what-if'
    $validationLevel = if ($Phase -eq 'Preview') { 'ProviderNoRbac' } else { 'Provider' }
    $preview = Get-AzSubscriptionDeploymentWhatIfResult -Location centralus -TemplateFile $TemplateFile `
        -TemplateParameterObject $parameters -ValidationLevel $validationLevel -ErrorAction Stop
    $result.whatIfResponseTypes = @($preview | ForEach-Object { $_.GetType().FullName })
    $result.whatIfResponseProperties = @($preview.PSObject.Properties.Name)
    $result.whatIfStatus = if ([string]$preview.Status -match '^[A-Za-z]{1,40}$') { [string]$preview.Status } else { 'Unknown' }
    if ($preview.Status -ne 'Succeeded') {
        if ($preview.PSObject.Properties['Error']) {
            $result.armErrorCodes = @(Get-SafeArmErrorCodes $preview.Error | Sort-Object -Unique)
        }
        throw 'ARM what-if did not succeed.'
    }
    $changes = @($preview.Changes | Sort-Object FullyQualifiedResourceId | ForEach-Object {
        # Never persist Before/After or delta values; ARM responses can contain resolved credentials.
        [ordered]@{ resourceId = $_.FullyQualifiedResourceId; changeType = [string]$_.ChangeType }
    })
    Save-Evidence 'preview.json' ([ordered]@{
        templateSha256 = $templateHash
        publicParametersSha256 = $parameterHash
        changes = $changes
    })
    $existingGroupId = "/subscriptions/$subscriptionId/resourceGroups/$resourceGroup"
    $blockedChanges = @($changes | Where-Object {
        $_.changeType -notin @('Create', 'Modify', 'NoChange') -and
        -not ($_.changeType -eq 'Ignore' -and $_.resourceId -ieq $existingGroupId)
    })
    if ($blockedChanges.Count -gt 0) {
        throw 'Preview contains deletes, unsupported, or unresolved changes; manual review and a new preview are required.'
    }
    if ($Phase -eq 'Preview') {
        $result.status = 'preview-succeeded'
    }
    elseif ($Phase -eq 'Validate') {
        $result.status = 'validation-succeeded'
    }
    else {
        $stage = 'approved-preview'
        if (-not $ApprovedPreview) { throw 'A successful preview from this workflow run is required.' }
        $approved = Get-Content -LiteralPath $ApprovedPreview -Raw | ConvertFrom-Json
        if ($approved.templateSha256 -ne $templateHash -or $approved.publicParametersSha256 -ne $parameterHash) {
            throw 'Template or public configuration changed after preview.'
        }
        $expectedChanges = ConvertTo-Json -InputObject @($approved.changes) -Compress
        $actualChanges = ConvertTo-Json -InputObject $changes -Compress
        if ($expectedChanges -cne $actualChanges) {
            throw 'Resource change types changed after approval; run a new preview.'
        }
        $stage = 'deployment'
        $deploymentName = 'yask-staging-' + [guid]::NewGuid().ToString('N').Substring(0, 16)
        $result.deploymentNames = @($deploymentName)
        $attempted = $true
        $result.deploymentAttempted = $true
        Save-Evidence 'result.json' $result
        $deployment = New-AzSubscriptionDeployment -Name $deploymentName -Location centralus `
            -TemplateFile $TemplateFile -TemplateParameterObject $parameters `
            -SkipTemplateParameterPrompt -ErrorAction Stop
        if ($deployment.ProvisioningState -ne 'Succeeded') { throw 'ARM deployment did not succeed.' }
        $result.status = 'infrastructure-succeeded'
    }
}
catch {
    $result.failureStage = $stage
    $result.failureType = $_.Exception.GetType().FullName
    $result.failureSourceLine = $_.InvocationInfo.ScriptLineNumber
    $result.failureCategory = [string]$_.CategoryInfo.Category
    $summary = ($_.Exception.Message -split '[\r\n]', 2)[0]
    foreach ($secretName in @('YASK_VM_ADMIN_PASSWORD', 'YASK_POSTGRES_ADMIN_PASSWORD')) {
        $secretValue = [Environment]::GetEnvironmentVariable($secretName)
        if (-not [string]::IsNullOrEmpty($secretValue)) { $summary = $summary.Replace($secretValue, '[REDACTED]') }
    }
    # Only retain a bounded text heading, never JSON/request bodies or credential assignments.
    if ($summary -notmatch '[{}\[\]]|(?i)bearer |password\s*[:=]|token\s*[:=]|secret\s*[:=]') {
        $result.failureSummary = $summary.Substring(0, [Math]::Min(500, $summary.Length))
    }
    $result.failureId = if ($_.FullyQualifiedErrorId -match '^[A-Za-z0-9_.,-]{1,300}$') { $_.FullyQualifiedErrorId } else { 'Unclassified' }
    if (-not $result.Contains('armErrorCodes')) {
        $errorText = $_.Exception.Message
        if ($null -ne $_.ErrorDetails) { $errorText += $_.ErrorDetails.Message }
        $result.armErrorCodes = @(
            [regex]::Matches($errorText, '(?i)(?:"code"\s*:\s*"|\bCode:\s*)([A-Za-z][A-Za-z0-9_.-]{0,100})(?=["\s,}]|$)') |
                ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique
        )
    }
    # Keep raw SDK error records out of logs and artifacts; they may echo secure request parameters.
    Write-Warning "Staging $Phase failed at '$stage'. No automatic retry or cleanup will be performed."
    $result.status = 'failed'
}
finally {
    if ($null -ne $baseline) {
        try {
            $after = @(Get-Inventory)
            Save-Evidence 'inventory-after.json' $after
            $beforeIds = @($baseline | ForEach-Object { $_.id })
            Save-Evidence 'observed-new-resources.json' ([ordered]@{
                attribution = 'Unverified observations across the subscription, not a cleanup list. Other actors may have created these resources.'
                resources = @($after | Where-Object { $_.id -notin $beforeIds })
                limitations = 'Get-AzResource does not enumerate all child resources, role assignments or custom role definitions. No claim of exhaustive ARM operation attribution is made.'
            })
            $result.inventoryVerified = $true
        }
        catch {
            $result.status = 'failed'
            $result.inventoryVerified = $false
            $result.inventoryFailure = 'Post-run resource inventory failed; use the ARM deployment and portal for investigation.'
            Write-Warning 'Post-run inventory failed. Do not infer cleanup candidates.'
        }
    }
    $result.completedUtc = [DateTime]::UtcNow.ToString('o')
    Save-Evidence 'result.json' $result
    if ($env:GITHUB_STEP_SUMMARY) {
        @(
            "## Staging infrastructure: $($result.status)"
            "Phase: $Phase. Deployment attempted: $attempted."
            "Review the evidence artifact and the compiled template. Preview lists resource IDs/change types only, not property-level diffs."
            'Application installation, migrations, TLS, Entra sign-in, monitoring, backup and recovery acceptance remain separate work.'
        ) | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY
    }
}
if ($result.status -eq 'failed') {
    throw "Staging $Phase failed; inspect the sanitized result artifact and ARM deployment operations."
}
