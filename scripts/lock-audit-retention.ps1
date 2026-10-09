[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)]
    [Guid]$ClientSubscriptionId,
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$ResourceGroupName,
    [Parameter(Mandatory)]
    [ValidatePattern('^[a-z0-9]{3,24}$')]
    [string]$StorageAccountName,
    [Parameter(Mandatory)]
    [ValidatePattern('^[a-z0-9-]{3,63}$')]
    [string]$ContainerName,
    [ValidateRange(2555, 146000)]
    [int]$MinimumRetentionDays = 2555,
    [switch]$LockConfirmed
)

$ErrorActionPreference = 'Stop'

function Invoke-AzJson {
    param([Parameter(Mandatory)][string[]]$Arguments)
    $output = & az @Arguments --only-show-errors --output json
    if ($LASTEXITCODE -ne 0) { throw "Azure CLI failed: az $($Arguments -join ' ')" }
    return ($output | ConvertFrom-Json -Depth 20)
}

$account = Invoke-AzJson -Arguments @('account', 'show', '--subscription', $ClientSubscriptionId.ToString('D'))
if ($account.id -ne $ClientSubscriptionId.ToString('D')) {
    throw "The active Azure context did not resolve to the client subscription $ClientSubscriptionId."
}

$policy = Invoke-AzJson -Arguments @(
    'storage', 'container', 'immutability-policy', 'show',
    '--subscription', $ClientSubscriptionId.ToString('D'),
    '--resource-group', $ResourceGroupName,
    '--account-name', $StorageAccountName,
    '--container-name', $ContainerName,
    '--auth-mode', 'login'
)

if ([int]$policy.immutabilityPeriodSinceCreationInDays -lt $MinimumRetentionDays) {
    throw "Container policy retention ($($policy.immutabilityPeriodSinceCreationInDays) days) is below the required $MinimumRetentionDays days."
}
if (-not $policy.allowProtectedAppendWrites) {
    throw 'Protected append writes are not enabled. Refusing to lock a policy that would prevent audit appends.'
}
if ($policy.state -eq 'Locked') {
    Write-Output "Policy is already locked for $($policy.immutabilityPeriodSinceCreationInDays) days with protected append writes."
    return
}
if ($policy.state -ne 'Unlocked' -or [string]::IsNullOrWhiteSpace($policy.etag)) {
    throw "Expected an unlocked policy with an ETag; received state '$($policy.state)'."
}

$confirmation = "LOCK $($ClientSubscriptionId.ToString('D'))/$StorageAccountName/$ContainerName"
if (-not $LockConfirmed) {
    throw "Locking is irreversible. Review the policy, then rerun with -LockConfirmed and confirm: $confirmation"
}
if (-not $PSCmdlet.ShouldProcess($confirmation, 'Permanently lock Azure Blob retention policy')) { return }

$null = Invoke-AzJson -Arguments @(
    'storage', 'container', 'immutability-policy', 'lock',
    '--subscription', $ClientSubscriptionId.ToString('D'),
    '--resource-group', $ResourceGroupName,
    '--account-name', $StorageAccountName,
    '--container-name', $ContainerName,
    '--if-match', [string]$policy.etag
)

$lockedPolicy = Invoke-AzJson -Arguments @(
    'storage', 'container', 'immutability-policy', 'show',
    '--subscription', $ClientSubscriptionId.ToString('D'),
    '--resource-group', $ResourceGroupName,
    '--account-name', $StorageAccountName,
    '--container-name', $ContainerName,
    '--auth-mode', 'login'
)
if (
    $lockedPolicy.state -ne 'Locked' -or
    -not $lockedPolicy.allowProtectedAppendWrites -or
    [int]$lockedPolicy.immutabilityPeriodSinceCreationInDays -lt $MinimumRetentionDays
) {
    throw 'Post-lock verification failed. Keep the License Server readiness probe disabled and investigate the storage policy.'
}

Write-Output "Locked and verified $StorageAccountName/$ContainerName in client subscription $ClientSubscriptionId for $($lockedPolicy.immutabilityPeriodSinceCreationInDays) days."
