using Kaijura.App.Models;

namespace Kaijura.App.Services;

public sealed class TimeTrackingService
{
    public bool HasActiveTracker(AppState state)
    {
        return state.ActiveTimeTracker is not null;
    }

    public bool IsActiveFor(AppState state, string issueIdOrKey)
    {
        var tracker = state.ActiveTimeTracker;
        return tracker is not null
            && (string.Equals(tracker.IssueId, issueIdOrKey, StringComparison.OrdinalIgnoreCase)
                || string.Equals(tracker.IssueKey, issueIdOrKey, StringComparison.OrdinalIgnoreCase));
    }

    public bool ShouldStopForMove(
        AppState state,
        string issueIdOrKey,
        BoardSection targetSection,
        BoardColumn targetColumn)
    {
        return IsActiveFor(state, issueIdOrKey)
            && (targetSection != BoardSection.Board || targetColumn != BoardColumn.Progress);
    }

    public void Start(AppState state, IssueState issue, DateTimeOffset startedAt)
    {
        state.ActiveTimeTracker = new ActiveTimeTracker
        {
            IssueId = issue.JiraId,
            IssueKey = issue.Key,
            StartedAt = startedAt
        };
    }

    public void Discard(AppState state)
    {
        state.ActiveTimeTracker = null;
    }

    public async Task<TimeTrackerWorklog?> StopActiveAsync(
        AppState state,
        Func<TimeTrackerWorklog, CancellationToken, Task> registerWorklog,
        DateTimeOffset stoppedAt,
        CancellationToken cancellationToken)
    {
        var worklog = BuildWorklog(state, stoppedAt);
        if (worklog is null)
        {
            return null;
        }

        await registerWorklog(worklog, cancellationToken);
        state.ActiveTimeTracker = null;
        return worklog;
    }

    public TimeTrackerWorklog? BuildWorklog(AppState state, DateTimeOffset stoppedAt)
    {
        var tracker = state.ActiveTimeTracker;
        if (tracker is null)
        {
            return null;
        }

        return new TimeTrackerWorklog(
            tracker.IssueId,
            tracker.IssueKey,
            tracker.StartedAt,
            stoppedAt,
            CalculateRoundedSeconds(tracker.StartedAt, stoppedAt));
    }

    public int CalculateRoundedSeconds(DateTimeOffset startedAt, DateTimeOffset stoppedAt)
    {
        var elapsed = stoppedAt - startedAt;
        if (elapsed <= TimeSpan.Zero)
        {
            return 60;
        }

        var roundedMinutes = Math.Max(1, (int)Math.Ceiling(elapsed.TotalMinutes));
        return roundedMinutes * 60;
    }
}
