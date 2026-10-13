using UpskillTracker.Models;
using UpskillTracker.Services;
using Xunit;

namespace UpskillTracker.Tests;

public sealed class ProductLearningTests
{
    [Theory]
    [InlineData("Azure AI Foundry SDK", "Microsoft Foundry")]
    [InlineData("FoundryIQ knowledge bases", "Foundry IQ")]
    [InlineData("Microsoft Fabric IQ ontology", "Fabric IQ")]
    [InlineData("Copilot Studio extends agents", "Copilot Studio")]
    [InlineData("Microsoft-Decision-1 decision scoring", "Microsoft Foundry")]
    [InlineData("APIM AI gateway policies", "API Management")]
    [InlineData("GitHub Copilot agentic workflows", "GitHub")]
    [InlineData("Container Apps sandboxes", "Container Apps")]
    public void ProductNamesAndSpecificCapabilitiesHaveConsistentPrimaryLabels(string text, string expected)
        => Assert.Equal(expected, LearningProductCatalog.ResolveTopic(text));

    [Fact]
    public void CopilotStudioIsNotMisclassifiedAsGitHub()
    {
        var product = LearningProductCatalog.Products.Single(product => product.Key == "github");
        Assert.False(product.Matches("Copilot Studio introduces low-code agents"));
        Assert.False(LearningProductCatalog.Products.Single(product => product.Key == "apim").Matches("rapidapimetrics"));
    }

    [Fact]
    public void CatchUpCountsOnlyOfficialDistinctUpdatesAndPreservesParentAndIqViews()
    {
        var item = new AnnouncementItem
        {
            Url = "https://example.com/foundry-iq", Title = "Foundry IQ knowledge sources",
            Topic = "Foundry IQ", Stream = AnnouncementStream.MicrosoftOfficial
        };
        var industry = new AnnouncementItem
        {
            Url = "https://example.com/opinion", Title = "Foundry IQ opinion",
            Stream = AnnouncementStream.IndustryInsights
        };
        var states = new Dictionary<string, AnnouncementState>(StringComparer.OrdinalIgnoreCase)
        {
            [item.Url] = new() { IsSeen = true }
        };
        var result = LearningProductCatalog.BuildCatchUp([item, item, industry], states, [], DateTime.UtcNow.Date);
        foreach (var key in new[] { "foundry", "foundry-iq" })
        {
            var product = result.Single(product => product.Product.Key == key);
            Assert.Single(product.Updates);
            Assert.Equal(0, product.UnreadUpdates);
        }
        Assert.Empty(result.Single(product => product.Product.Key == "github").Updates);
    }

    [Fact]
    public void ProductNextStepsExcludeCompletedAndBlockedWorkAndPreferCommitments()
    {
        var now = DateTime.UtcNow.Date;
        var items = new[]
        {
            new TrainingItem { Id = 1, Title = "GitHub core", Domain = "GitHub", TargetDate = now.AddDays(3) },
            new TrainingItem { Id = 2, Title = "GitHub optional", Domain = "GitHub", Lane = LearningLane.Stretch, TargetDate = now.AddDays(-20) },
            new TrainingItem { Id = 3, Title = "GitHub blocked", Domain = "GitHub", Status = TrackerStatus.Blocked },
            new TrainingItem { Id = 4, Title = "GitHub complete", Domain = "GitHub", Status = TrackerStatus.Completed }
        };
        var product = LearningProductCatalog.BuildCatchUp([], new Dictionary<string, AnnouncementState>(), items, now)
            .Single(product => product.Product.Key == "github");
        Assert.Equal(3, product.UnfinishedItems);
        Assert.Equal(1, product.NextItem?.Id);
        Assert.Equal(TrackerStatus.NotStarted, items[0].Status);
    }

    [Fact]
    public void PrimaryRadarReferenceRanksAheadOfGenericTopicMatches()
    {
        var primary = new ResourceEntry { Id = 1, Url = "https://example.com/decision", Title = "Decision API" };
        var generic = new ResourceEntry { Id = 2, Url = "https://example.com/foundry", Title = "Microsoft Foundry", Section = "Microsoft Foundry" };
        var item = new TrainingItem { Title = "Compare decision APIs", Domain = "Microsoft Foundry", Notes = "Link: https://example.com/decision\nSource: Official docs" };
        Assert.Same(primary, TaskResourceMatcher.GetMatches(item, [generic, primary], 1).Single());
    }

    [Fact]
    public void LabBriefsAreSmallSafeStartsWithVerifiedReferenceLocations()
    {
        Assert.Equal(10, LearningProductCatalog.Products.Select(product => product.Key).Distinct().Count());
        foreach (var product in LearningProductCatalog.Products)
        {
            Assert.Equal("https", new Uri(product.DocumentationUrl).Scheme);
            Assert.Contains("15-minute", product.LabBrief);
            Assert.Contains("synthetic or public data", product.LabBrief);
            Assert.Contains("does not provision", product.LabBrief);
        }
    }
}
