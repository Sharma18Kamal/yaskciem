[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) ('yask-authorization-tests-' + [guid]::NewGuid().ToString('N'))
$runner = Join-Path $PSScriptRoot 'Test-StagingAuthorization.ps1'
$state = @{}
function Assert-Probe([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}
function Get-AzContext {
    [CmdletBinding()]param()
    @{
        Subscription = @{ Id = '455b906b-799c-4c50-9d04-b092ea12116d' }
        Tenant = @{ Id = '56c9fa43-26a9-40e2-80b1-899780a2d78f' }
        Account = @{ Id = $state.clientId }
    }
}
function Get-AzAccessToken {
    [CmdletBinding()]param($ResourceUrl)
    Assert-Probe ($ResourceUrl -ceq 'https://management.azure.com/') 'Token must target ARM.'
    @{ Token = ConvertTo-SecureString 'offline-token-not-for-live-use' -AsPlainText -Force }
}
function Invoke-WebRequest {
    [CmdletBinding()]param($Method, $Uri, $Authentication, $Token, [switch]$SkipHttpErrorCheck, $Body, $ContentType)
    Assert-Probe ($Authentication -ceq 'Bearer' -and $Token -is [Security.SecureString] -and $SkipHttpErrorCheck) 'REST requests must use a protected token and explicit HTTP status handling.'
    Assert-Probe ($Uri -like 'https://management.azure.com/subscriptions/455b906b-799c-4c50-9d04-b092ea12116d/resourceGroups/Yask-Ciem-Staging/providers/Microsoft.Authorization/roleAssignments/*?api-version=2022-04-01') 'Probe request escaped the fixed staging assignment scope.'
    if ($Method -eq 'GET') {
        if ($state.assignments.ContainsKey($Uri)) {
            $properties = $state.assignments[$Uri].Clone()
            $properties.scope = if ($state.mismatch) { '/wrong-scope' } else { '/subscriptions/455b906b-799c-4c50-9d04-b092ea12116d/resourceGroups/Yask-Ciem-Staging' }
            return @{ StatusCode = 200; Content = (@{ properties = $properties } | ConvertTo-Json) }
        }
        return @{ StatusCode = $state.preflight; Content = '{}' }
    }
    Assert-Probe ($Method -eq 'PUT' -and $ContentType -ceq 'application/json') 'Probe must not delete or perform other operations.'
    $state.writes++
    $request = $Body | ConvertFrom-Json -AsHashtable
    $properties = $request.properties
    $allow = $properties.principalType -eq 'ServicePrincipal' -and (
        $properties.roleDefinitionId -match '/(2a2b9908-6ea1-4ae2-8e65-a410df84e7d1|4633458b-17de-408a-b874-0445c86b69e6|c7976974-a096-5251-8baa-796d7ac0d4e3)$' -or
        ($properties.roleDefinitionId -match '/b86a8fe4-44ce-4948-aee5-eccb2c155cd7$' -and $properties.principalId -eq 'e5f3aecd-613d-4a2c-95b3-209ed796b6cf')
    )
    if ($state.transportFailure) { throw 'Mock network failure' }
    if ($allow -or $state.unexpectedAllow) {
        $state.assignments[$Uri] = $properties
        return @{ StatusCode = 201; Content = '{}' }
    }
    return @{ StatusCode = $state.denialStatus; Content = (@{ error = @{ code = $state.denialCode } } | ConvertTo-Json) }
}
try {
    New-Item -ItemType Directory -Path $root | Out-Null
    $probeId = [guid]::NewGuid()
    foreach ($scenario in @('success', 'unexpected-allow', 'unauthorized', 'server-error', 'unrelated-forbidden', 'preexisting', 'readback-mismatch', 'transport-failure', 'wrong-identity')) {
        $state.Clear()
        $state.clientId = '5bc90f84-57b2-4487-866f-bc86cf96f671'
        $state.assignments = @{}
        $state.writes = 0
        $state.preflight = 404
        $state.denialStatus = 403
        $state.denialCode = 'AuthorizationFailed'
        $state.unexpectedAllow = $scenario -eq 'unexpected-allow'
        $state.mismatch = $scenario -eq 'readback-mismatch'
        $state.transportFailure = $scenario -eq 'transport-failure'
        switch ($scenario) {
            'unauthorized' { $state.denialStatus = 401 }
            'server-error' { $state.denialStatus = 500 }
            'unrelated-forbidden' { $state.denialCode = 'RequestDisallowedByPolicy' }
            'preexisting' { $state.preflight = 200 }
            'wrong-identity' { $state.clientId = 'administrator-not-deployer' }
        }
        $directory = Join-Path $root $scenario
        $failed = $false
        try {
            & $runner -ProbeId $probeId -RecipientObjectId '33333333-3333-3333-3333-333333333333' `
                -UserObjectId '44444444-4444-4444-4444-444444444444' -EvidenceDirectory $directory | Out-Null
        }
        catch { $failed = $true }
        Assert-Probe ($failed -eq ($scenario -ne 'success')) "Unexpected scenario outcome: $scenario"
        if ($scenario -eq 'wrong-identity') {
            Assert-Probe ($state.writes -eq 0 -and -not (Test-Path $directory)) 'Wrong identity must stop before evidence or writes.'
        } else {
            $evidence = Get-Content -LiteralPath (Join-Path $directory 'authorization-probes.json') -Raw | ConvertFrom-Json
            Assert-Probe ($evidence.cleanupRequired -and $evidence.cases.Count -eq 7) 'Evidence must record all planned assignment IDs and cleanup obligation.'
            Assert-Probe (@($evidence.cases.path | Select-Object -Unique).Count -eq 7) 'Assignment IDs must be distinct.'
            Assert-Probe ($evidence.status -eq $(if ($failed) { 'failed-awaiting-administrator-inspection-and-cleanup' } else { 'passed-awaiting-administrator-cleanup' })) 'Failed or uncleaned probes must not appear complete.'
            if ($scenario -eq 'success') {
                Assert-Probe ($state.writes -eq 7 -and $evidence.outcomes.Count -eq 7) 'All seven probes must execute.'
                Assert-Probe (@($evidence.outcomes | Where-Object { -not $_.passed }).Count -eq 0) 'Successful run must have seven passing results.'
            }
            if ($scenario -eq 'preexisting') { Assert-Probe ($state.writes -eq 0) 'Preexisting assignment must stop all writes.' }
            if ($scenario -eq 'unexpected-allow') { Assert-Probe ($state.writes -eq 5) 'Unexpected privilege grant must stop immediately.' }
        }
        Write-Output "PASS: $scenario"
    }
    Write-Output 'All 9 offline authorization harness tests passed. No Azure calls were made.'
}
finally {
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
