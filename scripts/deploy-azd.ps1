param(
    [string]$EnvironmentName = 'personal-learning',
    [string]$Location = 'eastus2',
    [string]$ResourceGroupName = '',
    [string]$WebAppName = '',
    [string]$SubscriptionId = '',
    [switch]$ProvisionOnly,
    [switch]$DeployOnly
)

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')

function Assert-Command {
    param([string]$Name)

    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "Required command '$Name' was not found in PATH."
    }
}

Assert-Command -Name 'azd'
Assert-Command -Name 'az'

Set-Location $repoRoot

if (-not [string]::IsNullOrWhiteSpace($SubscriptionId)) {
    az account set --subscription $SubscriptionId | Out-Null
}

$resolvedResourceGroupName = if ([string]::IsNullOrWhiteSpace($ResourceGroupName)) {
    "rg-$EnvironmentName"
}
else {
    $ResourceGroupName
}

$resolvedWebAppName = if ([string]::IsNullOrWhiteSpace($WebAppName)) {
    "pla-$EnvironmentName"
}
else {
    $WebAppName.ToLowerInvariant()
}

$existingEnvironment = azd env list --output json | ConvertFrom-Json | Where-Object { $_.Name -eq $EnvironmentName }
if (-not $existingEnvironment) {
    azd env new $EnvironmentName --no-prompt | Out-Host
}
else {
    azd env select $EnvironmentName | Out-Host
}

azd env set AZURE_LOCATION $Location | Out-Host
azd env set AZURE_RESOURCE_GROUP $resolvedResourceGroupName | Out-Host
azd env set AZURE_WEB_APP_NAME $resolvedWebAppName | Out-Host

if ($ProvisionOnly -and $DeployOnly) {
    throw 'Use either -ProvisionOnly or -DeployOnly, not both.'
}

if ($ProvisionOnly) {
    azd provision | Out-Host
    return
}

if ($DeployOnly) {
    azd deploy | Out-Host
    return
}

azd up | Out-Host
