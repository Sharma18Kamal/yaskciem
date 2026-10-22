param location string
param tags object
@maxLength(15)
param operatorIpv4 string = ''
param publicDnsLabel string

resource nsg 'Microsoft.Network/networkSecurityGroups@2023-09-01' = {
  name: 'nsg-yask-staging-7977'
  location: location
  tags: tags
  properties: {
    securityRules: concat(
      [
        {
          name: 'AllowHttps'
          properties: {
            priority: 100
            protocol: 'Tcp'
            access: 'Allow'
            direction: 'Inbound'
            sourceAddressPrefix: '*'
            sourcePortRange: '*'
            destinationAddressPrefix: '*'
            destinationPortRange: '443'
          }
        }
      ],
      empty(operatorIpv4)
        ? []
        : [
            {
              name: 'AllowRdpOperator'
              properties: {
                priority: 110
                protocol: 'Tcp'
                access: 'Allow'
                direction: 'Inbound'
                sourceAddressPrefix: '${operatorIpv4}/32'
                sourcePortRange: '*'
                destinationAddressPrefix: '*'
                destinationPortRange: '3389'
              }
            }
          ],
      [
        {
          name: 'DenyAllOtherInbound'
          properties: {
            priority: 4000
            protocol: '*'
            access: 'Deny'
            direction: 'Inbound'
            sourceAddressPrefix: '*'
            sourcePortRange: '*'
            destinationAddressPrefix: '*'
            destinationPortRange: '*'
          }
        }
      ]
    )
  }
}

resource vnet 'Microsoft.Network/virtualNetworks@2023-09-01' = {
  name: 'vnet-yask-staging-7977'
  location: location
  tags: tags
  properties: {
    addressSpace: { addressPrefixes: ['10.77.0.0/16'] }
    subnets: [
      {
        name: 'app'
        properties: {
          addressPrefix: '10.77.1.0/24'
          networkSecurityGroup: { id: nsg.id }
          serviceEndpoints: [
            { service: 'Microsoft.Storage' }
          ]
        }
      }
    ]
  }
}

resource publicIp 'Microsoft.Network/publicIPAddresses@2023-09-01' = {
  name: 'pip-yask-staging-7977'
  location: location
  tags: tags
  sku: { name: 'Standard' }
  properties: {
    publicIPAllocationMethod: 'Static'
    publicIPAddressVersion: 'IPv4'
    dnsSettings: {
      domainNameLabel: publicDnsLabel
    }
  }
}

output subnetId string = vnet.properties.subnets[0].id
output nsgId string = nsg.id
output publicIpId string = publicIp.id
output fqdn string = publicIp.properties.dnsSettings.fqdn
