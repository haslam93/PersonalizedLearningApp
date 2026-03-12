using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using UpskillTracker.Models;

namespace UpskillTracker.Services;

public sealed class CopilotChatService(
    GitHubTokenStore tokenStore,
    IOptions<CopilotSdkOptions> copilotOptions,
    IServiceScopeFactory serviceScopeFactory,
    ILogger<CopilotChatService> logger) : IAsyncDisposable
{
    private readonly Dictionary<string, RuntimeSession> _runtimeSessions = [];
    private readonly object _runtimeLock = new();

    public async Task<IReadOnlyList<ModelInfo>> GetAvailableModelsAsync(string authSessionId, CancellationToken cancellationToken = default)
    {
        var runtime = GetOrCreateRuntime(authSessionId);

        await runtime.SyncLock.WaitAsync(cancellationToken);
        try
        {
            await EnsureClientAsync(runtime);
            return await runtime.Client!.ListModelsAsync(cancellationToken);
        }
        finally
        {
            runtime.SyncLock.Release();
        }
    }

    public async Task ResetSessionAsync(string authSessionId)
    {
        RuntimeSession? runtime;

        lock (_runtimeLock)
        {
            _runtimeSessions.TryGetValue(authSessionId, out runtime);
        }

        if (runtime is null)
        {
            return;
        }

        await runtime.SyncLock.WaitAsync();
        try
        {
            if (runtime.Session is not null)
            {
                await runtime.Session.DisposeAsync();
                runtime.Session = null;
                runtime.ActiveModel = null;
            }
        }
        finally
        {
            runtime.SyncLock.Release();
        }
    }

    public async Task ReleaseUserSessionAsync(string authSessionId)
    {
        RuntimeSession? runtime;

        lock (_runtimeLock)
        {
            if (!_runtimeSessions.Remove(authSessionId, out runtime))
            {
                return;
            }
        }

        await runtime.DisposeAsync();
    }

    public async Task SendMessageAsync(
        string authSessionId,
        string? modelId,
        string prompt,
        Action<CopilotChatUpdate> onUpdate,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new ArgumentException("A prompt is required.", nameof(prompt));
        }

        var runtime = GetOrCreateRuntime(authSessionId);
        await runtime.SyncLock.WaitAsync(cancellationToken);

        try
        {
            await EnsureSessionAsync(runtime, modelId, cancellationToken);

            var completionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            string latestAssistantMessage = string.Empty;

            using var subscription = runtime.Session!.On(evt =>
            {
                switch (evt)
                {
                    case AssistantMessageDeltaEvent delta when !string.IsNullOrWhiteSpace(delta.Data.DeltaContent):
                        onUpdate(new CopilotChatUpdate(CopilotChatUpdateType.AssistantDelta, delta.Data.DeltaContent));
                        break;

                    case AssistantMessageEvent message:
                        latestAssistantMessage = message.Data.Content ?? string.Empty;
                        onUpdate(new CopilotChatUpdate(CopilotChatUpdateType.AssistantFinal, latestAssistantMessage));
                        break;

                    case SessionErrorEvent error:
                        var errorMessage = error.Data.Message ?? "GitHub Copilot returned an unknown error.";
                        completionSource.TrySetException(new InvalidOperationException(errorMessage));
                        break;

                    case SessionIdleEvent:
                        completionSource.TrySetResult(true);
                        break;
                }
            });

            await runtime.Session.SendAsync(new MessageOptions
            {
                Prompt = prompt
            });

            await completionSource.Task.WaitAsync(cancellationToken);

            if (string.IsNullOrWhiteSpace(latestAssistantMessage))
            {
                onUpdate(new CopilotChatUpdate(CopilotChatUpdateType.AssistantFinal, "Copilot did not return any content."));
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send a message to the Copilot SDK session.");
            onUpdate(new CopilotChatUpdate(CopilotChatUpdateType.Error, ex.Message));
            throw;
        }
        finally
        {
            runtime.SyncLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        List<RuntimeSession> runtimeSessions;

        lock (_runtimeLock)
        {
            runtimeSessions = [.. _runtimeSessions.Values];
            _runtimeSessions.Clear();
        }

        foreach (var runtime in runtimeSessions)
        {
            await runtime.DisposeAsync();
        }
    }

    private RuntimeSession GetOrCreateRuntime(string authSessionId)
    {
        if (!tokenStore.TryGet(authSessionId, out var tokenSession) || tokenSession is null)
        {
            throw new InvalidOperationException("Your GitHub Copilot sign-in session is no longer available. Sign in again to continue.");
        }

        lock (_runtimeLock)
        {
            if (_runtimeSessions.TryGetValue(authSessionId, out var existing))
            {
                return existing;
            }

            var created = new RuntimeSession(authSessionId, tokenSession.AccessToken);
            _runtimeSessions[authSessionId] = created;
            return created;
        }
    }

    private async Task EnsureClientAsync(RuntimeSession runtime)
    {
        if (runtime.Client is not null)
        {
            return;
        }

        var options = new CopilotClientOptions
        {
            GitHubToken = runtime.AccessToken,
            UseLoggedInUser = false
        };

        if (!string.IsNullOrWhiteSpace(copilotOptions.Value.CliPath))
        {
            options.CliPath = copilotOptions.Value.CliPath;
        }

        runtime.Client = new CopilotClient(options);
        await runtime.Client.StartAsync();
        logger.LogInformation("Started Copilot SDK client for auth session {AuthSessionId}.", runtime.AuthSessionId);
    }

    private async Task EnsureSessionAsync(RuntimeSession runtime, string? requestedModel, CancellationToken cancellationToken)
    {
        await EnsureClientAsync(runtime);

        var effectiveModel = string.IsNullOrWhiteSpace(requestedModel)
            ? copilotOptions.Value.DefaultModel
            : requestedModel;

        if (runtime.Session is not null)
        {
            if (!string.Equals(runtime.ActiveModel, effectiveModel, StringComparison.OrdinalIgnoreCase))
            {
                await runtime.Session.SetModelAsync(effectiveModel, cancellationToken);
                runtime.ActiveModel = effectiveModel;
            }

            return;
        }

        var tools = CreateTools();
        var toolNames = tools.Select(tool => tool.Name).Where(name => !string.IsNullOrWhiteSpace(name)).ToList();

        runtime.Session = await runtime.Client!.CreateSessionAsync(new SessionConfig
        {
            Model = effectiveModel,
            Streaming = true,
            Tools = tools,
            AvailableTools = toolNames,
            InfiniteSessions = new InfiniteSessionConfig
            {
                Enabled = false
            },
            SystemMessage = new SystemMessageConfig
            {
                Mode = SystemMessageMode.Append,
                Content = """
You are the learning copilot for UpskillTracker.

Rules:
- Ground factual answers in the tracker tools before making claims about the user's progress, notes, or resources.
- Prefer existing tracker data over generic advice when the user asks about their learning plan.
- Never invent IDs, task names, or saved resources.
- Use save tools only for small, explicit changes the user clearly asked you to make.
- When a save would create or overwrite meaningful content, ask the user for confirmation first if they have not already been explicit.
- Keep responses concise, practical, and focused on the user's learning work.
"""
            }
        });

        runtime.ActiveModel = effectiveModel;
    }

    private List<AIFunction> CreateTools()
    {
        return
        [
            AIFunctionFactory.Create(
                async () =>
                    await UseTrackerServiceAsync(trackerService => trackerService.GetDashboardSnapshotAsync()),
                "get_dashboard_snapshot",
                "Returns dashboard metrics, focus items, upcoming tasks, pinned resources, and recent notes."),

            AIFunctionFactory.Create(
                async () =>
                    await UseTrackerServiceAsync(trackerService => trackerService.GetTrainingItemsAsync()),
                "get_training_items",
                "Returns all training plan items with dates, status, lane, and progress."),

            AIFunctionFactory.Create(
                async () =>
                    await UseTrackerServiceAsync(trackerService => trackerService.GetResourcesAsync()),
                "get_resources",
                "Returns all saved learning resources with sections, tags, and links."),

            AIFunctionFactory.Create(
                async () =>
                    await UseTrackerServiceAsync(trackerService => trackerService.GetNotesAsync()),
                "get_notes",
                "Returns all saved notes and reflections."),

            AIFunctionFactory.Create(
                async ([Description("The id of the training item to match against saved resources.")] int trainingItemId) =>
                    await UseTrackerServiceAsync(async trackerService =>
                    {
                    var items = await trackerService.GetTrainingItemsAsync();
                    var resources = await trackerService.GetResourcesAsync();
                    var item = items.FirstOrDefault(candidate => candidate.Id == trainingItemId)
                        ?? throw new ArgumentException($"Training item {trainingItemId} was not found.", nameof(trainingItemId));

                    return TaskResourceMatcher.GetMatches(item, resources, 3);
                    }),
                "get_resource_matches_for_training_item",
                "Returns the best saved resources for a specific training item id."),

            AIFunctionFactory.Create(
                async (
                    [Description("Existing training item id to update. Omit or use 0 to create a new item.")] int? id,
                    [Description("Training item title.")] string? title,
                    [Description("Primary domain such as GitHub Copilot or App Service.")] string? domain,
                    [Description("Optional category.")] string? category,
                    [Description("Optional description.")] string? description,
                    [Description("Optional target date in ISO format, such as 2026-03-20.")] string? targetDate,
                    [Description("Optional status: NotStarted, InProgress, Blocked, or Completed.")] string? status,
                    [Description("Optional lane: Core, Stretch, or RapidRamp.")] string? lane,
                    [Description("Optional type: Learning, Lab, Project, or Capstone.")] string? type,
                    [Description("Optional progress percent from 0 to 100.")] int? progressPercent,
                    [Description("Optional estimated hours from 0.5 to 40.")] decimal? estimatedHours,
                    [Description("Optional priority from 1 to 5.")] int? priority,
                    [Description("Optional flag indicating whether the item is project-driven.")] bool? projectDriven,
                    [Description("Optional notes.")] string? notes,
                    [Description("Optional evidence or proof point.")] string? evidence) =>
                    await UseTrackerServiceAsync(async trackerService =>
                    {
                    var item = await GetTrainingItemAsync(trackerService, id) ?? new TrainingItem();

                    item.Title = PickRequired(title, item.Title, "Training items require a title.");
                    item.Domain = PickRequired(domain, item.Domain, "Training items require a domain.");
                    item.Category = Pick(category, item.Category);
                    item.Description = Pick(description, item.Description);
                    item.Notes = Pick(notes, item.Notes);
                    item.Evidence = Pick(evidence, item.Evidence);
                    item.TargetDate = ParseDate(targetDate, item.TargetDate);
                    item.Status = ParseEnum(status, item.Status);
                    item.Lane = ParseEnum(lane, item.Lane);
                    item.Type = ParseEnum(type, item.Type);
                    item.ProgressPercent = progressPercent ?? item.ProgressPercent;
                    item.EstimatedHours = estimatedHours ?? item.EstimatedHours;
                    item.Priority = priority ?? item.Priority;
                    item.ProjectDriven = projectDriven ?? item.ProjectDriven;

                    Validate(item);
                    await trackerService.SaveTrainingItemAsync(item);

                    return new
                    {
                        item.Id,
                        item.Title,
                        item.Domain,
                        item.Status,
                        item.TargetDate,
                        Saved = true
                    };
                    }),
                "save_training_item",
                "Creates or updates a single training item in the learning plan."),

            AIFunctionFactory.Create(
                async (
                    [Description("Existing note id to update. Omit or use 0 to create a new note.")] int? id,
                    [Description("Note title.")] string? title,
                    [Description("Optional note category.")] string? category,
                    [Description("Optional related area.")] string? relatedArea,
                    [Description("Optional comma-separated tags.")] string? tags,
                    [Description("Note content.")] string? content,
                    [Description("Optional pinned flag.")] bool? isPinned) =>
                    await UseTrackerServiceAsync(async trackerService =>
                    {
                    var note = await GetNoteAsync(trackerService, id) ?? new NoteEntry();

                    note.Title = PickRequired(title, note.Title, "Notes require a title.");
                    note.Category = Pick(category, note.Category);
                    note.RelatedArea = Pick(relatedArea, note.RelatedArea);
                    note.Tags = Pick(tags, note.Tags);
                    note.Content = PickRequired(content, note.Content, "Notes require content.");
                    note.IsPinned = isPinned ?? note.IsPinned;

                    Validate(note);
                    await trackerService.SaveNoteAsync(note);

                    return new
                    {
                        note.Id,
                        note.Title,
                        note.IsPinned,
                        Saved = true
                    };
                    }),
                "save_note",
                "Creates or updates a single note."),

            AIFunctionFactory.Create(
                async (
                    [Description("Existing resource id to update. Omit or use 0 to create a new resource.")] int? id,
                    [Description("Resource title.")] string? title,
                    [Description("Resource section such as GitHub Copilot or App Service.")] string? section,
                    [Description("Resource URL.")] string? url,
                    [Description("Optional kind: Learn, Documentation, GitHub, Video, Lab, Accelerator, or Other.")] string? kind,
                    [Description("Optional pinned flag.")] bool? isPinned,
                    [Description("Optional summary.")] string? summary,
                    [Description("Optional comma-separated tags.")] string? tags,
                    [Description("Optional notes.")] string? notes,
                    [Description("Optional sort order.")] int? sortOrder) =>
                    await UseTrackerServiceAsync(async trackerService =>
                    {
                    var resource = await GetResourceAsync(trackerService, id) ?? new ResourceEntry();

                    resource.Title = PickRequired(title, resource.Title, "Resources require a title.");
                    resource.Section = PickRequired(section, resource.Section, "Resources require a section.");
                    resource.Url = PickRequired(url, resource.Url, "Resources require a URL.");
                    resource.Kind = ParseEnum(kind, resource.Kind);
                    resource.IsPinned = isPinned ?? resource.IsPinned;
                    resource.Summary = Pick(summary, resource.Summary);
                    resource.Tags = Pick(tags, resource.Tags);
                    resource.Notes = Pick(notes, resource.Notes);
                    resource.SortOrder = sortOrder ?? resource.SortOrder;

                    Validate(resource);
                    await trackerService.SaveResourceAsync(resource);

                    return new
                    {
                        resource.Id,
                        resource.Title,
                        resource.Section,
                        resource.Url,
                        Saved = true
                    };
                    }),
                "save_resource",
                "Creates or updates a single saved resource.")
        ];
    }

    private async Task<T> UseTrackerServiceAsync<T>(Func<TrackerService, Task<T>> operation)
    {
        await using var scope = serviceScopeFactory.CreateAsyncScope();
        var trackerService = scope.ServiceProvider.GetRequiredService<TrackerService>();
        return await operation(trackerService);
    }

    private static string Pick(string? candidate, string currentValue)
        => string.IsNullOrWhiteSpace(candidate) ? currentValue : candidate.Trim();

    private static string PickRequired(string? candidate, string currentValue, string errorMessage)
    {
        var selected = string.IsNullOrWhiteSpace(candidate) ? currentValue : candidate.Trim();
        if (string.IsNullOrWhiteSpace(selected))
        {
            throw new ArgumentException(errorMessage);
        }

        return selected;
    }

    private static DateTime ParseDate(string? candidate, DateTime currentValue)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return currentValue;
        }

        return DateTime.TryParse(candidate, out var parsed)
            ? parsed.Date
            : throw new ArgumentException($"Could not parse '{candidate}' as a date.");
    }

    private static TEnum ParseEnum<TEnum>(string? candidate, TEnum currentValue) where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return currentValue;
        }

        return Enum.TryParse<TEnum>(candidate, true, out var parsed)
            ? parsed
            : throw new ArgumentException($"'{candidate}' is not a valid {typeof(TEnum).Name}.");
    }

    private static void Validate(object model)
    {
        var context = new ValidationContext(model);
        var validationResults = new List<ValidationResult>();
        if (!Validator.TryValidateObject(model, context, validationResults, validateAllProperties: true))
        {
            throw new ArgumentException(string.Join(" ", validationResults.Select(result => result.ErrorMessage)));
        }
    }

    private static async Task<TrainingItem?> GetTrainingItemAsync(TrackerService trackerService, int? id)
    {
        if (!id.HasValue || id.Value <= 0)
        {
            return null;
        }

        var items = await trackerService.GetTrainingItemsAsync();
        return items.FirstOrDefault(item => item.Id == id.Value)?.Clone();
    }

    private static async Task<NoteEntry?> GetNoteAsync(TrackerService trackerService, int? id)
    {
        if (!id.HasValue || id.Value <= 0)
        {
            return null;
        }

        var notes = await trackerService.GetNotesAsync();
        return notes.FirstOrDefault(note => note.Id == id.Value)?.Clone();
    }

    private static async Task<ResourceEntry?> GetResourceAsync(TrackerService trackerService, int? id)
    {
        if (!id.HasValue || id.Value <= 0)
        {
            return null;
        }

        var resources = await trackerService.GetResourcesAsync();
        return resources.FirstOrDefault(resource => resource.Id == id.Value)?.Clone();
    }

    private sealed class RuntimeSession(string authSessionId, string accessToken) : IAsyncDisposable
    {
        public string AuthSessionId { get; } = authSessionId;

        public string AccessToken { get; } = accessToken;

        public SemaphoreSlim SyncLock { get; } = new(1, 1);

        public CopilotClient? Client { get; set; }

        public CopilotSession? Session { get; set; }

        public string? ActiveModel { get; set; }

        public async ValueTask DisposeAsync()
        {
            SyncLock.Dispose();

            if (Session is not null)
            {
                await Session.DisposeAsync();
            }

            if (Client is not null)
            {
                await Client.DisposeAsync();
            }
        }
    }
}