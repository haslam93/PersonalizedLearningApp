using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using UpskillTracker.Models;
using UpskillTracker.Services;
using Xunit;

namespace UpskillTracker.Tests;

public sealed class LearningNotebookBridgeTests
{
    [Theory]
    [InlineData("https://example.sharepoint.com/personal/learner/learning.one?web=1")]
    [InlineData("https://onenote.com/notebooks/example")]
    [InlineData("https://onedrive.live.com/example")]
    [InlineData("https://1drv.ms/example")]
    public void MicrosoftNotebookWebLinksAreAccepted(string value)
        => Assert.True(LearningNotebookBridge.IsValidNotebookUrl(value));

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("http://example.sharepoint.com/learning")]
    [InlineData("https://example.sharepoint.com.evil.invalid/learning")]
    [InlineData("https://evilsharepoint.com/learning")]
    [InlineData("https://user:password@example.sharepoint.com/learning")]
    [InlineData("https://example.com/learning")]
    [InlineData("")]
    public void UntrustedSchemesHostsAndCredentialsAreRejected(string value)
        => Assert.False(LearningNotebookBridge.IsValidNotebookUrl(value));

    [Fact]
    public void OnlyAnExplicitlyTaggedPrivateResourceIsUsedAsTheNotebook()
    {
        var ordinary = new ResourceEntry { Url = "https://example.sharepoint.com/reference", Tags = "not-learning-notebook" };
        var notebook = new ResourceEntry { Url = "https://example.sharepoint.com/learning", Tags = "OneNote, learning-notebook" };
        Assert.Same(notebook, LearningNotebookBridge.FindNotebook([ordinary, notebook]));
        Assert.Null(LearningNotebookBridge.FindNotebook([ordinary]));
        Assert.False(LearningNotebookBridge.IsValidNotebookUrl("https://example.sharepoint.com/" + new string('a', 500)));
    }

    [Fact]
    public void ExportKeepsTheSavedNoteAndProvenanceAsPlainText()
    {
        var note = new NoteEntry { Title = "A takeaway", Category = "Active recall", RelatedArea = "Fabric", Content = "First line\nSecond line", Tags = "training:1" };
        var text = LearningNotebookBridge.BuildNoteText(note, "https://example.com/?view=notes");
        Assert.Contains(note.Title, text);
        Assert.Contains(note.Content, text);
        Assert.Contains(note.Tags, text);
        Assert.Contains("Source: https://example.com/?view=notes", text);
    }

    [Fact]
    public async Task ActualCopilotToolsOmitAndCannotModifyPrivateNotebookShortcuts()
    {
        await using var app = new PortalApplicationFactory();
        var tracker = app.Services.GetRequiredService<TrackerService>();
        var notebook = new ResourceEntry
        {
            Title = "Private shortcut", Section = "Learning notebook", IsPinned = true,
            Url = "https://example.sharepoint.com/private-work-notebook", Tags = LearningNotebookBridge.ResourceTag
        };
        var item = new TrainingItem { Title = "Investigate notebook learning", Domain = "Learning notebook" };
        await tracker.SaveResourceAsync(notebook);
        await tracker.SaveTrainingItemAsync(item);

        var service = app.Services.GetRequiredService<CopilotChatService>();
        var method = Assert.IsAssignableFrom<MethodInfo>(
            typeof(CopilotChatService).GetMethod("CreateTools", BindingFlags.NonPublic | BindingFlags.Instance));
        var tools = Assert.IsType<List<AIFunction>>(method.Invoke(service, null));
        foreach (var name in new[] { "get_dashboard_snapshot", "get_resources", "get_resource_matches_for_training_item" })
        {
            var arguments = new AIFunctionArguments();
            if (name == "get_resource_matches_for_training_item") arguments["trainingItemId"] = item.Id;
            var result = await tools.Single(tool => tool.Name == name).InvokeAsync(arguments);
            Assert.DoesNotContain("private-work-notebook", JsonSerializer.Serialize(result));
        }

        var save = tools.Single(tool => tool.Name == "save_resource");
        var saveArguments = new AIFunctionArguments
        {
            ["id"] = notebook.Id, ["title"] = "Do not rename", ["section"] = notebook.Section,
            ["url"] = notebook.Url, ["kind"] = "Other", ["isPinned"] = true,
            ["summary"] = "", ["tags"] = LearningNotebookBridge.ResourceTag, ["notes"] = "", ["sortOrder"] = 0
        };
        await Assert.ThrowsAsync<ArgumentException>(() => save.InvokeAsync(saveArguments).AsTask());
        saveArguments["id"] = 0;
        await Assert.ThrowsAsync<ArgumentException>(() => save.InvokeAsync(saveArguments).AsTask());
        Assert.Equal("Private shortcut", (await tracker.GetResourcesAsync()).Single(resource => resource.Id == notebook.Id).Title);
    }

}
