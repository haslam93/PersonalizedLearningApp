param(
    [string]$EnvironmentName = 'personal-learning',
    [string]$Location = 'eastus2',
    [string]$ResourceGroupName = '',
    [string]$WebAppName = '',
    [string]$SubscriptionId = '',
    [string]$GitHubOAuthClientId = '',
    [string]$GitHubOAuthClientSecret = '',
    [string]$YouTubeApiKey = '',
    [string]$CopilotCliPath = '',
    [string]$CopilotDefaultModel = 'gpt-5',
    [switch]$ProvisionOnly,
    [switch]$DeployOnly,
    [switch]$UseDeviceCode
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

function Invoke-NativeCommand {
    param(
        [Parameter(Mandatory = $true)]
        [scriptblock]$Command,

        [Parameter(Mandatory = $true)]
        [string]$FailureMessage
    )

    & $Command
    if ($LASTEXITCODE -ne 0) {
        throw $FailureMessage
    }
}

function Invoke-AppServiceFallbackDeployment {
    Write-Host 'Attempting direct App Service deployment fallback...' -ForegroundColor Yellow

    $deployScriptPath = Join-Path $repoRoot 'scripts\deploy-azure.ps1'
    & $deployScriptPath `
        -ResourceGroupName $resolvedResourceGroupName `
        -WebAppName $resolvedWebAppName `
        -Location $Location `
        -SubscriptionId $resolvedSubscriptionId `
        -GitHubOAuthClientId $GitHubOAuthClientId `
        -GitHubOAuthClientSecret $GitHubOAuthClientSecret `
        -YouTubeApiKey $YouTubeApiKey `
        -CopilotCliPath $CopilotCliPath `
        -CopilotDefaultModel $CopilotDefaultModel `
        -SkipProvisioning

    if ($LASTEXITCODE -ne 0) {
        throw 'Direct App Service deployment fallback failed.'
    }
}

function Set-AzdEnvironmentValueIfProvided {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter(Mandatory = $true)]
        [string]$Value,

        [Parameter(Mandatory = $true)]
        [string]$FailureMessage
    )

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return
    }

    Invoke-NativeCommand -Command { azd env set $Name $Value } -FailureMessage $FailureMessage
}

function Invoke-AzdDeployWithFallback {
    param(
        [switch]$AllowFallback
    )

    Write-Host 'Running azd deploy...' -ForegroundColor Cyan
    & { azd deploy --no-prompt }
    if ($LASTEXITCODE -eq 0) {
        return
    }

    if (-not $AllowFallback) {
        throw 'azd deploy failed.'
    }

    Write-Host 'azd deploy failed. This can happen when Microsoft.Web deployment history calls time out.' -ForegroundColor Yellow
    Invoke-AppServiceFallbackDeployment
}

if (-not [string]::IsNullOrWhiteSpace($SubscriptionId)) {
    Invoke-NativeCommand -Command { az account set --subscription $SubscriptionId } -FailureMessage 'Unable to select the requested Azure subscription.'
}

$resolvedSubscriptionId = if ([string]::IsNullOrWhiteSpace($SubscriptionId)) {
    az account show --query id --output tsv
}
else {
    $SubscriptionId
}

