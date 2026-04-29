using Kaijura.App.Models;
using Kaijura.App.Services;

namespace Kaijura.Tests;

public sealed class CommentSyncServiceTests
{
    [Fact]
    public async Task FirstCommentSyncEstablishesReadBaseline()
    {
        var state = CreateState();
        var issue = AddBoardIssue(state);
        var reader = new FakeCommentReader(new JiraComment(
            "50001",
            "alice",
            "alice-key",
            "Alice",
            "alice@example.local",
            DateTimeOffset.Parse("2026-04-28T10:00:00Z"),
            DateTimeOffset.Parse("2026-04-28T10:00:00Z")));

        await new CommentSyncService().SyncKanbanCommentsAsync(state, reader, "token", CancellationToken.None);

        Assert.True(issue.CommentBaselineEstablished);
        Assert.False(issue.HasUnreadComment);
        Assert.Equal("50001", issue.LastRelevantCommentId);
        Assert.Equal("50001", issue.LastReadCommentId);
    }

    [Fact]
    public async Task NewRelevantCommentMarksIssueUnread()
    {
        var state = CreateState();
        var issue = AddBoardIssue(state);
        var reader = new FakeCommentReader(new JiraComment(
            "50001",
            "alice",
            "alice-key",
            "Alice",
            "alice@example.local",
            DateTimeOffset.Parse("2026-04-28T10:00:00Z"),
            DateTimeOffset.Parse("2026-04-28T10:00:00Z")));
        var service = new CommentSyncService();

        await service.SyncKanbanCommentsAsync(state, reader, "token", CancellationToken.None);

        reader.Comment = new JiraComment(
            "50002",
            "bob",
            "bob-key",
            "Bob",
            "bob@example.local",
            DateTimeOffset.Parse("2026-04-28T10:10:00Z"),
            DateTimeOffset.Parse("2026-04-28T10:10:00Z"));

        await service.SyncKanbanCommentsAsync(state, reader, "token", CancellationToken.None);

        Assert.True(issue.HasUnreadComment);
        Assert.Equal("50002", issue.LastRelevantCommentId);
        Assert.Equal("50001", issue.LastReadCommentId);
        Assert.Equal("Bob", issue.LastRelevantCommentAuthor);
    }

    [Fact]
    public async Task ConfiguredUserAndIgnoredAuthorsDoNotCreateUnreadNotice()
    {
        var state = CreateState();
        state.Config.IgnoredCommentAuthors = ["ci-bot"];
        var issue = AddBoardIssue(state);
        var reader = new IgnoringFakeCommentReader(new JiraComment(
            "50001",
            "ci-bot",
            "ci-bot-key",
            "CI Bot",
            "ci@example.local",
            DateTimeOffset.Parse("2026-04-28T10:00:00Z"),
            DateTimeOffset.Parse("2026-04-28T10:00:00Z")));

        await new CommentSyncService().SyncKanbanCommentsAsync(state, reader, "token", CancellationToken.None);

        Assert.False(issue.HasUnreadComment);
        Assert.True(issue.CommentBaselineEstablished);
        Assert.Empty(issue.LastRelevantCommentId);
        Assert.Contains("rafa", reader.LastIgnoredAuthors, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("ci-bot", reader.LastIgnoredAuthors, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void MarkCommentReadClearsUnreadState()
    {
        var state = CreateState();
        var issue = AddBoardIssue(state);
        issue.CommentBaselineEstablished = true;
        issue.LastRelevantCommentId = "50002";
        issue.LastReadCommentId = "50001";
        issue.HasUnreadComment = true;

        var changed = new CommentSyncService().MarkCommentRead(state, issue.JiraId);

        Assert.True(changed);
        Assert.False(issue.HasUnreadComment);
        Assert.Equal("50002", issue.LastReadCommentId);
    }

    [Fact]
    public async Task BacklogIssuesAreNotCheckedForComments()
    {
        var state = CreateState();
        var issue = AddBoardIssue(state);
        issue.Section = BoardSection.Backlog;
        var reader = new FakeCommentReader(new JiraComment(
            "50001",
            "alice",
            "alice-key",
            "Alice",
            "alice@example.local",
            DateTimeOffset.Parse("2026-04-28T10:00:00Z"),
            DateTimeOffset.Parse("2026-04-28T10:00:00Z")));

        var summary = await new CommentSyncService().SyncKanbanCommentsAsync(state, reader, "token", CancellationToken.None);

        Assert.Equal(0, summary.CheckedCount);
        Assert.Equal(0, reader.RequestCount);
        Assert.False(issue.CommentBaselineEstablished);
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
                Jql = "project = BTR"
            }
        };
    }

    private static IssueState AddBoardIssue(AppState state)
    {
        var issue = new IssueState
        {
            JiraId = "10001",
            Key = "BTR-1802",
            Summary = "Summary",
            JiraStatus = "In Progress",
            IssueType = "Task",
            Kind = IssueKind.Task,
            Section = BoardSection.Board,
            Column = BoardColumn.Dev
        };

        state.Issues.Add(issue);
        return issue;
    }

    private sealed class FakeCommentReader : IJiraCommentReader
    {
        public FakeCommentReader(JiraComment? comment)
        {
            Comment = comment;
        }

        public JiraComment? Comment { get; set; }
        public int RequestCount { get; private set; }

        public Task<JiraComment?> GetLatestRelevantCommentAsync(
            AppConfig config,
            string token,
            string issueIdOrKey,
            IReadOnlyCollection<string> ignoredAuthors,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(Comment);
        }
    }

    private sealed class IgnoringFakeCommentReader : IJiraCommentReader
    {
        private readonly JiraComment _comment;

        public IgnoringFakeCommentReader(JiraComment comment)
        {
            _comment = comment;
        }

        public IReadOnlyCollection<string> LastIgnoredAuthors { get; private set; } = [];

        public Task<JiraComment?> GetLatestRelevantCommentAsync(
            AppConfig config,
            string token,
            string issueIdOrKey,
            IReadOnlyCollection<string> ignoredAuthors,
            CancellationToken cancellationToken)
        {
            LastIgnoredAuthors = ignoredAuthors.ToList();
            var isIgnored = ignoredAuthors.Any(author =>
                string.Equals(author, _comment.AuthorName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(author, _comment.AuthorDisplayName, StringComparison.OrdinalIgnoreCase));

            return Task.FromResult(isIgnored ? null : _comment);
        }
    }
}
