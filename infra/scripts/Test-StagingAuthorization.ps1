[CmdletBinding()]
param(
    [Parameter(Mandatory)][guid]$ProbeId,
    [Parameter(Mandatory)][guid]$RecipientObjectId,
    [Parameter(Mandatory)][guid]$UserObjectId,
    [Parameter(Mandatory)][string]$EvidenceDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$subscriptionId = '455b906b-799c-4c50-9d04-b092ea12116d'
$tenantId = '56c9fa43-26a9-40e2-80b1-899780a2d78f'
$clientId = '5bc90f84-57b2-4487-866f-bc86cf96f671'
$deployerId = 'e5f3aecd-613d-4a2c-95b3-209ed796b6cf'
$scope = "/subscriptions/$subscriptionId/resourceGroups/Yask-Ciem-Staging"
if ($ProbeId -eq [guid]::Empty -or $RecipientObjectId -eq [guid]::Empty -or $UserObjectId -eq [guid]::Empty -or
    $RecipientObjectId -eq $UserObjectId -or $RecipientObjectId -eq [guid]$deployerId -or $UserObjectId -eq [guid]$deployerId -or
    $RecipientObjectId -eq [guid]'e5640711-5501-4b02-aad4-9986d495380b') {
    throw 'Use distinct nonempty probe, disposable recipient and user IDs; never use either workflow identity as recipient.'
}
$context = Get-AzContext -ErrorAction Stop
if ($context.Subscription.Id -ne $subscriptionId -or $context.Tenant.Id -ne $tenantId -or $context.Account.Id -ne $clientId) {
    throw 'Authorization probes must run as the exact deployment application in the approved subscription and tenant.'
}
$accessToken = Get-AzAccessToken -ResourceUrl 'https://management.azure.com/' -ErrorAction Stop
if ($accessToken.Token -isnot [Security.SecureString]) { throw 'A recent Az.Accounts returning a SecureString token is required.' }
$cases = @(
    @{ name = 'allow-blob-reader'; role = '2a2b9908-6ea1-4ae2-8e65-a410df84e7d1'; principal = $RecipientObjectId.ToString(); type = 'ServicePrincipal'; allow = $true },
    @{ name = 'allow-secrets-user'; role = '4633458b-17de-408a-b874-0445c86b69e6'; principal = $RecipientObjectId.ToString(); type = 'ServicePrincipal'; allow = $true },
    @{ name = 'allow-backup-writer'; role = 'c7976974-a096-5251-8baa-796d7ac0d4e3'; principal = $RecipientObjectId.ToString(); type = 'ServicePrincipal'; allow = $true },
    @{ name = 'allow-own-secrets-officer'; role = 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7'; principal = $deployerId; type = 'ServicePrincipal'; allow = $true },
    @{ name = 'deny-contributor'; role = 'b24988ac-6180-42a0-ab88-20f7382dd24c'; principal = $RecipientObjectId.ToString(); type = 'ServicePrincipal'; allow = $false },
    @{ name = 'deny-wrong-secrets-officer'; role = 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7'; principal = $RecipientObjectId.ToString(); type = 'ServicePrincipal'; allow = $false },
    @{ name = 'deny-user-data-role'; role = '2a2b9908-6ea1-4ae2-8e65-a410df84e7d1'; principal = $UserObjectId.ToString(); type = 'User'; allow = $false }
)
foreach ($case in $cases) {
    $hash = [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes("$ProbeId/$($case.name)"))
    $assignmentId = [guid]::new([byte[]]$hash[0..15]).ToString()
    $case.path = "$scope/providers/Microsoft.Authorization/roleAssignments/$assignmentId"
    $case.roleDefinitionId = "/subscriptions/$subscriptionId/providers/Microsoft.Authorization/roleDefinitions/$($case.role)"
}
New-Item -ItemType Directory -Path $EvidenceDirectory -ErrorAction Stop | Out-Null
$result = [ordered]@{
    probeId = $ProbeId.ToString()
    status = 'running'
    scope = $scope
    clientId = $clientId
    recipientObjectId = $RecipientObjectId.ToString()
    userObjectId = $UserObjectId.ToString()
    cleanupRequired = $true
    cases = $cases
    outcomes = @()
}
function Save-ProbeEvidence {
    $result | ConvertTo-Json -Depth 12 |
        Set-Content -LiteralPath (Join-Path $EvidenceDirectory 'authorization-probes.json') -Encoding utf8
}
function Invoke-ProbeRequest([string]$Method, [string]$Path, [string]$Payload) {
    $arguments = @{
        Method = $Method
        Uri = "https://management.azure.com${Path}?api-version=2022-04-01"
        Authentication = 'Bearer'
        Token = $accessToken.Token
        SkipHttpErrorCheck = $true
        ErrorAction = 'Stop'
    }
    if ($Payload) { $arguments.Body = $Payload; $arguments.ContentType = 'application/json' }
    Invoke-WebRequest @arguments
}
Save-ProbeEvidence
try {
    # Preflight every ID before any write; never update an existing assignment.
    foreach ($case in $cases) {
        $response = Invoke-ProbeRequest GET $case.path ''
        if ([int]$response.StatusCode -ne 404) {
            throw "Assignment preflight must return 404: $($case.name). Use a fresh probe ID only after administrator inspection."
        }
    }
    foreach ($case in $cases) {
        $payload = @{ properties = @{
            roleDefinitionId = $case.roleDefinitionId
            principalId = $case.principal
            principalType = $case.type
        } } | ConvertTo-Json -Depth 5 -Compress
        $response = Invoke-ProbeRequest PUT $case.path $payload
        $statusCode = [int]$response.StatusCode
        $errorCode = $null
        if ($statusCode -notin @(200, 201)) {
            $body = $response.Content | ConvertFrom-Json -ErrorAction Stop
            $errorCode = [string]$body.error.code
        }
        $passed = if ($case.allow) {
            $statusCode -in @(200, 201)
        } else {
            $statusCode -eq 403 -and $errorCode -in @('AuthorizationFailed', 'RoleAssignmentConditionNotSatisfied')
        }
        $result.outcomes += [ordered]@{ name = $case.name; statusCode = $statusCode; errorCode = $errorCode; passed = $passed }
        Save-ProbeEvidence
        if (-not $passed) {
            throw "Authorization probe failed: $($case.name), HTTP $statusCode. Stop and inspect evidence; administrator cleanup is required."
        }
        if ($case.allow) {
            $read = Invoke-ProbeRequest GET $case.path ''
            if ([int]$read.StatusCode -ne 200) { throw "Allowed assignment could not be read back: $($case.name)" }
            $assignment = ($read.Content | ConvertFrom-Json).properties
            if ($assignment.principalId -ne $case.principal -or $assignment.roleDefinitionId -ne $case.roleDefinitionId -or
                $assignment.principalType -ne $case.type -or $assignment.scope -ne $scope) {
                throw "Allowed assignment read-back mismatch: $($case.name)"
            }
        }
    }
    $result.status = 'passed-awaiting-administrator-cleanup'
}
catch {
    $result.status = 'failed-awaiting-administrator-inspection-and-cleanup'
    throw
}
finally {
    Save-ProbeEvidence
    Write-Output 'Authorization evidence saved. Administrator must inspect and remove exact test grants; this identity has no assignment-delete permission.'
}
