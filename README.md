---
title: Hammad's Learning Portal
description: Personal learning tracker with dual announcement streams, GitHub Copilot chat, notes, resources, timelines, and Azure deployment automation
author: Microsoft
ms.date: 2026-03-17
ms.topic: overview
keywords:
   - learning portal
  - training tracker
  - blazor
  - app service
  - github actions
estimated_reading_time: 6
---

## Overview

This repository contains a Blazor-based learning hub for Azure AI and App
Innovation upskilling. It combines a structured training tracker, a live
dual-stream announcement experience, and an in-app GitHub Copilot chat
experience that is grounded in your saved plan, notes, and resources.

## High-level architecture

The app is organized as a lightweight interactive Blazor experience hosted on
Azure App Service.

* The browser loads a Blazor web app with a lightweight PIN gate in the main layout.
* Feature views for Dashboard, Plan, Timeline, Resources, Notes, and Copilot run
   through shared application services.
* `AnnouncementFeedService` loads and caches both official Microsoft updates and
  curated thought-leader or industry posts for the dashboard feed.
* `CopilotAuthService` and `CopilotChatService` manage in-app GitHub OAuth and
  grounded Copilot chat sessions.
* EF Core writes training data, resources, and notes into a SQLite database.
* GitHub Actions builds the app and deploys `main` to the Azure web app.

For the Mermaid version of the architecture, see [arch.md](arch.md).

## Portal screenshot

