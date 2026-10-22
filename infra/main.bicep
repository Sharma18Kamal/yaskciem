targetScope = 'subscription'

@description('Existing resource group approved for the staging deployment.')
param resourceGroupName string = 'Yask-Ciem-Staging'
@minLength(1)
param environmentName string = 'yask-staging-7977'
@allowed(['centralus'])
param location string = 'centralus'
param sessionId string = '797788cd-09ac-45f7-8272-518a2307dbfb'
param deployedBy string = 'Kamal Kumar Sharma'
param createdAt string
param deployerObjectId string
@allowed(['User', 'ServicePrincipal'])
param deployerPrincipalType string = 'User'
@secure()
param vmAdminPassword string
@secure()
param postgresAdminPassword string
@description('Optional single operator IPv4 address, without CIDR suffix. RDP remains disabled when empty.')
@maxLength(15)
param operatorIpv4 string = ''
param publicDnsLabel string = 'yask-staging-7977'

var tags = {
  'app-onboard-skill': 'true'
  'app-onboard-session-id': sessionId
  'created-at': createdAt
  environment: environmentName
  'deployed-by': deployedBy
}

resource rg 'Microsoft.Resources/resourceGroups@2023-07-01' existing = {
  name: resourceGroupName
}

// Custom data role permits backup creation/append without delete access to retained blobs.
resource backupWriterRole 'Microsoft.Authorization/roleDefinitions@2022-04-01' = {
  name: guid(subscription().id, 'yask-staging-backup-writer')
  properties: {
    roleName: 'YASK staging immutable backup writer'
    description: 'Create and append backup blobs without delete permissions.'
    type: 'CustomRole'
    permissions: [
      {
        dataActions: [
          'Microsoft.Storage/storageAccounts/blobServices/containers/blobs/read'
          'Microsoft.Storage/storageAccounts/blobServices/containers/blobs/write'
          'Microsoft.Storage/storageAccounts/blobServices/containers/blobs/add/action'
        ]
      }
    ]
    assignableScopes: [
      subscription().id
    ]
  }
}

module network './modules/network.bicep' = {
  name: 'network'
  scope: rg
  params: {
    location: location
    tags: tags
    operatorIpv4: operatorIpv4
    publicDnsLabel: publicDnsLabel
  }
}

module backup './modules/storage.bicep' = {
  name: 'backup-storage'
  scope: rg
  params: {
    location: location
    tags: tags
    subnetId: network.outputs.subnetId
  }
}

module keyVault './modules/keyvault.bicep' = {
  name: 'key-vault'
  scope: rg
  params: {
    location: location
    tags: tags
    vaultName: 'kv-yask-staging-7977'
    deployerObjectId: deployerObjectId
    deployerPrincipalType: deployerPrincipalType
    vmAdminPassword: vmAdminPassword
    postgresAdminPassword: postgresAdminPassword
  }
}

module postgres './modules/postgres.bicep' = {
  name: 'postgres'
  scope: rg
  params: {
    location: location
    tags: tags
    postgresAdminPassword: postgresAdminPassword
  }
}

module vm './modules/vm.bicep' = {
  name: 'windows-vm'
  scope: rg
  params: {
    location: location
    tags: tags
    subnetId: network.outputs.subnetId
    nsgId: network.outputs.nsgId
    publicIpId: network.outputs.publicIpId
    vmAdminPassword: vmAdminPassword
    keyVaultId: keyVault.outputs.keyVaultId
    backupContainerId: backup.outputs.containerId
    backupWriterRoleDefinitionId: backupWriterRole.name
  }
}

module monitoring './modules/monitoring.bicep' = {
  name: 'monitoring'
  scope: rg
  params: {
    location: location
    tags: tags
  }
}

output stagingHostname string = network.outputs.fqdn
output virtualMachineId string = vm.outputs.vmId
output vmManagedIdentityObjectId string = vm.outputs.principalId
output postgresHostname string = postgres.outputs.hostname
output backupContainerId string = backup.outputs.containerId
output keyVaultId string = keyVault.outputs.keyVaultId
output workspaceId string = monitoring.outputs.workspaceId
