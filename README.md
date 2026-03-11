---
title: Azure AI Upskilling Hub
description: Personal training tracker for Azure AI, App Innovation, notes, resources, timelines, and deployment automation
author: Microsoft
ms.date: 2026-03-10
ms.topic: overview
keywords:
  - azure ai
  - training tracker
  - blazor
  - app service
  - github actions
estimated_reading_time: 6
---

## Overview

This repository contains a small Blazor-based training tracker for Azure AI and
App Innovation learning.

It is designed to help you:

* Track plan items, notes, evidence, and timelines
* Add new work as customer projects shift priorities
* Keep a reusable resource library for Microsoft Foundry, GitHub Copilot,
  App Service, Container Apps, and related topics
* Run locally first and deploy easily to Azure App Service
* Push updates through GitHub Actions CI/CD

## Main app features

* Dashboard with completion and focus metrics
* Planner tab for adding and editing training items
* Timeline tab grouped by month
* Resources tab with editable sections and links
* Notes tab for reflections, architecture notes, and lab takeaways
* Seeded content for the March to September 2026 plan, including Azure SRE Agent

## Local development

1. Restore dependencies:

   ```powershell
   dotnet restore .\src\UpskillTracker\UpskillTracker.csproj
   ```

2. Run the app:

   ```powershell
   dotnet run --project .\src\UpskillTracker\UpskillTracker.csproj
   ```

3. Open the local URL shown in the terminal.

The app stores its SQLite database in the local `Data` folder by default.

## Azure deployment

Use the deployment script to provision an Azure App Service plan, create the web
app, publish the app, and deploy it.

```powershell
.\scripts\deploy-azure.ps1 -ResourceGroupName rg-upskilltracker -WebAppName <unique-web-app-name>
```

The script sets these app settings for you:

* `ASPNETCORE_ENVIRONMENT=Production`
* `Storage__ConnectionString=Data Source=/home/data/upskilltracker.db`
* `WEBSITES_ENABLE_APP_SERVICE_STORAGE=true`

## GitHub Actions CD setup

After the first Azure deployment:

1. Export the publish profile:

   ```powershell
   .\scripts\get-publish-profile.ps1 -ResourceGroupName rg-upskilltracker -WebAppName <unique-web-app-name>
   ```

2. Copy the publish profile contents into the GitHub secret
   `AZURE_WEBAPP_PUBLISH_PROFILE`.
3. Add a repository variable named `AZURE_WEBAPP_NAME` with the App Service name.
4. Push to `main` or run the CD workflow manually.

## Repository automation

* CI workflow: [.github/workflows/ci.yml](.github/workflows/ci.yml)
* CD workflow: [.github/workflows/cd.yml](.github/workflows/cd.yml)
* Azure deployment script: [scripts/deploy-azure.ps1](scripts/deploy-azure.ps1)
* Publish profile helper: [scripts/get-publish-profile.ps1](scripts/get-publish-profile.ps1)
