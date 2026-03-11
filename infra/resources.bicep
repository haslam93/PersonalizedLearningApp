targetScope = 'resourceGroup'

@description('Azure location for all resources.')
param location string = resourceGroup().location

@description('Tags to apply to all resources.')
param tags object = {}

@description('Name of the App Service plan.')
param appServicePlanName string

@description('SKU for the App Service plan.')
@allowed([
  'B1'
  'P0v3'
  'P1v3'
])
param appServicePlanSku string = 'B1'

@description('Name of the App Service web app.')
param webAppName string

@description('Optional custom hostname to bind to the App Service web app.')
param customHostname string = ''

@description('Enable App Service Authentication with Microsoft Entra ID.')
param enableAppServiceAuth bool = false

@description('Tenant ID used for App Service Authentication.')
param authTenantId string = ''

@description('Client ID of the Microsoft Entra app registration used for App Service Authentication.')
param authClientId string = ''

@description('Client secret for the Microsoft Entra app registration used for App Service Authentication.')
@secure()
param authClientSecret string = ''

@description('Allowed Microsoft Entra object IDs that can access the app when App Service Authentication is enabled.')
param allowedUserObjectIds array = []

@description('Name of the Log Analytics workspace.')
param logAnalyticsWorkspaceName string

@description('Name of the Application Insights component.')
param appInsightsName string

var serviceName = 'web'
var hostingTags = union(tags, {
  'azd-service-name': serviceName
})
var linuxRuntime = 'DOTNETCORE|8.0'
var authClientSecretSettingName = 'MICROSOFT_PROVIDER_AUTHENTICATION_SECRET'
var authOpenIdIssuer = '${environment().authentication.loginEndpoint}${authTenantId}/v2.0'

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
    siteConfig: {
      alwaysOn: true
      ftpsState: 'Disabled'
      http20Enabled: true
      linuxFxVersion: linuxRuntime
      minTlsVersion: '1.2'
      appSettings: concat([
        {
          name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
          value: appInsights.properties.ConnectionString
        }
        {
          name: 'ASPNETCORE_ENVIRONMENT'
          value: 'Production'
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
          name: 'Storage__ConnectionString'
          value: 'Data Source=/home/data/upskilltracker.db'
        }
        {
          name: 'WEBSITES_ENABLE_APP_SERVICE_STORAGE'
          value: 'true'
        }
      ], enableAppServiceAuth ? [
        {
          name: authClientSecretSettingName
          value: authClientSecret
        }
      ] : [])
      metadata: [
        {
          name: 'CURRENT_STACK'
          value: 'dotnetcore'
        }
      ]
    }
  }
}

resource authSettings 'Microsoft.Web/sites/config@2022-09-01' = if (enableAppServiceAuth) {
  name: 'authsettingsV2'
  parent: webApp
  properties: {
    platform: {
      enabled: true
      runtimeVersion: '~1'
    }
    globalValidation: {
      requireAuthentication: true
      unauthenticatedClientAction: 'RedirectToLoginPage'
      redirectToProvider: 'azureActiveDirectory'
    }
    httpSettings: {
      requireHttps: true
      routes: {
        apiPrefix: '/.auth'
      }
      forwardProxy: {
        convention: 'NoProxy'
      }
    }
    login: {
      tokenStore: {
        enabled: true
      }
      preserveUrlFragmentsForLogins: true
    }
    identityProviders: {
      azureActiveDirectory: {
        enabled: true
        registration: {
          openIdIssuer: authOpenIdIssuer
          clientId: authClientId
          clientSecretSettingName: authClientSecretSettingName
        }
        validation: {
          defaultAuthorizationPolicy: {
            allowedPrincipals: {
              identities: allowedUserObjectIds
            }
          }
        }
      }
    }
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
    azureResourceName: webApp.name
    azureResourceType: 'Website'
    customHostNameDnsRecordType: 'CName'
    hostNameType: 'Verified'
    siteName: webApp.name
    sslState: 'Disabled'
  }
}

output webAppName string = webApp.name
output webAppUrl string = 'https://${webApp.properties.defaultHostName}'
output appInsightsConnectionString string = appInsights.properties.ConnectionString
