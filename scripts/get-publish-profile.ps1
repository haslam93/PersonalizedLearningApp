param(
    [Parameter(Mandatory = $true)]
    [string]$ResourceGroupName,

    [Parameter(Mandatory = $true)]
    [string]$WebAppName,

    [string]$OutputPath = '.\.artifacts\publish-profile.publishsettings'
)

$ErrorActionPreference = 'Stop'

if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    throw "Azure CLI (az) is required."
}

$outputFile = Join-Path (Get-Location) $OutputPath
$outputDirectory = Split-Path -Parent $outputFile
if (-not (Test-Path $outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}

$profileXml = az webapp deployment list-publishing-profiles --resource-group $ResourceGroupName --name $WebAppName --xml
$profileXml | Set-Content -Path $outputFile -Encoding UTF8

Write-Host "Publish profile saved to $outputFile" -ForegroundColor Green
Write-Host 'Copy the full file contents into the GitHub secret named AZURE_WEBAPP_PUBLISH_PROFILE.'
