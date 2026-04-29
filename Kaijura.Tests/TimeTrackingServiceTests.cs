using Kaijura.App.Models;
using Kaijura.App.Services;

namespace Kaijura.Tests;

public sealed class TimeTrackingServiceTests
{
    [Fact]
    public void StartStoresSingleActiveTracker()
    {
        var state = new AppState();
        var service = new TimeTrackingService();
        var started = DateTimeOffset.Parse("2026-04-29T10:00:00+02:00");

        service.Start(state, Issue("10001", "BTR-1802"), started);
        service.Start(state, Issue("10002", "BTR-1803"), started.AddMinutes(5));

        Assert.NotNull(state.ActiveTimeTracker);
        Assert.Equal("10002", state.ActiveTimeTracker.IssueId);
        Assert.Equal("BTR-1803", state.ActiveTimeTracker.IssueKey);
        Assert.Equal(started.AddMinutes(5), state.ActiveTimeTracker.StartedAt);
    }

    [Theory]
    [InlineData(1, 60)]
    [InlineData(59, 60)]
    [InlineData(60, 60)]
    [InlineData(61, 120)]
    [InlineData(125, 180)]
    public void CalculateRoundedSecondsRoundsUpToFullMinutes(int elapsedSeconds, int expectedSeconds)
    {
        var service = new TimeTrackingService();
        var started = DateTimeOffset.Parse("2026-04-29T10:00:00+02:00");

        var seconds = service.CalculateRoundedSeconds(started, started.AddSeconds(elapsedSeconds));

        Assert.Equal(expectedSeconds, seconds);
    }

    [Fact]
    public async Task StopActiveClearsTrackerAfterSuccessfulRegistration()
    {
        var state = StateWithActiveTracker();
        var service = new TimeTrackingService();
        TimeTrackerWorklog? registered = null;

        await service.StopActiveAsync(
            state,
            (worklog, _) =>
            {
                registered = worklog;
                return Task.CompletedTask;
            },
            DateTimeOffset.Parse("2026-04-29T10:02:01+02:00"),
            CancellationToken.None);

        Assert.Null(state.ActiveTimeTracker);
        Assert.NotNull(registered);
        Assert.Equal("10001", registered.IssueId);
        Assert.Equal(180, registered.TimeSpentSeconds);
    }

    [Fact]
    public async Task StopActiveKeepsTrackerWhenRegistrationFails()
    {
        var state = StateWithActiveTracker();
        var service = new TimeTrackingService();

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StopActiveAsync(
            state,
            (_, _) => throw new InvalidOperationException("Jira failed"),
            DateTimeOffset.Parse("2026-04-29T10:02:00+02:00"),
            CancellationToken.None));

        Assert.NotNull(state.ActiveTimeTracker);
        Assert.Equal("10001", state.ActiveTimeTracker.IssueId);
    }

    [Fact]
    public void ShouldStopForMoveOnlyWhenActiveTicketLeavesProgress()
    {
        var state = StateWithActiveTracker();
        var service = new TimeTrackingService();

        Assert.False(service.ShouldStopForMove(state, "10001", BoardSection.Board, BoardColumn.Progress));
        Assert.True(service.ShouldStopForMove(state, "10001", BoardSection.Board, BoardColumn.PendingQa));
        Assert.True(service.ShouldStopForMove(state, "10001", BoardSection.Backlog, BoardColumn.ToDo));
        Assert.False(service.ShouldStopForMove(state, "10002", BoardSection.Board, BoardColumn.PendingQa));
    }

    private static AppState StateWithActiveTracker()
    {
        return new AppState
        {
            ActiveTimeTracker = new ActiveTimeTracker
            {
                IssueId = "10001",
                IssueKey = "BTR-1802",
                StartedAt = DateTimeOffset.Parse("2026-04-29T10:00:00+02:00")
            }
        };
    }

    private static IssueState Issue(string id, string key)
    {
        return new IssueState
        {
            JiraId = id,
            Key = key,
            Section = BoardSection.Board,
            Column = BoardColumn.Progress
        };
    }
}
