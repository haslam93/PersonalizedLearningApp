param(
    [Parameter(Mandatory = $true)]
    [string]$ResourceGroupName,

    [Parameter(Mandatory = $true)]
    [string]$WebAppName,

    [string]$Location = 'eastus2',
    [string]$AppServicePlanName = '',
    [string]$Sku = 'B1',
    [string]$ProjectPath = '.\src\UpskillTracker\UpskillTracker.csproj',
    [string]$PublishOutput = '.\.artifacts\publish',
    [string]$PackagePath = '.\.artifacts\upskilltracker.zip',
    [string]$Runtime = 'DOTNETCORE|8.0',
    [string]$SubscriptionId = ''
)

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$resolvedPlanName = if ([string]::IsNullOrWhiteSpace($AppServicePlanName)) { "$WebAppName-plan" } else { $AppServicePlanName }
$resolvedProjectPath = if ([System.IO.Path]::IsPathRooted($ProjectPath)) {
    Resolve-Path $ProjectPath
}
else {
    Resolve-Path (Join-Path $repoRoot $ProjectPath)
}

$resolvedPublishOutput = if ([System.IO.Path]::IsPathRooted($PublishOutput)) {
    $PublishOutput
}
else {
    Join-Path $repoRoot $PublishOutput
}

$resolvedPackagePath = if ([System.IO.Path]::IsPathRooted($PackagePath)) {
    $PackagePath
}
else {
    Join-Path $repoRoot $PackagePath
}

function Assert-Command {
    param([string]$Name)

    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "Required command '$Name' was not found in PATH."
    }
}

Assert-Command -Name 'az'
Assert-Command -Name 'dotnet'

Set-Location $repoRoot

if (-not [string]::IsNullOrWhiteSpace($SubscriptionId)) {
    az account set --subscription $SubscriptionId | Out-Null
}

Write-Host "Publishing the app..." -ForegroundColor Cyan
if (Test-Path $resolvedPublishOutput) {
    Remove-Item $resolvedPublishOutput -Recurse -Force
}

New-Item -ItemType Directory -Path $resolvedPublishOutput -Force | Out-Null

dotnet publish $resolvedProjectPath -c Release -o $resolvedPublishOutput | Out-Host

if (Test-Path $resolvedPackagePath) {
    Remove-Item $resolvedPackagePath -Force
}

$packageDirectory = Split-Path -Parent $resolvedPackagePath
if (-not (Test-Path $packageDirectory)) {
    New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null
}

Compress-Archive -Path (Join-Path $resolvedPublishOutput '*') -DestinationPath $resolvedPackagePath -Force

Write-Host "Provisioning Azure resources..." -ForegroundColor Cyan
az group create --name $ResourceGroupName --location $Location | Out-Null

$planExists = az appservice plan show --resource-group $ResourceGroupName --name $resolvedPlanName --query name --output tsv 2>$null
if (-not $planExists) {
    az appservice plan create --resource-group $ResourceGroupName --name $resolvedPlanName --sku $Sku --is-linux | Out-Null
}

$appExists = az webapp show --resource-group $ResourceGroupName --name $WebAppName --query name --output tsv 2>$null
if (-not $appExists) {
    az webapp create --resource-group $ResourceGroupName --plan $resolvedPlanName --name $WebAppName --runtime $Runtime | Out-Null
}

az webapp config appsettings set --resource-group $ResourceGroupName --name $WebAppName --settings `
    ASPNETCORE_ENVIRONMENT=Production `
    Storage__ConnectionString='Data Source=/home/data/upskilltracker.db' `
    WEBSITES_ENABLE_APP_SERVICE_STORAGE=true | Out-Null

Write-Host "Deploying package to Azure App Service..." -ForegroundColor Cyan
az webapp deploy --resource-group $ResourceGroupName --name $WebAppName --src-path $resolvedPackagePath --type zip --clean true | Out-Null

$hostname = az webapp show --resource-group $ResourceGroupName --name $WebAppName --query defaultHostName --output tsv

Write-Host ''
Write-Host 'Deployment completed successfully.' -ForegroundColor Green
Write-Host "Web app URL: https://$hostname"
Write-Host "Publish profile command: .\scripts\get-publish-profile.ps1 -ResourceGroupName $ResourceGroupName -WebAppName $WebAppName"