if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($resolvedSubscriptionId)) {
    throw 'Unable to determine the active Azure subscription. Run az login first or pass -SubscriptionId.'
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

Write-Host "Preparing azd environment '$EnvironmentName'..." -ForegroundColor Cyan
$existingEnvironment = azd env list --output json | ConvertFrom-Json | Where-Object { $_.Name -eq $EnvironmentName }
if (-not $existingEnvironment) {
    Invoke-NativeCommand -Command { azd env new $EnvironmentName --no-prompt } -FailureMessage "Failed to create azd environment '$EnvironmentName'."
}
else {
    Invoke-NativeCommand -Command { azd env select $EnvironmentName } -FailureMessage "Failed to select azd environment '$EnvironmentName'."
}

Invoke-NativeCommand -Command { azd env set AZURE_LOCATION $Location } -FailureMessage 'Failed to set AZURE_LOCATION in the azd environment.'
Invoke-NativeCommand -Command { azd env set AZURE_RESOURCE_GROUP $resolvedResourceGroupName } -FailureMessage 'Failed to set AZURE_RESOURCE_GROUP in the azd environment.'
Invoke-NativeCommand -Command { azd env set AZURE_WEB_APP_NAME $resolvedWebAppName } -FailureMessage 'Failed to set AZURE_WEB_APP_NAME in the azd environment.'
Invoke-NativeCommand -Command { azd env set AZURE_SUBSCRIPTION_ID $resolvedSubscriptionId } -FailureMessage 'Failed to set AZURE_SUBSCRIPTION_ID in the azd environment.'
Set-AzdEnvironmentValueIfProvided -Name 'GITHUB_OAUTH_CLIENT_ID' -Value $GitHubOAuthClientId -FailureMessage 'Failed to set GITHUB_OAUTH_CLIENT_ID in the azd environment.'
Set-AzdEnvironmentValueIfProvided -Name 'GITHUB_OAUTH_CLIENT_SECRET' -Value $GitHubOAuthClientSecret -FailureMessage 'Failed to set GITHUB_OAUTH_CLIENT_SECRET in the azd environment.'
Set-AzdEnvironmentValueIfProvided -Name 'YOUTUBE_API_KEY' -Value $YouTubeApiKey -FailureMessage 'Failed to set YOUTUBE_API_KEY in the azd environment.'
Set-AzdEnvironmentValueIfProvided -Name 'COPILOT_CLI_PATH' -Value $CopilotCliPath -FailureMessage 'Failed to set COPILOT_CLI_PATH in the azd environment.'
Set-AzdEnvironmentValueIfProvided -Name 'COPILOT_DEFAULT_MODEL' -Value $CopilotDefaultModel -FailureMessage 'Failed to set COPILOT_DEFAULT_MODEL in the azd environment.'

if ([string]::IsNullOrWhiteSpace($GitHubOAuthClientId) -or [string]::IsNullOrWhiteSpace($GitHubOAuthClientSecret)) {
    Write-Host 'GitHub OAuth values were not supplied to this script. Copilot sign-in stays disabled until GITHUB_OAUTH_CLIENT_ID and GITHUB_OAUTH_CLIENT_SECRET are set in the azd environment or Azure App Service settings.' -ForegroundColor DarkYellow
}

Write-Host 'Checking azd authentication state...' -ForegroundColor Cyan
Invoke-NativeCommand -Command { azd auth login --check-status --no-prompt } -FailureMessage 'azd is not authenticated. Run azd auth login and try again.'

if ($UseDeviceCode) {
    Write-Host 'Refreshing azd authentication by using device code...' -ForegroundColor Yellow
    Invoke-NativeCommand -Command { azd auth login --use-device-code } -FailureMessage 'azd device-code authentication failed.'
}

if ($ProvisionOnly -and $DeployOnly) {
    throw 'Use either -ProvisionOnly or -DeployOnly, not both.'
}

if ($ProvisionOnly) {
    Write-Host 'Running azd provision...' -ForegroundColor Cyan
    Invoke-NativeCommand -Command { azd provision --no-prompt } -FailureMessage 'azd provision failed. If the error mentions expired auth, run azd auth login and retry.'
    return
}

if ($DeployOnly) {
    Invoke-AzdDeployWithFallback -AllowFallback
    return
}

Write-Host 'Running azd provision...' -ForegroundColor Cyan
Invoke-NativeCommand -Command { azd provision --no-prompt } -FailureMessage 'azd provision failed. If the error mentions expired auth, run azd auth login and retry.'

Invoke-AzdDeployWithFallback -AllowFallback

Write-Host ''
Write-Host 'azd deployment completed successfully.' -ForegroundColor Green
Write-Host 'Tip: If azd ever appears stuck at "Initialize bicep provider", refresh auth with: azd auth login' -ForegroundColor DarkYellow
