using './main.bicep'

param resourceGroupName = 'rg-yask-ciem-audit-client'
param location = 'eastus2'
param storageAccountName = 'yaskciemauditclient001'
param managedIdentityName = 'id-yask-ciem-audit-api'
param privateEndpointSubnetResourceId = '/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-client-network/providers/Microsoft.Network/virtualNetworks/vnet-client/subnets/snet-private-endpoints'
param blobPrivateDnsZoneResourceId = '/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-client-network/providers/Microsoft.Network/privateDnsZones/privatelink.blob.core.windows.net'
param logAnalyticsWorkspaceResourceId = '/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-client-monitoring/providers/Microsoft.OperationalInsights/workspaces/law-client'
param retentionDays = 2555
param storageSku = 'Standard_ZRS'
param tags = {
  workload: 'yask-ciem-central-audit'
  owner: 'client-platform-team'
  dataClassification: 'audit'
}
