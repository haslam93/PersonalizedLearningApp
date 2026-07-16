---
title: Copilot project memory
description: Persistent summary of the Azure AI Upskilling Hub application, GitHub configuration, deployment model, and maintenance instructions for future coding sessions
author: Microsoft
ms.date: 2026-07-16
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
* User-facing title: `Hammad's Learning Portal`
* Repository: `haslam93/PersonalizedLearningApp`
* Primary production URL: <https://halearningapp.azurewebsites.net>
* Custom domain: <https://skilling.hammadaslam.com>
* Current access model: PIN gate in the app UI backed by Azure app setting `AccessPin`

## Current application architecture

* Framework: .NET 8 Blazor Web App
* UI library: MudBlazor
* Data layer: EF Core with SQLite locally and Azure Database for PostgreSQL Flexible Server in production
* Main functional areas:
  * Dashboard
  * Live announcements
  * Plan
  * Certifications
  * Timeline
  * Learning History
  * My Tools
  * Resources
  * Notes
  * Copilot
* Resource suggestion behavior:
  * the Plan tab shows task-level matching links from the shared Resources library
  * the Dashboard focus list also surfaces matching links for active items
  * the Resources tab remains the single place where users add or edit links that power those suggestions
* Learning-plan behavior:
  * `TrainingPlanPrioritizer` ranks overdue, due-soon, active, core, and nice-to-have items consistently across the Dashboard and Plan tabs
  * core completion excludes `LearningLane.Stretch` items unless they are project-driven
  * Microsoft Fabric and Azure Databricks beginner tasks are inserted into existing databases by title without overwriting user changes
  * certifications reuse the existing `TrainingItems` table with `TrainingItemType.Certification`, avoiding a production schema migration
  * the Certifications tab tracks target date, progress, status, preparation notes, and evidence and can import curated Microsoft, GitHub, and Databricks goals
  * AI-103 is the seeded near-term certification goal with an August 31, 2026 target
* Learning-history behavior:
  * `LearningActivities` is an append-only event table for starts, progress, completions, certifications, resource and announcement reads, watched videos, reflections, and personal-tool launches
  * existing production data is backfilled once behind metadata key `learning-history-backfill-v1`
  * detailed activity from the configured legacy SQLite database uses the separate import marker `legacy-sqlite-learning-history-import-v1`
  * live activity writes use provider-specific conflict-safe inserts so duplicate history tracking never rolls back the primary user action
  * repeated resource, video, announcement, and tool activity is deduplicated per source and UTC day
  * history rows keep denormalized titles and details and do not use foreign keys, so source deletion does not erase the learning record
  * browser-local history dates and times are produced through `BrowserTimeZoneService`; do not group user-facing calendar days using the Azure host time zone
  * the History heatmap uses a roving keyboard tab stop with arrow-key navigation and does not frame inactive days as failures
* Navigation behavior:
  * overview, reminder, dashboard, and planner summary controls navigate to the relevant Plan filter
  * cross-tab Plan requests include a monotonically increasing request id so later Home renders do not overwrite a user's manual Plan filters
  * the forward Timeline excludes completed work; historical accomplishments belong in Learning History
* Personal tools:
  * the My Tools tab mirrors the four current entries on `https://hammadaslam.com/tools-and-demos/`
  * tool launches are recorded in Learning History
* UI shell behavior:
  * the main app bar uses a minimal title and no fixed date badge
  * the Home page opens with a tracker-driven workspace overview instead of hardcoded focus-area copy
  * the Dashboard summary card adapts to live completion, in-progress, and overdue counts
  * the Dashboard emphasizes schedule pressure, core-plan completion, and a ranked "Do next" list
  * overdue optional work does not trigger the main reminder warning
  * the Dashboard announcement feed initially renders six items and uses show-more/show-fewer controls to avoid excessively long mobile pages
  * data tables inside surface cards keep `overflow-x: auto` so wide rows stay horizontally scrollable and the trailing action column (for example the Plan tab Edit button) stays reachable
  * the Plan tab uses a single-column layout: a full-width training items table on top and the "Add/Edit training item" form below it, and the per-row Edit button scrolls to that form via the `scrollToElement` helper in `wwwroot/app.js`
* Live feed behavior:
  * the Dashboard shows a server-side cached feed of official Microsoft announcements
  * each filtered feed initially displays six announcements and can be expanded in six-item increments
  * users can open an announcement directly or save it into the shared Resources library
* Main services:
  * `TrackerService`
  * `AnnouncementFeedService`
  * `CopilotAuthService`
  * `CopilotChatService`
* Database storage:
  * local development uses SQLite in the project data path
  * Azure uses a privately networked PostgreSQL Flexible Server
  * the web app authenticates to PostgreSQL with its managed identity

## Important source files

