namespace UpskillTracker.Services;

public enum CopilotChatUpdateType
{
    AssistantDelta,
    AssistantFinal,
    Error
}

public sealed record CopilotChatUpdate(CopilotChatUpdateType Type, string Content);