namespace UpskillTracker.Models;

public sealed class CertificationCatalogItem
{
    public required string Key { get; init; }

    public required string Provider { get; init; }

    public required string Code { get; init; }

    public required string Title { get; init; }

    public required string Description { get; init; }

    public required string CredentialUrl { get; init; }

    public string StudyGuideUrl { get; init; } = string.Empty;

    public required DateTime RecommendedTargetDate { get; init; }

    public decimal EstimatedHours { get; init; } = 20;

    public int Priority { get; init; } = 3;

    public bool IsRecommended { get; init; }

    public string PreparationNotes { get; init; } = string.Empty;

    public TrainingItem CreateTrainingItem()
    {
        return new TrainingItem
        {
            Title = Title,
            Domain = Provider,
            Category = Code,
            Description = Description,
            TargetDate = DateTime.SpecifyKind(RecommendedTargetDate.Date, DateTimeKind.Utc),
            Lane = LearningLane.Core,
            Type = TrainingItemType.Certification,
            Status = TrackerStatus.NotStarted,
            EstimatedHours = EstimatedHours,
            Priority = Priority,
            Notes = PreparationNotes
        };
    }

    public ResourceEntry CreateResource()
    {
        return new ResourceEntry
        {
            Title = string.IsNullOrWhiteSpace(Code) ? $"{Title} credential page" : $"{Code} credential page",
            Section = "Certifications",
            Url = CredentialUrl,
            Kind = ResourceKind.Learn,
            Summary = Description,
            Tags = $"certification, {Provider}, {Code}, {Title}",
            SortOrder = Priority * -10
        };
    }
}
