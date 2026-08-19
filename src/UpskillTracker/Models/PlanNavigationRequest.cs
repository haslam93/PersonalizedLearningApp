namespace UpskillTracker.Models;

public sealed record PlanNavigationRequest(string FocusFilter, int? TrainingItemId = null);
