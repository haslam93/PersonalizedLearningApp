using System.Text.RegularExpressions;
using UpskillTracker.Models;

namespace UpskillTracker.Services;

public static class LearningProductCatalog
{
    public static IReadOnlyList<LearningProduct> Products { get; } =
    [
        new("foundry", "Microsoft Foundry", "Agents, models, evaluation, and decision APIs.",
            "https://ai.azure.com/", "https://learn.microsoft.com/en-us/azure/foundry/",
            "Sketch a synthetic support-ticket router with three labels and a low-confidence handoff. Write down the expected input, typed output, and one test case before deploying anything.",
            ["foundry", "azure ai studio", "microsoft-decision-1"]),
        new("foundry-iq", "Foundry IQ", "Permission-aware knowledge retrieval for agents.",
            "https://ai.azure.com/", "https://learn.microsoft.com/en-us/azure/foundry/agents/concepts/what-is-foundry-iq",
            "Read the overview, choose two synthetic documents, and sketch a knowledge source, knowledge base, retrieval request, and citation. Identify the access boundary; do not connect your work notebook.",
            ["foundry iq", "foundryiq"]),
        new("fabric", "Microsoft Fabric", "OneLake, engineering, analytics, and governance.",
            "https://app.fabric.microsoft.com/", "https://learn.microsoft.com/en-us/fabric/get-started/microsoft-fabric-overview",
            "Open an existing workspace and sketch a Bronze-to-Silver-to-Gold path using the public NYC Taxi dataset. Identify one data-quality check. Do not start a trial or create paid capacity just for this session.",
            ["fabric", "onelake", "direct lake", "dp-600"]),
        new("fabric-iq", "Fabric IQ", "Shared business meaning and relationships over data.",
            "https://app.fabric.microsoft.com/", "https://learn.microsoft.com/en-us/fabric/iq/overview",
            "Read the overview and define three synthetic business entities and two relationships. Explain how this business context differs from retrieving passages from documents in Foundry IQ.",
            ["fabric iq", "fabriciq"]),
        new("github", "GitHub", "Copilot, agentic workflows, and developer delivery.",
            "https://github.com/copilot", "https://docs.github.com/en/copilot",
            "Choose one workflow in a scratch repository. Describe its trigger, read permissions, reviewable output, and a failure case. Ask Copilot to explain it without allowing a push or merge.",
            ["github", "gh-600", "gh-200", "gh-300", "gh-500"]),
        new("apim", "API Management", "Governed APIs, AI gateways, and MCP access.",
            "https://portal.azure.com/", "https://learn.microsoft.com/en-us/azure/api-management/",
            "Read one AI gateway policy example and sketch authentication, token limits, and backend routing for a synthetic request. Identify how the request should fail when its quota is exhausted.",
            ["api management", "apim", "ai gateway", "apiops"]),
        new("app-service", "App Service", "Application hosting, identity, and diagnostics.",
            "https://portal.azure.com/", "https://learn.microsoft.com/en-us/azure/app-service/",
            "Inspect an existing test app's runtime stack, Always On, and health-check configuration without changing them. Write down one startup failure and the diagnostic you would use.",
            ["app service", "app services", "web apps"]),
        new("copilot-studio", "Copilot Studio", "Low-code agents, tools, and M365 experiences.",
            "https://copilotstudio.microsoft.com/", "https://learn.microsoft.com/en-us/microsoft-copilot-studio/",
            "Inspect an existing test agent or workflow. Sketch one synthetic learning question, its knowledge source, an allowed tool, and an escalation rule. Do not publish or add a connector during the first session.",
            ["copilot studio", "copilotstudio", "power virtual agents"]),
        new("container-apps", "Container Apps", "Containerized apps and isolated agent execution.",
            "https://portal.azure.com/", "https://learn.microsoft.com/en-us/azure/container-apps/overview",
            "Compare a synthetic agent workload on App Service and Container Apps. Write down one scaling trigger, one isolation requirement, and the condition that would change your hosting choice.",
            ["container apps", "container app", "aca"]),
        new("agent-framework", "Agent Framework", "Code-first agents and recoverable orchestration.",
            "https://github.com/microsoft/agent-framework", "https://learn.microsoft.com/en-us/agent-framework/",
            "Sketch a two-step workflow with one read-only tool, a checkpoint, and an explicit approval boundary. Describe what happens if execution is interrupted between the two steps.",
            ["agent framework", "microsoft agent framework"])
    ];

    public static bool Matches(LearningProduct product, AnnouncementItem item)
        => product.Matches(string.Join(' ', item.Title, item.Summary, item.Topic));

    public static bool Matches(LearningProduct product, TrainingItem item)
        => product.Matches(string.Join(' ', item.Title, item.Domain, item.Category, item.Description));

    public static string? ResolveTopic(string text)
    {
        // Specific capabilities win over their parent product for the primary feed label.
        foreach (var key in new[] { "foundry-iq", "fabric-iq", "copilot-studio" })
        {
            var product = Products.First(product => product.Key == key);
            if (product.Matches(text)) return product.Name;
        }

        return Products.FirstOrDefault(product => product.Matches(text))?.Name;
    }

    public static IReadOnlyList<ProductCatchUp> BuildCatchUp(
        IEnumerable<AnnouncementItem> announcements,
        IReadOnlyDictionary<string, AnnouncementState> states,
        IEnumerable<TrainingItem> trainingItems,
        DateTime today)
    {
        var official = announcements.Where(item => item.Stream == AnnouncementStream.MicrosoftOfficial)
            .DistinctBy(item => item.Url, StringComparer.OrdinalIgnoreCase).ToList();
        var plan = trainingItems.ToList();
        return Products.Select(product =>
        {
            var updates = official.Where(item => Matches(product, item))
                .OrderByDescending(item => item.PublishedUtc).ToList();
            var unfinished = plan.Where(item => item.Status != TrackerStatus.Completed && Matches(product, item)).ToList();
            var next = TrainingPlanPrioritizer.OrderForFocus(
                unfinished.Where(item => item.Status != TrackerStatus.Blocked), today).FirstOrDefault();
            return new ProductCatchUp(product, updates,
                updates.Count(item => !states.TryGetValue(item.Url, out var state) || !state.IsSeen),
                unfinished.Count, next);
        }).ToList();
    }
}

public sealed class LearningProduct(
    string key, string name, string focus, string workspaceUrl, string documentationUrl,
    string firstStep, IReadOnlyList<string> terms)
{
    private readonly Regex matcher = new(
        $@"(?<![\p{{L}}\p{{N}}])(?:{string.Join('|', terms.Select(Regex.Escape))})(?![\p{{L}}\p{{N}}])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public string Key { get; } = key;
    public string Name { get; } = name;
    public string Focus { get; } = focus;
    public string WorkspaceUrl { get; } = workspaceUrl;
    public string DocumentationUrl { get; } = documentationUrl;
    public string FirstStep { get; } = firstStep;
    public bool Matches(string text) => matcher.IsMatch(text);
    public string LabBrief => $"{Name} - 15-minute learning start\n\n{FirstStep}\n\n" +
        $"Reference: {DocumentationUrl}\nWorkspace: {WorkspaceUrl}\n\n" +
        "Use an existing sandbox and synthetic or public data. Browser profile selects the tenant; opening a link does not provision anything.\n" +
        "Finish by recording: what I learned, one trade-off, and my next small step. No automatic completion or study-time claim.";
}

public sealed record ProductCatchUp(
    LearningProduct Product, IReadOnlyList<AnnouncementItem> Updates,
    int UnreadUpdates, int UnfinishedItems, TrainingItem? NextItem);
