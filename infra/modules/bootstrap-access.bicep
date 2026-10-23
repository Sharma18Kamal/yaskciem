param deployerObjectId string
param assignmentWriterRoleId string
param backupWriterRoleId string

var contributorRoleId = 'b24988ac-6180-42a0-ab88-20f7382dd24c'
var secretsOfficerRoleId = 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7'
var secretsUserRoleId = '4633458b-17de-408a-b874-0445c86b69e6'
var blobReaderRoleId = '2a2b9908-6ea1-4ae2-8e65-a410df84e7d1'

resource groupContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(resourceGroup().id, deployerObjectId, contributorRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', contributorRoleId)
    principalId: deployerObjectId
    principalType: 'ServicePrincipal'
  }
}

// The VM principal does not exist yet. Restrict its grants by role and principal type.
var assignmentCondition = format('''
(
  (!(ActionMatches{{'Microsoft.Authorization/roleAssignments/write'}}))
  OR
  (
    @Request[Microsoft.Authorization/roleAssignments:PrincipalType] StringEqualsIgnoreCase 'ServicePrincipal'
    AND
    (
      (
        @Request[Microsoft.Authorization/roleAssignments:RoleDefinitionId] GuidEquals {0}
        AND
        @Request[Microsoft.Authorization/roleAssignments:PrincipalId] GuidEquals {1}
      )
      OR
      (
        @Request[Microsoft.Authorization/roleAssignments:RoleDefinitionId] ForAnyOfAnyValues:GuidEquals {{{2}, {3}, {4}}}
      )
    )
  )
)
''', secretsOfficerRoleId, deployerObjectId, secretsUserRoleId, blobReaderRoleId, backupWriterRoleId)

resource groupAssignmentWriter 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(resourceGroup().id, deployerObjectId, assignmentWriterRoleId)
  properties: {
    roleDefinitionId: assignmentWriterRoleId
    principalId: deployerObjectId
    principalType: 'ServicePrincipal'
    conditionVersion: '2.0'
    condition: assignmentCondition
  }
}
