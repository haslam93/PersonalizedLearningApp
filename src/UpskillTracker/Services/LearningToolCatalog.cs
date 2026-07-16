using MudBlazor;
using UpskillTracker.Models;

namespace UpskillTracker.Services;

public static class LearningToolCatalog
{
    public const string DirectoryUrl = "https://hammadaslam.com/tools-and-demos/";

    public static IReadOnlyList<LearningTool> Items { get; } =
    [
        new()
        {
            Key = "azure-integration-hub",
            Title = "Azure Integration Hub",
            Category = "Azure architecture",
            Description = "A single source of truth for Azure API Management, Logic Apps, Functions, Container Apps, Service Bus, and App Service.",
            BestFor = "Comparing integration services, reviewing architecture patterns, and preparing customer guidance.",
            Url = "https://haslam93.github.io/azure-integration-hub/",
            Icon = Icons.Material.Filled.Hub,
            AccentClass = "learning-tool-azure",
            Topics = ["APIM", "Logic Apps", "Functions", "Container Apps", "Service Bus", "App Service"]
        },
        new()
        {
            Key = "foundry-updates",
            Title = "Microsoft Foundry Updates Portal",
            Category = "AI and agents",
            Description = "A living portal for Microsoft Foundry and Agent Framework updates, concepts, and demos.",
            BestFor = "Staying current before demos, customer conversations, labs, and certification preparation.",
            Url = "https://haslam93.github.io/Foundry-Demo-Site/",
            Icon = Icons.Material.Filled.AutoAwesome,
            AccentClass = "learning-tool-foundry",
            Topics = ["Microsoft Foundry", "Agent Framework", "Updates", "Demos"]
        },
        new()
        {
            Key = "github-admin-hub",
            Title = "GitHub Enterprise Admin Hub",
            Category = "GitHub administration",
            Description = "Practical guidance for cost centers, AI credits, Copilot management, and GitHub Enterprise administration.",
            BestFor = "Answering administration questions and preparing for GitHub platform or governance work.",
            Url = "https://haslam93.github.io/github-admin-hub/",
            Icon = Icons.Material.Filled.AdminPanelSettings,
            AccentClass = "learning-tool-github",
            Topics = ["Enterprise", "Copilot", "Cost centers", "AI credits", "Governance"]
        },
        new()
        {
            Key = "github-agentic-workflows",
            Title = "GitHub Agentic Workflows Lab",
            Category = "GitHub automation",
            Description = "An interactive demo and hands-on learning lab for GitHub Agentic Workflows.",
            BestFor = "Learning gh-aw by doing, exploring workflow patterns, and preparing practical demonstrations.",
            Url = "https://haslam93.github.io/gh-aw-demo-lab/",
            Icon = Icons.Material.Filled.Bolt,
            AccentClass = "learning-tool-workflows",
            Topics = ["Agentic workflows", "gh-aw", "Automation", "Hands-on lab"]
        }
    ];
}
