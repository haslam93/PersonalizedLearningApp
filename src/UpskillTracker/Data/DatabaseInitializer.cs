using Microsoft.EntityFrameworkCore;
using UpskillTracker.Models;

namespace UpskillTracker.Data;

public static class DatabaseInitializer
{
    public static async Task InitializeAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<TrackerDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();

        await db.Database.EnsureCreatedAsync();

        if (!await db.TrainingItems.AnyAsync())
        {
            db.TrainingItems.AddRange(GetSeedTrainingItems());
        }

        await EnsureSeedResourcesAsync(db);

        if (!await db.Notes.AnyAsync())
        {
            db.Notes.AddRange(GetSeedNotes());
        }

        await db.SaveChangesAsync();
    }

    private static async Task EnsureSeedResourcesAsync(TrackerDbContext db)
    {
        var existingResourceKeys = await db.Resources
            .AsNoTracking()
            .Select(resource => new { resource.Title, resource.Section })
            .ToListAsync();

        var keySet = existingResourceKeys
            .Select(resource => BuildResourceKey(resource.Title, resource.Section))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var resource in GetSeedResources())
        {
            if (keySet.Contains(BuildResourceKey(resource.Title, resource.Section)))
            {
                continue;
            }

            db.Resources.Add(resource);
        }
    }

    private static string BuildResourceKey(string title, string section)
        => string.Concat(section.Trim(), "::", title.Trim());

    private static IEnumerable<TrainingItem> GetSeedTrainingItems()
    {
        var today = DateTime.Today;
        var march = new DateTime(2026, 3, 17);
        var april = new DateTime(2026, 4, 18);
        var may = new DateTime(2026, 5, 16);
        var june = new DateTime(2026, 6, 20);
        var july = new DateTime(2026, 7, 18);
        var august = new DateTime(2026, 8, 15);
        var september = new DateTime(2026, 9, 12);

        return
        [
            new TrainingItem
            {
                Title = "Continue C# beginner series through arrays, lists, and beyond",
                Domain = "Coding Foundations",
                Category = "C#/.NET",
                Description = "Finish the beginner C# modules, capture key takeaways, and turn each milestone into a small coding exercise.",
                TargetDate = march,
                Lane = LearningLane.Core,
                Type = TrainingItemType.Learning,
                EstimatedHours = 4,
                Priority = 5,
                Notes = "Use the YouTube series as the structured path, then reinforce with one small console or Blazor exercise.",
                ProgressPercent = today > march ? 25 : 0
            },
            new TrainingItem
            {
                Title = "Build Python muscle memory for AI app workflows",
                Domain = "Coding Foundations",
                Category = "Python",
                Description = "Refresh Python fundamentals used in SDK samples, notebooks, APIs, and automation scripts for Azure AI work.",
                TargetDate = march.AddDays(10),
                Lane = LearningLane.Core,
                Type = TrainingItemType.Project,
                EstimatedHours = 4,
                Priority = 4,
                Notes = "Focus on virtual environments, requests, SDK auth, async basics, and data shaping.",
                ProjectDriven = true
            },
            new TrainingItem
            {
                Title = "Rapid-ramp Microsoft Foundry and the 2.x SDK",
                Domain = "Microsoft Foundry",
                Category = "Foundry",
                Description = "Learn the new portal, resource model, current SDK flow, model deployments, evaluations, tracing, and agent capabilities.",
                TargetDate = april,
                Lane = LearningLane.RapidRamp,
                Type = TrainingItemType.Lab,
                EstimatedHours = 6,
                Priority = 5,
                Notes = "Bias toward the latest Foundry docs, then compare with older samples only when needed for customer context.",
                ProjectDriven = true
            },
            new TrainingItem
            {
                Title = "Ground a RAG app with Azure AI Search",
                Domain = "Azure AI Search",
                Category = "RAG",
                Description = "Work through indexing, chunking, vector or hybrid retrieval, semantic ranker, and quality tradeoffs for enterprise AI apps.",
                TargetDate = may,
                Lane = LearningLane.Core,
                Type = TrainingItemType.Lab,
                EstimatedHours = 5,
                Priority = 5,
                Notes = "Document the chunking strategy and retrieval tuning choices you would explain to customers."
            },
            new TrainingItem
            {
                Title = "Adopt Microsoft Agent Framework patterns",
                Domain = "Agent Framework",
                Category = "Agents",
                Description = "Understand when to use agents vs workflows, tool use, MCP integration, memory, and orchestration patterns.",
                TargetDate = june,
                Lane = LearningLane.Core,
                Type = TrainingItemType.Project,
                EstimatedHours = 5,
                Priority = 4,
                Notes = "Keep examples aligned with Foundry-backed agents where possible."
            },
            new TrainingItem
            {
                Title = "Deep dive APIM, App Service, and Container Apps for AI apps",
                Domain = "App Innovation",
                Category = "Hosting and API layer",
                Description = "Map service selection guidance, deployment tradeoffs, auth, networking, and observability for customer architectures.",
                TargetDate = july,
                Lane = LearningLane.Core,
                Type = TrainingItemType.Project,
                EstimatedHours = 6,
                Priority = 5,
                Notes = "Capture decision points for when App Service beats Container Apps and vice versa."
            },
            new TrainingItem
            {
                Title = "Cover Service Bus, Logic Apps, and Redis integration patterns",
                Domain = "Integration Services",
                Category = "Messaging and workflow",
                Description = "Learn how AI apps connect to enterprise workflows, messaging, caching, and back-end systems.",
                TargetDate = july.AddDays(10),
                Lane = LearningLane.Stretch,
                Type = TrainingItemType.Learning,
                EstimatedHours = 4,
                Priority = 4,
                Notes = "Bias toward patterns that show durable orchestration, queue-based decoupling, and low-latency caching."
            },
            new TrainingItem
            {
                Title = "Run the Azure SRE Agent lab end to end",
                Domain = "Azure SRE Agent",
                Category = "Reliability",
                Description = "Deploy the lab, run the IT operations, developer, and workflow automation scenarios, then capture customer-facing takeaways.",
                TargetDate = august,
                Lane = LearningLane.RapidRamp,
                Type = TrainingItemType.Lab,
                EstimatedHours = 6,
                Priority = 5,
                Notes = "Use the lab to connect App Innovation architecture to agentic operations and incident response.",
                ProjectDriven = true
            },
            new TrainingItem
            {
                Title = "Productionize an AI app architecture",
                Domain = "Architecture and Operations",
                Category = "Security, cost, reliability",
                Description = "Document the security, monitoring, governance, and deployment story for AI apps on Azure.",
                TargetDate = august.AddDays(20),
                Lane = LearningLane.Core,
                Type = TrainingItemType.Project,
                EstimatedHours = 5,
                Priority = 4,
                Notes = "Include managed identity, telemetry, operational readiness, and customer tradeoff guidance."
            },
            new TrainingItem
            {
                Title = "Capstone 1: Foundry + Azure AI Search + App Service",
                Domain = "Capstones",
                Category = "Reference architecture",
                Description = "Build and document a polished end-to-end AI application with retrieval, observability, and deployment guidance.",
                TargetDate = september,
                Lane = LearningLane.Core,
                Type = TrainingItemType.Capstone,
                EstimatedHours = 8,
                Priority = 5,
                Notes = "Treat this as a reusable customer demo and architecture narrative."
            },
            new TrainingItem
            {
                Title = "Capstone 2: Agentic integration app with APIM and Service Bus",
                Domain = "Capstones",
                Category = "Integration architecture",
                Description = "Create an agent-enabled application pattern that includes API management, messaging, and operational guidance.",
                TargetDate = september.AddDays(7),
                Lane = LearningLane.Stretch,
                Type = TrainingItemType.Capstone,
                EstimatedHours = 8,
                Priority = 4,
                Notes = "Use this when customer demand shifts toward system integration and operational reliability."
            }
        ];
    }

    private static IEnumerable<ResourceEntry> GetSeedResources()
    {
        return
        [
            new ResourceEntry { Title = "Foundry SDK overview (Python)", Section = "Microsoft Foundry", Url = "https://learn.microsoft.com/en-us/azure/foundry/how-to/develop/sdk-overview?pivots=programming-language-python", Kind = ResourceKind.Learn, IsPinned = true, SortOrder = 10, Summary = "Primary starting point for the latest Foundry 2.x SDK workflow and project-based development.", Tags = "foundry, sdk, python, latest" },
            new ResourceEntry { Title = "Foundry documentation home", Section = "Microsoft Foundry", Url = "https://learn.microsoft.com/en-us/azure/foundry/", Kind = ResourceKind.Documentation, SortOrder = 20, Summary = "Broader Foundry product documentation across projects, models, agents, evaluations, and observability.", Tags = "foundry, docs" },
            new ResourceEntry { Title = "GitHub Copilot documentation", Section = "GitHub Copilot", Url = "https://docs.github.com/en/copilot", Kind = ResourceKind.Documentation, IsPinned = true, SortOrder = 10, Summary = "Keep customer-ready Copilot knowledge current across chat, coding workflows, and governance.", Tags = "copilot, docs, github" },
            new ResourceEntry { Title = "App Service documentation", Section = "App Service", Url = "https://learn.microsoft.com/en-us/azure/app-service/", Kind = ResourceKind.Documentation, SortOrder = 10, Summary = "Core hosting reference for web apps, APIs, auth, deployment, and scaling on App Service.", Tags = "app service, hosting" },
            new ResourceEntry { Title = "Container Apps documentation", Section = "Container Apps", Url = "https://learn.microsoft.com/en-us/azure/container-apps/", Kind = ResourceKind.Documentation, SortOrder = 10, Summary = "Managed container platform reference for APIs, event-driven apps, and background workloads.", Tags = "container apps, hosting, containers" },
            new ResourceEntry { Title = "Azure API Management documentation", Section = "APIM", Url = "https://learn.microsoft.com/en-us/azure/api-management/", Kind = ResourceKind.Documentation, SortOrder = 10, Summary = "Use for API governance, security, throttling, mediation, and AI gateway patterns.", Tags = "apim, api management" },
            new ResourceEntry { Title = "Azure AI Search documentation", Section = "Azure AI Search", Url = "https://learn.microsoft.com/en-us/azure/search/", Kind = ResourceKind.Documentation, IsPinned = true, SortOrder = 10, Summary = "Main landing page for search, indexing, vector search, and RAG design guidance.", Tags = "search, rag, vector" },
            new ResourceEntry { Title = "Microsoft Agent Framework overview", Section = "Agent Framework", Url = "https://learn.microsoft.com/agent-framework/overview/", Kind = ResourceKind.Learn, IsPinned = true, SortOrder = 10, Summary = "Official landing page for agents, workflows, tools, and provider integrations in C# and Python.", Tags = "agent framework, agents, workflows" },
            new ResourceEntry { Title = "Microsoft Agent Framework Dev Blog", Section = "Agent Framework", Url = "https://devblogs.microsoft.com/agent-framework/", Kind = ResourceKind.Documentation, SortOrder = 15, Summary = "Official Microsoft Agent Framework blog with announcements, deep dives, samples, and GitHub Copilot SDK integration posts.", Tags = "agent framework, microsoft, devblogs, copilot sdk" },
            new ResourceEntry { Title = "Service Bus documentation", Section = "Service Bus", Url = "https://learn.microsoft.com/en-us/azure/service-bus-messaging/", Kind = ResourceKind.Documentation, SortOrder = 10, Summary = "Messaging patterns for durable decoupling, asynchronous work, and enterprise integration.", Tags = "service bus, messaging" },
            new ResourceEntry { Title = "Logic Apps documentation", Section = "Logic Apps", Url = "https://learn.microsoft.com/en-us/azure/logic-apps/", Kind = ResourceKind.Documentation, SortOrder = 10, Summary = "Workflow automation guidance for connecting AI apps to enterprise systems.", Tags = "logic apps, workflow" },
            new ResourceEntry { Title = "Azure Cache for Redis documentation", Section = "Redis", Url = "https://learn.microsoft.com/en-us/azure/azure-cache-for-redis/", Kind = ResourceKind.Documentation, SortOrder = 10, Summary = "Caching guidance for low-latency access patterns and session or memory scenarios.", Tags = "redis, cache" },
            new ResourceEntry { Title = "Azure SRE Agent GA announcement", Section = "Azure SRE Agent", Url = "https://techcommunity.microsoft.com/blog/appsonazureblog/announcing-general-availability-for-the-azure-sre-agent/4500682", Kind = ResourceKind.Documentation, IsPinned = true, SortOrder = 10, Summary = "Practical framing for how Azure SRE Agent supports diagnostics, knowledge reuse, and governed remediation.", Tags = "sre agent, reliability, operations" },
            new ResourceEntry { Title = "Azure SRE Agent hands-on lab", Section = "Azure SRE Agent", Url = "https://github.com/dm-chelupati/sre-agent-lab/tree/main?tab=readme-ov-file", Kind = ResourceKind.Lab, IsPinned = true, SortOrder = 20, Summary = "Deployable lab for incident investigation, code-aware diagnosis, and GitHub issue triage.", Tags = "sre agent, github, lab, azd" },
            new ResourceEntry { Title = "C# beginner series", Section = "Coding Foundations", Url = "https://www.youtube.com/watch?v=9THmGiSPjBQ&list=PLdo4fOcmZ0oULFjxrOagaERVAMbmG20Xe", Kind = ResourceKind.Video, SortOrder = 10, Summary = "Continue from arrays and lists, then carry the learning into .NET UI and services work.", Tags = "c#, beginner, dotnet, video" },
            new ResourceEntry { Title = "Python for beginners learning path", Section = "Coding Foundations", Url = "https://learn.microsoft.com/en-us/training/paths/beginner-python/", Kind = ResourceKind.Learn, SortOrder = 20, Summary = "Structured Microsoft Learn path for Python syntax, functions, collections, files, and practical exercises.", Tags = "python, fundamentals, beginner, learn" },
            new ResourceEntry { Title = "Python tutorial", Section = "Coding Foundations", Url = "https://docs.python.org/3/tutorial/", Kind = ResourceKind.Documentation, SortOrder = 30, Summary = "Official Python tutorial covering control flow, data structures, modules, and classes for day-to-day coding fluency.", Tags = "python, fundamentals, tutorial, docs" },
            new ResourceEntry { Title = "Build a RAG solution with Azure AI Search", Section = "Azure AI Search", Url = "https://learn.microsoft.com/en-us/azure/search/retrieval-augmented-generation-overview", Kind = ResourceKind.Learn, SortOrder = 20, Summary = "RAG-specific guidance for grounding apps with Azure AI Search, including indexing and retrieval patterns.", Tags = "search, rag, grounding, ai search" },
            new ResourceEntry { Title = "Well-Architected Framework", Section = "Architecture and Operations", Url = "https://learn.microsoft.com/en-us/azure/well-architected/", Kind = ResourceKind.Documentation, SortOrder = 10, Summary = "Use this to shape production guidance for security, reliability, operational excellence, performance, and cost.", Tags = "architecture, reliability, security, cost, operations" },
            new ResourceEntry { Title = "Azure Monitor and Application Insights", Section = "Architecture and Operations", Url = "https://learn.microsoft.com/en-us/azure/azure-monitor/app/app-insights-overview", Kind = ResourceKind.Documentation, SortOrder = 20, Summary = "Telemetry and observability foundation for productionizing AI and App Innovation workloads.", Tags = "monitoring, telemetry, application insights, observability" },
            new ResourceEntry { Title = "Choose between App Service, Container Apps, and AKS", Section = "App Innovation", Url = "https://learn.microsoft.com/en-us/azure/architecture/guide/technology-choices/compute-decision-tree", Kind = ResourceKind.Documentation, SortOrder = 10, Summary = "Decision guidance for selecting the right Azure compute platform for APIs, web apps, containers, and AI workloads.", Tags = "app innovation, app service, container apps, hosting, architecture" },
            new ResourceEntry { Title = "Develop generative AI apps in Azure", Section = "Learning Paths", Url = "https://learn.microsoft.com/en-us/training/paths/develop-ai-solutions-azure-openai/", Kind = ResourceKind.Learn, SortOrder = 10, Summary = "Structured Microsoft Learn path to complement Foundry and AI app development work.", Tags = "learn, azure ai, path" }
        ];
    }

    private static IEnumerable<NoteEntry> GetSeedNotes()
    {
        return
        [
            new NoteEntry
            {
                Title = "Operating model for this tracker",
                Category = "System",
                RelatedArea = "Execution cadence",
                Tags = "weekly review, evidence, cadence",
                IsPinned = true,
                Content = "Use the Core lane as the minimum weekly commitment. When project pressure rises, switch to the matching Rapid-Ramp items and log the learning outcome as evidence. When time opens up, pull from Stretch items."
            },
            new NoteEntry
            {
                Title = "Customer-facing architecture note template",
                Category = "Architecture",
                RelatedArea = "Reusable pattern",
                Tags = "tradeoffs, decision log",
                Content = "For each major topic, capture: problem statement, service choice, rejected alternatives, security considerations, operational concerns, cost considerations, and how to explain the pattern to a customer."
            },
            new NoteEntry
            {
                Title = "Hands-on lab reflection template",
                Category = "Labs",
                RelatedArea = "Evidence",
                Tags = "retrospective, demos",
                Content = "When you finish a lab, note what worked, where it broke, the architecture lesson, the customer story, and what would be required for production readiness."
            }
        ];
    }
}
