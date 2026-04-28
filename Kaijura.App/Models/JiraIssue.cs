namespace Kaijura.App.Models;

public sealed record JiraIssue(
    string Id,
    string Key,
    string Summary,
    string JiraStatus,
    string IssueType,
    string BrowseUrl,
    DateTimeOffset? Updated);