![Hammad's Learning Portal](docs/images/portal-home.png)

It is designed to help you:

* Track plan items, notes, evidence, and timelines
* Add new work as customer projects shift priorities
* Review a live dashboard feed that switches between Microsoft updates and curated industry posts that matter to your plan
* Keep a reusable resource library for Microsoft Foundry, GitHub Copilot,
  App Service, Container Apps, and related topics
* Ask grounded GitHub Copilot questions against your saved learning data
* Run locally first and deploy easily to Azure App Service
* Push updates through GitHub Actions CI/CD

## Main app features

* Dashboard with completion and focus metrics
* Dashboard cards with readable semantic color accents for progress, videos, resources, notes, and announcements
* Dynamic home and dashboard summary cards that react to live tracker data instead of fixed promotional copy
* Dual announcement streams with Microsoft updates and thought-leader or industry posts, plus actions to open and save useful updates
* Planner tab for adding and editing training items, with direct task links suggested from the shared resource library
* Timeline tab grouped by month
* Resources tab with editable sections and links that power task-level suggestions across the app
* Notes tab for reflections, architecture notes, and lab takeaways
* Copilot tab with GitHub OAuth sign-in, runtime model discovery, and tracker-grounded chat tools
* PIN login backed by a secure Azure app setting and GitHub Actions secret
* Seeded content for the March to September 2026 plan, including Azure SRE Agent

## UI notes

The current shell avoids fixed labels where tracker data is already available.

* The top app bar uses the product name Hammad's Learning Portal and no hardcoded date badge
* The home view opens with a tracker-driven overview card instead of a commentary-style hero title
* The dashboard summary card adapts to overdue, in-progress, and completed work with short, direct headings
* Dashboard cards use soft semantic color surfaces so sections are easier to scan without sacrificing contrast
* The dashboard includes a dedicated video watch tracker with queue, seen count, and completion progress
* The announcement section uses a stream switcher so Microsoft updates and curated industry posts stay separate
* Reminder copy stays short and action-oriented so it remains useful as the plan changes

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

Local development uses the configured `AccessPin` value if present. In Azure,
the PIN is stored as an app setting and supplied through deployment secrets, not
hardcoded in source.

## GitHub Copilot SDK setup

The app now includes a Copilot chat tab backed by the official GitHub Copilot
SDK. The current Azure production callback URL is
`https://halearningapp.azurewebsites.net/signin-github`.

### Where to get the real GitHub OAuth values

Create a GitHub OAuth App in GitHub Developer Settings:

1. Sign in to GitHub.
2. Open `Settings` > `Developer settings` > `OAuth Apps`.
3. Select `New OAuth App`.
4. Create one app per environment you want to support.

Recommended callback URLs for this repo:

* Local development: `https://localhost:7172/signin-github`
* Azure App Service: `https://<your-web-app-host>/signin-github`

Use the values GitHub shows after the app is created:

* `Client ID` -> `GitHubOAuth:ClientId`
* `Client Secret` -> `GitHubOAuth:ClientSecret`

Because GitHub OAuth Apps use a fixed callback URL, the cleanest setup is one
OAuth app for local development and a second OAuth app for the Azure site.

### Where to put the values locally

For local development, keep the secret out of source control and use ASP.NET
Core user secrets:

1. Initialize user secrets for the project if needed:

   ```powershell
   dotnet user-secrets init --project .\src\UpskillTracker\UpskillTracker.csproj
   ```

2. Store the GitHub OAuth client id:

   ```powershell
   dotnet user-secrets set "GitHubOAuth:ClientId" "<your-local-client-id>" --project .\src\UpskillTracker\UpskillTracker.csproj
   ```

3. Store the GitHub OAuth client secret:

   ```powershell
   dotnet user-secrets set "GitHubOAuth:ClientSecret" "<your-local-client-secret>" --project .\src\UpskillTracker\UpskillTracker.csproj
   ```

4. Optionally set the default model:

   ```powershell
   dotnet user-secrets set "CopilotSdk:DefaultModel" "gpt-5" --project .\src\UpskillTracker\UpskillTracker.csproj
   ```

The app already uses `/signin-github` as the callback path, so you do not need
to change code after the secrets are set.

### Local Copilot CLI behavior

The .NET SDK downloads the matching Copilot CLI during build and copies it into
the app output automatically. On Windows, local development uses the bundled
Windows CLI from the build output.

## Azure deployment

The easiest deployment path is now `azd`.

### Recommended: one-command deployment with azd

1. Sign in first:

   ```powershell
   az login
   azd auth login
   ```

2. From the repository root, run:

   ```powershell
   .\scripts\deploy-azd.ps1 -EnvironmentName personal-learning -Location eastus2 -ResourceGroupName rg-personal-learning -WebAppName <unique-web-app-name>
   ```

3. For later updates, rerun the same command or use:

   ```powershell
   azd deploy
   ```

The wrapper runs `azd provision` and `azd deploy` as separate steps so failures
are easier to identify.

Before the first Copilot-enabled Azure deployment, set the GitHub OAuth values
in the azd environment:

```powershell
azd env set GITHUB_OAUTH_CLIENT_ID <your-production-client-id>
azd env set GITHUB_OAUTH_CLIENT_SECRET <your-production-client-secret>
azd env set YOUTUBE_API_KEY <your-youtube-api-key>
azd env set COPILOT_DEFAULT_MODEL gpt-5
```

You can also pass these values directly to the wrapper script:

```powershell
.\scripts\deploy-azd.ps1 -EnvironmentName personal-learning -Location eastus2 -ResourceGroupName rg-personal-learning -WebAppName <unique-web-app-name> -GitHubOAuthClientId <client-id> -GitHubOAuthClientSecret <client-secret> -YouTubeApiKey <youtube-api-key>
```

If you already have the App Service created, you can place the same values in
Azure Portal under the web app's `Environment variables` page:

* `GitHubOAuth__ClientId`
* `GitHubOAuth__ClientSecret`
* `GitHubOAuth__CallbackPath` = `/signin-github`
* `YouTube__ApiKey`
* `CopilotSdk__DefaultModel` = `gpt-5`

### GitHub Actions production secret for YouTube

The production deployment workflow reads the YouTube key from the repository or
environment secret named `APP_YOUTUBE_API_KEY` and pushes it into the Azure web
app as the `YouTube__ApiKey` application setting during deployment.

If you are deploying from the `production` GitHub environment, add the secret
there so environment protection rules continue to apply. A repository-level
secret also works if you do not need environment-scoped separation.

The `azd` path uses these files:

* [azure.yaml](azure.yaml)
* [infra/main.bicep](infra/main.bicep)
* [infra/resources.bicep](infra/resources.bicep)
* [infra/main.parameters.json](infra/main.parameters.json)
* [scripts/deploy-azd.ps1](scripts/deploy-azd.ps1)

This deployment provisions:

* Azure App Service plan
* Linux App Service web app
* Log Analytics workspace
* Application Insights

### Common azd auth issue

If deployment appears stuck around `Initialize bicep provider` or you see an
AAD refresh token expiration error, refresh the Azure Developer CLI login and
run the wrapper again:

```powershell
azd auth login
```

If browser login is inconvenient, use device code:

```powershell
.\scripts\deploy-azd.ps1 -EnvironmentName personal-learning -Location eastus2 -ResourceGroupName rg-personal-learning -WebAppName <unique-web-app-name> -UseDeviceCode
```

### Microsoft.Web gateway timeout during azd deploy

If `azd deploy` fails while checking App Service deployment history with a
`504 Gateway Timeout` from `Microsoft.Web`, the wrapper now falls back to a
direct zip deployment to the existing App Service.

This means you can rerun the same command and let the script continue with the
fallback path automatically.

It also configures these app settings:

* `APPLICATIONINSIGHTS_CONNECTION_STRING`
* `ASPNETCORE_ENVIRONMENT=Production`
* `Storage__ConnectionString=Data Source=/home/data/upskilltracker.db`
* `WEBSITES_ENABLE_APP_SERVICE_STORAGE=true`

### Direct deployment script

If you prefer Azure CLI without `azd`, this script is still available and now
resolves paths correctly even when run from the `scripts` folder:

```powershell
.\scripts\deploy-azure.ps1 -ResourceGroupName rg-upskilltracker -WebAppName <unique-web-app-name>
```

That script now publishes for `linux-x64` so the bundled Copilot CLI matches
the Linux App Service host.

## GitHub Actions CD setup

After the first Azure deployment:

1. Create a Microsoft Entra app registration or user-assigned identity for
   GitHub Actions OpenID Connect access.
2. Grant the identity access to the App Service deployment scope.
3. Add these GitHub Actions secrets:

   * `AZURE_CLIENT_ID`
   * `AZURE_TENANT_ID`
   * `AZURE_SUBSCRIPTION_ID`
   * `APP_ACCESS_PIN`
   * `APP_GH_OAUTH_CLIENT_ID`
   * `APP_GH_OAUTH_CLIENT_SECRET`

4. Add these repository variables:

   * `AZURE_WEBAPP_NAME`

5. Push to `main` or run the CD workflow manually.

This repository now uses OpenID Connect for GitHub Actions CD instead of a
publish profile, which avoids basic authentication and aligns with App Service
policy restrictions.

The CD workflow also:

* publishes the app for `linux-x64` so the bundled Copilot CLI matches App Service
* deploys infrastructure through Bicep on each run so secure app settings stay in sync
* applies the secure `AccessPin`, GitHub OAuth, and Copilot model settings through Azure deployment parameters
* targets App Service plan SKU `B2` by default

## Repository automation

* CI workflow: [.github/workflows/ci.yml](.github/workflows/ci.yml)
* CD workflow: [.github/workflows/cd.yml](.github/workflows/cd.yml)
* Azure deployment script: [scripts/deploy-azure.ps1](scripts/deploy-azure.ps1)
* Publish profile helper: [scripts/get-publish-profile.ps1](scripts/get-publish-profile.ps1)
