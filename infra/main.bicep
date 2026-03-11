targetScope = 'subscription'

@description('Name of the azd environment.')
param environmentName string

@description('Azure location for all resources.')
param location string

@description('Resource group name for the environment.')
param resourceGroupName string = 'rg-${environmentName}-${take(uniqueString(subscription().id, environmentName, location), 6)}'

@description('Optional override for the web app name. Must be globally unique when provided.')
param webAppName string = ''

@description('Optional custom hostname to bind to the web app. Leave empty to skip custom domain deployment.')
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

@description('Optional override for the app service plan SKU.')
@allowed([
  'B1'
  'P0v3'
  'P1v3'
])
param appServicePlanSku string = 'B1'

var resourceSuffix = toLower(take(uniqueString(subscription().id, resourceGroupName, environmentName), 6))
var tags = {
  'azd-env-name': environmentName
  workload: 'personal-learning-app'
  owner: 'hammad'
}
var effectiveWebAppName = empty(webAppName)
  ? take('pla-${toLower(environmentName)}-${resourceSuffix}', 60)
  : toLower(webAppName)
var effectivePlanName = take('plan-${toLower(environmentName)}-${resourceSuffix}', 40)
var effectiveWorkspaceName = take('log-${toLower(environmentName)}-${resourceSuffix}', 63)
var effectiveInsightsName = take('appi-${toLower(environmentName)}-${resourceSuffix}', 64)

resource resourceGroup 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: resourceGroupName
  location: location
  tags: tags
}

module resources './resources.bicep' = {
  name: 'learning-app-resources'
  scope: resourceGroup
  params: {
    appInsightsName: effectiveInsightsName
    appServicePlanName: effectivePlanName
    appServicePlanSku: appServicePlanSku
    authClientId: authClientId
    authClientSecret: authClientSecret
    authTenantId: authTenantId
    allowedUserObjectIds: allowedUserObjectIds
    customHostname: customHostname
    enableAppServiceAuth: enableAppServiceAuth
    location: location
    logAnalyticsWorkspaceName: effectiveWorkspaceName
    tags: tags
    webAppName: effectiveWebAppName
  }
}

output AZURE_WEBAPP_NAME string = resources.outputs.webAppName
output AZURE_WEBAPP_URL string = resources.outputs.webAppUrl
output AZURE_RESOURCE_GROUP string = resourceGroup.name
