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

@description('Access PIN shown by the app login screen. Stored as a secure app setting in Azure.')
@secure()
param accessPin string = ''

@description('GitHub OAuth client id used by the in-app Copilot sign-in flow.')
param gitHubOAuthClientId string = ''

@description('GitHub OAuth client secret used by the in-app Copilot sign-in flow.')
@secure()
param gitHubOAuthClientSecret string = ''

@description('Optional path to the GitHub Copilot CLI binary when it is bundled with the app.')
param copilotCliPath string = ''

@description('Default model shown in the Copilot chat model picker.')
param copilotDefaultModel string = 'gpt-5'

@description('Optional override for the app service plan SKU.')
@allowed([
  'B1'
  'B2'
  'P0v3'
  'P1v3'
])
param appServicePlanSku string = 'B2'

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
    accessPin: accessPin
    appInsightsName: effectiveInsightsName
    appServicePlanName: effectivePlanName
    appServicePlanSku: appServicePlanSku
    copilotCliPath: copilotCliPath
    copilotDefaultModel: copilotDefaultModel
    customHostname: customHostname
    gitHubOAuthClientId: gitHubOAuthClientId
    gitHubOAuthClientSecret: gitHubOAuthClientSecret
    location: location
    logAnalyticsWorkspaceName: effectiveWorkspaceName
    tags: tags
    webAppName: effectiveWebAppName
  }
}

output AZURE_WEBAPP_NAME string = resources.outputs.webAppName
output AZURE_WEBAPP_URL string = resources.outputs.webAppUrl
output AZURE_RESOURCE_GROUP string = resourceGroup.name
