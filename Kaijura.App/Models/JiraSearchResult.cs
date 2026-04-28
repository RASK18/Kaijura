namespace Kaijura.App.Models;

public sealed record JiraSearchResult(
    IReadOnlyList<JiraIssue> Issues,
    int Total,
    bool Truncated);