* App shell: [src/UpskillTracker/Components/App.razor](../src/UpskillTracker/Components/App.razor)
* Main layout: [src/UpskillTracker/Components/Layout/MainLayout.razor](../src/UpskillTracker/Components/Layout/MainLayout.razor)
* PIN gate: [src/UpskillTracker/Components/Features/PinGate.razor](../src/UpskillTracker/Components/Features/PinGate.razor)
* Dashboard: [src/UpskillTracker/Components/Features/DashboardView.razor](../src/UpskillTracker/Components/Features/DashboardView.razor)
* Training planner: [src/UpskillTracker/Components/Features/TrainingPlannerView.razor](../src/UpskillTracker/Components/Features/TrainingPlannerView.razor)
* Certifications: [src/UpskillTracker/Components/Features/CertificationsView.razor](../src/UpskillTracker/Components/Features/CertificationsView.razor)
* Learning history: [src/UpskillTracker/Components/Features/LearningHistoryView.razor](../src/UpskillTracker/Components/Features/LearningHistoryView.razor)
* Learning heatmap: [src/UpskillTracker/Components/Features/LearningHeatmap.razor](../src/UpskillTracker/Components/Features/LearningHeatmap.razor)
* Personal tools: [src/UpskillTracker/Components/Features/LearningToolsView.razor](../src/UpskillTracker/Components/Features/LearningToolsView.razor)
* Home page: [src/UpskillTracker/Components/Pages/Home.razor](../src/UpskillTracker/Components/Pages/Home.razor)
* Copilot chat UI: [src/UpskillTracker/Components/Features/CopilotChatView.razor](../src/UpskillTracker/Components/Features/CopilotChatView.razor)
* Styles: [src/UpskillTracker/wwwroot/app.css](../src/UpskillTracker/wwwroot/app.css)
* Announcement feed service: [src/UpskillTracker/Services/AnnouncementFeedService.cs](../src/UpskillTracker/Services/AnnouncementFeedService.cs)
* Certification catalog: [src/UpskillTracker/Services/CertificationCatalog.cs](../src/UpskillTracker/Services/CertificationCatalog.cs)
* Plan prioritization: [src/UpskillTracker/Services/TrainingPlanPrioritizer.cs](../src/UpskillTracker/Services/TrainingPlanPrioritizer.cs)
* Activity recorder: [src/UpskillTracker/Services/LearningActivityRecorder.cs](../src/UpskillTracker/Services/LearningActivityRecorder.cs)
* Personal tool catalog: [src/UpskillTracker/Services/LearningToolCatalog.cs](../src/UpskillTracker/Services/LearningToolCatalog.cs)
* Copilot chat service: [src/UpskillTracker/Services/CopilotChatService.cs](../src/UpskillTracker/Services/CopilotChatService.cs)
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
* App Service plan SKU target: `P0v3`
* App Insights: `appi-personal-learning-tn33sb`
* Log Analytics workspace: `log-personal-learning-tn33sb`
* Hosting model: Linux App Service

## Current authentication and access model

* App Service Authentication is intentionally disabled
* Access is controlled by the Blazor `PinGate` component
* GitHub OAuth is used inside the app for the Copilot tab only
* The PIN must not be stored in source code
* The PIN is read from configuration key `AccessPin`
* In Azure, `AccessPin` is stored as an app setting
* In GitHub Actions, the PIN is supplied through a repository secret
* GitHub OAuth values are read from Azure app settings under `GitHubOAuth__*`
* The Copilot default model is read from `CopilotSdk__DefaultModel`

## GitHub Actions configuration

### Secrets currently present on GitHub

Record secret names and purpose only. Do not store secret values in this file.

* `APP_ACCESS_PIN` - access PIN injected into Azure app setting `AccessPin`
* `APP_GH_OAUTH_CLIENT_ID` - GitHub OAuth app client ID injected into Azure app setting `GitHubOAuth__ClientId`
* `APP_GH_OAUTH_CLIENT_SECRET` - GitHub OAuth app client secret injected into Azure app setting `GitHubOAuth__ClientSecret`
* `APP_POSTGRES_ADMIN_PASSWORD` - bootstrap PostgreSQL administrator password used during infrastructure deployment
* `APP_YOUTUBE_API_KEY` - YouTube Data API key injected into Azure app setting `YouTube__ApiKey`
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
* Publishes the app for `linux-x64` and creates a zip package
* Bundles the matching Copilot CLI into the deployment artifact
* Deploys infrastructure on each run so secure settings remain synchronized
* Deploys infrastructure with `az deployment group create`
* Deploys app package with `az webapp deploy`
* Passes `APP_ACCESS_PIN` into Bicep as secure parameter `accessPin`
* Passes `APP_GH_OAUTH_CLIENT_ID` and `APP_GH_OAUTH_CLIENT_SECRET` into Bicep for the in-app Copilot sign-in flow
* Targets App Service plan SKU `P0v3`
* Provisions PostgreSQL and configures the web app identity as its Microsoft Entra administrator

### Cost control tag workflow

File: [workflows/cost-control-tag.yml](workflows/cost-control-tag.yml)

* Runs daily at 06:00 UTC and can also run manually with `workflow_dispatch`
* Uses GitHub OIDC with `azure/login@v2`
* Finds the single PostgreSQL Flexible Server in `hammadlearningapp` and checks its runtime state
* Exits without changing anything when PostgreSQL is already `Ready`
* Starts PostgreSQL when it is `Stopped`, waits until it is `Ready`, and then applies `CostControl=Ignore` to that server
* The `CostControl=Ignore` tag is also defined in [../infra/main.bicep](../infra/main.bicep) so it is applied on every infrastructure deployment

## Known platform notes

* `azd deploy` has previously failed due to `Microsoft.Web` deployment history `504 Gateway Timeout`
* The direct GitHub Actions CD workflow is the more reliable deployment path right now
* The custom domain currently exists, but HTTPS certificate binding should be revalidated before relying on it
* The default Azure URL is the most reliable validation endpoint
* The Copilot SDK requires a permission handler when creating sessions; current code uses `PermissionHandler.ApproveAll`

## Important historical context

* The app started as a personalized training plan and evolved into a working tracker app
* The dashboard later gained a live Microsoft announcement feed
* The app later gained an in-app GitHub Copilot SDK chat experience with GitHub OAuth
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
* Keep the task-to-resource suggestion experience working so users can share the repo and personalize plans without code changes
