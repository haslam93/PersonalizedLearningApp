targetScope = 'resourceGroup'

@description('Azure location for all resources.')
param location string = resourceGroup().location

@description('Azure location for PostgreSQL resources when the app region is quota restricted.')
param postgresLocation string = 'centralus'

@description('Tags to apply to all resources.')
param tags object = {}

@description('Access PIN shown by the app login screen. Stored as a secure app setting in Azure.')
@secure()
param accessPin string = ''

@description('GitHub OAuth client id used by the in-app Copilot sign-in flow.')
param gitHubOAuthClientId string = ''

@description('GitHub OAuth client secret used by the in-app Copilot sign-in flow.')
@secure()
param gitHubOAuthClientSecret string = ''

@description('YouTube Data API key used to sync channel and video metadata.')
@secure()
param youTubeApiKey string = ''

@description('Optional path to the GitHub Copilot CLI binary when it is bundled with the app.')
param copilotCliPath string = ''

@description('Default model shown in the Copilot chat model picker.')
param copilotDefaultModel string = 'gpt-5'

@description('Name of the App Service plan.')
param appServicePlanName string

@description('SKU for the App Service plan. Private networking requires Premium v3 or better.')
@allowed([
  'P0v3'
  'P1v3'
])
param appServicePlanSku string = 'P0v3'

@description('Name of the App Service web app.')
param webAppName string

@description('Optional custom hostname to bind to the App Service web app.')
param customHostname string = ''

@description('Name of the Log Analytics workspace.')
param logAnalyticsWorkspaceName string

@description('Name of the Application Insights component.')
param appInsightsName string

@description('Bootstrap PostgreSQL admin login used only for server creation and emergency access.')
param postgresAdminLogin string = 'pgbootstrap'

@description('Bootstrap PostgreSQL admin password used only for server creation and emergency access.')
@secure()
param postgresAdminPassword string

var serviceName = 'web'
var hostingTags = union(tags, {
  'azd-service-name': serviceName
})
var linuxRuntime = 'DOTNETCORE|8.0'
var nameSuffix = toLower(take(uniqueString(resourceGroup().id, webAppName, location), 6))
var postgresNameSuffix = toLower(take(uniqueString(resourceGroup().id, webAppName, postgresLocation, 'postgres'), 6))
var appVnetName = take('vnet-${webAppName}-${nameSuffix}', 64)
var postgresVnetName = take('vnet-${webAppName}-pg-${take(uniqueString(resourceGroup().id, webAppName, postgresLocation), 6)}', 64)
var storageAccountName = 'st${take(uniqueString(resourceGroup().id, webAppName, 'storage'), 22)}'
var postgresServerName = take('pg-${toLower(webAppName)}-${postgresNameSuffix}', 63)
var postgresDatabaseName = 'upskilltracker'
var blobContainerName = 'dataprotection'
var blobName = 'keyring.xml'
var postgresDnsZoneName = 'private.postgres.database.azure.com'
var storageDnsZoneName = 'privatelink.blob.${environment().suffixes.storage}'
var blobServiceHostSuffix = environment().suffixes.storage
var blobDataContributorRoleDefinitionId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
var webAppManagedIdentityPrincipalId = webApp.identity.principalId

