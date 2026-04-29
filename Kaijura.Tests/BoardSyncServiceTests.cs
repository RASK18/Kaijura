using Kaijura.App.Models;
using Kaijura.App.Services;

namespace Kaijura.Tests;

public sealed class BoardSyncServiceTests
{
    [Fact]
    public void NewIssuesEnterBacklogByIssueTypeMapping()
    {
        var state = CreateState();
        var service = new BoardSyncService();

        service.Sync(state, Search(Issue("10001", "BTR-1802", "Task")), DateTimeOffset.Parse("2026-04-28T10:00:00Z"));

        var issue = Assert.Single(state.Issues);
        Assert.Equal(IssueKind.Task, issue.Kind);
        Assert.Equal(BoardSection.Backlog, issue.Section);
        Assert.Equal(BoardColumn.ToDo, issue.Column);
    }

    [Fact]
    public void MissingIssueRestoresPreviousPositionWhenReturned()
    {
        var state = CreateState();
        var service = new BoardSyncService();
        var issue = Issue("10001", "BTR-1802", "Task");

        service.Sync(state, Search(issue), DateTimeOffset.Parse("2026-04-28T10:00:00Z"));
        service.MoveIssue(state, "10001", BoardSection.Board, BoardColumn.PendingQa, ["10001"]);

        service.Sync(state, Search(), DateTimeOffset.Parse("2026-04-28T10:05:00Z"));

        Assert.Equal(BoardSection.Missing, state.Issues[0].Section);
        Assert.Equal(BoardSection.Board, state.Issues[0].LastVisibleSection);
        Assert.True(state.Issues[0].IsMissing);

        service.Sync(state, Search(issue), DateTimeOffset.Parse("2026-04-28T10:10:00Z"));

        Assert.Equal(BoardSection.Board, state.Issues[0].Section);
        Assert.Equal(BoardColumn.PendingQa, state.Issues[0].Column);
        Assert.False(state.Issues[0].IsMissing);
    }

    [Fact]
    public void ArchiveRequiresValidatedQaColumn()
    {
        var state = CreateState();
        var service = new BoardSyncService();

        service.Sync(state, Search(Issue("10001", "BTR-1802", "Task")), DateTimeOffset.Parse("2026-04-28T10:00:00Z"));
        service.MoveIssue(state, "10001", BoardSection.Board, BoardColumn.PendingQa, ["10001"]);

        Assert.False(service.ArchiveIssue(state, "10001", DateTimeOffset.Parse("2026-04-28T10:05:00Z")));

        service.MoveIssue(state, "10001", BoardSection.Board, BoardColumn.ValidatedQa, ["10001"]);

        Assert.True(service.ArchiveIssue(state, "10001", DateTimeOffset.Parse("2026-04-28T10:06:00Z")));
        Assert.Equal(BoardSection.Archived, state.Issues[0].Section);
        Assert.NotNull(state.Issues[0].ArchivedAt);
    }

    [Fact]
    public void UnknownIssueTypesStayUnmapped()
    {
        var state = CreateState();
        var service = new BoardSyncService();

        service.Sync(state, Search(Issue("10001", "OPS-1", "Change")), DateTimeOffset.Parse("2026-04-28T10:00:00Z"));

        Assert.Equal(IssueKind.Unmapped, state.Issues[0].Kind);
    }

    [Fact]
    public void UpdateIssueFromJiraRefreshesStatusWithoutMovingCard()
    {
        var state = CreateState();
        var service = new BoardSyncService();
        service.Sync(state, Search(Issue("10001", "BTR-1802", "Task")), DateTimeOffset.Parse("2026-04-28T10:00:00Z"));
        service.MoveIssue(state, "10001", BoardSection.Board, BoardColumn.PendingQa, ["10001"]);

        var changed = service.UpdateIssueFromJira(
            state,
            new JiraIssue(
                "10001",
                "BTR-1802",
                "Updated summary",
                "Resolved",
                "Task",
                "https://jira.example.local/browse/BTR-1802",
                DateTimeOffset.Parse("2026-04-28T10:20:00Z")),
            DateTimeOffset.Parse("2026-04-28T10:21:00Z"));

        Assert.True(changed);
        Assert.Equal("Resolved", state.Issues[0].JiraStatus);
        Assert.Equal("Updated summary", state.Issues[0].Summary);
        Assert.Equal(BoardSection.Board, state.Issues[0].Section);
        Assert.Equal(BoardColumn.PendingQa, state.Issues[0].Column);
    }

    private static AppState CreateState()
    {
        return new AppState
        {
            Config =
            {
                JiraHost = "https://jira.example.local",
                UserName = "rafa",
                EncryptedToken = "token",
                Jql = "project = BTR",
                TaskIssueTypes = ["Task", "Story"],
                IncidentIssueTypes = ["Bug", "Incident"]
            }
        };
    }

    private static JiraSearchResult Search(params JiraIssue[] issues)
    {
        return new JiraSearchResult(issues, issues.Length, Truncated: false);
    }

    private static JiraIssue Issue(string id, string key, string type)
    {
        return new JiraIssue(id, key, "Summary", "In Progress", type, $"https://jira.example.local/browse/{key}", null);
    }
}
