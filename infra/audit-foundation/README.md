# Client-Subscription Audit Storage

This stack targets the subscription selected by the deployment wrapper. It creates an Azure Storage account, a user-assigned managed identity, one audit container, a private endpoint, container-scoped Storage Blob Data Contributor, account-scoped Reader for readiness verification, and Blob diagnostics. It does not create resources in YASK's subscription.

## Prerequisites

- Sign in to Azure CLI as the client-approved deployment identity and select the client's subscription.
- Use an existing private-endpoint subnet in the client application VNet and its linked `privatelink.blob.core.windows.net` private DNS zone.
- Use an existing Log Analytics workspace in the client subscription.
- Confirm the selected region supports Standard ZRS (or select an approved supported SKU).
- Use a deployment identity authorized to create the resource group, storage account, private endpoint, managed identity, and role assignments.

Copy `client.parameters.example.bicepparam` to a client-specific parameter file and replace every example subscription/resource ID and name. Never commit client identifiers or secrets. Deploy with:

```powershell
./scripts/deploy-client-audit-foundation.ps1 `
  -ClientSubscriptionId '<client-subscription-guid>' `
  -ParametersFile '<client-parameters-file>' `
  -Location '<region>'
```

This runs a subscription-scoped `what-if` only. Review it, then apply with both `-Apply` and `-ConfirmSubscriptionId '<same-client-subscription-guid>'`. The wrapper checks that the current Azure CLI account is on the specified subscription before either operation.

## Lock Retention

The deployed time-based immutability policy starts unlocked so its retention period and append permission can be reviewed. Verify the Blob private endpoint, DNS resolution, managed-identity role assignments, and diagnostics, then permanently lock it:

```powershell
./scripts/lock-audit-retention.ps1 `
  -ClientSubscriptionId '<client-subscription-guid>' `
  -ResourceGroupName '<audit-resource-group>' `
  -StorageAccountName '<storage-account>' `
  -ContainerName 'audit-events' `
  -MinimumRetentionDays 2555 `
  -LockConfirmed
```

Locking is irreversible except permitted retention extensions. The script verifies subscription, current policy retention, and protected append writes before requesting confirmation and verifies `Locked` afterward. The API refuses production readiness until that verification succeeds.

## API Configuration

Configure the License Server host with the Bicep outputs and a secret-provider reference:

- `AuditRetention__Provider=AzureBlob`
- `AuditRetention__BlobServiceUri=<blobServiceUri output>`
- `AuditRetention__ContainerName=<auditContainerName output>`
- `AuditRetention__StorageAccountResourceId=<storageAccountResourceId output>`
- `AuditRetention__ManagedIdentityClientId=<managedIdentityClientId output>`
- `AuditRetention__MinimumRetentionDays=2555`
- `AuditRetention__IntegrityKey=<Base64 value from the host's secret provider, at least 32 random bytes>`

Attach the user-assigned identity to the License Server host and configure it for private VNet access to the Blob private endpoint. Do not configure storage keys or connection strings. If the policy, network access, shared-key setting, retention, or protected-append configuration cannot be verified, `/api/v1/readiness` returns HTTP 503 and audit append/read operations fail closed.