resource logAnalyticsWorkspace 'Microsoft.OperationalInsights/workspaces@2025-02-01' = {
  name: logAnalyticsWorkspaceName
  location: location
  tags: tags
  properties: {
    retentionInDays: 30
    sku: {
      name: 'PerGB2018'
    }
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: appInsightsName
  location: location
  kind: 'web'
  tags: tags
  properties: {
    Application_Type: 'web'
    Flow_Type: 'Bluefield'
    IngestionMode: 'LogAnalytics'
    Request_Source: 'rest'
    WorkspaceResourceId: logAnalyticsWorkspace.id
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery: 'Enabled'
  }
}

resource appVnet 'Microsoft.Network/virtualNetworks@2024-05-01' = {
  name: appVnetName
  location: location
  tags: tags
  properties: {
    addressSpace: {
      addressPrefixes: [
        '10.42.0.0/16'
      ]
    }
    subnets: [
      {
        name: 'appsvc'
        properties: {
          addressPrefix: '10.42.0.0/24'
          delegations: [
            {
              name: 'webapp-delegation'
              properties: {
                serviceName: 'Microsoft.Web/serverFarms'
              }
            }
          ]
        }
      }
      {
        name: 'postgres'
        properties: {
          addressPrefix: '10.42.1.0/24'
          delegations: [
            {
              name: 'postgres-delegation'
              properties: {
                serviceName: 'Microsoft.DBforPostgreSQL/flexibleServers'
              }
            }
          ]
        }
      }
      {
        name: 'private-endpoints'
        properties: {
          addressPrefix: '10.42.2.0/24'
          privateEndpointNetworkPolicies: 'Disabled'
        }
      }
    ]
  }
}

resource postgresVnet 'Microsoft.Network/virtualNetworks@2024-05-01' = {
  name: postgresVnetName
  location: postgresLocation
  tags: tags
  properties: {
    addressSpace: {
      addressPrefixes: [
        '10.43.0.0/16'
      ]
    }
    subnets: [
      {
        name: 'postgres'
        properties: {
          addressPrefix: '10.43.0.0/24'
          delegations: [
            {
              name: 'postgres-delegation'
              properties: {
                serviceName: 'Microsoft.DBforPostgreSQL/flexibleServers'
              }
            }
          ]
        }
      }
    ]
  }
}

resource appSubnet 'Microsoft.Network/virtualNetworks/subnets@2024-05-01' existing = {
  parent: appVnet
  name: 'appsvc'
}

resource postgresSubnet 'Microsoft.Network/virtualNetworks/subnets@2024-05-01' existing = {
  parent: postgresVnet
  name: 'postgres'
}

resource privateEndpointsSubnet 'Microsoft.Network/virtualNetworks/subnets@2024-05-01' existing = {
  parent: appVnet
  name: 'private-endpoints'
}

resource postgresPrivateDnsZone 'Microsoft.Network/privateDnsZones@2024-06-01' = {
  name: postgresDnsZoneName
  location: 'global'
  tags: tags
}

resource storagePrivateDnsZone 'Microsoft.Network/privateDnsZones@2024-06-01' = {
  name: storageDnsZoneName
  location: 'global'
  tags: tags
}

resource postgresPrivateDnsZoneLink 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2024-06-01' = {
  parent: postgresPrivateDnsZone
  name: '${appVnet.name}-link'
  location: 'global'
  properties: {
    registrationEnabled: false
    virtualNetwork: {
      id: appVnet.id
    }
  }
}

resource postgresPrivateDnsZonePostgresLink 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2024-06-01' = {
  parent: postgresPrivateDnsZone
  name: '${postgresVnet.name}-link'
  location: 'global'
  properties: {
    registrationEnabled: false
    virtualNetwork: {
      id: postgresVnet.id
    }
  }
}

resource appToPostgresPeering 'Microsoft.Network/virtualNetworks/virtualNetworkPeerings@2024-05-01' = {
  parent: appVnet
  name: '${appVnet.name}-to-${postgresVnet.name}'
  properties: {
    allowForwardedTraffic: true
    allowVirtualNetworkAccess: true
    remoteVirtualNetwork: {
      id: postgresVnet.id
    }
  }
}

resource postgresToAppPeering 'Microsoft.Network/virtualNetworks/virtualNetworkPeerings@2024-05-01' = {
  parent: postgresVnet
  name: '${postgresVnet.name}-to-${appVnet.name}'
  properties: {
    allowForwardedTraffic: true
    allowVirtualNetworkAccess: true
    remoteVirtualNetwork: {
      id: appVnet.id
    }
  }
}

