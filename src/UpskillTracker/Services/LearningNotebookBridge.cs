using UpskillTracker.Models;

namespace UpskillTracker.Services;

public static class LearningNotebookBridge
{
    public const string ResourceTag = "learning-notebook";

    public static bool IsNotebookResource(ResourceEntry resource)
        => resource.Tags.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Contains(ResourceTag, StringComparer.OrdinalIgnoreCase);

    public static List<ResourceEntry> WithoutNotebookLinks(IEnumerable<ResourceEntry> resources)
        => resources.Where(resource => !IsNotebookResource(resource)).ToList();

    public static ResourceEntry? FindNotebook(IEnumerable<ResourceEntry> resources)
        => resources.Where(resource => IsNotebookResource(resource) && IsValidNotebookUrl(resource.Url))
            .OrderByDescending(resource => resource.UpdatedUtc)
            .FirstOrDefault();

    public static bool IsValidNotebookUrl(string? value)
    {
        if (value is null || value.Length > 500 || !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        return uri.Host.EndsWith(".sharepoint.com", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.Equals("onenote.com", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith(".onenote.com", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.Equals("onedrive.live.com", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.Equals("1drv.ms", StringComparison.OrdinalIgnoreCase);
    }

    public static string BuildNoteText(NoteEntry note, string portalUrl)
        => $"{note.Title}\n{note.Category} | {note.RelatedArea}\n\n{note.Content}\n\n" +
            $"Tags: {note.Tags}\nSource: {portalUrl}";
}
