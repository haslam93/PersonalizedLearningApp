---
title: Azure AI Upskilling Hub architecture
description: High-level architecture for the Blazor training tracker, data layer, and Azure hosting flow
author: Microsoft
ms.date: 2026-03-11
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

This diagram shows the main runtime and delivery path for the training tracker.

```mermaid
flowchart TB
    User[User browser]
    Pin[PIN gate in MainLayout]
    UI[Blazor components\nDashboard, Plan, Timeline, Resources, Notes]
    Service[TrackerService]
    Data[EF Core DbContext]
    Sqlite[(SQLite database)]
    AppInsights[Application Insights]
    GitHub[GitHub repository]
    Actions[GitHub Actions\nCI and CD]
    AppService[Azure App Service\nLinux web app]

    User --> Pin --> UI
    UI --> Service --> Data --> Sqlite
    UI --> AppInsights
    GitHub --> Actions --> AppService
    AppService --> UI
    AppService --> Sqlite
    AppService --> AppInsights
```

## Runtime notes

* The user opens the Blazor web app and passes through a simple client-side PIN gate.
* Interactive Razor components render the dashboard and editable tracker tabs.
* `TrackerService` handles reads and writes for training items, resources, and notes.
* EF Core persists data to a local SQLite database.
* Azure App Service hosts the application, while Application Insights captures telemetry.
* GitHub Actions builds the app and deploys changes from `main` to the production web app.
