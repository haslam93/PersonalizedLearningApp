---
title: Changelog
description: Chronological record of major product, infrastructure, deployment, authentication, and documentation changes for the Azure AI Upskilling Hub
author: Microsoft
ms.date: 2026-03-12
ms.topic: reference
keywords:
  - changelog
  - blazor
  - azure
  - github actions
  - deployment
estimated_reading_time: 6
---

## 2026-03-12

### Modernized the shell and summary experience

* Replaced the hardcoded home hero with a tracker-driven workspace overview
* Replaced the dashboard marketing hero with a live progress summary card
* Removed the fixed date badge from the app bar and simplified reminder copy

### Refreshed repository documentation

* Updated [README.md](README.md) to reflect the live Microsoft announcement feed, GitHub Copilot SDK chat, and current GitHub Actions secret names
* Updated [arch.md](arch.md) with runtime and delivery diagrams for the live feed and Copilot integration
* Updated [./.github/copilot-instruction.md](.github/copilot-instruction.md) so future sessions inherit the current application, deployment, and authentication model

### Added GitHub Copilot SDK chat integration

* Added an in-app Copilot chat tab backed by the official GitHub Copilot SDK for .NET
* Added GitHub OAuth-based sign-in so users can authenticate from inside the app and use their own GitHub-linked Copilot access
* Added runtime model discovery, grounded tracker tools, and deployment settings for GitHub OAuth and Copilot defaults
* Updated Azure deployment files and scripts so GitHub OAuth settings can flow through `azd`, direct App Service deployment, and portal configuration

### Added a live Microsoft announcement feed

* Added a dashboard feed for official Microsoft announcement sources covering Foundry, GitHub Copilot, APIM, App Service, and related Azure updates
* Added dashboard actions to open official posts, mark items as seen, and save useful announcements into the shared resource library
* Kept the first version server-side and memory-cached so the app can avoid browser feed parsing and database schema changes

### Added task-level learning links

* Updated the Plan tab so each training item now shows matching links from the shared resource library
* Updated the Dashboard focus area so active items also expose direct links to relevant resources
* Added Python fundamentals resources so the Python task is directly actionable alongside the existing C# beginner series link

### Improved shared personalization

* Updated seed behavior so new shared resources are added for existing databases without overwriting user content
* Kept the Resources tab as the single place where each user can add, edit, and personalize links for a shared plan

## 2026-03-11

### Added the project memory file

* Added [./.github/copilot-instruction.md](.github/copilot-instruction.md)
* Documented the current architecture, Azure resources, GitHub Actions setup, and maintenance rules
* Recorded the currently configured GitHub Actions secret and variable names without exposing values

### Built the personalized learning tracker

* Created the `UpskillTracker` Blazor Web App for the Azure AI upskilling plan
* Added MudBlazor-based UI for Dashboard, Plan, Timeline, Resources, and Notes
* Added EF Core with SQLite storage and seeded learning-plan data

### Added documentation and architecture artifacts

* Updated [README.md](README.md) with deployment guidance and project overview
* Added [arch.md](arch.md) with Mermaid architecture documentation
* Added a portal screenshot under `docs/images`

### Added and later refined access control

* Added an initial client-side PIN gate
* Replaced the hardcoded PIN approach with configuration-backed PIN access
* Moved the PIN to Azure app settings and GitHub Actions secret `APP_ACCESS_PIN`
* Restored the PIN model after App Service Authentication introduced live callback failures

### Updated Azure hosting and deployment

* Provisioned Azure App Service, Application Insights, and Log Analytics with Bicep
* Added custom hostname support for `skilling.hammadaslam.com`
* Switched GitHub Actions deployment to OIDC-based Azure login
* Updated the CD workflow to deploy infrastructure conditionally before app deployment
* Increased the App Service plan target SKU from `B1` to `B2`

### Troubleshot production issues

* Fixed Linux App Service deployment packaging issues related to zip path format
* Fixed MudBlazor provider placement issues that caused dead tabs in production
* Investigated App Service Authentication callback `401` failures
* Disabled App Service Authentication in Azure CLI to restore app access quickly
* Verified the default production site returned `200` after the live rollback

## Maintenance rule

Whenever a meaningful change is made to the app, infrastructure, authentication,
GitHub configuration, workflows, or documentation, append a new entry to this
file in the same commit.
