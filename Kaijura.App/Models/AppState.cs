namespace Kaijura.App.Models;

public sealed class AppState
{
    public AppConfig Config { get; set; } = new();
    public List<IssueState> Issues { get; set; } = [];
    public ActiveTimeTracker? ActiveTimeTracker { get; set; }
    public DateTimeOffset? LastSuccessfulSyncAt { get; set; }
}
