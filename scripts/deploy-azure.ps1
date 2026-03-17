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
    [string]$RuntimeIdentifier = 'linux-x64',
    [string]$SubscriptionId = '',
    [string]$GitHubOAuthClientId = '',
    [string]$GitHubOAuthClientSecret = '',
    [string]$YouTubeApiKey = '',
    [string]$CopilotCliPath = '',
    [string]$CopilotDefaultModel = 'gpt-5',
    [switch]$SkipProvisioning
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

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

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

function New-PosixZipArchive {
    param(
        [Parameter(Mandatory = $true)]
        [string]$SourceDirectory,

        [Parameter(Mandatory = $true)]
        [string]$DestinationPath
    )

    if (Test-Path $DestinationPath) {
        Remove-Item $DestinationPath -Force
    }

    $sourceRoot = (Resolve-Path $SourceDirectory).Path.TrimEnd('\')
    $sourcePrefix = "$sourceRoot\"
    $destinationDirectory = Split-Path -Parent $DestinationPath

    if (-not (Test-Path $destinationDirectory)) {
        New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
    }

    $fileStream = [System.IO.File]::Open($DestinationPath, [System.IO.FileMode]::CreateNew)

    try {
        $archive = New-Object System.IO.Compression.ZipArchive($fileStream, [System.IO.Compression.ZipArchiveMode]::Create, $false)

        try {
            Get-ChildItem -Path $sourceRoot -Recurse -Force | ForEach-Object {
                $relativePath = $_.FullName.Substring($sourcePrefix.Length).Replace('\', '/')

                if ([string]::IsNullOrWhiteSpace($relativePath)) {
                    return
                }

                if ($_.PSIsContainer) {
                    $directoryEntry = $archive.CreateEntry($relativePath.TrimEnd('/') + '/')
                    $directoryEntry.ExternalAttributes = (493 -bor 16384) -shl 16
                    return
                }

                $entry = $archive.CreateEntry($relativePath, [System.IO.Compression.CompressionLevel]::Optimal)
                if ($relativePath -like 'runtimes/linux-*/native/copilot') {
                    $entry.ExternalAttributes = (493 -bor 32768) -shl 16
                }
                $entryStream = $entry.Open()

                try {
                    $inputStream = [System.IO.File]::OpenRead($_.FullName)

                    try {
                        $inputStream.CopyTo($entryStream)
                    }
                    finally {
                        $inputStream.Dispose()
                    }
                }
                finally {
                    $entryStream.Dispose()
                }
            }
        }
        finally {
            $archive.Dispose()
        }
    }
    finally {
        $fileStream.Dispose()
    }
}

if (-not [string]::IsNullOrWhiteSpace($SubscriptionId)) {
    Invoke-NativeCommand -Command { az account set --subscription $SubscriptionId } -FailureMessage 'Unable to select the requested Azure subscription.'
}

Write-Host "Publishing the app..." -ForegroundColor Cyan
if (Test-Path $resolvedPublishOutput) {
    Remove-Item $resolvedPublishOutput -Recurse -Force
}

New-Item -ItemType Directory -Path $resolvedPublishOutput -Force | Out-Null

Invoke-NativeCommand -Command { dotnet publish $resolvedProjectPath -c Release -r $RuntimeIdentifier --self-contained false -o $resolvedPublishOutput } -FailureMessage 'dotnet publish failed.'

if (Test-Path $resolvedPackagePath) {
    Remove-Item $resolvedPackagePath -Force
}

New-PosixZipArchive -SourceDirectory $resolvedPublishOutput -DestinationPath $resolvedPackagePath

if (-not $SkipProvisioning) {
    Write-Host "Provisioning Azure resources..." -ForegroundColor Cyan
    Invoke-NativeCommand -Command { az group create --name $ResourceGroupName --location $Location } -FailureMessage 'Failed to create or validate the Azure resource group.'

    $planExists = az appservice plan show --resource-group $ResourceGroupName --name $resolvedPlanName --query name --output tsv 2>$null
    if (-not $planExists) {
        Invoke-NativeCommand -Command { az appservice plan create --resource-group $ResourceGroupName --name $resolvedPlanName --sku $Sku --is-linux } -FailureMessage 'Failed to create the App Service plan.'
    }

    $appExists = az webapp show --resource-group $ResourceGroupName --name $WebAppName --query name --output tsv 2>$null
    if (-not $appExists) {
        Invoke-NativeCommand -Command { az webapp create --resource-group $ResourceGroupName --plan $resolvedPlanName --name $WebAppName --runtime $Runtime } -FailureMessage 'Failed to create the App Service web app.'
    }
}

$appSettings = @(
    'ASPNETCORE_ENVIRONMENT=Production',
    'Storage__ConnectionString=Data Source=/home/data/upskilltracker.db',
    'WEBSITES_ENABLE_APP_SERVICE_STORAGE=true',
    'SCM_DO_BUILD_DURING_DEPLOYMENT=false',
    'ENABLE_ORYX_BUILD=false',
    'GitHubOAuth__CallbackPath=/signin-github',
    "CopilotSdk__DefaultModel=$CopilotDefaultModel"
)

if (-not [string]::IsNullOrWhiteSpace($GitHubOAuthClientId)) {
    $appSettings += "GitHubOAuth__ClientId=$GitHubOAuthClientId"
}

if (-not [string]::IsNullOrWhiteSpace($GitHubOAuthClientSecret)) {
    $appSettings += "GitHubOAuth__ClientSecret=$GitHubOAuthClientSecret"
}

if (-not [string]::IsNullOrWhiteSpace($YouTubeApiKey)) {
    $appSettings += "YouTube__ApiKey=$YouTubeApiKey"
}

if (-not [string]::IsNullOrWhiteSpace($CopilotCliPath)) {
    $appSettings += "CopilotSdk__CliPath=$CopilotCliPath"
}

Invoke-NativeCommand -Command {
    az webapp config appsettings set --resource-group $ResourceGroupName --name $WebAppName --settings $appSettings
} -FailureMessage 'Failed to apply App Service application settings.'

Write-Host "Deploying package to Azure App Service..." -ForegroundColor Cyan
Invoke-NativeCommand -Command {
    az webapp deploy --resource-group $ResourceGroupName --name $WebAppName --src-path $resolvedPackagePath --type zip --clean true --restart true --async true --track-status false --timeout 180000
} -FailureMessage 'Zip deployment to App Service failed.'

$hostname = az webapp show --resource-group $ResourceGroupName --name $WebAppName --query defaultHostName --output tsv

Write-Host ''
Write-Host 'Deployment completed successfully.' -ForegroundColor Green
Write-Host "Web app URL: https://$hostname"
Write-Host "Publish profile command: .\scripts\get-publish-profile.ps1 -ResourceGroupName $ResourceGroupName -WebAppName $WebAppName"
