namespace UpskillTracker.Models;

public sealed class LearningTool
{
    public required string Key { get; init; }

    public required string Title { get; init; }

    public required string Category { get; init; }

    public required string Description { get; init; }

    public required string BestFor { get; init; }

    public required string Url { get; init; }

    public required string Icon { get; init; }

    public required string AccentClass { get; init; }

    public IReadOnlyList<string> Topics { get; init; } = [];
}
