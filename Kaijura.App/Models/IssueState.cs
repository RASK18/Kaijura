namespace Kaijura.App.Models;

public sealed class IssueState
{
    public string JiraId { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string JiraStatus { get; set; } = string.Empty;
    public string IssueType { get; set; } = string.Empty;
    public string BrowseUrl { get; set; } = string.Empty;
    public IssueKind Kind { get; set; } = IssueKind.Unmapped;
    public BoardSection Section { get; set; } = BoardSection.Backlog;
    public BoardSection LastVisibleSection { get; set; } = BoardSection.Backlog;
    public BoardColumn Column { get; set; } = BoardColumn.ToDo;
    public int SortOrder { get; set; }
    public bool IsMissing { get; set; }
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset? MissingSince { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
}
