using System.Text.RegularExpressions;
using UpskillTracker.Models;

namespace UpskillTracker.Services;

public static partial class TaskResourceMatcher
{
    private static readonly Dictionary<string, string[]> PreferredResourcesByTaskTitle = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Continue C# beginner series through arrays, lists, and beyond"] =
        [
            "C# beginner series"
        ],
        ["Build Python muscle memory for AI app workflows"] =
        [
            "Python for beginners learning path",
            "Python tutorial",
            "Foundry SDK overview (Python)"
        ],
        ["Rapid-ramp Microsoft Foundry and the 2.x SDK"] =
        [
            "Foundry SDK overview (Python)",
            "Foundry documentation home"
        ],
        ["Ground a RAG app with Azure AI Search"] =
        [
            "Build a RAG solution with Azure AI Search",
            "Azure AI Search documentation"
        ],
        ["Adopt Microsoft Agent Framework patterns"] =
        [
            "Microsoft Agent Framework overview",
            "Foundry documentation home"
        ],
        ["Deep dive APIM, App Service, and Container Apps for AI apps"] =
        [
            "Choose between App Service, Container Apps, and AKS",
            "Azure API Management documentation",
            "App Service documentation",
            "Container Apps documentation"
        ],
        ["Cover Service Bus, Logic Apps, and Redis integration patterns"] =
        [
            "Service Bus documentation",
            "Logic Apps documentation",
            "Azure Cache for Redis documentation"
        ],
        ["Run the Azure SRE Agent lab end to end"] =
        [
            "Azure SRE Agent hands-on lab",
            "Azure SRE Agent GA announcement"
        ],
        ["Productionize an AI app architecture"] =
        [
            "Well-Architected Framework",
            "Azure Monitor and Application Insights"
        ],
        ["Capstone 1: Foundry + Azure AI Search + App Service"] =
        [
            "Foundry SDK overview (Python)",
            "Build a RAG solution with Azure AI Search",
            "App Service documentation"
        ],
        ["Capstone 2: Agentic integration app with APIM and Service Bus"] =
        [
            "Microsoft Agent Framework overview",
            "Azure API Management documentation",
            "Service Bus documentation"
        ],
        ["Fabric sprint 1: Map workloads and start the trial"] =
        [
            "Start a Microsoft Fabric trial",
            "Introduction to Microsoft Fabric",
            "Microsoft Fabric training hub"
        ],
        ["Fabric sprint 2: Ingest NYC Taxi data with Data Factory"] =
        [
            "NYC Taxi Azure Open Dataset",
            "NYC TLC trip record data",
            "Fabric Data Factory documentation"
        ],
        ["Fabric sprint 3: Build Bronze and Silver Delta tables in OneLake"] =
        [
            "OneLake overview",
            "Fabric medallion architecture module",
            "Microsoft Fabric lakehouse tutorial"
        ],
        ["Fabric sprint 4: Transform data with PySpark and quality checks"] =
        [
            "Transform data with Fabric notebooks",
            "Fabric medallion architecture module",
            "NYC Taxi Azure Open Dataset"
        ],
        ["Fabric sprint 5: Build a Gold star schema and Fabric warehouse"] =
        [
            "Fabric warehouse learning path",
            "Microsoft Fabric end-to-end tutorials",
            "NYC Taxi Azure Open Dataset"
        ],
        ["Fabric sprint 6: Build a Direct Lake semantic model and Power BI report"] =
        [
            "Get started with Microsoft Fabric learning path",
            "Microsoft Fabric end-to-end tutorials",
            "DP-600 study guide"
        ],
        ["Fabric sprint 7: Replay taxi events with Eventstream and KQL"] =
        [
            "Fabric Real-Time Intelligence module",
            "Microsoft Fabric end-to-end tutorials",
            "NYC Taxi Azure Open Dataset"
        ],
        ["Fabric sprint 8: Apply governance, lineage, and row-level security"] =
        [
            "Govern analytics data in Fabric",
            "DP-600 study guide",
            "Microsoft Fabric documentation"
        ],
        ["Fabric sprint 9: Add monitoring, Git, and deployment pipelines"] =
        [
            "Fabric Git integration",
            "Fabric deployment pipelines",
            "Fabric Data Factory documentation"
        ],
        ["Fabric sprint 10: Deliver the OpenText-ready NYC Taxi capstone"] =
        [
            "NYC Taxi Azure Open Dataset",
            "Microsoft Fabric end-to-end tutorials",
            "DP-600 study guide"
        ],
        ["Fabric follow-up: Run the DP-600 gap assessment"] =
        [
            "DP-600 certification page",
            "DP-600 study guide",
            "Microsoft Fabric training hub"
        ],
        ["Fabric Analytics Engineer Associate"] =
        [
            "DP-600 certification page",
            "DP-600 study guide",
            "Microsoft Fabric training hub"
        ],
        ["GitHub Certified: Agentic AI Developer"] =
        [
            "GH-600 certification page",
            "GH-600 study guide"
        ],
        ["Catch up on Microsoft Build 2026 announcements"] =
        [
            "Microsoft Build 2026"
        ],
        ["Track GitHub Universe 2026"] =
        [
            "GitHub Universe 2026"
        ],
        ["Review GitHub Universe 2026 announcements"] =
        [
            "GitHub Universe 2026"
        ],
        ["Track Microsoft Ignite 2026"] =
        [
            "Microsoft Ignite 2026"
        ],
        ["Review Microsoft Ignite 2026 announcements"] =
        [
            "Microsoft Ignite 2026"
        ],
        ["Explore Azure Databricks fundamentals"] =
        [
            "Explore Azure Databricks module",
            "Azure Databricks documentation"
        ],
        ["Run a first Azure Databricks notebook"] =
        [
            "Azure Databricks getting started tutorials",
            "Azure Databricks documentation"
        ],
        ["Complete the Azure Databricks data engineering path"] =
        [
            "Azure Databricks data engineering learning path",
            "Microsoft Learn Azure Databricks labs"
        ],
        ["Build a Delta Lake ETL pipeline in Azure Databricks"] =
        [
            "Microsoft Learn Azure Databricks labs",
            "Azure Databricks documentation"
        ],
        ["Azure AI Apps and Agents Developer Associate"] =
        [
            "AI-103 certification page",
            "AI-103 study guide"
        ]
    };

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "app", "apps", "as", "azure", "beyond", "build", "complete", "continue",
        "cover", "current", "deep", "dive", "end", "for", "from", "how", "in", "into", "learn",
        "learning", "microsoft", "of", "on", "or", "plan", "project", "rapid", "ramp", "run",
        "series", "task", "the", "through", "to", "upskill", "use", "with"
    };

    public static IReadOnlyList<ResourceEntry> GetMatches(TrainingItem item, IEnumerable<ResourceEntry> resources, int maxResults = 3)
    {
        var resourceList = resources.ToList();
        var preferredMatches = GetPreferredMatches(item, resourceList, maxResults);
        if (preferredMatches.Count >= maxResults)
        {
            return preferredMatches;
        }

        var itemText = string.Join(' ', item.Title, item.Domain, item.Category, item.Description, item.Notes, item.Evidence);
        var itemTokens = Tokenize(itemText);

        if (itemTokens.Count == 0)
        {
            return preferredMatches;
        }

        var rankedMatches = resourceList
            .Select(resource => new { Resource = resource, Score = Score(item, itemTokens, resource) })
            .Where(result => result.Score > 0)
            .OrderByDescending(result => result.Score)
            .ThenByDescending(result => result.Resource.IsPinned)
            .ThenBy(result => result.Resource.SortOrder)
            .ThenBy(result => result.Resource.Title)
            .Select(result => result.Resource)
            .ToList();

        return preferredMatches
            .Concat(rankedMatches.Where(resource => preferredMatches.All(match => match.Id != resource.Id)))
            .Take(maxResults)
            .ToList();
    }

    private static List<ResourceEntry> GetPreferredMatches(TrainingItem item, IReadOnlyList<ResourceEntry> resources, int maxResults)
    {
        if (!PreferredResourcesByTaskTitle.TryGetValue(item.Title, out var preferredTitles))
        {
            return [];
        }

        return preferredTitles
            .Select(title => resources.FirstOrDefault(resource => resource.Title.Equals(title, StringComparison.OrdinalIgnoreCase)))
            .Where(resource => resource is not null)
            .Cast<ResourceEntry>()
            .Take(maxResults)
            .ToList();
    }

    private static int Score(TrainingItem item, HashSet<string> itemTokens, ResourceEntry resource)
    {
        var score = 0;
        var resourceText = string.Join(' ', resource.Title, resource.Section, resource.Tags, resource.Summary, resource.Notes);
        var resourceTokens = Tokenize(resourceText);

        score += itemTokens.Intersect(resourceTokens, StringComparer.OrdinalIgnoreCase).Count() * 4;

        if (resource.Section.Equals(item.Domain, StringComparison.OrdinalIgnoreCase) ||
            resource.Section.Equals(item.Category, StringComparison.OrdinalIgnoreCase))
        {
            score += 12;
        }

        if (ContainsValue(resource.Tags, item.Category) || ContainsValue(resource.Title, item.Category))
        {
            score += 8;
        }

        if (ContainsValue(resource.Tags, item.Domain) || ContainsValue(resource.Title, item.Domain))
        {
            score += 6;
        }

        if (item.Type == TrainingItemType.Lab && resource.Kind == ResourceKind.Lab)
        {
            score += 3;
        }

        if (item.Type == TrainingItemType.Learning && (resource.Kind == ResourceKind.Video || resource.Kind == ResourceKind.Learn))
        {
            score += 2;
        }

        if (item.Type == TrainingItemType.Project && (resource.Kind == ResourceKind.Documentation || resource.Kind == ResourceKind.Learn))
        {
            score += 2;
        }

        if (item.Type == TrainingItemType.Capstone && (resource.Kind == ResourceKind.Documentation || resource.Kind == ResourceKind.Lab))
        {
            score += 1;
        }

        if (item.Type == TrainingItemType.Certification && resource.Section.Equals("Certifications", StringComparison.OrdinalIgnoreCase))
        {
            score += 10;
        }

        return score;
    }

    private static bool ContainsValue(string source, string candidate)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        return source.Contains(candidate, StringComparison.OrdinalIgnoreCase);
    }

    private static HashSet<string> Tokenize(string value)
    {
        return TokenRegex()
            .Matches(value)
            .Select(match => match.Value.Trim().ToLowerInvariant())
            .Where(token => token.Length > 2 && !StopWords.Contains(token))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    [GeneratedRegex("[A-Za-z0-9.+#-]+")]
    private static partial Regex TokenRegex();
}
