using Kaijura.App.Models;

namespace Kaijura.App.Services;

public sealed class CommentSyncService
{
    private const int MaxConcurrentRequests = 4;

    public async Task<CommentSyncSummary> SyncKanbanCommentsAsync(
        AppState state,
        IJiraCommentReader commentReader,
        string token,
        CancellationToken cancellationToken)
    {
        var ignoredAuthors = BuildIgnoredAuthors(state.Config);
        var issues = state.Issues
            .Where(issue => issue.Section == BoardSection.Board && !issue.IsMissing)
            .ToList();

        var checkedCount = 0;
        var failedCount = 0;
        using var throttler = new SemaphoreSlim(MaxConcurrentRequests);

        var tasks = issues.Select(async issue =>
        {
            await throttler.WaitAsync(cancellationToken);
            try
            {
                var latestComment = await commentReader.GetLatestRelevantCommentAsync(
                    state.Config,
                    token,
                    issue.Key,
                    ignoredAuthors,
                    cancellationToken);

                var hadUnreadComment = issue.HasUnreadComment;
                ApplyLatestComment(issue, latestComment);
                if (hadUnreadComment != issue.HasUnreadComment)
                {
                    issue.PendingAutomationTriggers.Add(AutomationTrigger.RelevantCommentChanged);
                }

                Interlocked.Increment(ref checkedCount);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                Interlocked.Increment(ref failedCount);
            }
            finally
            {
                throttler.Release();
            }
        });

        await Task.WhenAll(tasks);
        return new CommentSyncSummary(checkedCount, failedCount);
    }

    public bool MarkCommentRead(AppState state, string jiraId)
    {
        var issue = state.Issues.FirstOrDefault(candidate =>
            string.Equals(candidate.JiraId, jiraId, StringComparison.OrdinalIgnoreCase));

        if (issue is null || string.IsNullOrWhiteSpace(issue.LastRelevantCommentId))
        {
            return false;
        }

        issue.LastReadCommentId = issue.LastRelevantCommentId;
        issue.HasUnreadComment = false;
        issue.CommentBaselineEstablished = true;
        return true;
    }

    private static void ApplyLatestComment(IssueState issue, JiraComment? latestComment)
    {
        if (latestComment is null || string.IsNullOrWhiteSpace(latestComment.Id))
        {
            issue.CommentBaselineEstablished = true;
            issue.HasUnreadComment = false;
            issue.LastRelevantCommentId = string.Empty;
            issue.LastReadCommentId = string.Empty;
            issue.LastRelevantCommentAuthor = string.Empty;
            issue.LastRelevantCommentAt = null;
            return;
        }

        if (!issue.CommentBaselineEstablished)
        {
            issue.LastRelevantCommentId = latestComment.Id;
            issue.LastReadCommentId = latestComment.Id;
            issue.LastRelevantCommentAuthor = latestComment.AuthorLabel;
            issue.LastRelevantCommentAt = latestComment.LastChangedAt;
            issue.HasUnreadComment = false;
            issue.CommentBaselineEstablished = true;
            return;
        }

        if (!string.Equals(issue.LastRelevantCommentId, latestComment.Id, StringComparison.OrdinalIgnoreCase))
        {
            issue.LastRelevantCommentId = latestComment.Id;
            issue.LastRelevantCommentAuthor = latestComment.AuthorLabel;
            issue.LastRelevantCommentAt = latestComment.LastChangedAt;
            issue.HasUnreadComment = !string.Equals(issue.LastReadCommentId, latestComment.Id, StringComparison.OrdinalIgnoreCase);
            return;
        }

        issue.LastRelevantCommentAuthor = latestComment.AuthorLabel;
        issue.LastRelevantCommentAt = latestComment.LastChangedAt;
        issue.HasUnreadComment = !string.Equals(issue.LastReadCommentId, latestComment.Id, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyCollection<string> BuildIgnoredAuthors(AppConfig config)
    {
        return config.IgnoredCommentAuthors
            .Select(author => author.Trim())
            .Where(author => !string.IsNullOrWhiteSpace(author))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
