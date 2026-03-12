---
title: Azure AI Upskilling Hub architecture
description: High-level architecture for the Blazor learning hub, live announcement feed, GitHub Copilot SDK integration, and Azure hosting flow
author: Microsoft
ms.date: 2026-03-12
ms.topic: overview
keywords:
  - architecture
  - blazor
  - azure app service
  - sqlite
  - github actions
estimated_reading_time: 3
---

## System overview

This diagram shows the current application runtime, including the live feed and
the grounded GitHub Copilot chat path.

```mermaid
flowchart TB
    User[User browser]
    Pin[PIN gate in MainLayout]
  UI[Blazor components\nDashboard, Plan, Timeline, Resources, Notes, Copilot]
  Tracker[TrackerService]
  Feed[AnnouncementFeedService]
  Auth[CopilotAuthService]
  Chat[CopilotChatService]
  GitHubOAuth[GitHub OAuth]
  CopilotSdk[GitHub Copilot SDK and bundled CLI]
    Data[EF Core DbContext]
    Sqlite[(SQLite database)]
    AppInsights[Application Insights]
  MicrosoftNews[Official Microsoft announcement sources]
  AppService[Azure App Service\nLinux web app]

    User --> Pin --> UI
  UI --> Tracker --> Data --> Sqlite
  UI --> Feed --> MicrosoftNews
  UI --> Auth --> GitHubOAuth
  UI --> Chat --> CopilotSdk
  Chat --> Tracker
  Chat --> Sqlite
    UI --> AppInsights
    AppService --> UI
    AppService --> Sqlite
    AppService --> AppInsights
  AppService --> CopilotSdk
```

## Runtime notes

* The user opens the Blazor web app and passes through a simple client-side PIN gate.
* Interactive Razor components render the dashboard, editable tracker tabs, and the Copilot chat workspace.
* The home view and dashboard summary cards are driven by tracker data so the shell avoids stale fixed date ranges and static campaign copy.
* `AnnouncementFeedService` loads and memory-caches official Microsoft announcement sources for the dashboard feed.
* `TrackerService` handles reads and writes for training items, resources, and notes.
* `CopilotAuthService` and `CopilotChatService` manage GitHub OAuth, runtime model discovery, and grounded Copilot interactions.
* EF Core persists data to a local SQLite database.
* Azure App Service hosts the application, while Application Insights captures telemetry.

## Delivery and configuration flow

This diagram shows how source changes and secure settings flow into Azure.

```mermaid
flowchart LR
  Repo[GitHub repository]
  Workflow[GitHub Actions CD workflow]
  Secrets[Repository secrets\nAPP_ACCESS_PIN\nAPP_GH_OAUTH_CLIENT_ID\nAPP_GH_OAUTH_CLIENT_SECRET]
  Bicep[Bicep templates\ninfra/main.bicep and infra/resources.bicep]
  Package[Linux publish artifact\nwith bundled Copilot CLI]
  Azure[Azure App Service]
  Settings[App settings\nAccessPin\nGitHubOAuth__*\nCopilotSdk__*]

  Repo --> Workflow
  Secrets --> Workflow
  Workflow --> Bicep --> Azure
  Workflow --> Package --> Azure
  Azure --> Settings
```

## Delivery notes

* GitHub Actions builds and publishes the app for `linux-x64`.
* The published artifact includes the platform-matching Copilot CLI required by the .NET SDK on Azure App Service.
* The CD workflow passes PIN, GitHub OAuth, and Copilot model settings into Bicep so Azure app settings remain aligned with source-controlled infrastructure.
