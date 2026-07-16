---
title: Changelog
description: Chronological record of major product, infrastructure, deployment, authentication, and documentation changes for Hammad's Learning Portal
author: Microsoft
ms.date: 2026-07-16
ms.topic: reference
keywords:
  - changelog
  - blazor
  - azure
  - github actions
  - deployment
estimated_reading_time: 6
---

## 2026-07-16

### Expanded the tracked learning plan

* Added beginner Microsoft Fabric and Azure Databricks learning sequences with official Microsoft Learn, product documentation, tutorials, and hands-on lab resources
* Added practical Fabric lakehouse and Databricks Delta Lake project milestones so the new topics progress from fundamentals into applied work
* Added an AI-103 Azure AI Apps and Agents Developer Associate goal targeting August 31, 2026, including official credential and study-guide links
* Added deduplicating startup inserts so the new plan items and resources reach an existing production PostgreSQL database without overwriting user-managed records

### Added persistent certification planning

* Added a Certifications tab that tracks target dates, progress, status, study notes, and evidence using the existing training-item data model
* Added a curated import catalog for Microsoft AI, Microsoft Fabric, Azure Databricks, Databricks, and GitHub credentials
* Added custom certification-goal creation so credentials outside the curated catalog can be tracked without a code or schema change

### Made deadlines and priorities actionable

* Added shared plan-prioritization rules for overdue, due-soon, active, core, and nice-to-have work
* Updated the Dashboard with schedule-pressure metrics, a core completion rate, and a ranked "Do next" list with direct resource links
* Updated the Plan tab with at-risk, core, active, optional, and certification focus filters plus commitment and attention labels
* Changed reminder warnings so overdue optional backlog no longer obscures urgent core commitments

### Repaired the README and PostgreSQL recovery workflow

* Fixed the malformed README front matter, restored a visible page title, and updated the architecture and deployment documentation for the production PostgreSQL, managed identity, private networking, storage, and `P0v3` configuration
* Reworked [.github/workflows/cost-control-tag.yml](.github/workflows/cost-control-tag.yml) to run daily, leave an already-running PostgreSQL server unchanged, start it when stopped, wait until it is ready, and then apply `CostControl=Ignore` to that server only
* Added explicit failures when the workflow cannot find exactly one PostgreSQL Flexible Server or encounters a terminal or unexpected server state

### Improved the live dashboard on desktop and mobile

* Corrected the workspace summary grammar for a single in-progress learning item
* Limited the initial announcement feed to six items and added show-more/show-fewer controls so the mobile dashboard no longer renders every unread announcement in one extremely long page

## 2026-07-06

### Made the CostControl tag refresh workflow resilient to provisioning conflicts

* Updated [.github/workflows/cost-control-tag.yml](.github/workflows/cost-control-tag.yml) so `az tag update` calls retry with a wait when Azure reports a `Conflict` because a resource is still provisioning (`provisioning state is not Succeeded`)
* Each resource is retried up to 5 times with a 30-second wait between attempts; if it is still provisioning afterward the resource is skipped with a warning instead of failing the whole workflow
* Genuine (non-conflict) tag failures still surface as errors so real problems are not silently swallowed, and a summary reports how many resources were refreshed versus skipped

## 2026-06-25

### Redesigned the Plan tab for easier navigation

* Reworked the Plan tab into a single-column layout: the training items table now spans the full width instead of being squeezed into a narrow side-by-side column, so the trailing Edit action stays on screen without horizontal scrolling
* Moved the "Add training item" form below the table and arranged its fields in a responsive multi-column grid so the whole interface is easier to scan and fill in
* Made the per-row Edit button a filled button that smoothly scrolls down to the editor (via a new `scrollToElement` JS helper) so editing a task's timeline is always reachable
### Added a weekly CostControl tag refresh workflow

* Added [.github/workflows/cost-control-tag.yml](.github/workflows/cost-control-tag.yml), a scheduled workflow that runs every Monday at 06:00 UTC (and on demand via `workflow_dispatch`)
* The workflow signs in with GitHub OIDC and removes then re-adds the `CostControl=Ignore` tag on the `hammadlearningapp` resource group and all of its resources
* Re-applying the tag weekly refreshes its timestamp so the company 2-week cost-control exemption window never expires and the PostgreSQL server is not shut down nightly
* Confirmed the `CostControl=Ignore` tag is already declared in `infra/main.bicep` and applied to every resource on each infrastructure deployment

### Fixed the Plan tab table so tasks can be scrolled and edited

* Fixed a CSS regression where `.surface-card .mud-table-container` used `overflow: hidden`, which clipped the right side of the Plan tab table and removed the horizontal scrollbar
* Restored horizontal scrolling with `overflow-x: auto` so the suggested resource links and the per-row Edit action stay reachable on narrow widths
* Re-enabled editing a task timeline by keeping the Edit button (which opens the target-date picker) accessible for overdue and past-due items

## 2026-03-17

### Repaired and refreshed the dashboard UI

* Fixed the dashboard card layout after a CSS regression caused stat and tracker content to misalign
* Added a dedicated video watch tracker card with queue, seen count, and watch progress summary
* Restored subtle semantic color to dashboard cards with readable tinted surfaces instead of flat white panels

### Updated documentation for the dashboard refresh

* Updated [README.md](README.md) to reflect the current dashboard tracker and card styling approach
* Kept the changelog aligned with the dashboard UI update in the same release

## 2026-03-16

### Added the thought-leader and industry announcement stream

* Extended the dashboard announcement experience so it can switch between Microsoft updates and curated industry posts
* Added stream metadata to the announcement model and feed registry so both streams share one fetch, cache, dedupe, and save-to-resource pipeline
* Added curated industry sources for thought leaders, research, and major AI platform voices, including an HTML-based Anthropic research source path

### Updated shell titles and direct-status copy

* Renamed the visible app shell to Hammad's Learning Portal
* Replaced commentary-style hero and idle-state titles with shorter, more direct headings such as On Track
* Updated the dashboard announcement section copy so it no longer reads as Microsoft-only when the industry stream is selected

### Refreshed core documentation

* Updated [README.md](README.md) to reflect the final app name and the dual-stream announcement model
* Updated [arch.md](arch.md) so the runtime diagram and architecture notes show both Microsoft and industry announcement sources
* Kept the changelog aligned with the implementation changes in the same update

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
