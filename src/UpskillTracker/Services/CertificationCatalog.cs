using UpskillTracker.Models;

namespace UpskillTracker.Services;

public static class CertificationCatalog
{
    public static IReadOnlyList<CertificationCatalogItem> Items { get; } =
    [
        new()
        {
            Key = "ai-103",
            Provider = "Microsoft",
            Code = "AI-103",
            Title = "Azure AI Apps and Agents Developer Associate",
            Description = "Build Azure AI applications and agentic solutions with Microsoft Foundry, generative AI, vision, language, and information extraction services.",
            CredentialUrl = "https://learn.microsoft.com/en-us/credentials/certifications/azure-ai-apps-and-agents-developer-associate/",
            StudyGuideUrl = "https://learn.microsoft.com/en-us/credentials/certifications/resources/study-guides/ai-103",
            RecommendedTargetDate = new DateTime(2026, 8, 31),
            EstimatedHours = 30,
            Priority = 5,
            IsRecommended = true,
            PreparationNotes = "Primary near-term certification. Complete the AI-103 study guide, training course, hands-on Foundry practice, and exam readiness review before scheduling."
        },
        new()
        {
            Key = "ai-200",
            Provider = "Microsoft",
            Code = "AI-200",
            Title = "Azure AI Cloud Developer Associate",
            Description = "Develop and operate cloud-native AI back ends using containers, Azure data services, messaging, security, monitoring, and troubleshooting.",
            CredentialUrl = "https://learn.microsoft.com/en-us/credentials/certifications/azure-ai-cloud-developer-associate/",
            StudyGuideUrl = "https://learn.microsoft.com/en-us/credentials/certifications/resources/study-guides/ai-200",
            RecommendedTargetDate = new DateTime(2026, 9, 20),
            EstimatedHours = 35,
            Priority = 4,
            IsRecommended = true,
            PreparationNotes = "Take after AI-103. Focus on containers, PostgreSQL and vector data, messaging, identity, monitoring, and production troubleshooting."
        },
        new()
        {
            Key = "dp-600",
            Provider = "Microsoft",
            Code = "DP-600",
            Title = "Fabric Analytics Engineer Associate",
            Description = "Prepare, model, analyze, and secure enterprise data with Microsoft Fabric lakehouses, warehouses, semantic models, SQL, DAX, and Power BI.",
            CredentialUrl = "https://learn.microsoft.com/en-us/credentials/certifications/fabric-analytics-engineer-associate/",
            StudyGuideUrl = "https://learn.microsoft.com/en-us/credentials/certifications/resources/study-guides/dp-600",
            RecommendedTargetDate = new DateTime(2026, 11, 30),
            EstimatedHours = 35,
            Priority = 3,
            PreparationNotes = "Import after completing the Fabric beginner path and lakehouse project."
        },
        new()
        {
            Key = "dp-700",
            Provider = "Microsoft",
            Code = "DP-700",
            Title = "Fabric Data Engineer Associate",
            Description = "Design and implement Microsoft Fabric data engineering solutions with lakehouses, warehouses, pipelines, SQL, PySpark, and KQL.",
            CredentialUrl = "https://learn.microsoft.com/en-us/credentials/certifications/fabric-data-engineer-associate/",
            StudyGuideUrl = "https://learn.microsoft.com/en-us/credentials/certifications/resources/study-guides/dp-700",
            RecommendedTargetDate = new DateTime(2026, 12, 20),
            EstimatedHours = 40,
            Priority = 3,
            PreparationNotes = "Review the latest study guide after the July 2026 skills refresh."
        },
        new()
        {
            Key = "dp-750",
            Provider = "Microsoft",
            Code = "DP-750",
            Title = "Azure Databricks Data Engineer Associate",
            Description = "Implement Azure Databricks data engineering solutions with Delta Lake, Unity Catalog, SQL, Python, optimized pipelines, governance, and Azure integrations.",
            CredentialUrl = "https://learn.microsoft.com/en-us/credentials/certifications/implementing-data-engineering-solutions-using-azure-databricks/",
            StudyGuideUrl = "https://learn.microsoft.com/en-us/credentials/certifications/resources/study-guides/dp-750",
            RecommendedTargetDate = new DateTime(2027, 1, 31),
            EstimatedHours = 40,
            Priority = 3,
            PreparationNotes = "Import after completing the Azure Databricks fundamentals, notebook, and data engineering learning path."
        },
        new()
        {
            Key = "databricks-data-engineer-associate",
            Provider = "Databricks",
            Code = "DE Associate",
            Title = "Databricks Certified Data Engineer Associate",
            Description = "Validate vendor-neutral Databricks data engineering skills across the lakehouse platform, pipelines, governance, and production workflows.",
            CredentialUrl = "https://www.databricks.com/learn/certification/data-engineer-associate",
            RecommendedTargetDate = new DateTime(2027, 2, 28),
            EstimatedHours = 35,
            Priority = 2,
            PreparationNotes = "Consider after DP-750 if a multi-cloud Databricks credential is useful."
        },
        new()
        {
            Key = "github-foundations",
            Provider = "GitHub",
            Code = "GH-900",
            Title = "GitHub Foundations",
            Description = "Validate Git, repositories, collaboration, project management, and core GitHub product knowledge.",
            CredentialUrl = "https://learn.microsoft.com/en-us/credentials/certifications/github-foundations/",
            RecommendedTargetDate = new DateTime(2026, 9, 15),
            EstimatedHours = 10,
            Priority = 3,
            PreparationNotes = "A short confidence-building certification that can fit between the two Azure AI credentials."
        },
        new()
        {
            Key = "github-copilot",
            Provider = "GitHub",
            Code = "GitHub Copilot",
            Title = "GitHub Copilot Certification",
            Description = "Validate responsible AI, Copilot features, prompt engineering, privacy, testing, and effective AI-assisted development workflows.",
            CredentialUrl = "https://learn.microsoft.com/en-us/credentials/certifications/github-copilot/",
            RecommendedTargetDate = new DateTime(2026, 9, 30),
            EstimatedHours = 16,
            Priority = 4,
            IsRecommended = true,
            PreparationNotes = "Use the app's Copilot integration and day-to-day coding work as practical preparation."
        },
        new()
        {
            Key = "github-actions",
            Provider = "GitHub",
            Code = "GH-200",
            Title = "GitHub Actions Certification",
            Description = "Validate workflow automation, CI/CD design, reusable workflows, security, and deployment practices with GitHub Actions.",
            CredentialUrl = "https://learn.microsoft.com/en-us/credentials/certifications/github-actions/",
            RecommendedTargetDate = new DateTime(2026, 11, 15),
            EstimatedHours = 20,
            Priority = 3,
            PreparationNotes = "Use this application's CI, deployment, and cost-control workflows as study examples."
        },
        new()
        {
            Key = "github-advanced-security",
            Provider = "GitHub",
            Code = "GH-500",
            Title = "GitHub Advanced Security Certification",
            Description = "Validate code scanning, secret scanning, Dependabot, CodeQL, and software supply-chain security practices.",
            CredentialUrl = "https://learn.microsoft.com/en-us/credentials/certifications/github-advanced-security/",
            RecommendedTargetDate = new DateTime(2027, 1, 15),
            EstimatedHours = 25,
            Priority = 2,
            PreparationNotes = "Best after GitHub Foundations or Actions unless security work becomes project-driven."
        },
        new()
        {
            Key = "github-administration",
            Provider = "GitHub",
            Code = "GitHub Admin",
            Title = "GitHub Administration Certification",
            Description = "Validate organization, repository, access, governance, and enterprise administration skills for GitHub.",
            CredentialUrl = "https://learn.microsoft.com/en-us/credentials/certifications/github-administration/",
            RecommendedTargetDate = new DateTime(2027, 2, 15),
            EstimatedHours = 25,
            Priority = 2,
            PreparationNotes = "Keep optional unless GitHub platform administration becomes part of current work."
        }
    ];

    public static CertificationCatalogItem? FindByKey(string key)
        => Items.FirstOrDefault(item => item.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    public static CertificationCatalogItem? FindForGoal(TrainingItem goal)
        => Items.FirstOrDefault(item =>
            (!string.IsNullOrWhiteSpace(goal.Category) && item.Code.Equals(goal.Category, StringComparison.OrdinalIgnoreCase)) ||
            item.Title.Equals(goal.Title, StringComparison.OrdinalIgnoreCase));
}
