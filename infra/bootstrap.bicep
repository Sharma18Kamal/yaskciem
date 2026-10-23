targetScope = 'subscription'

@description('New deployment service principal object ID, not the existing preview-only identity or application object ID.')
@minLength(36)
@maxLength(36)
param deployerObjectId string

@allowed(['Yask-Ciem-Staging'])
param resourceGroupName string = 'Yask-Ciem-Staging'

var readerRoleId = 'acdd72a7-3385-48ef-bd42-f606fba81ae7'

resource rg 'Microsoft.Resources/resourceGroups@2023-07-01' existing = {
  name: resourceGroupName
}

resource backupWriterRole 'Microsoft.Authorization/roleDefinitions@2022-04-01' = {
  name: guid(subscription().id, 'yask-staging-backup-writer')
  properties: {
    roleName: 'YASK staging immutable backup writer'
    description: 'Create and append backup blobs without delete permissions.'
    type: 'CustomRole'
    permissions: [
      {
        actions: []
        notActions: []
        dataActions: [
          'Microsoft.Storage/storageAccounts/blobServices/containers/blobs/read'
          'Microsoft.Storage/storageAccounts/blobServices/containers/blobs/write'
          'Microsoft.Storage/storageAccounts/blobServices/containers/blobs/add/action'
        ]
        notDataActions: []
      }
    ]
    assignableScopes: [
      subscription().id
    ]
  }
}

resource deploymentRole 'Microsoft.Authorization/roleDefinitions@2022-04-01' = {
  name: guid(subscription().id, 'yask-staging-subscription-deployer')
  properties: {
    roleName: 'YASK staging subscription deployment submitter'
    description: 'Submit and validate ARM deployments without subscription resource or RBAC write permissions.'
    type: 'CustomRole'
    permissions: [
      {
        actions: [
          'Microsoft.Resources/deployments/write'
          'Microsoft.Resources/deployments/validate/action'
          'Microsoft.Resources/deployments/whatIf/action'
        ]
        notActions: []
        dataActions: []
        notDataActions: []
      }
    ]
    assignableScopes: [
      subscription().id
    ]
  }
}

resource assignmentWriterRole 'Microsoft.Authorization/roleDefinitions@2022-04-01' = {
  name: guid(subscription().id, 'yask-staging-role-assignment-writer')
  properties: {
    roleName: 'YASK staging role assignment writer'
    description: 'Assignment writes only; assign this role only with the reviewed role/principal condition.'
    type: 'CustomRole'
    permissions: [
      {
        actions: [
          'Microsoft.Authorization/roleAssignments/write'
        ]
        notActions: []
        dataActions: []
        notDataActions: []
      }
    ]
    assignableScopes: [
      rg.id
    ]
  }
}

resource subscriptionReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(subscription().id, deployerObjectId, readerRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', readerRoleId)
    principalId: deployerObjectId
    principalType: 'ServicePrincipal'
  }
}

resource subscriptionDeployer 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(subscription().id, deployerObjectId, deploymentRole.name)
  properties: {
    roleDefinitionId: deploymentRole.id
    principalId: deployerObjectId
    principalType: 'ServicePrincipal'
  }
}

module groupAccess './modules/bootstrap-access.bicep' = {
  name: 'staging-deployer-access'
  scope: rg
  params: {
    deployerObjectId: deployerObjectId
    assignmentWriterRoleId: assignmentWriterRole.id
    backupWriterRoleId: backupWriterRole.name
  }
}

output backupWriterRoleDefinitionId string = backupWriterRole.id
output deploymentRoleDefinitionId string = deploymentRole.id
output assignmentWriterRoleDefinitionId string = assignmentWriterRole.id