resource storagePrivateDnsZoneLink 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2024-06-01' = {
  parent: storagePrivateDnsZone
  name: '${appVnet.name}-link'
  location: 'global'
  properties: {
    registrationEnabled: false
    virtualNetwork: {
      id: appVnet.id
    }
  }
}

resource storageAccount 'Microsoft.Storage/storageAccounts@2024-01-01' = {
  name: storageAccountName
  location: location
  tags: tags
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    accessTier: 'Hot'
    allowBlobPublicAccess: false
    minimumTlsVersion: 'TLS1_2'
    publicNetworkAccess: 'Disabled'
    supportsHttpsTrafficOnly: true
    networkAcls: {
      bypass: 'AzureServices'
      defaultAction: 'Deny'
    }
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storageAccount
  name: 'default'
}

resource blobContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: blobContainerName
  properties: {
    publicAccess: 'None'
  }
}

resource appServicePlan 'Microsoft.Web/serverfarms@2024-11-01' = {
  name: appServicePlanName
  location: location
  kind: 'linux'
  tags: tags
  sku: {
    name: appServicePlanSku
    capacity: 1
  }
  properties: {
    reserved: true
  }
}

resource webApp 'Microsoft.Web/sites@2024-11-01' = {
  name: webAppName
  location: location
  kind: 'app,linux'
  tags: hostingTags
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: appServicePlan.id
    httpsOnly: true
    publicNetworkAccess: 'Enabled'
    virtualNetworkSubnetId: appSubnet.id
    siteConfig: {
      alwaysOn: true
      ftpsState: 'Disabled'
      http20Enabled: true
      linuxFxVersion: linuxRuntime
      minTlsVersion: '1.2'
      vnetRouteAllEnabled: true
      appSettings: [
        {
          name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
          value: appInsights.properties.ConnectionString
        }
        {
          name: 'ASPNETCORE_ENVIRONMENT'
          value: 'Production'
        }
        {
          name: 'AccessPin'
          value: accessPin
        }
        {
          name: 'GitHubOAuth__ClientId'
          value: gitHubOAuthClientId
        }
        {
          name: 'GitHubOAuth__ClientSecret'
          value: gitHubOAuthClientSecret
        }
        {
          name: 'YouTube__ApiKey'
          value: youTubeApiKey
        }
        {
          name: 'GitHubOAuth__CallbackPath'
          value: '/signin-github'
        }
        {
          name: 'CopilotSdk__CliPath'
          value: copilotCliPath
        }
        {
          name: 'CopilotSdk__DefaultModel'
          value: copilotDefaultModel
        }
        {
          name: 'ENABLE_ORYX_BUILD'
          value: 'false'
        }
        {
          name: 'SCM_DO_BUILD_DURING_DEPLOYMENT'
          value: 'false'
        }
        {
          name: 'Storage__Provider'
          value: 'Postgres'
        }
        {
          name: 'Storage__ConnectionString'
          value: 'Host=${postgresServer.properties.fullyQualifiedDomainName};Database=${postgresDatabase.name};Username=${webAppName};Ssl Mode=Require'
        }
        {
          name: 'Storage__DatabaseUser'
          value: webAppName
        }
        {
          name: 'Storage__UseManagedIdentity'
          value: 'true'
        }
        {
          name: 'Storage__LegacySqliteConnectionString'
          value: 'Data Source=/home/data/upskilltracker.db'
        }
        {
          name: 'Storage__EnableLegacySqliteImport'
          value: 'true'
        }
        {
          name: 'Storage__KeyBlobUri'
          value: 'https://${storageAccount.name}.blob.${blobServiceHostSuffix}/${blobContainerName}/${blobName}'
        }
        {
          name: 'Storage__DataProtectionApplicationName'
          value: webAppName
        }
        {
          name: 'WEBSITES_ENABLE_APP_SERVICE_STORAGE'
          value: 'true'
        }
      ]
      metadata: [
        {
          name: 'CURRENT_STACK'
          value: 'dotnetcore'
        }
      ]
    }
  }
}

