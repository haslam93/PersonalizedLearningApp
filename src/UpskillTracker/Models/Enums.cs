namespace UpskillTracker.Models;

public enum TrackerStatus
{
    NotStarted,
    InProgress,
    Blocked,
    Completed
}

public enum LearningLane
{
    Core,
    Stretch,
    RapidRamp
}

public enum TrainingItemType
{
    Learning,
    Lab,
    Project,
    Capstone,
    Certification
}

public enum PlanAttentionLevel
{
    Overdue,
    DueSoon,
    InProgress,
    Planned,
    NiceToHave,
    Completed
}

public enum LearningActivityType
{
    ItemStarted,
    ProgressUpdated,
    ItemCompleted,
    CertificationEarned,
    ResourceRead,
    VideoWatched,
    AnnouncementRead,
    ReflectionAdded,
    ToolUsed
}

public enum ResourceKind
{
    Learn,
    Documentation,
    GitHub,
    Video,
    Lab,
    Accelerator,
    Other
}

public enum AnnouncementStream
{
    MicrosoftOfficial,
    IndustryInsights
}

public enum VideoWatchState
{
    Inbox,
    NeedToWatch,
    Seen,
    Removed
}
