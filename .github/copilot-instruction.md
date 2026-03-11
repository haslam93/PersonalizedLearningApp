---
title: Copilot project memory
description: Persistent summary of the Azure AI Upskilling Hub application, GitHub configuration, deployment model, and maintenance instructions for future coding sessions
author: Microsoft
ms.date: 2026-03-11
ms.topic: reference
keywords:
  - copilot
  - project memory
  - github actions
  - azure app service
  - blazor
estimated_reading_time: 7
---

## Purpose

Use this file as the primary project memory for future work in this repository.
When making meaningful changes, update this file and also update
[changelog.md](../changelog.md) in the same commit.

## App identity

* App name: `UpskillTracker`
* User-facing title: `Azure AI Upskilling Hub`
* Repository: `haslam93/PersonalizedLearningApp`
* Primary production URL: <https://halearningapp.azurewebsites.net>
* Custom domain: <https://skilling.hammadaslam.com>
* Current access model: PIN gate in the app UI backed by Azure app setting `AccessPin`

## Current application architecture

* Framework: .NET 8 Blazor Web App
* UI library: MudBlazor
* Data layer: EF Core with SQLite
* Main functional areas:
  * Dashboard
  * Plan
  * Timeline
  * Resources
  * Notes
* Main service: `TrackerService`
* Database storage:
  * local development uses the project data path
  * Azure uses `Data Source=/home/data/upskilltracker.db`

## Important source files

* App shell: [src/UpskillTracker/Components/App.razor](../src/UpskillTracker/Components/App.razor)
* Main layout: [src/UpskillTracker/Components/Layout/MainLayout.razor](../src/UpskillTracker/Components/Layout/MainLayout.razor)
* PIN gate: [src/UpskillTracker/Components/Features/PinGate.razor](../src/UpskillTracker/Components/Features/PinGate.razor)
* Styles: [src/UpskillTracker/wwwroot/app.css](../src/UpskillTracker/wwwroot/app.css)
* Infra entry point: [infra/main.bicep](../infra/main.bicep)
* RG-scoped infra: [infra/resources.bicep](../infra/resources.bicep)
* CI workflow: [workflows/ci.yml](workflows/ci.yml)
* CD workflow: [workflows/cd.yml](workflows/cd.yml)
* Main docs: [../README.md](../README.md)
* Architecture doc: [../arch.md](../arch.md)

## Azure deployment state

* Subscription ID: `65135929-9d4d-48c2-bea4-86a24070ea4c`
* Tenant ID: `16b3c013-d300-468d-ac64-7eda0820b6d3`
* Resource group: `hammadlearningapp`
* Web app: `halearningapp`
* App Service plan: `plan-personal-learning-tn33sb`
* App Service plan SKU target: `B2`
* App Insights: `appi-personal-learning-tn33sb`
* Log Analytics workspace: `log-personal-learning-tn33sb`
* Hosting model: Linux App Service

## Current authentication and access model

* App Service Authentication is intentionally disabled
* Access is controlled by the Blazor `PinGate` component
* The PIN must not be stored in source code
* The PIN is read from configuration key `AccessPin`
* In Azure, `AccessPin` is stored as an app setting
* In GitHub Actions, the PIN is supplied through a repository secret

## GitHub Actions configuration

### Secrets currently present on GitHub

Record secret names and purpose only. Do not store secret values in this file.

* `APP_ACCESS_PIN` - access PIN injected into Azure app setting `AccessPin`
* `AZURE_CLIENT_ID` - OIDC app/client ID used by GitHub Actions for Azure login
* `AZURE_SUBSCRIPTION_ID` - Azure subscription used by deployment workflow
* `AZURE_TENANT_ID` - Azure tenant used by deployment workflow

### Variables currently present on GitHub

* `AZURE_WEBAPP_NAME=halearningapp`

## GitHub Actions pipeline summary

### CI workflow

File: [workflows/ci.yml](workflows/ci.yml)

* Runs on pull requests
* Runs on pushes to:
  * `main`
  * `develop`
  * `feature/**`
* Steps:
  * checkout
  * setup .NET 8
  * restore
  * build

### CD workflow

File: [workflows/cd.yml](workflows/cd.yml)

* Runs on pushes to `main`
* Can also run manually with `workflow_dispatch`
* Uses GitHub OIDC with `azure/login@v2`
* Publishes the app and creates a zip package
* Deploys infrastructure only when:
  * files under `infra/**` change, or
  * `azure.yaml` changes, or
  * manual run sets `deployInfra=true`
* Deploys infrastructure with `az deployment group create`
* Deploys app package with `az webapp deploy`
* Passes `APP_ACCESS_PIN` into Bicep as secure parameter `accessPin`
* Targets App Service plan SKU `B2`

## Known platform notes

* `azd deploy` has previously failed due to `Microsoft.Web` deployment history `504 Gateway Timeout`
* The direct GitHub Actions CD workflow is the more reliable deployment path right now
* The custom domain currently exists, but HTTPS certificate binding should be revalidated before relying on it
* The default Azure URL is the most reliable validation endpoint

## Important historical context

* The app started as a personalized training plan and evolved into a working tracker app
* A client-side hardcoded PIN was originally used
* The app was migrated to App Service Authentication with Microsoft Entra ID
* The App Service Authentication callback flow later failed with `401` responses
* The repository was intentionally reverted to the PIN model, but with the PIN moved out of source code into deployment secrets and Azure app settings
* The App Service plan was increased from `B1` to `B2`

## Rules for future changes

* Update this file when architecture, deployment, auth, secrets, variables, URLs, or Azure resources change
* Update [../changelog.md](../changelog.md) in the same commit for any meaningful project, infra, auth, workflow, or documentation change
* Never write secret values into source-controlled files
* Prefer documenting secret names and purpose only
* If authentication is changed again, document both the live Azure change and the long-term Bicep/workflow change here