resource blobDataContributorRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccount.id, webAppName, 'blob-data-contributor')
  scope: storageAccount
  properties: {
    principalId: webAppManagedIdentityPrincipalId
    roleDefinitionId: blobDataContributorRoleDefinitionId
    principalType: 'ServicePrincipal'
  }
}

resource storagePrivateEndpoint 'Microsoft.Network/privateEndpoints@2024-05-01' = {
  name: 'pe-${storageAccount.name}-blob'
  location: location
  tags: tags
  properties: {
    subnet: {
      id: privateEndpointsSubnet.id
    }
    privateLinkServiceConnections: [
      {
        name: 'blob'
        properties: {
          privateLinkServiceId: storageAccount.id
          groupIds: [
            'blob'
          ]
        }
      }
    ]
  }
}

resource storagePrivateEndpointDnsGroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2024-05-01' = {
  parent: storagePrivateEndpoint
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'blob-dns'
        properties: {
          privateDnsZoneId: storagePrivateDnsZone.id
        }
      }
    ]
  }
}

resource postgresServer 'Microsoft.DBforPostgreSQL/flexibleServers@2024-08-01' = {
  name: postgresServerName
  location: postgresLocation
  tags: tags
  identity: {
    type: 'SystemAssigned'
  }
  sku: {
    name: 'Standard_D2s_v3'
    tier: 'GeneralPurpose'
  }
  properties: {
    administratorLogin: postgresAdminLogin
    administratorLoginPassword: postgresAdminPassword
    authConfig: {
      activeDirectoryAuth: 'Enabled'
      passwordAuth: 'Enabled'
      tenantId: tenant().tenantId
    }
    backup: {
      backupRetentionDays: 7
      geoRedundantBackup: 'Disabled'
    }
    createMode: 'Default'
    network: {
      delegatedSubnetResourceId: postgresSubnet.id
      privateDnsZoneArmResourceId: postgresPrivateDnsZone.id
      publicNetworkAccess: 'Disabled'
    }
    storage: {
      autoGrow: 'Enabled'
      storageSizeGB: 32
      tier: 'P4'
      type: 'Premium_LRS'
    }
    version: '16'
  }
}

resource postgresDatabase 'Microsoft.DBforPostgreSQL/flexibleServers/databases@2024-08-01' = {
  parent: postgresServer
  name: postgresDatabaseName
  properties: {
    charset: 'UTF8'
    collation: 'en_US.utf8'
  }
}

resource webLogs 'Microsoft.Web/sites/config@2024-04-01' = {
  name: 'logs'
  parent: webApp
  properties: {
    applicationLogs: {
      fileSystem: {
        level: 'Information'
      }
    }
    detailedErrorMessages: {
      enabled: true
    }
    failedRequestsTracing: {
      enabled: true
    }
    httpLogs: {
      fileSystem: {
        enabled: true
        retentionInDays: 7
        retentionInMb: 35
      }
    }
  }
}

resource customHostnameBinding 'Microsoft.Web/sites/hostNameBindings@2024-11-01' = if (!empty(customHostname)) {
  parent: webApp
  name: customHostname
  properties: {
    azureResourceName: webAppName
    azureResourceType: 'Website'
    customHostNameDnsRecordType: 'CName'
    hostNameType: 'Verified'
    siteName: webAppName
    sslState: 'Disabled'
  }
}

output webAppName string = webApp.name
output webAppUrl string = 'https://${webApp.properties.defaultHostName}'
output appInsightsConnectionString string = appInsights.properties.ConnectionString
output postgresServerName string = postgresServer.name
output postgresDatabaseName string = postgresDatabase.name
output storageAccountName string = storageAccount.name
