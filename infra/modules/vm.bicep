param location string
param tags object
param subnetId string
param nsgId string
param publicIpId string
@secure()
param vmAdminPassword string
param keyVaultId string
param backupContainerId string
param backupWriterRoleDefinitionId string
param adminUsername string = 'yaskadmin'

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: 'kv-yask-staging-7977'
}

resource backupStorage 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  name: 'styaskstaging7977'
}

resource backupBlobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' existing = {
  parent: backupStorage
  name: 'default'
}

resource backupContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' existing = {
  parent: backupBlobService
  name: 'postgres-backups'
}

resource nic 'Microsoft.Network/networkInterfaces@2023-09-01' = {
  name: 'nic-yask-7977'
  location: location
  tags: tags
  properties: {
    networkSecurityGroup: { id: nsgId }
    ipConfigurations: [
      {
        name: 'ipconfig1'
        properties: {
          privateIPAllocationMethod: 'Dynamic'
          subnet: { id: subnetId }
          publicIPAddress: { id: publicIpId }
        }
      }
    ]
  }
}

resource vm 'Microsoft.Compute/virtualMachines@2023-09-01' = {
  name: 'vm-yask-staging-7977'
  location: location
  tags: tags
  identity: { type: 'SystemAssigned' }
  properties: {
    hardwareProfile: { vmSize: 'Standard_D2as_v5' }
    securityProfile: {
      securityType: 'TrustedLaunch'
      uefiSettings: {
        secureBootEnabled: true
        vTpmEnabled: true
      }
    }
    osProfile: {
      computerName: 'yaskvm7977'
      adminUsername: adminUsername
      adminPassword: vmAdminPassword
      windowsConfiguration: {
        provisionVMAgent: true
        enableAutomaticUpdates: true
      }
    }
    storageProfile: {
      imageReference: {
        publisher: 'MicrosoftWindowsServer'
        offer: 'WindowsServer'
        sku: '2022-datacenter-azure-edition'
        version: 'latest'
      }
      osDisk: {
        name: 'osdisk-yask-staging-7977'
        createOption: 'FromImage'
        diskSizeGB: 128
        managedDisk: { storageAccountType: 'StandardSSD_LRS' }
      }
    }
    networkProfile: {
      networkInterfaces: [
        {
          id: nic.id
          properties: { primary: true }
        }
      ]
    }
    diagnosticsProfile: { bootDiagnostics: { enabled: true } }
  }
}

resource vmSecretsUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVaultId, vm.name, '4633458b-17de-408a-b874-0445c86b69e6')
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      '4633458b-17de-408a-b874-0445c86b69e6'
    )
    principalId: vm.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource backupReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(backupContainerId, vm.name, '2a2b9908-6ea1-4ae2-8e65-a410df84e7d1')
  scope: backupContainer
  properties: {
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      '2a2b9908-6ea1-4ae2-8e65-a410df84e7d1'
    )
    principalId: vm.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource backupWriter 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(backupContainerId, vm.name, backupWriterRoleDefinitionId)
  scope: backupContainer
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', backupWriterRoleDefinitionId)
    principalId: vm.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

output vmId string = vm.id
output principalId string = vm.identity.principalId
