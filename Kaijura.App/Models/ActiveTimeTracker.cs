namespace Kaijura.App.Models;

public sealed class ActiveTimeTracker
{
    public string IssueId { get; set; } = string.Empty;
    public string IssueKey { get; set; } = string.Empty;
    public DateTimeOffset StartedAt { get; set; }
}

public sealed record TimeTrackerWorklog(
    string IssueId,
    string IssueKey,
    DateTimeOffset StartedAt,
    DateTimeOffset StoppedAt,
    int TimeSpentSeconds);
