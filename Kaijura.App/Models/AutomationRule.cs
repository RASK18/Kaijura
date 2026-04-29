namespace Kaijura.App.Models;

public sealed class AutomationRule
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public AutomationTrigger Trigger { get; set; } = AutomationTrigger.TicketNew;
    public AutomationIssueScope IssueScope { get; set; } = AutomationIssueScope.All;
    public AutomationLocation CurrentLocation { get; set; } = AutomationLocation.Any;
    public List<AutomationCondition> Conditions { get; set; } = [];
    public AutomationAction Action { get; set; } = new();
    public bool StopProcessing { get; set; } = true;
}

public sealed class AutomationCondition
{
    public AutomationConditionField Field { get; set; } = AutomationConditionField.JiraStatus;
    public AutomationConditionOperator Operator { get; set; } = AutomationConditionOperator.IsAnyOf;
    public List<string> Values { get; set; } = [];
    public bool BoolValue { get; set; }
    public int Days { get; set; }
}

public sealed class AutomationAction
{
    public AutomationDestination Destination { get; set; } = AutomationDestination.ToDo;
}

public enum AutomationTrigger
{
    TicketNew,
    JiraStatusChanged,
    IssueClassificationChanged,
    RelevantCommentChanged,
    TemporalCheck
}

public enum AutomationIssueScope
{
    All,
    Task,
    Incident,
    Unmapped
}

public enum AutomationLocation
{
    Any,
    Backlog,
    ToDo,
    Progress,
    PendingQa,
    ValidatedQa
}

public enum AutomationConditionField
{
    JiraStatus,
    IssueType,
    HasUnreadComment,
    JiraUpdatedMoreThanDaysAgo,
    FirstSeenMoreThanDaysAgo
}

public enum AutomationConditionOperator
{
    IsAnyOf,
    IsNotAnyOf,
    Is,
    IsNot,
    MoreThanDaysAgo
}

public enum AutomationDestination
{
    Backlog,
    ToDo,
    Progress,
    PendingQa,
    ValidatedQa,
    Archived
}
