targetScope = 'subscription'

@description('Resource group to create within the explicitly selected client subscription.')
param resourceGroupName string

@description('Azure region for the audit resource group, account, identity, and private endpoint.')
param location string

@description('Globally unique, lowercase Azure Storage account name (3-24 letters or digits).')
@minLength(3)
@maxLength(24)
param storageAccountName string

@description('Name for the user-assigned identity used by the License Server.')
param managedIdentityName string

@description('Private Endpoint subnet in the client application VNet.')
param privateEndpointSubnetResourceId string

@description('Existing privatelink.blob.core.windows.net DNS zone linked to the client application VNet.')
param blobPrivateDnsZoneResourceId string

@description('Existing Log Analytics workspace resource ID for Storage data-plane diagnostics.')
param logAnalyticsWorkspaceResourceId string

@description('Audit container name.')
param auditContainerName string = 'audit-events'

@description('Container-level WORM retention period. Locking the policy is irreversible except for permitted extensions.')
@minValue(2555)
@maxValue(146000)
param retentionDays int = 2555

@description('Storage redundancy. Standard_ZRS requires an availability-zone supporting region.')
@allowed([
  'Standard_ZRS'
  'Standard_GZRS'
])
param storageSku string = 'Standard_ZRS'

@description('Resource tags applied to the client-owned audit foundation.')
param tags object = {}

resource auditResourceGroup 'Microsoft.Resources/resourceGroups@2025-04-01' = {
  name: resourceGroupName
  location: location
  tags: union(tags, {
    workload: 'yask-ciem-central-audit'
    dataClassification: 'audit'
  })
}

module auditIdentity 'br/public:avm/res/managed-identity/user-assigned-identity:0.6.0' = {
  scope: auditResourceGroup
  params: {
    name: managedIdentityName
    location: location
    enableTelemetry: false
    tags: tags
  }
}

module auditStorage 'br/public:avm/res/storage/storage-account:0.33.1' = {
  scope: auditResourceGroup
  params: {
    name: storageAccountName
    location: location
    kind: 'StorageV2'
    skuName: storageSku
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    defaultToOAuthAuthentication: true
    publicNetworkAccess: 'Disabled'
    supportsHttpsTrafficOnly: true
    minimumTlsVersion: 'TLS1_2'
    requireInfrastructureEncryption: true
    enableTelemetry: false
    lock: {
      kind: 'CanNotDelete'
      name: 'protect-audit-storage-account'
      notes: 'Prevents accidental storage account deletion; immutable audit blobs have independent retention protection.'
    }
    networkAcls: {
      bypass: 'None'
      defaultAction: 'Deny'
    }
    roleAssignments: [
      {
        name: guid(storageAccountName, auditIdentity.outputs.principalId, 'storage-account-reader')
        principalId: auditIdentity.outputs.principalId
        principalType: 'ServicePrincipal'
        roleDefinitionIdOrName: 'Reader'
        description: 'Allows the audit API to verify storage security configuration during readiness checks.'
      }
    ]
    blobServices: {
      isVersioningEnabled: false
      deleteRetentionPolicyEnabled: true
      deleteRetentionPolicyDays: 14
      containerDeleteRetentionPolicyEnabled: true
      containerDeleteRetentionPolicyDays: 14
      diagnosticSettings: [
        {
          name: 'audit-blob-diagnostics'
          workspaceResourceId: logAnalyticsWorkspaceResourceId
          logCategoriesAndGroups: [
            {
              categoryGroup: 'allLogs'
            }
          ]
          metricCategories: [
            {
              category: 'Transaction'
            }
          ]
        }
      ]
      containers: [
        {
          name: auditContainerName
          publicAccess: 'None'
          immutabilityPolicy: {
            immutabilityPeriodSinceCreationInDays: retentionDays
            allowProtectedAppendWrites: true
          }
          roleAssignments: [
            {
              name: guid(storageAccountName, auditContainerName, auditIdentity.outputs.principalId, 'blob-data-contributor')
              principalId: auditIdentity.outputs.principalId
              principalType: 'ServicePrincipal'
              roleDefinitionIdOrName: 'Storage Blob Data Contributor'
              description: 'Scoped to the audit container; locked WORM protects existing records while allowing append blobs.'
            }
          ]
        }
      ]
    }
    privateEndpoints: [
      {
        name: '${storageAccountName}-blob-pe'
        service: 'blob'
        subnetResourceId: privateEndpointSubnetResourceId
        privateDnsZoneGroup: {
          privateDnsZoneGroupConfigs: [
            {
              privateDnsZoneResourceId: blobPrivateDnsZoneResourceId
            }
          ]
        }
      }
    ]
    tags: union(tags, {
      workload: 'yask-ciem-central-audit'
      immutableAudit: 'true'
    })
  }
}

output targetSubscriptionId string = subscription().subscriptionId
output resourceGroupName string = auditResourceGroup.name
output storageAccountName string = auditStorage.outputs.name
output storageAccountResourceId string = auditStorage.outputs.resourceId
output blobServiceUri string = auditStorage.outputs.primaryBlobEndpoint
output auditContainerName string = auditContainerName
output managedIdentityClientId string = auditIdentity.outputs.clientId
output managedIdentityPrincipalId string = auditIdentity.outputs.principalId
output retentionDays int = retentionDays
