using Kaijura.App.Models;

namespace Kaijura.App.Services;

public sealed class BoardSyncService
{
    public SyncSummary Sync(AppState state, JiraSearchResult searchResult, DateTimeOffset now)
    {
        var byId = state.Issues
            .Where(issue => !string.IsNullOrWhiteSpace(issue.JiraId))
            .ToDictionary(issue => issue.JiraId, StringComparer.OrdinalIgnoreCase);

        var returnedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var jiraIssue in searchResult.Issues)
        {
            var id = string.IsNullOrWhiteSpace(jiraIssue.Id) ? jiraIssue.Key : jiraIssue.Id;
            returnedIds.Add(id);

            if (!byId.TryGetValue(id, out var issue))
            {
                issue = CreateIssueState(jiraIssue, state, now);
                state.Issues.Add(issue);
                byId[id] = issue;
            }
            else
            {
                UpdateFromJira(issue, jiraIssue, state.Config, now);
                RestoreIfReturned(issue);
            }
        }

        foreach (var issue in state.Issues)
        {
            if (returnedIds.Contains(issue.JiraId))
            {
                continue;
            }

            if (issue.Section == BoardSection.Archived)
            {
                issue.IsMissing = true;
                issue.MissingSince ??= now;
                continue;
            }

            if (issue.Section != BoardSection.Missing)
            {
                issue.LastVisibleSection = issue.Section;
                issue.Section = BoardSection.Missing;
                issue.IsMissing = true;
                issue.MissingSince = now;
            }
        }

        NormalizeOrders(state);

        var visibleCount = state.Issues.Count(issue => issue.Section is BoardSection.Backlog or BoardSection.Board);
        var missingCount = state.Issues.Count(issue => issue.IsMissing);
        var unmappedCount = state.Issues.Count(issue => issue.Kind == IssueKind.Unmapped && !issue.IsMissing);
        return new SyncSummary(searchResult.Total, visibleCount, missingCount, unmappedCount, searchResult.Truncated);
    }

    public void MoveIssue(
        AppState state,
        string jiraId,
        BoardSection targetSection,
        BoardColumn targetColumn,
        IReadOnlyList<string> orderedIssueIds)
    {
        var issue = FindIssue(state, jiraId);
        if (issue is null || issue.Section is BoardSection.Archived or BoardSection.Missing)
        {
            return;
        }

        issue.Section = targetSection;
        issue.Column = targetColumn;
        issue.LastVisibleSection = targetSection;
        issue.IsMissing = false;
        issue.MissingSince = null;

        ApplyOrdering(state, issue, targetSection, targetColumn, orderedIssueIds);
    }

    public bool ArchiveIssue(AppState state, string jiraId, DateTimeOffset now)
    {
        var issue = FindIssue(state, jiraId);
        if (issue is null || issue.Section != BoardSection.Board || issue.Column != BoardColumn.Ready)
        {
            return false;
        }

        issue.LastVisibleSection = issue.Section;
        issue.Section = BoardSection.Archived;
        issue.ArchivedAt = now;
        return true;
    }

    public bool RestoreIssue(AppState state, string jiraId)
    {
        var issue = FindIssue(state, jiraId);
        if (issue is null)
        {
            return false;
        }

        issue.Section = issue.IsMissing ? BoardSection.Missing : BoardSection.Backlog;
        issue.LastVisibleSection = issue.Section;
        issue.ArchivedAt = null;
        issue.SortOrder = NextSortOrder(state, issue.Section, issue.Kind, issue.Column);
        return true;
    }

    private IssueState CreateIssueState(JiraIssue jiraIssue, AppState state, DateTimeOffset now)
    {
        var kind = Classify(jiraIssue.IssueType, state.Config);
        var issue = new IssueState
        {
            JiraId = string.IsNullOrWhiteSpace(jiraIssue.Id) ? jiraIssue.Key : jiraIssue.Id,
            Key = jiraIssue.Key,
            Summary = jiraIssue.Summary,
            JiraStatus = jiraIssue.JiraStatus,
            IssueType = jiraIssue.IssueType,
            BrowseUrl = jiraIssue.BrowseUrl,
            Kind = kind,
            Section = BoardSection.Backlog,
            LastVisibleSection = BoardSection.Backlog,
            Column = BoardColumn.ToDo,
            SortOrder = NextSortOrder(state, BoardSection.Backlog, kind, BoardColumn.ToDo),
            FirstSeenAt = now,
            LastSeenAt = now
        };

        return issue;
    }

    private void UpdateFromJira(IssueState issue, JiraIssue jiraIssue, AppConfig config, DateTimeOffset now)
    {
        issue.Key = jiraIssue.Key;
        issue.Summary = jiraIssue.Summary;
        issue.JiraStatus = jiraIssue.JiraStatus;
        issue.IssueType = jiraIssue.IssueType;
        issue.BrowseUrl = jiraIssue.BrowseUrl;
        issue.Kind = Classify(jiraIssue.IssueType, config);
        issue.LastSeenAt = now;
    }

    private static void RestoreIfReturned(IssueState issue)
    {
        issue.IsMissing = false;
        issue.MissingSince = null;

        if (issue.Section != BoardSection.Missing)
        {
            return;
        }

        issue.Section = issue.LastVisibleSection is BoardSection.Missing or BoardSection.Archived
            ? BoardSection.Backlog
            : issue.LastVisibleSection;
    }

    private static IssueKind Classify(string issueType, AppConfig config)
    {
        if (Contains(config.TaskIssueTypes, issueType))
        {
            return IssueKind.Task;
        }

        if (Contains(config.IncidentIssueTypes, issueType))
        {
            return IssueKind.Incident;
        }

        return IssueKind.Unmapped;
    }

    private static bool Contains(IEnumerable<string> values, string value)
    {
        return values.Any(candidate => string.Equals(candidate.Trim(), value.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private static IssueState? FindIssue(AppState state, string jiraId)
    {
        return state.Issues.FirstOrDefault(issue => string.Equals(issue.JiraId, jiraId, StringComparison.OrdinalIgnoreCase));
    }

    private static void ApplyOrdering(
        AppState state,
        IssueState movedIssue,
        BoardSection targetSection,
        BoardColumn targetColumn,
        IReadOnlyList<string> orderedIssueIds)
    {
        var ids = orderedIssueIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!ids.Contains(movedIssue.JiraId, StringComparer.OrdinalIgnoreCase))
        {
            ids.Add(movedIssue.JiraId);
        }

        for (var index = 0; index < ids.Count; index++)
        {
            var issue = FindIssue(state, ids[index]);
            if (issue is null || issue.Kind != movedIssue.Kind)
            {
                continue;
            }

            issue.Section = targetSection;
            issue.Column = targetColumn;
            issue.SortOrder = index;
        }

        NormalizeOrders(state);
    }

    private static int NextSortOrder(AppState state, BoardSection section, IssueKind kind, BoardColumn column)
    {
        return state.Issues
            .Where(issue => issue.Section == section && issue.Kind == kind && issue.Column == column)
            .Select(issue => issue.SortOrder)
            .DefaultIfEmpty(-1)
            .Max() + 1;
    }

    private static void NormalizeOrders(AppState state)
    {
        foreach (var group in state.Issues.GroupBy(issue => new { issue.Section, issue.Kind, issue.Column }))
        {
            var ordered = group.OrderBy(issue => issue.SortOrder).ThenBy(issue => issue.Key).ToList();
            for (var index = 0; index < ordered.Count; index++)
            {
                ordered[index].SortOrder = index;
            }
        }
    }
}
